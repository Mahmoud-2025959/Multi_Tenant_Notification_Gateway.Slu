<#
.SYNOPSIS
    Practical demonstration of the Multi-Tenant Notification Gateway (Task 8).
    Runs on Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Drives the RUNNING API through every area the task is evaluated on and prints a PASS/FAIL line per check:
      1  Provider abstraction & catalog              8  Preferences (T10)
      2  Central API + fallback (T01, T05, T17)      9  Quota (T15) and optional rate limit
      3  Second tenant, own providers (T03, T09)    10  Tenant isolation & authorization (T07, T08)
      4  Idempotency (T11)                          11  Secrets & rotation (T18)
      5  Onboarding a tenant by configuration       12  Webhooks: Sent vs Delivered, signatures, replay (T14)
      6  Routing rules + provider switch (T04)      13  Restart with pending work (T12)
      7  Failures: dead letter, requeue, timeout,   14  Reports & cost
         reject, cancel (T06, T13)

    Safe to run many times: scenes that change configuration run on a NEW tenant (demo-<timestamp>) created by the
    script, so the seeded tenant-001 / tenant-002 demo data stays as it is.

.PARAMETER Api
    Base URL of the API (Development profile). Default: http://localhost:5025

.PARAMETER Pause
    Wait for Enter between scenes (use it when presenting live).

.PARAMETER SkipRestart
    Skip scene 13 (it asks you to stop and start the API).

.PARAMETER WithRateLimit
    Also run the per-tenant rate-limit check (adds ~35 s: the tenant policy is cached for 30 s).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\demo\Run-Demo.ps1 -Pause
#>
param(
    [string]$Api = "http://localhost:5025",
    [switch]$Pause,
    [switch]$SkipRestart,
    [switch]$WithRateLimit
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
Add-Type -AssemblyName System.Net.Http
$Http = [System.Net.Http.HttpClient]::new()
$Http.Timeout = [TimeSpan]::FromSeconds(60)
$Api = $Api.TrimEnd('/')
$T1Id = "11111111-1111-1111-1111-111111111111"
$T2Id = "22222222-2222-2222-2222-222222222222"
$Run = Get-Date -Format "MMddHHmmss"
$Results = [System.Collections.Generic.List[object]]::new()
$Names = @{}          # provider configuration id -> display name (for readable attempt chains)
$Final = @('Sent', 'Delivered', 'Failed', 'DeadLettered', 'Uncertain', 'Suppressed', 'Cancelled')

# ------------------------------------------------------------------ output helpers
function Scene([string]$No, [string]$Title, [string]$Covers) {
    Write-Host ""
    Write-Host ("=" * 86) -ForegroundColor DarkCyan
    Write-Host " Scene $No | $Title" -ForegroundColor Cyan
    if ($Covers) { Write-Host " Covers : $Covers" -ForegroundColor DarkGray }
    Write-Host ("=" * 86) -ForegroundColor DarkCyan
}
function Step([string]$Text) { Write-Host "  > $Text" -ForegroundColor Gray }
function Info([string]$Text) { Write-Host "      $Text" -ForegroundColor DarkGray }
function Check([string]$Id, [string]$Name, [bool]$Ok, [string]$Detail = "") {
    $Results.Add([pscustomobject]@{ Id = $Id; Check = $Name; Result = $(if ($Ok) { "PASS" } else { "FAIL" }); Detail = $Detail })
    if ($Ok) { Write-Host "  [PASS] $Id  $Name" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $Id  $Name" -ForegroundColor Red }
    if ($Detail) { Info $Detail }
}
function Next { if ($Pause) { Read-Host "  [Enter] next scene" | Out-Null } }

# ------------------------------------------------------------------ HTTP helpers
function Call {
    param([string]$Method, [string]$Path, [string]$Token, $Body, [hashtable]$Headers = @{}, [switch]$RawBody)
    $msg = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$Api$Path")
    if ($Token) { $null = $msg.Headers.TryAddWithoutValidation("Authorization", "Bearer $Token") }
    foreach ($k in $Headers.Keys) { $null = $msg.Headers.TryAddWithoutValidation($k, [string]$Headers[$k]) }
    if ($null -ne $Body) {
        $text = $(if ($RawBody -or $Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 12 -Compress })
        $msg.Content = [System.Net.Http.StringContent]::new($text, [Text.Encoding]::UTF8, "application/json")
    }
    $resp = $Http.SendAsync($msg).GetAwaiter().GetResult()
    $raw = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $hdr = @{}
    foreach ($h in $resp.Headers) { $hdr[$h.Key] = ($h.Value -join ",") }
    $json = $null
    if ($raw) { try { $json = ConvertFrom-Json $raw } catch { } }
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $json; Raw = $raw; Headers = $hdr }
}
function Test-Health {
    try { return (Call GET "/health").Status -eq 200 } catch { return $false }
}

function New-Token([string]$UserId, [string]$TenantId, [string[]]$Roles) {
    $b = @{ userId = $UserId; roles = $Roles; lifetimeMinutes = 240 }
    if ($TenantId) { $b.tenantId = $TenantId }
    $r = Call POST "/api/dev/token" -Body $b
    if ($r.Status -ne 200) { throw "POST /api/dev/token failed ($($r.Status)). Is the API running in Development? $($r.Raw)" }
    $r.Body.accessToken
}

function Send([string]$Token, [hashtable]$Request, [string]$Key) {
    $h = @{}
    if ($Key) { $h["Idempotency-Key"] = $Key }
    Call POST "/api/notifications/send" $Token $Request -Headers $h
}

function Get-Notification([string]$Id, [string]$Token) { (Call GET "/api/notifications/$Id" $Token).Body }

# Polls until $Done returns true (or the timeout passes) and returns the last notification read.
function Wait-For([string]$Id, [string]$Token, [scriptblock]$Done, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $n = Get-Notification $Id $Token
        if ($n -and (& $Done $n)) { return $n }
        Start-Sleep -Milliseconds 800
    } while ((Get-Date) -lt $deadline)
    return $n
}
function All-Final($n) { @($n.deliveries | Where-Object { $_.status -notin $Final }).Count -eq 0 }
function Delivery($n, [string]$Channel) { @($n.deliveries | Where-Object { $_.channel -eq $Channel })[0] }

