# Testing Report

> **Status:** all suites green — 84 tests, 0 failed (run of 2026-11-22 on Windows, .NET 8 SDK, runtime 8.0.34, SQL Server LocalDB).
> Integration results come from `test-results.txt` (`dotnet test --logger "console;verbosity=normal"`).

## 1. How to run

```powershell
# all tests (integration tests use a throw-away LocalDB database by default; no Docker needed on Windows)
dotnet test --logger "console;verbosity=normal"

# only the 18 mandatory scenarios
dotnet test NotificationGateway.IntegrationTests --filter "FullyQualifiedName~SpecScenarioTests"

# use Docker SQL Server instead of LocalDB
$env:NG_TEST_SQL="docker"; dotnet test
```

## 2. Test suites

| Project | Kind | What it proves |
|---|---|---|
| `NotificationGateway.UnitTests` | Unit | Delivery state machine, preferences/quiet hours, template rendering (missing placeholders, HTML encoding, Arabic, no expression execution), routing rule matching, backoff with jitter, webhook signatures (reference HMAC + timestamp, Twilio, SendGrid ECDSA), SSRF IP rules and endpoint validation, SMS GSM-7/UCS-2 segments |
| `NotificationGateway.IntegrationTests` | Integration (real API host + real SQL Server) | T01–T18 + security boundaries |
| `NotificationGateway.ArchitectureTests` | Architecture (NetArchTest) | Clean Architecture dependency rules, provider SDKs only in adapters |

Integration tests run the real HTTP pipeline (JWT, tenant middleware, controllers, EF Core, SQL Server). Background
workers are disabled and the dispatcher is driven explicitly, so results are deterministic. External vendors are replaced by
the `fake-*` adapters or a stubbed HTTP endpoint (SendGrid in T04 — the real adapter code runs).

## 3. Mandatory scenarios (spec §8)

| ID | Scenario | Test | Key assertions | Result |
|---|---|---|---|---|
| T01 | Send In-App notification | `SpecScenarioTests.T01_InApp` | Status Delivered; recipient's inbox contains rendered text; unread count 1; another user's inbox empty || ✅ Passed (2 s) |
| T02 | Send SignalR notification | `T02_SignalR` | Connected target receives payload; same-tenant other user and other-tenant same user id receive nothing; status Sent (not Delivered) || ✅ Passed (2 s) |
| T03 | Email via provider A | `T03_EmailProviderA` | Attempt uses provider A; adapter A invoked with A's configuration || ✅ Passed (518 ms) |
| T04 | Switch Email to provider B | `T04_SwitchEmailProvider` | Only API calls; SendGrid adapter called with the tenant's decrypted key; provider message id recorded || ✅ Passed (329 ms) |
| T05 | Primary provider fails | `T05_Fallback` | Attempt 1 primary TransientFailure, attempt 2 fallback Accepted; Sent || ✅ Passed (539 ms) |
| T06 | All providers fail | `T06_RetryThenDeadLetter` | Retrying with future NextAttemptAt, 3 attempts, DeadLettered, visible in dead letters, requeue works || ✅ Passed (4 s) |
| T07 | Tenant A requests B history | `T07_HistoryIsolation` | 403 on B's route (code and GUID) and reports; 404 on B's notification id; B's id absent from A's list || ✅ Passed (373 ms) |
| T08 | Tenant A uses B provider id | `T08_ForeignProviderId` | Chain, get, update, delete, webhook registration with B's id → 404 || ✅ Passed (169 ms) |
| T09 | Tenant-specific templates | `T09_TenantTemplate` | Tenant A's own template used for A; tenant B falls back to shared || ✅ Passed (791 ms) |
| T10 | User disables a channel | `T10_UserDisablesChannel` | SMS Suppressed, zero attempts, provider never called; InApp Delivered || ✅ Passed (556 ms) |
| T11 | Duplicate request | `T11_Idempotency` | Replay returns same id + `Idempotent-Replayed`; different body → 409; 6 concurrent duplicates → one row; one inbox item || ✅ Passed (545 ms) |
| T12 | Restart with pending messages | `T12_RestartResumes` | New host on same DB delivers queued item; in-flight item after crash → Uncertain, not re-sent || ✅ Passed (8 s) |
| T13 | Provider timeout | `T13_Timeout`, `T13b_TransientIsRetried` | Call cut at ~1 s, `timeout` attempt, Uncertain, no failover/duplicate; transient failure → Retrying with backoff → Sent || ✅ Passed (1 s + 1 s (T13b)) |
| T14 | Invalid webhook signature | `T14_InvalidWebhookSignature` | Wrong signature / wrong secret / stale timestamp → 401, nothing applied; valid → Delivered; replay → duplicate || ✅ Passed (875 ms) |
| T15 | Quota exceeded | `T15_Quota` | Reject policy → 429 and nothing consumed; Defer policy → 202 with warning, delivery scheduled next period || ✅ Passed (552 ms) |
| T16 | Concurrent dispatchers | `T16_ConcurrentDispatchers` | 4 dispatchers lease disjoint sets; non-owner → Skipped; each user exactly one inbox item || ✅ Passed (1 s) |
| T17 | Arabic and English templates | `T17_Localization` | Language from request, user preference, tenant default; Arabic email preview subject + `dir="rtl"` || ✅ Passed (790 ms) |
| T18 | Provider secret rotation | `T18_SecretRotation` | Adapter receives old then new secret; responses never contain values; version 2; ciphertext only in DB || ✅ Passed (384 ms) |

