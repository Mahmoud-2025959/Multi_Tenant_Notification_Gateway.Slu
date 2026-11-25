# API Documentation

Base URL (dev): the URL printed by `dotnet run` · Swagger UI: `/swagger` · All bodies JSON, enums as strings.
Authentication: `Authorization: Bearer <JWT>` (claims `sub`, `tenant_id`, `role`). `{tenantId}` = tenant code (`tenant-001`) or GUID.

## 1. Conventions

| Topic | Rule |
|---|---|
| Errors | RFC 7807 `application/problem+json` with `code` and `traceId` |
| 400 | Validation (`errors` per field), business rule (`code`) |
| 401 | Missing/invalid token, invalid webhook signature |
| 403 | Wrong role, or `{tenantId}`/body `tenantId` ≠ token tenant, unknown/inactive tenant |
| 404 | Not found **or belongs to another tenant** (existence is not revealed) |
| 409 | Idempotency key reused with a different body, concurrent duplicate, concurrency conflict |
| 429 | Tenant rate limit (`Retry-After`) or quota exceeded (Reject policy) |
| Idempotency | `Idempotency-Key` header (or body `idempotencyKey`). Replays return the original result with `Idempotent-Replayed: true` |

## 2. Central notification API (role: Service / TenantAdmin)

### `POST /api/notifications/send` (alias `POST /api/notifications`)

```json
{
  "tenantId": "tenant-001",
  "recipient": { "userId": "123", "phone": "+201000000000", "email": "user@example.com" },
  "notificationType": "attendance.late",
  "channels": ["InApp", "SignalR", "Email", "WhatsApp"],
  "templateCode": "ATTENDANCE_LATE",
  "data": { "EmployeeName": "Ahmed", "CheckInTime": "08:30", "Date": "2026-11-22" },
  "priority": "High",
  "correlationId": "attendance-evt-889"
}
```

- `recipient` (single, spec shape) and/or `recipients` (array, up to 1000). Recipient fields: `userId`, `email`, `phone`
  (E.164), `deviceToken`, `language` (`Ar`/`En`).
- `tenantId` optional; must match the token.
- `channels` optional; routing rules decide when omitted or when the rule does not allow caller override.

**202 Accepted** (`Location: /api/notifications/{id}`):

```json
{
  "notificationId": "9b1c…",
  "replayed": false,
  "deliveries": [
    { "deliveryId": "…", "recipientUserId": "123", "channel": "InApp", "status": "Queued", "nextAttemptAt": "2026-11-22T10:00:00Z" },
    { "deliveryId": "…", "recipientUserId": "123", "channel": "Email", "status": "Queued", "nextAttemptAt": "…" }
  ],
  "warnings": ["Channel WhatsApp is not enabled for this tenant and was skipped."]
}
```

### `GET /api/notifications/{id}` — tracking
Every delivery (status, attempts count, provider, provider message id, masked address, timestamps) and every attempt
(`providerKey`, `outcome`, `errorCode`, `httpStatus`, `durationMs`). Overall status: `InProgress | Sent | Delivered | PartiallyDelivered | Failed`.

### `GET /api/notifications` · `GET /api/tenants/{tenantId}/notifications` — history
Query: `notificationType, status, channel, providerKey, priority, from, to, page, pageSize`.

### `POST /api/notifications/{id}/cancel`
Cancels deliveries not yet handed to a provider → `{ "cancelled": n }`.

## 3. Tenant notification settings (role: TenantAdmin; SystemAdmin for any tenant)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/tenants/{tenantId}/notification-settings` | Policies + every channel with its provider chain + quotas with consumption |
| PUT | `/api/tenants/{tenantId}/notification-settings` | `{criticalOverridesUserPreferences, defaultLanguage, timeZoneId}` |
| GET | `/api/tenants/{tenantId}/channels` | All channels (unconfigured = disabled) |
| PUT | `/api/tenants/{tenantId}/channels/{channelId}` | `{isEnabled, maxAttempts, baseRetryDelaySeconds, maxRetryDelaySeconds, providerTimeoutSeconds}` |
| GET/PUT | `/api/tenants/{tenantId}/channels/{channelId}/providers` | Chain: `{providerConfigurationIds: [primary, fallback1, …]}` |
| GET | `/api/tenants/{tenantId}/providers[?channel=]` | Own + shared configurations (no secret values) |
| GET | `/api/tenants/{tenantId}/providers/{providerId}` | One configuration |
| POST | `/api/tenants/{tenantId}/providers` | `{providerKey, name, settings, secrets, senderIdentity, shared}` (`shared` = SystemAdmin only) |
| PUT | `/api/tenants/{tenantId}/providers/{providerId}` | `{name, settings, senderIdentity, isActive}` |
| PUT | `/api/tenants/{tenantId}/providers/{providerId}/secrets` | Rotate: `{secrets: {...}}` → `secretVersion + 1` |
| DELETE | `/api/tenants/{tenantId}/providers/{providerId}` | Deactivate |
| GET/POST | `/api/tenants/{tenantId}/routing-rules` | `{name, notificationType, minPriority, channels, order, allowCallerOverride, isActive}` |
| PUT/DELETE | `/api/tenants/{tenantId}/routing-rules/{ruleId}` | |
| GET | `/api/tenants/{tenantId}/quotas` | Quotas + used in current period |
| PUT/DELETE | `/api/tenants/{tenantId}/quotas[/{quotaId}]` | **SystemAdmin**: `{channel|null, period: Daily|Monthly, limit}` |
| GET/POST | `/api/tenants/{tenantId}/webhooks` | Register provider callbacks: `{providerConfigurationId, signingSecret?}` → callback path + secret (shown once) |
| POST | `/api/tenants/{tenantId}/webhooks/{id}/rotate-secret` | `{signingSecret?, gracePeriodMinutes}` |
| DELETE | `/api/tenants/{tenantId}/webhooks/{id}` | Deactivate |
| GET | `/api/tenants/{tenantId}/dead-letters` | Dead-lettered, failed and uncertain deliveries |
| POST | `/api/tenants/{tenantId}/dead-letters/{deliveryId}/requeue` | Replay (fresh attempt budget) |