function Chain($d) {
    (@($d.attempts) | Sort-Object attemptNumber, startedAt | ForEach-Object {
        $name = $Names[[string]$_.providerConfigurationId]; if (-not $name) { $name = $_.providerKey }
        "$name=$($_.outcome)"
    }) -join "  ->  "
}
function Show($n) {
    $n.deliveries | ForEach-Object {
        [pscustomobject]@{
            Channel  = $_.channel
            Status   = $_.status
            Attempts = "$($_.attemptCount)/$($_.maxAttempts)"
            Chain    = Chain $_
            Error    = $(if ($_.lastError -and $_.lastError.Length -gt 60) { $_.lastError.Substring(0, 60) + "..." } else { $_.lastError })
        }
    } | Format-Table -AutoSize -Wrap | Out-String -Width 200 | ForEach-Object { Write-Host $_.TrimEnd() -ForegroundColor White }
}

function Remember-Providers([string]$Token, [string]$Tenant) {
    $r = Call GET "/api/tenants/$Tenant/providers" $Token
    foreach ($p in @($r.Body)) { $Names[[string]$p.id] = $p.name }
    @($r.Body)
}
function New-Provider([string]$Token, [string]$Tenant, [string]$Key, [string]$Name, [hashtable]$Settings, [hashtable]$Secrets = @{}, [string]$Sender) {
    $b = @{ providerKey = $Key; name = $Name; settings = $Settings; secrets = $Secrets }
    if ($Sender) { $b.senderIdentity = $Sender }
    $r = Call POST "/api/tenants/$Tenant/providers" $Token $b
    if ($r.Status -ne 201 -and $r.Status -ne 200) { throw "Create provider '$Name' failed ($($r.Status)): $($r.Raw)" }
    $Names[[string]$r.Body.id] = $Name
    $r.Body
}
function Set-Chain([string]$Token, [string]$Tenant, [string]$Channel, [string[]]$Ids) {
    $r = Call PUT "/api/tenants/$Tenant/channels/$Channel/providers" $Token @{ providerConfigurationIds = $Ids }
    if ($r.Status -ne 200) { throw "Set $Channel chain failed ($($r.Status)): $($r.Raw)" }
}
function Set-Channel([string]$Token, [string]$Tenant, [string]$Channel, [int]$MaxAttempts, [int]$Base, [int]$Max, [int]$Timeout) {
    $r = Call PUT "/api/tenants/$Tenant/channels/$Channel" $Token @{
        isEnabled = $true; maxAttempts = $MaxAttempts; baseRetryDelaySeconds = $Base; maxRetryDelaySeconds = $Max; providerTimeoutSeconds = $Timeout }
    if ($r.Status -ne 200) { throw "Update channel $Channel failed ($($r.Status)): $($r.Raw)" }
}
function Late([string]$UserId, [string[]]$Channels, [string]$Name = "Ahmed", [string]$Priority = "High", [string]$Type = "attendance.late") {
    @{
        recipient        = @{ userId = $UserId; phone = "+201000000000"; email = "$UserId@agency.gov.eg" }
        notificationType = $Type
        channels         = $Channels
        templateCode     = "ATTENDANCE_LATE"
        data             = @{ EmployeeName = $Name; CheckInTime = "08:30"; Date = (Get-Date -Format "yyyy-MM-dd") }
        priority         = $Priority
    }
}

# ====================================================================== 0. preflight
Scene "0" "Preflight: API, seeded tenants, tokens" ""
if (-not (Test-Health)) {
    Write-Host "  The API is not reachable at $Api. Start it first:" -ForegroundColor Red
    Write-Host "      dotnet run --project Multi_Tenant_Notification_Gateway --launch-profile http" -ForegroundColor Yellow
    exit 1
}
Step "API healthy at $Api"

$SYS = New-Token "platform-admin" $null @("SystemAdmin")
$T1 = New-Token "attendance-service" $T1Id @("Service", "TenantAdmin")
$T2 = New-Token "branch-admin" $T2Id @("Service", "TenantAdmin")
$U1 = New-Token "manager-1" $T1Id @("TenantUser")
$U2 = New-Token "branch-user-1" $T2Id @("TenantUser")
Step "Tokens issued: SystemAdmin, tenant-001 admin, tenant-002 admin, manager-1, branch-user-1"

