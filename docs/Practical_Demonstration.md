# Practical Demonstration — Multi-Tenant Notification Gateway

The task asks for "a practical demonstration" and the reviewer is told to check that the gateway *works correctly across tenants, providers and failure scenarios*. This demo does exactly that against the running API, in about 6–8 minutes, and prints a PASS/FAIL line for every claim.

## What is in the box

| File | Purpose |
|---|---|
| `demo/Run-Demo.ps1` | Automated demo: 14 scenes, 56 checks, CSV result file at the end |
| `Multi_Tenant_Notification_Gateway/wwwroot/demo/live.html` | Live SignalR view (bell + unread count), served by the API in Development |
| `Multi_Tenant_Notification_Gateway/demo.http` | The same flow as single requests, for VS / Rider |
| `docs/Notification_Gateway_Manual_Test_Guide.docx` | Full manual walkthrough of every endpoint (89 tests) |

## Before the review (once)

1. SQL Server LocalDB running (the API migrates and seeds itself in Development).
2. PowerShell 7 (`pwsh`). The script uses `-SkipHttpErrorCheck`, which Windows PowerShell 5.1 does not have.
3. Start the API:
   ```
   dotnet run --project Multi_Tenant_Notification_Gateway --launch-profile http
   ```
4. Open `http://localhost:5025/demo/live.html`, choose tenant-001, user `manager-1`, press **اتصال**.
5. In a second terminal, from the solution folder:
   ```
   pwsh ./demo/Run-Demo.ps1 -Pause
   ```
   `-Pause` waits for Enter between scenes so you can talk. `-SkipRestart` skips scene 13. `-WithRateLimit` adds the rate-limit check (+35 s).

The script never changes the seeded tenant-001 / tenant-002 configuration: everything that edits settings runs on a new tenant `demo-<timestamp>` that the script onboards itself. It can be run many times.

## Scene map (what to say)

| # | Scene | What the audience sees | Tests | Criterion (weight) |
|---|---|---|---|---|
| 1 | Provider catalog | 7 channels, adapters + capability flags; SendGrid without `apiKey` → 400 | — | Architecture (15), Providers (15) |
| 2 | Central API | One `POST /send` → InApp **Delivered**, SignalR **Sent** (pops up in live.html), SMS primary fails → backup **Sent**; Arabic inbox | T01 T02 T05 T17 | Routing & fallback (15) |
| 3 | Second tenant | Same template, tenant-002 uses *its* e-mail provider; English inbox; tenant-002's own Arabic SMS template wins | T03 T09 T17 | Multi-tenancy (20) |
| 4 | Idempotency | Same key → same id + `Idempotent-Replayed: true`; same key, other body → 409 | T11 | Reliability (15) |
| 5 | Onboarding | SystemAdmin creates a tenant; channels, providers, secrets set by API only | — | "Final expectation" |
| 6 | Routing + switch | Critical rule overrides the caller's channels; announcements → InApp; e-mail vendor A → B by one PUT | T04 | Routing (15) |
| 7 | Failures | Retry with growing gaps → DeadLettered → requeue → Sent; timeout → **Uncertain, no failover**; rejected number → Failed, no failover; cancel a retrying delivery | T06 T13 | Reliability (15) |
| 8 | Preferences | User disables SMS → SMS Suppressed, InApp delivered | T10 | — |
| 9 | Quota | Tenant admin cannot raise its quota (403); 2nd message → 429; other tenant unaffected | T15 | Security (10) |
| 10 | Isolation | Cross-tenant read → 404/403, body tenantId → 403, foreign provider id → 404, user/no token/unknown tenant → 403/401 | T07 T08 | Multi-tenancy (20) |
| 11 | Secrets | Secret never returned; rotation → version 2; next send works with the new key | T18 | Security (10) |
| 12 | Webhooks | **Sent ≠ Delivered**; forged signature / old timestamp → 401; signed receipt → Delivered; replay → duplicate | T14 | Security (10) |
| 13 | Restart | Delivery is Retrying → you stop/start the API → still there → vendor recovers → Sent | T12 | Reliability (15) |
| 14 | Reports | Summary, provider performance (failing primary visible), cost (0.25 EGP/SMS), platform usage (SystemAdmin only) | — | Monitoring |

T16 (concurrent dispatchers) needs many parallel workers, so it is shown by the automated suite (`dotnet test`, see `Testing_Report.md`) rather than live.

## Points worth saying out loud

- **Scene 2:** "The caller never named a provider. The SMS primary is configured to fail; the engine tried the backup because the failure was *before* the provider accepted the message."
- **Scene 7c:** "This is the opposite decision. The request left us and we got no answer, so the SMS may already be on the phone. Failing over could send it twice, so we stop at Uncertain and wait for a receipt or an operator."
- **Scene 10:** "404, not 403, for an id: tenant-002 cannot even learn that the notification exists. The query filter removes other tenants' rows before the WHERE on id."
- **Scene 12:** "Sent only means the provider accepted it. Delivered appears only after a signed receipt from the provider, and the same receipt twice changes nothing."
- **Scene 13:** "Nothing is held in memory. The delivery, its attempts and its next retry time are rows in SQL, so a restart, or a second instance, simply continues."

## If something goes wrong during the review

| Symptom | Cause / fix |
|---|---|
| "The API is not reachable" | Start the API with the `http` profile, or pass `-Api https://localhost:7264` |
| Scene 0 says seeded tenants missing | `Database:SeedDemoData` must be true in Development (it is by default) |
| live.html shows nothing | Reconnect on the page; it must be the same tenant and user as the send (tenant-001 / manager-1) |
| A single FAIL | The CSV next to the script lists the check and the HTTP status; every scene can be repeated in Swagger with the manual guide |