## 4. Additional security tests (`TenantIsolationAndSettingsTests`)

| Test | Assertion | Result |
|---|---|---|
| No token | 401 || ✅ Passed (6 ms) |
| Token without tenant / unknown tenant | 403 || ✅ Passed (3 ms / 4 ms) |
| Tenant user on admin/send endpoints | 403 || ✅ Passed (20 ms) |
| Body `tenantId` of another tenant | 403; own tenant (spec `recipient` shape) → 202 || ✅ Passed (129 ms) |
| SystemAdmin acting via route vs other tenant admin | 200 vs 403 || ✅ Passed (71 ms) |
| Secrets write-only | Responses contain secret names only || ✅ Passed (68 ms) |
| Adapter configuration validation | Missing Twilio secrets → 400 listing them || ✅ Passed (43 ms) |
| Platform-only operations | Shared config / quotas by tenant admin → 403 || ✅ Passed (26 ms) |
| Audit | Audit rows exist, no secret values || ✅ Passed (63 ms) |
| Webhook ownership | Valid callback of tenant B cannot change tenant A's delivery || ✅ Passed (754 ms) |
| Unknown webhook token | 404 || ✅ Passed (14 ms) |

## 5. Run results

| Suite | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|
| Unit (`NotificationGateway.UnitTests`) | 49 | 0 | 0 | ~1 s |
| Integration (`NotificationGateway.IntegrationTests`) | 31 (19 spec scenarios incl. T13b + 12 security) | 0 | 0 | 41.9 s |
| Architecture (`NotificationGateway.ArchitectureTests`) | 4 | 0 | 0 | 0.9 s |
| **Total** | **84** | **0** | **0** | |

Environment: Windows 11 · Visual Studio 2026 · .NET 8 SDK (test host runtime 8.0.34) · xUnit 2.8.2 · SQL Server LocalDB
(throw-away database per run) · background workers and RabbitMQ disabled in tests (dispatcher driven explicitly).

Notes from the run:
- The `QuotaExceededException` stack trace in the log belongs to T15 and is expected (the Reject policy returns 429).
- Build warnings in that run: 2 nullable warnings (CS8604) in `SmtpProvider`; fixed afterwards (host is a required, validated setting; empty password when only a username is configured).
- During development T16 first asserted that one concurrent leasing round must take all due rows. `READPAST` correctly makes
  concurrent dispatchers skip rows another one holds, so the test was corrected to assert *no double lease* plus *no lost row*
  across rounds — which is the actual requirement.

## 6. Practical demonstration

`Multi_Tenant_Notification_Gateway/demo.http` (VS / Rider / VS Code REST Client) walks through: tokens for both tenants,
sending the spec example, tracking (fallback visible on tenant-001 SMS), inbox, isolation (403/404), template preview in
Arabic, provider switch, secret rotation, reports.

## 7. Known limitations

- Exactly-once delivery to a handset is not claimed (see *Notification_Routing_and_Fallback.md* §6); ambiguous cases are
  surfaced as Uncertain.
- Per-instance rate limiting (N instances ⇒ N × limit); quotas are global (SQL).
- Uncertain reconciliation via provider status APIs is an extension point (`StatusQuery` capability), not automated yet.