$tenants = (Call GET "/api/admin/tenants" $SYS).Body
$seeded = @($tenants | Where-Object code -in "tenant-001", "tenant-002").Count -eq 2
Check "SETUP" "Seeded tenants tenant-001 and tenant-002 exist" $seeded
if (-not $seeded) { Write-Host "  Run the API once in Development with Database:SeedDemoData = true." -ForegroundColor Red; exit 1 }
$null = Remember-Providers $T1 "tenant-001"; $null = Remember-Providers $T2 "tenant-002"
Write-Host "  Tip: open $Api/demo/live.html, connect as tenant-001 / manager-1 to watch SignalR pushes live." -ForegroundColor Yellow
Next

# ====================================================================== 1. catalog
Scene "1" "Provider abstraction: one interface, many adapters" "Architecture & provider abstraction, Provider integrations"
$catalog = @((Call GET "/api/providers" $T1).Body)
$catalog | Select-Object key, channel, displayName, capabilities | Format-Table -AutoSize | Out-String | Write-Host
$channels = $catalog.channel | Sort-Object -Unique
$missing = @(@("InApp", "SignalR", "Email", "Sms", "WhatsApp", "Push", "Voice") | Where-Object { $_ -notin $channels })
Check "ARCH-1" "Adapters registered for every channel (InApp, SignalR, Email, SMS, WhatsApp, Push, Voice)" ($missing.Count -eq 0) `
    "$($catalog.Count) adapters, discovered from DI. No switch statement selects them."
Step "Provider-specific config validation: SendGrid without its apiKey secret"
$bad = Call POST "/api/tenants/tenant-001/providers" $T1 @{ providerKey = "sendgrid"; name = "SendGrid (no key)"; settings = @{}; secrets = @{} }
Check "ARCH-2" "Invalid provider configuration is rejected (400)" ($bad.Status -eq 400) "HTTP $($bad.Status)"
Next

# ====================================================================== 2. central API
Scene "2" "Central API: one request, three channels, SMS primary fails -> backup" "T01, T02, T05, T17 (Arabic) | Routing & fallback"
$key = "demo-$Run-checkin"
$req = Late "manager-1" @("InApp", "SignalR", "Sms") "أحمد"
$req.tenantId = "tenant-001"
Step "POST /api/notifications/send  (tenant-001, Idempotency-Key: $key)"
$s = Send $T1 $req $key
$nid = $s.Body.notificationId
Check "API-1" "Accepted with a tracking id (202)" ($s.Status -eq 202 -and $nid) "notificationId = $nid; deliveries created: $(@($s.Body.deliveries).Count)"
$n = Wait-For $nid $T1 { param($x) All-Final $x } 30
Show $n
$inApp = Delivery $n "InApp"; $sr = Delivery $n "SignalR"; $sms = Delivery $n "Sms"
Check "T01" "In-App stored -> Delivered" ($inApp.status -eq "Delivered")
Check "T02" "SignalR pushed to the user's group -> Sent" ($sr.status -in "Sent", "Delivered") "Sent, not Delivered: a push to a hub group is not proof the user saw it."
$smsOutcomes = @($sms.attempts | Sort-Object attemptNumber, startedAt | ForEach-Object outcome)
Check "T05" "SMS primary failed and the backup provider accepted it" `
    ($sms.status -eq "Sent" -and $smsOutcomes.Count -ge 2 -and $smsOutcomes[-1] -eq "Accepted" -and $smsOutcomes[0] -in "TransientFailure", "Skipped") `
    (Chain $sms)
$inbox = (Call GET "/api/me/inbox?pageSize=5" $U1).Body.items
$item = @($inbox | Where-Object deliveryId -eq $inApp.deliveryId)[0]
Check "T17a" "manager-1 inbox shows the Arabic text (tenant-001 default language)" ($item -and $item.body -match "[؀-ۿ]") "$($item.title): $($item.body)"
Next

# ====================================================================== 3. second tenant
Scene "3" "Same engine, second tenant, its own providers and templates" "T03, T09, T17 (English) | Multi-tenancy"
$req2 = Late "branch-user-1" @("InApp", "Email") "Mona"
$s2 = Send $T2 $req2 "demo-$Run-branch"
$n2 = Wait-For $s2.Body.notificationId $T2 { param($x) All-Final $x } 30
Show $n2
$mail = Delivery $n2 "Email"
Check "T03" "tenant-002 e-mail goes through ITS provider ('Branch e-mail'), tenant-001 uses the shared SMTP" `
    ($mail.status -eq "Sent" -and (Chain $mail) -match "Branch e-mail") (Chain $mail)
$inbox2 = (Call GET "/api/me/inbox?pageSize=5" $U2).Body.items
$i2 = @($inbox2 | Where-Object deliveryId -eq (Delivery $n2 "InApp").deliveryId)[0]
Check "T17b" "branch-user-1 inbox is in English (tenant-002 default language)" ($i2 -and $i2.body -match "Mona" -and $i2.body -notmatch "[؀-ۿ]") $i2.body
$data = @{ EmployeeName = "منى"; CheckInTime = "08:45"; Date = "2026-11-26" }
$p1 = (Call POST "/api/tenants/tenant-001/templates/preview" $T1 @{ code = "ATTENDANCE_LATE"; channel = "Sms"; language = "Ar"; data = $data }).Body
$p2 = (Call POST "/api/tenants/tenant-002/templates/preview" $T2 @{ code = "ATTENDANCE_LATE"; channel = "Sms"; language = "Ar"; data = $data }).Body
Info "tenant-001 (shared template)  : $($p1.body)"
Info "tenant-002 (its own override) : $($p2.body)"
Check "T09" "Tenant-specific template wins over the shared one" ($p2.body -like "فرع*" -and $p1.body -notlike "فرع*")
Next

