# Security and Secrets Management

## 1. Authentication

- JWT bearer tokens issued by the Attendance System (shared issuer/audience/HMAC-SHA256 key, configured in `Jwt:*`).
  Validated: signature, issuer, audience, lifetime (30 s clock skew). Claims are kept unmapped: `sub`, `tenant_id`, `role`.
- SignalR accepts the token as `?access_token=` on `/hubs/*` only (browsers cannot set headers on WebSockets).
- `POST /api/dev/token` exists **only** when `Jwt:EnableDevTokens = true` (Development config).

## 2. Authorization

| Policy | Roles | Protects |
|---|---|---|
| `SystemAdmin` | SystemAdmin | Tenants, policies, quotas, shared configurations, catalog, re-encryption, cross-tenant usage |
| `TenantAdmin` | TenantAdmin, SystemAdmin | Tenant settings, providers & secrets, chains, templates, routing, webhooks, reports, dead letters |
| `Sender` | Service, TenantAdmin, SystemAdmin | Send, status, history, cancel |
| `TenantMember` | any authenticated tenant user | Own inbox and preferences |

Application services repeat critical checks (e.g. only SystemAdmin may create shared configurations or re-encrypt),
so a misconfigured controller cannot bypass them.

## 3. Tenant isolation on every API and query

See *Multi_Tenancy_Strategy.md*. Summary: tenant from the token only; route/body tenant ids validated (403); EF global query
filters (foreign ids → 404); SaveChanges write guard; composite foreign keys in the schema. Covered by T07, T08 and the `SEC` tests.

## 4. Provider credentials

### Encryption at rest
- Secrets are sent once (`POST/PUT …/providers`, `PUT …/providers/{id}/secrets`) and stored as an
  **AES-256-GCM envelope**: `v1:<keyId>:<nonce>:<ciphertext+tag>`.
- **Associated data** = `provider-config:<configurationId>` (webhooks: `webhook:<webhookId>`). Copying an envelope into
  another row — e.g. another tenant's — fails authentication on decrypt.
- Master keys come from configuration (`SecretProtection:Keys`, `ActiveKeyId`): environment variables, Docker secrets or
  Azure Key Vault in production. Never in the database, never in source control (Development uses a dev-only key).

### Never returned, never logged
- API responses expose only secret **names**, `SecretVersion` and `SecretsUpdatedAt`.
- `ProviderConfigurationContext.ToString()` prints `Secrets = ***`; the audit trail writes `***` for any property containing "Secret".
- Decryption happens only inside the dispatcher, immediately before the provider call, in memory.

### Provisioning and rotation
| Operation | How | Effect |
|---|---|---|
| Rotate a tenant credential | `PUT /api/tenants/{t}/providers/{id}/secrets` | New envelope, `SecretVersion + 1`, the next delivery uses it (T18). No restart. |
| Rotate a webhook signing secret | `POST /api/tenants/{t}/webhooks/{id}/rotate-secret` with a grace period | Old secret still verifies during the grace window (no lost receipts during the vendor switch) |
| Rotate a master key | Add `Keys:k2`, set `ActiveKeyId = k2`, deploy, call `POST /api/providers/re-encrypt-secrets` | All envelopes re-encrypted with k2 (values and versions unchanged), then k1 can be removed |
| Revoke a configuration | `DELETE /api/tenants/{t}/providers/{id}` | Deactivated (kept for history), skipped by the resolver |

## 5. Webhooks (provider callbacks)

| Threat | Control |
|---|---|
| Forged callback | Signature verified with the webhook's own secret: SendGrid ECDSA P-256 (public key), Twilio HMAC-SHA1 over URL + sorted params, Meta HMAC-SHA256 (`X-Hub-Signature-256`), reference/fake HMAC-SHA256 over `timestamp.body`. Constant-time comparison. |
| Replay of an old callback | Timestamps checked where the provider signs them (SendGrid, reference handler: ±5 min, timestamp inside the signature) + **event de-duplication** `ProcessedWebhookEvents(ProviderKey, EventId)` for all providers |
| Callback for another tenant's message | URL contains an unguessable per-tenant token → resolves tenant + provider configuration **before** parsing; a delivery is updated only if its `LastProviderConfigurationId` equals that configuration (spec: callback ownership) |
| Out-of-order receipts | Domain transitions never move a terminal state backwards |
| Large payloads | Request size limit 1 MB |

## 6. SSRF protection for provider URLs

- At save time every setting whose name ends in `url` or `host` is validated: absolute https URL, no credentials, DNS resolved,
  rejected if any address is loopback, RFC 1918, link-local (incl. `169.254.169.254` metadata), CGNAT, multicast, IPv6 ULA/link-local.
- At send time provider `HttpClient`s use a `SocketsHttpHandler.ConnectCallback` that re-resolves DNS and refuses private
  addresses (closes DNS rebinding), and **automatic redirects are disabled**.
- `OutboundSecurity:AllowPrivateNetworks = true` only in Development (smtp4dev on localhost).

## 7. Abuse and resource protection

- Per-tenant rate limit on the send API (fixed window, `Tenant.RequestsPerMinute`) → 429 + `Retry-After`.
- Daily/monthly quotas per channel, consumed atomically in SQL (T15).
- Request validation: max 1000 recipients, length limits, JSON object data only.
- Templates are placeholder-only (no code execution); e-mail values are HTML-encoded.

## 8. Audit

`AuditLogs` row for every insert/update/delete of configuration entities (tenants, provider configurations, chains,
templates, rules, quotas, webhooks, preferences) with tenant, user id (from the token, including admins acting for a tenant),
entity, action, changed values (secrets redacted) and timestamp. Written in the same `SaveChanges` as the change, so a change
without an audit row is impossible.

## 9. Personal data

Addresses (e-mail/phone) are masked in API responses (`a****@x.com`, `*******4567`). Logs use ids, not addresses.
