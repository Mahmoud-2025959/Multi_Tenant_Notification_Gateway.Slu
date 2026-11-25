using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationGateway.Application.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NotificationGateway.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public bool Enabled { get; set; } = true;
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";
    public string Exchange { get; set; } = "notification-gateway";
    public string WakeupQueue { get; set; } = "ng.dispatch.wakeup";
}

/// <summary>
/// In-process wake-up for dispatcher loops. Bounded + DropWrite: many signals collapse into one pending wake-up.
/// </summary>
public sealed class DispatchSignal : IDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _channel.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* poll interval elapsed */ }
    }
}

public interface IMessageBusPublisher
{
    /// <summary>Returns false when the broker is unavailable (the outbox keeps the message and retries).</summary>
    bool TryPublish(string routingKey, string payload);
}

/// <summary>
/// RabbitMQ is used as a WAKE-UP signal, not as the source of truth: the DB delivery row is. So a lost message only
/// delays work until the next DB poll, and a duplicated message is harmless (the SQL lease admits one dispatcher).
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IMessageBusPublisher, IDisposable
{
    private readonly object _sync = new();
    private IConnection? _connection;
    private IModel? _publishChannel;

    public RabbitMqOptions Options => options.Value;

    public IConnection? GetConnection()
    {
        if (!Options.Enabled) return null;
        lock (_sync)
        {
            if (_connection is { IsOpen: true }) return _connection;
            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(Options.Uri),
                    DispatchConsumersAsync = true,
                    AutomaticRecoveryEnabled = true,
                    ClientProvidedName = "notification-gateway"
                };
                _connection = factory.CreateConnection();
                using var setup = _connection.CreateModel();
                setup.ExchangeDeclare(Options.Exchange, ExchangeType.Topic, durable: true);
                setup.QueueDeclare(Options.WakeupQueue, durable: true, exclusive: false, autoDelete: false);
                setup.QueueBind(Options.WakeupQueue, Options.Exchange, "deliveries.#");
                return _connection;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ unavailable ({Message}); dispatchers fall back to DB polling.", ex.Message);
                _connection = null;
                return null;
            }
        }
    }

    public bool TryPublish(string routingKey, string payload)
    {
        var connection = GetConnection();
        if (connection is null) return false;
        lock (_sync)
        {
            try
            {
                if (_publishChannel is not { IsOpen: true })
                {
                    _publishChannel = connection.CreateModel();
                    _publishChannel.ConfirmSelect();
                }
                var props = _publishChannel.CreateBasicProperties();
                props.Persistent = true;
                props.ContentType = "application/json";
                _publishChannel.BasicPublish(Options.Exchange, routingKey, props, Encoding.UTF8.GetBytes(payload));
                _publishChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ publish failed: {Message}", ex.Message);
                _publishChannel = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        try { _publishChannel?.Close(); } catch { /* ignore */ }
        try { _connection?.Close(); } catch { /* ignore */ }
    }
}

/// <summary>Consumes wake-up messages and pokes the local dispatcher loop.</summary>
public sealed class RabbitMqWakeupConsumer(RabbitMqConnection rabbit, DispatchSignal signal, ILogger<RabbitMqWakeupConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!rabbit.Options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = rabbit.GetConnection();
            if (connection is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            IModel? channel = null;
            try
            {
                channel = connection.CreateModel();
                channel.BasicQos(0, 50, false);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += (_, ea) =>
                {
                    signal.Notify();
                    channel.BasicAck(ea.DeliveryTag, multiple: false);
                    return Task.CompletedTask;
                };
                channel.BasicConsume(rabbit.Options.WakeupQueue, autoAck: false, consumer);
                logger.LogInformation("Listening for dispatch wake-ups on {Queue}", rabbit.Options.WakeupQueue);

                while (!stoppingToken.IsCancellationRequested && channel.IsOpen)
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ consumer error: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            finally
            {
                try { channel?.Close(); } catch { /* ignore */ }
            }
        }
    }
}