# ====================================================================== 4. idempotency
Scene "4" "Idempotency: the Attendance outbox may retry the same event" "T11 | Reliability"
$again = Send $T1 $req $key
Check "T11a" "Same key + same body -> same notification, nothing new created" `
    ($again.Status -eq 202 -and $again.Body.notificationId -eq $nid -and "$($again.Headers['Idempotent-Replayed'])" -eq "true") `
    "notificationId = $($again.Body.notificationId); Idempotent-Replayed: $($again.Headers['Idempotent-Replayed'])"
$after = Get-Notification $nid $T1
Check "T11b" "Still exactly 3 deliveries" (@($after.deliveries).Count -eq 3)
$changed = Late "manager-1" @("InApp", "SignalR", "Sms") "Someone else"; $changed.tenantId = "tenant-001"
$conflict = Send $T1 $changed $key
Check "T11c" "Same key + different body -> 409 (key reuse is a client bug, not a duplicate)" ($conflict.Status -eq 409) "HTTP $($conflict.Status)"
Next

# ====================================================================== 5. onboarding
Scene "5" "Onboard a new tenant with configuration only (no code, no deploy)" "Multi-tenancy | Final expectation of the task"
$code = "demo-$Run"
$t3 = Call POST "/api/admin/tenants" $SYS @{ code = $code; name = "Demo Branch $Run"; defaultLanguage = "En"; timeZoneId = "Africa/Cairo" }
Check "ONB-1" "SystemAdmin created tenant '$code'" ($t3.Status -in 200, 201) "id = $($t3.Body.id)"
$T3Id = $t3.Body.id
$T3 = New-Token "demo-admin" $T3Id @("Service", "TenantAdmin")
$U3 = New-Token "demo-user" $T3Id @("TenantUser")

Step "Enable InApp / Email / SMS with fast demo retry policies (3 attempts, 1-3 s backoff, 2 s provider timeout)"
foreach ($c in "InApp", "Email", "Sms") { Set-Channel $T3 $code $c 3 1 3 2 }
$shared = Remember-Providers $T3 $code
$sharedInApp = @($shared | Where-Object { $_.isShared -and $_.channel -eq "InApp" })[0]
Set-Chain $T3 $code "InApp" @($sharedInApp.id)
Step "Create this tenant's own provider configurations (secrets are encrypted on write)"
$mailA = New-Provider $T3 $code "fake-email" "Email vendor A" @{ mode = "success" } @{ apiKey = "a-$Run" } "a@demo.local"
$mailB = New-Provider $T3 $code "fake-email" "Email vendor B" @{ mode = "success" } @{ apiKey = "b-$Run" } "b@demo.local"
$mailDown = New-Provider $T3 $code "fake-email" "Email vendor (outage)" @{ mode = "transient" }
$smsOk = New-Provider $T3 $code "fake-sms" "SMS vendor OK" @{ mode = "success" } @{} "DEMO"
$smsDown = New-Provider $T3 $code "fake-sms" "SMS always down" @{ mode = "transient" }
$smsHang = New-Provider $T3 $code "fake-sms" "SMS hangs" @{ mode = "hang" }
$smsReject = New-Provider $T3 $code "fake-sms" "SMS rejects number" @{ mode = "reject" }
$sg = Call POST "/api/tenants/$code/providers" $T3 @{ providerKey = "sendgrid"; name = "SendGrid (real adapter)"; settings = @{}; secrets = @{ apiKey = "SG.demo-$Run" }; senderIdentity = "noreply@demo.local" }
Check "ONB-2" "A real SendGrid configuration validates and is stored (not routed: no live account in the demo)" ($sg.Status -in 200, 201) "secretNames = $($sg.Body.secretNames -join ','); secret value returned: $($sg.Raw -match 'SG\.demo')"
$settings = (Call GET "/api/tenants/$code/notification-settings" $T3).Body
Check "ONB-3" "Tenant is fully configured through the settings APIs" (@($settings.channels | Where-Object { $_.setting.isEnabled }).Count -ge 3)
Next