Provider settings/secrets per product are listed by `GET /api/providers` (`requiredSettings`, `requiredSecrets`, capabilities).

## 4. Templates (role: TenantAdmin)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/tenants/{tenantId}/templates?code=&includeInactive=` | Own + shared templates, all versions |
| POST | `/api/tenants/{tenantId}/templates` | New version: `{code, channel, language, subject, body, shared}` (older versions deactivated) |
| POST | `/api/tenants/{tenantId}/templates/{id}/activate` · `/deactivate` | Toggle |
| POST | `/api/tenants/{tenantId}/templates/preview` | `{code, channel, language, data}` → rendered `{subject, body}`; 400 lists missing placeholders |

## 5. Reports (role: TenantAdmin)

Common query: `from, to (≤ 1 year, default last 30 days), channel, provider, notificationType, status`.

| Path | Content |
|---|---|
| `GET /api/tenants/{tenantId}/reports/summary` | Totals, success/failure rates, status by channel, retries, dead letters, uncertain, quota consumption |
| `GET /api/tenants/{tenantId}/reports/providers` | Per provider: calls, accepted, failed, uncertain, skipped, avg/max latency, success rate |
| `GET /api/tenants/{tenantId}/reports/cost` | Accepted messages × catalog price |
| `GET /api/tenants/{tenantId}/reports/daily-volume` | Deliveries per day and channel |
| `GET /api/admin/reports/tenant-usage` | **SystemAdmin**: usage per tenant |

## 6. Platform administration (role: SystemAdmin)

| Method | Path | Purpose |
|---|---|---|
| GET/POST | `/api/admin/tenants` | List / onboard `{code, name, defaultLanguage, timeZoneId}` |
| GET | `/api/admin/tenants/{id}` | |
| PUT | `/api/admin/tenants/{id}/policies` | `{criticalOverridesUserPreferences, quotaExceededPolicy, requestsPerMinute, defaultLanguage, timeZoneId}` |
| POST | `/api/admin/tenants/{id}/activate` · `/deactivate` | |
| GET | `/api/providers` | Catalog (TenantAdmin can read) |
| PUT | `/api/providers/{key}` | Enable/disable a product platform-wide, set pricing |
| POST | `/api/providers/re-encrypt-secrets` | After master-key rotation |

## 7. End users (role: TenantUser)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/me/inbox?unreadOnly=&page=&pageSize=` | Own in-app notifications |
| GET | `/api/me/inbox/unread-count` | |
| POST | `/api/me/inbox/{id}/read` · `/api/me/inbox/read-all` | |
| GET/PUT | `/api/me/preferences` | `{language, preferredChannels, disabledChannels, disabledNotificationTypes, quietHoursStart, quietHoursEnd, timeZoneId, allowCriticalOverride}` |

### SignalR hub `/hubs/notifications`
Connect with the JWT (`accessTokenFactory`). Server → client method **`notification`**:
`{ deliveryId, notificationId, notificationType, priority, title, body, sentAt }`. Each connection joins only
`t:<tenant>:u:<user>` from its token.

## 8. Provider callbacks (anonymous, signed)

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/webhooks/{providerKey}/{token}` | Delivery receipts → `{received, applied, duplicates, unmatched}` |
| GET | `/api/webhooks/{providerKey}/{token}?hub.mode=subscribe&hub.verify_token={token}&hub.challenge=…` | Meta subscription handshake |

## 9. Development

`POST /api/dev/token` `{userId, tenantId, roles[], lifetimeMinutes}` → `{accessToken}` (only when `Jwt:EnableDevTokens`).
Ready-made request sequence: `Multi_Tenant_Notification_Gateway/demo.http`.
