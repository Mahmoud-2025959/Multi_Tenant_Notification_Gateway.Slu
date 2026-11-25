# Multi-Tenancy Strategy

## 1. Model

- **Shared database, shared schema, `TenantId` column** on every tenant-owned table.
- Chosen over database-per-tenant because the Attendance System has a moderate number of tenants (agencies/branches),
  configuration and reporting are cross-cutting, and onboarding must be configuration-only.
- Isolation is enforced in **four independent layers**, so a bug in one layer does not leak data.

## 2. Tenant identification and resolution

| Source | Trusted? | Used for |
|---|---|---|
| JWT claim `tenant_id` (issued by the Attendance System, validated issuer/audience/signature/lifetime) | **Yes** — the only source of truth | Every request |
| Route `{tenantId}` (`/api/tenants/tenant-001/...`, code or GUID) | No | Must resolve to the token's tenant, else **403** (unknown tenants also 403 — no enumeration) |
| Body `tenantId` on `POST /api/notifications/send` | No | Must match the token's tenant, else **403** |
| Header `X-Tenant-Id` / route for a **SystemAdmin** | Only for SystemAdmin | Support/onboarding; every change is audited with the admin's user id |

Implemented in `TenantResolutionMiddleware` → `TenantContext.SetFromAuthenticatedUser(...)` (can be set once per request).
Background workers use `BeginScope(tenantId)` after reading the row they lease (`BeginSystemScope` only to find the row).

## 3. Isolation layers

```mermaid
flowchart LR
    A[1. API: tenant from token, route/body validated, role policies] --> B[2. EF global query filters on ITenantOwned]
    B --> C[3. SaveChanges write guard]
    C --> D[4. Database: composite FKs (TenantId, Id)]
```

1. **API layer** — authorization policies (`SystemAdmin`, `TenantAdmin`, `Sender`, `TenantMember`), tenant from token.
2. **Read isolation** — EF Core global query filters on every `ITenantOwned` entity:
   `e.TenantId == CurrentTenantId` (shared-capable entities: `TenantId == null || TenantId == CurrentTenantId`).
   A foreign id is simply *not found* → 404, which also hides existence.
3. **Write isolation** — `NotificationGatewayDbContext.SaveChanges` rejects any added/modified/deleted tenant-owned row whose
   `TenantId` differs from the current tenant, and any change to a shared row unless the caller is a SystemAdmin.
4. **Database integrity** — children reference parents with composite keys `(TenantId, NotificationId)`,
   `(TenantId, RecipientId)`, `(TenantId, DeliveryId)`. Even raw SQL cannot attach tenant A's delivery to tenant B's notification.

## 4. Tenant-specific vs shared configuration

| Item | Tenant-specific | Shared (platform) |
|---|---|---|
| Provider configurations (credentials, sender identity) | Yes (`TenantId = X`) | Yes (`TenantId = NULL`, SystemAdmin only) — e.g. platform SMTP, in-app, SignalR |
| Channel activation + retry/timeout policy | Yes (`TenantChannelSettings`) | — |
| Provider chain (primary + fallbacks) | Yes (`ChannelProviderAssignments`, may point at shared configs) | — |
| Templates | Yes (override) | Yes (default) — resolution prefers the tenant's |
| Routing rules | Yes | — |
| Quotas | Set by the platform per tenant | — |
| Rate limit | `Tenant.RequestsPerMinute` | — |
| History, reports, inbox, preferences | Yes | — |

A tenant can **use** a shared configuration in its chain but cannot read its secrets or modify it.
A tenant can never reference another tenant's configuration (T08): it is invisible through the query filter.

## 5. Quotas and rate limits (resource fairness)

- **Rate limit** (`tenant-send` policy): fixed window per tenant partition on the send API → 429 with `Retry-After`.
- **Quotas**: daily/monthly, per channel or all channels, counted in deliveries, consumed atomically in SQL inside the
  request transaction (two instances cannot both take the last unit). Policy per tenant:
  `Reject` → 429 for the whole request (nothing consumed), `Defer` → deliveries scheduled for the next period.
- **Dispatcher fairness**: priority ordering, bounded parallelism, circuit breaker per provider configuration.

## 6. Permissions

| Role (JWT `role`) | Scope |
|---|---|
| `SystemAdmin` | Tenants, tenant policies, quotas, shared providers/templates, provider catalog & pricing, cross-tenant usage report |
| `TenantAdmin` | Own tenant: settings, channels, providers & secrets, chains, templates, routing rules, webhooks, reports, dead letters |
| `Service` | Send notifications, read their status/history (business modules) |
| `TenantUser` | Own inbox, own preferences, SignalR hub |

## 7. Onboarding a tenant (configuration only)

1. `POST /api/admin/tenants` `{code, name, defaultLanguage, timeZoneId}`
2. `PUT /api/admin/tenants/{id}/policies` (rate limit, quota policy, critical override) and `PUT /api/tenants/{code}/quotas`
3. Tenant admin: `PUT /api/tenants/{code}/channels/{channel}` to enable channels
4. `POST /api/tenants/{code}/providers` (own credentials) or use shared configurations
5. `PUT /api/tenants/{code}/channels/{channel}/providers` — primary + fallbacks
6. Optional: tenant templates, routing rules, webhook registrations for receipts

No deployment, no code change.