# ====================================================================== 6. routing + switch
Scene "6" "Routing rules per tenant, and switching providers without code" "T04 | Routing & fallback"
$null = Call POST "/api/tenants/$code/routing-rules" $T3 @{ name = "Critical -> InApp + Email + SMS"; notificationType = $null; minPriority = "Critical"; channels = @("InApp", "Email", "Sms"); order = 10; allowCallerOverride = $false }
$null = Call POST "/api/tenants/$code/routing-rules" $T3 @{ name = "Announcements -> InApp"; notificationType = "general.announcement"; minPriority = $null; channels = @("InApp"); order = 20; allowCallerOverride = $false }
Set-Chain $T3 $code "Email" @($mailA.id)
Set-Chain $T3 $code "Sms" @($smsOk.id)
$crit = Send $T3 (Late "demo-user" @("InApp") "Critical case" "Critical")
$critChannels = @($crit.Body.deliveries.channel) | Sort-Object
Check "ROUTE-1" "Critical priority -> rule forces InApp + Email + SMS (caller asked for InApp only)" (($critChannels -join ",") -eq "Email,InApp,Sms") ($critChannels -join ", ")
$ann = Send $T3 (Late "demo-user" @("Email", "Sms") "All staff" "Normal" "general.announcement")
Check "ROUTE-2" "general.announcement -> InApp only" ((@($ann.Body.deliveries.channel) -join ",") -eq "InApp") (@($ann.Body.deliveries.channel) -join ", ")

$a = Wait-For $crit.Body.notificationId $T3 { param($x) All-Final $x } 20
Info ("Email with vendor A: " + (Chain (Delivery $a "Email")))
Step "PUT /api/tenants/$code/channels/Email/providers -> [Email vendor B]   (a configuration change, nothing else)"
Set-Chain $T3 $code "Email" @($mailB.id)
$b = Send $T3 (Late "demo-user" @("Email"))
$bn = Wait-For $b.Body.notificationId $T3 { param($x) All-Final $x } 20
Info ("Email with vendor B: " + (Chain (Delivery $bn "Email")))
Check "T04" "Same request now goes through vendor B" ((Chain (Delivery $a "Email")) -match "vendor A" -and (Chain (Delivery $bn "Email")) -match "vendor B")
Next

# ====================================================================== 7. failures
Scene "7" "Failure handling: retry, dead letter, requeue, timeout, reject, cancel" "T06, T13 | Reliability & background processing"
Step "7a  The only SMS provider is down -> retry with exponential backoff -> dead letter"
Set-Chain $T3 $code "Sms" @($smsDown.id)
$f = Send $T3 (Late "demo-user" @("Sms"))
$fn = Wait-For $f.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -in $Final } 60
$fd = Delivery $fn "Sms"
$times = @($fd.attempts | Sort-Object attemptNumber | ForEach-Object { [datetimeoffset]$_.startedAt })
$gaps = for ($i = 1; $i -lt $times.Count; $i++) { "{0:N1}s" -f ($times[$i] - $times[$i - 1]).TotalSeconds }
Show $fn
Check "T06" "Retried up to MaxAttempts (3), then DeadLettered" ($fd.status -eq "DeadLettered" -and $fd.attemptCount -eq 3) "gaps between attempts: $($gaps -join ', ')"
$dl = (Call GET "/api/tenants/$code/dead-letters" $T3).Body.items
Check "DLQ-1" "Visible in the dead-letter list" (@($dl | Where-Object deliveryId -eq $fd.deliveryId).Count -eq 1)

Step "7b  Vendor recovered -> operator requeues the dead letter"
Set-Chain $T3 $code "Sms" @($smsOk.id)
$rq = Call POST "/api/tenants/$code/dead-letters/$($fd.deliveryId)/requeue" $T3
$rn = Wait-For $f.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -in $Final } 20
Check "DLQ-2" "Requeued (202) and sent" ($rq.Status -eq 202 -and (Delivery $rn "Sms").status -eq "Sent") (Chain (Delivery $rn "Sms"))

Step "7c  Primary times out after sending -> Uncertain, and NO failover (would risk a duplicate SMS)"
Set-Chain $T3 $code "Sms" @($smsHang.id, $smsOk.id)
$t = Send $T3 (Late "demo-user" @("Sms"))
$tn = Wait-For $t.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -in $Final } 25
$td = Delivery $tn "Sms"
Check "T13" "Timeout -> Uncertain, backup provider not called" ($td.status -eq "Uncertain" -and @($td.attempts).Count -eq 1) (Chain $td)

Step "7d  Provider rejects the recipient -> Failed, no failover (every vendor would reject it)"
Set-Chain $T3 $code "Sms" @($smsReject.id, $smsOk.id)
$j = Send $T3 (Late "demo-user" @("Sms"))
$jd = Delivery (Wait-For $j.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -in $Final } 20) "Sms"
Check "REJ" "RecipientRejected -> Failed after one attempt" ($jd.status -eq "Failed" -and @($jd.attempts).Count -eq 1) (Chain $jd)

Step "7e  Cancel a notification that is waiting for a retry"
Set-Channel $T3 $code "Email" 5 60 120 2
Set-Chain $T3 $code "Email" @($mailDown.id)
$c = Send $T3 (Late "demo-user" @("Email"))
$null = Wait-For $c.Body.notificationId $T3 { param($x) (Delivery $x "Email").status -eq "Retrying" } 15
$cx = Call POST "/api/notifications/$($c.Body.notificationId)/cancel" $T3
$cd = Delivery (Get-Notification $c.Body.notificationId $T3) "Email"
Check "CANCEL" "Retrying delivery cancelled; the dispatcher will not pick it up again" ($cx.Body.cancelled -eq 1 -and $cd.status -eq "Cancelled") "status = $($cd.status)"
Set-Chain $T3 $code "Email" @($mailB.id); Set-Channel $T3 $code "Email" 3 1 3 2
Set-Chain $T3 $code "Sms" @($smsOk.id)
Next

