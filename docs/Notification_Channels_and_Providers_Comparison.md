# Notification Channels and Providers — Comparison and Selection

The spec lists candidate products per channel and asks for "a reasonable set" behind one architecture.
This document records what was investigated, what was implemented, and why.

## 1. Channels

| Channel | Typical use in the Attendance System | Confirmation possible? | Cost | Notes |
|---|---|---|---|---|
| In-App | Inbox of every event (record of truth for the user) | Yes — the row is the delivery | None | Always kept even when the user prefers other channels |
| Real-time (SignalR) | Live popup for managers on the dashboard | No — fire-and-forget | None | Final status is Sent, never Delivered |
| Email | Daily/late reports, external parties | With receipts (SendGrid events) | Low | Bounces come back asynchronously |
| SMS | Critical alerts, absence of a field employee | With receipts (Twilio callbacks) | Per segment; Arabic uses UCS-2 (70 chars) | Sender id rules (NTRA in Egypt) |
| WhatsApp | External notifications to employees' phones | With receipts (delivered/read) | Per conversation | Business-initiated messages need pre-approved templates |
| Push | Mobile app users | Provider dependent | Low | Needs a device token |
| Voice | Escalation when SMS is not acknowledged | Call status | Highest | Text-to-speech, Arabic supported |

## 2. Providers — implemented

| Key | Product | Channel | Capabilities declared | Receipts webhook | Why chosen |
|---|---|---|---|---|---|
| `inapp` | Internal DB | In-App | SynchronousDelivery, IdempotencyKey | — | Required |
| `signalr` | ASP.NET Core SignalR | Real-time | IdempotencyKey | — | Required; already used by the Attendance System |
| `smtp` | Any SMTP server (MailKit) | Email | SenderIdentity | — | Works on-premises (government networks), no vendor lock-in |
| `sendgrid` | SendGrid v3 API | Email | DeliveryReceipts, SenderIdentity | Signed Event Webhook (ECDSA) | Market standard API + signed events; proves the "switch provider" scenario (T04) |
| `twilio-sms` | Twilio Programmable Messaging | SMS | DeliveryReceipts, StatusQuery, SenderIdentity | Status callback (HMAC-SHA1) | Global coverage, reliable receipts |
| `local-sms` | Local Egyptian SMS gateway (HTTP) | SMS | SenderIdentity | — | Ported from the Attendance System; local routes, GSM-7/UCS-2 aware |
| `meta-whatsapp` | WhatsApp Business Cloud API | WhatsApp | DeliveryReceipts | `X-Hub-Signature-256` + hub.challenge | Official API; ported from the Attendance System |
| `twilio-whatsapp` | Twilio WhatsApp | WhatsApp | DeliveryReceipts, StatusQuery, SenderIdentity | Twilio callback | Same account/API as Twilio SMS; ported |
| `onesignal` | OneSignal REST API | Push | IdempotencyKey | — | Simple REST + server-side idempotency key |
| `twilio-voice` | Twilio Programmable Voice | Voice | SenderIdentity, StatusQuery | — | Text-to-speech in ar-EG / en-US with one API call |
| `fake-*` | Deterministic fake | Email/SMS/WhatsApp/Push/Voice | DeliveryReceipts, SenderIdentity | HMAC-SHA256 + timestamp | Tests and demos: success, transient, provider-failure, reject, uncertain, hang, failFirst |

## 3. Investigated, not implemented (and how to add them)

| Product | Reason not implemented now | Adding it |
|---|---|---|
| Amazon SES | SendGrid + SMTP already cover Email; SES needs AWS SigV4 signing | One adapter (`AmazonSesProvider`) + SNS receipt handler |
| Vonage SMS | Two SMS products already prove multi-provider + fallback | One adapter, same JSON/HTTP helper as `local-sms` |
| Firebase Cloud Messaging | Requires service-account OAuth2 token exchange; OneSignal covers Push | One adapter + token cache |

None of these require changes in the orchestrator, router, dispatcher or database.

## 4. Capability differences the engine respects

| Capability | Effect in the engine |
|---|---|
| `SynchronousDelivery` | Accepted → **Delivered** immediately (only InApp) |
| `DeliveryReceipts` | Accepted → **Sent**; a signed callback later moves it to Delivered/Failed |
| `IdempotencyKey` | After an *uncertain* result the same provider may be retried (it de-duplicates); otherwise the delivery stays **Uncertain** |
| `StatusQuery` | Allows reconciliation of Uncertain deliveries (documented extension point) |
| `SenderIdentity` | The configuration's `senderIdentity` (From address / sender id) is used |

## 5. Error classification (normalized by every adapter)

| Outcome | Examples | Engine reaction |
|---|---|---|
| Accepted | HTTP 2xx with message id | Sent (or Delivered if synchronous) |
| TransientFailure | 429, 5xx, connection refused before sending | Next provider now; retry later with backoff |
| ProviderFailure | 401/403, invalid credentials, missing sender | Next provider now (configuration problem of this provider) |
| RecipientRejected | Invalid number/address, unsubscribed, Twilio 21211, Meta 131026 | **Failed**, no fallback (other providers would reject too) |
| Uncertain | Timeout or connection lost after the request was sent | **Uncertain**, no fallback (duplicate risk) |

## 6. Cost estimation

`NotificationProviders.EstimatedCostPerMessage` / `CostCurrency` are set by the platform admin
(`PUT /api/providers/{key}`) from the contracted price list. `GET /api/tenants/{tenantId}/reports/cost` multiplies accepted
messages per provider by that price. Prices are not hard-coded because they differ per contract, country and SMS segment count.

## 7. Template conventions per channel

- **Email:** subject + HTML body; caller values are HTML-encoded.
- **SMS / Voice / Push:** plain text; Arabic automatically switches SMS encoding to UCS-2 (segments: 70/67 instead of 160/153).
- **WhatsApp (template mode):** the gateway template *subject* is the approved Meta/Twilio template name; each line of the
  body is one positional parameter (`{{1}}`, `{{2}}` …). Text mode sends the body as free text (24-hour window only).
