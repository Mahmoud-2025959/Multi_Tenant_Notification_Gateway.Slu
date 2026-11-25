# Multi-Tenant Notification Gateway

Task 8: Attendance System, الجهاز المركزي للتعمير. .NET 8, Clean Architecture, SQL Server, RabbitMQ, SignalR.

## Layout

```
Multi_Tenant_Notification_Gateway     API host: controllers, JWT auth, tenant resolution, rate limiting, SignalR hub, dev seed
NotificationGateway.Domain            entities + delivery state machine (no dependencies)
NotificationGateway.Application       settings, routing, templates, accept flow, dispatcher logic, webhooks, reports
NotificationGateway.Infrastructure    EF Core, tenant isolation, AES-GCM secrets, SQL leasing/quotas, RabbitMQ, workers, provider adapters
NotificationGateway.UnitTests | IntegrationTests (Testcontainers SQL Server) | ArchitectureTests (NetArchTest)
```

## Run locally

```bash
docker compose up -d          # SQL Server, RabbitMQ, smtp4dev (http://localhost:5080), Seq (http://localhost:5341)
dotnet build
dotnet run --project Multi_Tenant_Notification_Gateway
```

In Development the API migrates the database, seeds two demo tenants and opens Swagger.

1. `POST /api/dev/token` → `{ "userId": "attendance-service", "tenantId": "11111111-1111-1111-1111-111111111111", "roles": ["Service","TenantAdmin"] }`
2. Authorize in Swagger with the token.
3. `POST /api/notifications`:

```json
{
  "notificationType": "attendance.late",
  "templateCode": "ATTENDANCE_LATE",
  "priority": "Normal",
  "data": { "EmployeeName": "Ahmed Ali", "CheckInTime": "09:42", "Date": "2026-11-22" },
  "recipients": [ { "userId": "manager-1", "email": "manager@agency.gov.eg", "phone": "+201001234567" } ],
  "channels": [ "InApp", "Email", "Sms" ]
}
```

4. `GET /api/notifications/{id}` shows every delivery and every provider attempt. For tenant-001 the SMS primary is a
   simulated outage, so the attempts show the fallback to the backup provider. E-mails appear in smtp4dev.

New migration (only if the model changes):

```bash
dotnet ef migrations add <Name> -p NotificationGateway.Infrastructure -s Multi_Tenant_Notification_Gateway -o Migrations
```

## Roles (JWT claim `role`) and tenant (`tenant_id`)

| Role | Can |
|---|---|
| `SystemAdmin` | tenants, quotas, shared providers/templates, provider catalog, act for a tenant via `X-Tenant-Id` |
| `TenantAdmin` | own channels, provider configurations + chains, templates, routing rules, webhooks, reports, dead letters |
| `Service` | send notifications and read their status (the Attendance System backend) |
| `TenantUser` | own inbox (`/api/me/inbox`) and preferences (`/api/me/preferences`), SignalR `/hubs/notifications` |

## Documentation (`docs/`)

| Document | Content |
|---|---|
| [Architecture](docs/Multi_Tenant_Notification_Gateway_Architecture.md) | Layers, components, flows, decisions, answers to the technical discussion |
| [Channels and providers](docs/Notification_Channels_and_Providers_Comparison.md) | What was implemented, capabilities, error classification |
| [Multi-tenancy](docs/Multi_Tenancy_Strategy.md) | Tenant resolution, 4 isolation layers, shared vs tenant config, quotas |
| [Database ERD](docs/Database_ERD.md) | ERD, keys, unique constraints, indexes, migrations |
| [Routing and fallback](docs/Notification_Routing_and_Fallback.md) | Rules, when fallback happens, duplicates, retries, recovery |
| [Security](docs/Security_and_Secrets_Management.md) | AuthZ, encryption, rotation, webhooks, SSRF, audit |
| [API](docs/API_Documentation.md) | Every endpoint with roles and examples |
| [Testing report](docs/Testing_Report.md) | T01–T18 mapping and results |
| [Deployment](docs/Deployment_and_Configuration.md) | Configuration reference, production checklist |

Demo requests: `Multi_Tenant_Notification_Gateway/demo.http`.

## Progress

- [x] Domain model, delivery state machine, persistence, 4-layer tenant isolation, AES-GCM secrets, audit
- [x] JWT + tenant resolution (route/body tenant validated), spec routes `/api/tenants/{tenantId}/...`
- [x] Orchestrator / router / dispatcher / provider resolver / retry + circuit breaker, outbox + RabbitMQ, lease recovery
- [x] Providers: InApp, SignalR, SMTP, SendGrid, Twilio SMS/WhatsApp/Voice, Meta WhatsApp, Local SMS, OneSignal, Fake
- [x] Templates (versioned, AR/EN), preferences (preferred/disabled channels, quiet hours, critical override)
- [x] Webhooks (signed, timestamped, replay-protected, ownership), SSRF guard, quotas, rate limits, reports with filters
- [x] Tests T01–T18 + security tests + unit + architecture
- [x] 9 documents + ERD + diagrams + demo
- [x] Test run: 84 passed, 0 failed (see docs/Testing_Report.md)
- [ ] Attendance System integration (feature flag + outbox forwarder)