# ====================================================================== 8. preferences
Scene "8" "User preferences: the user turns SMS off" "T10"
$pref = Call PUT "/api/me/preferences" $U3 @{ disabledChannels = @("Sms"); allowCriticalOverride = $true }
$pn = Send $T3 (Late "demo-user" @("InApp", "Sms"))
$pw = Wait-For $pn.Body.notificationId $T3 { param($x) All-Final $x } 20
Show $pw
Check "T10" "SMS suppressed by the user's preference, In-App still delivered" `
    ($pref.Status -eq 200 -and (Delivery $pw "Sms").status -eq "Suppressed" -and (Delivery $pw "InApp").status -eq "Delivered")
$null = Call PUT "/api/me/preferences" $U3 @{ disabledChannels = @(); allowCriticalOverride = $true }
Next

# ====================================================================== 9. quota / rate limit
Scene "9" "Quotas and rate limits: one tenant cannot consume everything" "T15 | Security (tenant-level limits)"
$self = Call PUT "/api/tenants/$code/quotas" $T3 @{ channel = "InApp"; period = "Daily"; limit = 1 }
Check "Q-1" "Tenant admin cannot raise its own quota (403)" ($self.Status -eq 403) "HTTP $($self.Status)"
$q = Call PUT "/api/tenants/$code/quotas" $SYS @{ channel = "InApp"; period = "Daily"; limit = 1 }
Info "SystemAdmin set InApp daily quota = 1 for $code"
$q1 = Send $T3 (Late "demo-user" @("InApp"))
$q2 = Send $T3 (Late "demo-user" @("InApp"))
Check "T15" "1st message accepted, 2nd -> 429 (quota consumed atomically in SQL, policy: Reject)" ($q1.Status -eq 202 -and $q2.Status -eq 429) "HTTP $($q1.Status), then HTTP $($q2.Status) $($q2.Body.title)"
$other = Send $T1 (Late "manager-1" @("InApp") "Unaffected")
Check "Q-2" "tenant-001 is not affected by tenant '$code' quota" ($other.Status -eq 202)
if ($q.Body.id) { $null = Call DELETE "/api/tenants/$code/quotas/$($q.Body.id)" $SYS }

if ($WithRateLimit) {
    $pol = @{ criticalOverridesUserPreferences = $true; quotaExceededPolicy = "Reject"; requestsPerMinute = 3; defaultLanguage = "En"; timeZoneId = "Africa/Cairo" }
    $null = Call PUT "/api/admin/tenants/$T3Id/policies" $SYS $pol
    Step "requestsPerMinute = 3 for $code. Waiting 35 s for the cached tenant policy to refresh..."
    Start-Sleep -Seconds 35
    $codes = 1..5 | ForEach-Object { (Send $T3 (Late "demo-user" @("InApp"))).Status }
    $one = Send $T1 (Late "manager-1" @("InApp") "Still fine")
    Check "RATE" "Burst of 5 -> some 429, tenant-001 still accepted" (($codes -contains 429) -and $one.Status -eq 202) "statuses: $($codes -join ', ')"
    $pol.requestsPerMinute = 600; $null = Call PUT "/api/admin/tenants/$T3Id/policies" $SYS $pol
}
Next

# ====================================================================== 10. isolation
Scene "10" "Tenant isolation and authorization" "T07, T08 | Multi-tenancy & data isolation (20%), Security"
$t1Chain = (Call GET "/api/tenants/tenant-001/channels/Sms/providers" $T1).Body
$t1Backup = @($t1Chain | Sort-Object order)[-1].providerConfigurationId
$x1 = Call GET "/api/notifications/$nid" $T2
Check "T07a" "tenant-002 reads tenant-001's notification by id -> 404 (it does not exist for tenant-002)" ($x1.Status -eq 404) "HTTP $($x1.Status)"
$x2 = Call GET "/api/tenants/tenant-001/notifications" $T2
Check "T07b" "tenant-002 opens /api/tenants/tenant-001/notifications -> 403" ($x2.Status -eq 403) "HTTP $($x2.Status)"
$x3 = Call GET "/api/tenants/$T1Id/providers" $T2
Check "ISO-1" "Same with the tenant GUID in the route -> 403" ($x3.Status -eq 403) "HTTP $($x3.Status)"
$body = Late "x" @("InApp"); $body.tenantId = "tenant-001"
$x4 = Send $T2 $body
Check "ISO-2" "tenantId in the body that is not the caller's tenant -> 403 (never trusted)" ($x4.Status -eq 403) "HTTP $($x4.Status)"
$x5 = Call PUT "/api/tenants/tenant-002/channels/Sms/providers" $T2 @{ providerConfigurationIds = @($t1Backup) }
Check "T08a" "tenant-002 puts tenant-001's provider id in its own chain -> 404" ($x5.Status -eq 404) "HTTP $($x5.Status)"
$x6 = Call GET "/api/tenants/tenant-002/providers/$t1Backup" $T2
Check "T08b" "tenant-002 reads tenant-001's provider by id -> 404" ($x6.Status -eq 404) "HTTP $($x6.Status)"
$x7 = Send $U1 (Late "manager-1" @("InApp"))
Check "AUTHZ-1" "End-user token cannot send notifications -> 403" ($x7.Status -eq 403) "HTTP $($x7.Status)"
$x8 = Call GET "/api/notifications"
Check "AUTHZ-2" "No token -> 401" ($x8.Status -eq 401) "HTTP $($x8.Status)"
$ghost = New-Token "ghost" ([guid]::NewGuid().ToString()) @("TenantAdmin")
$x9 = Call GET "/api/notifications" $ghost
Check "AUTHZ-3" "Token for a tenant that does not exist -> 403" ($x9.Status -eq 403) "HTTP $($x9.Status)"
Next

# ====================================================================== 11. secrets
Scene "11" "Provider secrets: encrypted at rest, never returned, rotated without downtime" "T18 | Security"
$first = "FIRST-secret-$Run"; $second = "SECOND-secret-$Run"
$rot = New-Provider $T3 $code "fake-sms" "Rotating vendor" @{ mode = "success" } @{ apiKey = $first } "DEMO"
$get1 = Call GET "/api/tenants/$code/providers/$($rot.id)" $T3
Check "SEC-1" "Secret value never appears in API responses (only names + version)" ($get1.Raw -notmatch $first) "secretNames = $($get1.Body.secretNames -join ','), secretVersion = $($get1.Body.secretVersion)"
$r2 = Call PUT "/api/tenants/$code/providers/$($rot.id)/secrets" $T3 @{ secrets = @{ apiKey = $second } }
Check "SEC-2" "Rotation -> secretVersion 2, new value not echoed" ($r2.Status -eq 200 -and $r2.Body.secretVersion -eq 2 -and $r2.Raw -notmatch $second) "secretVersion = $($r2.Body.secretVersion)"
Set-Chain $T3 $code "Sms" @($rot.id)
$sn = Send $T3 (Late "demo-user" @("Sms"))
$sd = Delivery (Wait-For $sn.Body.notificationId $T3 { param($x) All-Final $x } 20) "Sms"
Check "T18" "Next message is sent with the rotated credentials" ($sd.status -eq "Sent") (Chain $sd)
$list = Call GET "/api/tenants/$code/providers" $T3
Check "SEC-3" "Listing all providers leaks neither the old nor the new secret" ($list.Raw -notmatch $first -and $list.Raw -notmatch $second)
Set-Chain $T3 $code "Sms" @($smsOk.id)
Next

# ====================================================================== 12. webhooks
Scene "12" "Webhooks: Sent vs Delivered, signature + timestamp, replay protection" "T14 | Security"
$branchSms = @((Remember-Providers $T2 "tenant-002") | Where-Object { $_.channel -eq "Sms" -and $_.name -eq "Branch SMS" })[0]
$wh = Call POST "/api/tenants/tenant-002/webhooks" $T2 @{ providerConfigurationId = $branchSms.id }
Check "WH-1" "Webhook registered for 'Branch SMS' (signing secret shown once)" ($wh.Status -eq 200 -and $wh.Body.signingSecret) "callbackPath = $($wh.Body.callbackPath)"
$ws = Send $T2 (Late "branch-user-1" @("Sms") "Mona")
$wn = Wait-For $ws.Body.notificationId $T2 { param($x) All-Final $x } 20
$wd = Delivery $wn "Sms"
Check "SENT" "Provider accepted -> status is Sent, NOT Delivered" ($wd.status -eq "Sent" -and $wd.providerMessageId) "providerMessageId = $($wd.providerMessageId)"

function Post-Receipt([string]$Secret, [string]$Body, [long]$Ts) {
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    $sig = -join ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$Ts.$Body")) | ForEach-Object { $_.ToString("x2") })
    Call POST $wh.Body.callbackPath -Body $Body -RawBody -Headers @{ "X-Fake-Signature" = $sig; "X-Fake-Timestamp" = "$Ts" }
}
$receipt = '{"events":[{"eventId":"evt-' + [guid]::NewGuid() + '","providerMessageId":"' + $wd.providerMessageId + '","status":"delivered"}]}'
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$forged = Post-Receipt "wrong-secret" $receipt $now
Check "T14a" "Wrong signature -> 401, status unchanged" ($forged.Status -eq 401 -and (Delivery (Get-Notification $ws.Body.notificationId $T2) "Sms").status -eq "Sent") "HTTP $($forged.Status)"
$stale = Post-Receipt $wh.Body.signingSecret $receipt ($now - 3600)
Check "T14b" "Valid signature but timestamp 1 hour old -> 401" ($stale.Status -eq 401) "HTTP $($stale.Status)"
$ok = Post-Receipt $wh.Body.signingSecret $receipt $now
$dn = Wait-For $ws.Body.notificationId $T2 { param($x) (Delivery $x "Sms").status -eq "Delivered" } 10
Check "DELIV" "Signed receipt -> Delivered (only a provider receipt can prove delivery)" ($ok.Status -eq 200 -and (Delivery $dn "Sms").status -eq "Delivered") "received = $($ok.Body.received), applied = $($ok.Body.applied)"
$replay = Post-Receipt $wh.Body.signingSecret $receipt ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
Check "REPLAY" "Same event replayed -> counted as duplicate, ignored" ($replay.Status -eq 200 -and $replay.Body.duplicates -eq 1) "duplicates = $($replay.Body.duplicates)"
$null = Call DELETE "/api/tenants/tenant-002/webhooks/$($wh.Body.id)" $T2
Next

# ====================================================================== 13. restart
Scene "13" "Application restart with pending work" "T12 | Recovery after restart"
if ($SkipRestart) {
    Info "Skipped (-SkipRestart). Covered by the automated test T12."
} else {
    Set-Channel $T3 $code "Sms" 8 30 30 2
    $outage = New-Provider $T3 $code "fake-sms" "SMS vendor (outage during restart)" @{ mode = "transient" }
    Set-Chain $T3 $code "Sms" @($outage.id)
    $rs = Send $T3 (Late "demo-user" @("Sms"))
    $before = Delivery (Wait-For $rs.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -eq "Retrying" } 15) "Sms"
    Info "Before restart: status = $($before.status), attempts = $($before.attemptCount), nextAttemptAt = $($before.nextAttemptAt)"
    Write-Host ""
    Write-Host "  NOW: stop the API (Ctrl+C in its window) and start it again." -ForegroundColor Yellow
    Read-Host "  Press Enter when the API is running again" | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    do { $up = Test-Health; if (-not $up) { Start-Sleep 2 } } while (-not $up -and (Get-Date) -lt $deadline)
    $mid = Delivery (Get-Notification $rs.Body.notificationId $T3) "Sms"
    Check "T12a" "Pending delivery survived the restart (state is in SQL, not in memory)" ($mid.status -in "Retrying", "Processing", "Queued" -and $mid.attemptCount -ge 1) "status = $($mid.status), attempts = $($mid.attemptCount)"
    Step "The vendor comes back: switch its mode to success"
    $null = Call PUT "/api/tenants/$code/providers/$($outage.id)" $T3 @{ name = "SMS vendor (outage during restart)"; settings = @{ mode = "success" }; isActive = $true }
    $rd = Delivery (Wait-For $rs.Body.notificationId $T3 { param($x) (Delivery $x "Sms").status -in $Final } 75) "Sms"
    Check "T12b" "After restart the dispatcher resumed and sent it, attempt history kept" ($rd.status -eq "Sent" -and $rd.attemptCount -ge 2) (Chain $rd)
    Set-Channel $T3 $code "Sms" 3 1 3 2
}
Next

# ====================================================================== 14. reports
Scene "14" "Monitoring and reports" "Monitoring & reporting"
$null = Call PUT "/api/providers/fake-sms" $SYS @{ isEnabled = $true; estimatedCostPerMessage = 0.25; costCurrency = "EGP" }
$sum = (Call GET "/api/tenants/tenant-001/reports/summary" $T1).Body
Info ("tenant-001: notifications={0}  deliveries={1}  success={2:P0}  failure={3:P0}  deadLettered={4}  retries={5}" -f `
        $sum.totalNotifications, $sum.totalDeliveries, $sum.successRate, $sum.failureRate, $sum.deadLettered, $sum.totalRetries)
