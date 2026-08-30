param(
    [int]$Sessions = 2,
    [int]$StreamSeconds = 5,
    [ValidateSet("new-rows", "growing-tail", "sdk-read-fallback")][string]$Shape = "new-rows",
    [switch]$PositiveControl,
    [string]$OutRoot = (Join-Path $env:TEMP "cockpit-app-repro")
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repo "src\Cockpit.App\Cockpit.App.csproj"
& dotnet build $project --configuration Debug --nologo
if ($LASTEXITCODE) { throw "Debug build failed." }

$exe = Join-Path $repo "src\Cockpit.App\bin\Debug\net10.0\Cockpit.App.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Build did not produce $exe" }
$run = Join-Path $OutRoot ([guid]::NewGuid().ToString("N"))
$work = Join-Path $run "work"
$state = Join-Path $run "state"
New-Item -ItemType Directory -Force -Path $work, $state | Out-Null
@{
    Layout = @{ FocusRailLayout = $true; SingleSessionLayout = $false; StackSessionsVertically = $false }
    Debug = @{ LogDiagnosticSnapshots = $true; ShowDebugControls = $false }
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $state "cockpit.json")

function Wait-ForJson([string]$Path, [int]$TimeoutSeconds, [string]$What) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try { return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 200 }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timeout waiting for ${What}: $Path"
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($exe)
$startInfo.UseShellExecute = $false
foreach ($pair in @{
    COCKPIT_MEASUREMENT_HARNESS = "1"; COCKPIT_LEAKSIM = "1"; COCKPIT_MEASUREMENT_ROOT = $work
    COCKPIT_STATE_ROOT = $state; TEMP = $work; TMP = $work
}.GetEnumerator()) { $startInfo.Environment[$pair.Key] = $pair.Value }
$process = [Diagnostics.Process]::Start($startInfo)
$ownPid = $process.Id
@{ pid = $ownPid; exe = $exe; stateRoot = $state } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $run "started.json")
Write-Output "started pid=$ownPid"

try {
    $hostReady = Wait-ForJson (Join-Path $work "measurement-host.ready.json") 90 "host-ready positive control"
    if ($hostReady.pid -ne $ownPid -or $hostReady.stateRoot -ne $state) { throw "Host-ready control mismatch." }
    if ($Shape -eq "sdk-read-fallback") { $Sessions = 4; $StreamSeconds = 1 }
    $control = if ($PositiveControl) { ",retain" } else { "" }
    [IO.File]::WriteAllText((Join-Path $work "cockpit-leaksim.trigger"), "apprepro:$Sessions,$StreamSeconds,$Shape$control")
    $ready = Wait-ForJson (Join-Path $work "app-repro.ready.json") 180 "app reproduction"
    if ($ready.pid -ne $ownPid -or $ready.started -ne $Sessions -or $ready.shape -ne $Shape) { throw "Repro control mismatch." }
    $doneTimeout = if ($Shape -eq "sdk-read-fallback") { 180 } else { $StreamSeconds + 30 }
    $done = Wait-ForJson (Join-Path $work "app-repro.done.json") $doneTimeout "stream completion"
    if ($done.pid -ne $ownPid -or $done.shape -ne $Shape) { throw "Completion control mismatch." }
    if ($Shape -eq "sdk-read-fallback") {
        $series = @($done.reachableBytes)
        if ($done.callsPerSession -ne 20 -or $done.resultBytes -ne 5MB -or $done.orphanedResultRows -ne 80 -or $series.Count -ne 20) { throw "SDK Read reproduction mismatch." }
        if ([bool]$done.positiveControl -ne [bool]$PositiveControl) { throw "Positive-control mismatch." }
        $rise = [long]$series[-1] - [long]$series[0]
        Write-Output "reachable-bytes: first=$($series[0]) last=$($series[-1]) delta=$rise"
        if ($PositiveControl -and $rise -lt 100MB) { throw "Positive control did not retain enough reachable memory: $rise bytes." }
    }
    @{ pid = $ownPid; hostReady = $hostReady; reproReady = $ready; reproDone = $done } |
        ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $run "result.json")
    Write-Output "passed: $run"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(20000) | Out-Null
    }
}
