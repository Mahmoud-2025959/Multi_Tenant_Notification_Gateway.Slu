# Deployment and Configuration

## 1. Prerequisites

- .NET 8 SDK
- SQL Server (LocalDB for development, SQL Server 2019+ for production)
- Optional: RabbitMQ (wake-up signal; without it dispatchers poll every 2 s), smtp4dev (dev e-mail), Seq (logs), Docker
- `dotnet tool install --global dotnet-ef --version 8.*`

## 2. Local run

```powershell
docker compose up -d        # optional: SQL Server, RabbitMQ, smtp4dev (http://localhost:5080), Seq (http://localhost:5341)
dotnet ef database update -p NotificationGateway.Infrastructure -s Multi_Tenant_Notification_Gateway
dotnet run --project Multi_Tenant_Notification_Gateway
```

In `Development` the API migrates, seeds two demo tenants (`tenant-001`, `tenant-002`), enables Swagger and `/api/dev/token`.

## 3. Configuration reference (`appsettings.json` / environment variables)

Environment variables use `__` for nesting, e.g. `Jwt__SigningKey`.

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings:NotificationGateway` | LocalDB | SQL Server connection string |
| `Jwt:Issuer` / `Jwt:Audience` | `attendance-system` / `notification-gateway` | Must match the token issuer |
| `Jwt:SigningKey` | — (required, ≥ 32 chars) | Shared HMAC key with the issuer. **Secret** |
| `Jwt:EnableDevTokens` | `false` | Dev token endpoint. Never `true` in production |
| `SecretProtection:ActiveKeyId` | — | Key id used to encrypt new envelopes |
| `SecretProtection:Keys:<id>` | — | Base64 32-byte master keys. **Secret** — Key Vault / Docker secrets |
| `RabbitMq:Enabled` / `Uri` / `Exchange` / `WakeupQueue` | `true` / `amqp://guest:guest@localhost:5672/` | Broker for dispatch wake-ups |
| `Workers:Enabled` | `true` | Run dispatcher/outbox/recovery workers in this instance |
| `Workers:BatchSize` / `Parallelism` / `PollSeconds` / `LeaseSeconds` | 20 / 8 / 2 / 120 | Throughput and safety (`LeaseSeconds` > max provider timeout) |
| `CircuitBreaker:FailureThreshold` / `OpenSeconds` | 5 / 60 | Per provider configuration |
| `OutboundSecurity:AllowPrivateNetworks` | `false` (`true` in Development) | SSRF guard for provider endpoints |
| `Database:MigrateOnStartup` | `false` (always in Development) | Apply migrations at startup |
| `Database:SeedDemoData` | `false` (`true` in Development) | Demo tenants/providers/templates |
| `Serilog:*` | Console + Seq | Structured logs (`TenantId`, `DeliveryId` in scope) |

Generate a master key: `[Convert]::ToBase64String((1..32 | % { Get-Random -Max 256 }) -as [byte[]])` (PowerShell) or
`openssl rand -base64 32`.

## 4. Production checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production`
- [ ] `Jwt:SigningKey`, `SecretProtection:Keys:*` from Key Vault / secrets store, not files
- [ ] `Jwt:EnableDevTokens=false`, `Database:SeedDemoData=false`, `OutboundSecurity:AllowPrivateNetworks=false`
- [ ] Apply migrations with the idempotent script (`docs/sql`) or `Database:MigrateOnStartup` on one instance only
- [ ] HTTPS termination; public URL reachable by providers for `/api/webhooks/*`
- [ ] RabbitMQ with credentials (not guest)
- [ ] Provider pricing set in the catalog for cost reports
- [ ] Health check `/health` wired to the load balancer
- [ ] SignalR backplane (Azure SignalR / Redis) when running more than one instance

## 5. Topologies

| Topology | Settings |
|---|---|
| Single instance (pilot) | Defaults |
| N API instances + same workers | All instances `Workers:Enabled=true` (leasing makes it safe) |
| API instances + dedicated workers | API: `Workers:Enabled=false`; workers: same image with `Workers:Enabled=true`, no public ingress |

## 6. Onboarding a provider product

1. Platform: product appears automatically in `GET /api/providers` (catalog sync). Optionally set price/enable.
2. Tenant admin: `POST /api/tenants/{t}/providers` with the product's `requiredSettings` / `requiredSecrets`.
3. `PUT /api/tenants/{t}/channels/{channel}/providers` to place it in the chain.
4. If the product sends receipts: `POST /api/tenants/{t}/webhooks`, configure the returned URL + secret at the vendor.

### Product-specific settings

| Key | Settings | Secrets |
|---|---|---|
| `smtp` | `host`, `port`, `security` (none/starttls/ssl), `from` | `username`, `password` (optional) |
| `sendgrid` | `from`, `baseUrl`? | `apiKey` |
| `twilio-sms` | `from` or `messagingServiceSid`, `statusCallbackUrl`? | `accountSid`, `authToken` |
| `twilio-whatsapp` | `from`, `contentSid`?, `statusCallbackUrl`? | `accountSid`, `authToken` |
| `twilio-voice` | `from`, `voiceLanguage`? | `accountSid`, `authToken` |
| `meta-whatsapp` | `phoneNumberId`, `apiVersion`?, `messageType` (template/text), `languageCode`? | `accessToken` (webhook secret = Meta app secret) |
| `local-sms` | `url`, `sender`, `apiKeyHeader`?, `messageIdField`? | `apiKey`? |
| `onesignal` | `appId` | `restApiKey` |
| `fake-*` | `mode`, `failFirst`, `latencyMs` | — |

## 7. Operations

- **Dead letters:** `GET /api/tenants/{t}/dead-letters`, replay with `…/requeue`.
- **Uncertain deliveries:** check the provider console / receipts; requeue only if the message was not delivered.
- **Provider outage:** circuit opens automatically; fallbacks take over; provider report shows skipped calls.
- **Retention:** purge `ProcessedWebhookEvents` older than the providers' retry window (indexed on `ReceivedAt`).
- **Logs:** Serilog with `TenantId` and `DeliveryId` scopes; never secrets or full addresses.

## 8. Integration with the Attendance System

The Attendance System keeps its own notification module and can switch to the gateway with a feature flag
(`Notifications:Mode = Local | Gateway`). Requests are written to an Attendance outbox and forwarded to
`POST /api/notifications/send` with `Idempotency-Key = EventId`, so a gateway outage never fails a check-in and a resend never
duplicates a notification. Tokens: the Attendance System issues a short-lived `Service` token with `tenant_id`; user tokens
carry `tenant_id` so the bell/inbox/SignalR can use the gateway too.