Check "REP-1" "Summary report (totals, rates, by channel/status, retries, quota use)" ($sum.totalNotifications -ge 1)
$prov = @((Call GET "/api/tenants/tenant-001/reports/providers" $T1).Body)
$prov | Select-Object providerKey, calls, accepted, failed, uncertain, skipped, averageLatencyMs, successRate | Format-Table -AutoSize | Out-String | Write-Host
Check "REP-2" "Provider report shows the failing primary and the healthy backup (fake-sms)" (@($prov | Where-Object { $_.providerKey -eq "fake-sms" -and $_.failed -ge 1 -and $_.accepted -ge 1 }).Count -eq 1)
$cost = @((Call GET "/api/tenants/tenant-001/reports/cost" $T1).Body)
$cost | Format-Table -AutoSize | Out-String | Write-Host
Check "REP-3" "Cost estimate uses the catalog price (0.25 EGP per SMS)" (@($cost | Where-Object { $_.providerKey -eq "fake-sms" -and $_.estimatedCost -gt 0 }).Count -eq 1)
$usage = Call GET "/api/admin/reports/tenant-usage" $SYS
$usage.Body | Format-Table -AutoSize | Out-String | Write-Host
$deny = Call GET "/api/admin/reports/tenant-usage" $T1
Check "REP-4" "Platform-wide usage for SystemAdmin only (tenant admin -> 403)" ($usage.Status -eq 200 -and $deny.Status -eq 403)

# ====================================================================== summary
Write-Host ""
Write-Host ("=" * 86) -ForegroundColor DarkCyan
$pass = @($Results | Where-Object Result -eq "PASS").Count
$fail = @($Results | Where-Object Result -eq "FAIL").Count
Write-Host (" RESULT: {0} passed, {1} failed   (demo tenant: {2})" -f $pass, $fail, $code) -ForegroundColor $(if ($fail) { "Red" } else { "Green" })
Write-Host ("=" * 86) -ForegroundColor DarkCyan
if ($fail) { $Results | Where-Object Result -eq "FAIL" | Format-Table Id, Check, Detail -AutoSize -Wrap | Out-String | Write-Host }
$out = Join-Path $PSScriptRoot "demo-result-$Run.csv"
$Results | Export-Csv $out -NoTypeInformation -Encoding utf8
Write-Host " Saved: $out"
Write-Host " T16 (concurrent dispatchers) is proven by the automated suite: dotnet test" -ForegroundColor DarkGray
