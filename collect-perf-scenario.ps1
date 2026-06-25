param(
    [Parameter(Mandatory = $true)]
    [string]$Scenario,
    [int]$DurationSeconds = 30,
    [int]$TraceSeconds = 20,
    [string]$OutputRoot = ".tmp_build/perf",
    [switch]$SkipTrace
)

$ErrorActionPreference = "Stop"

$process = Get-Process GitDeployPro -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) {
    throw "GitDeployPro process was not found. Start the app first."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$safeScenario = ($Scenario -replace "[^a-zA-Z0-9\-_]", "_").ToLowerInvariant()
$scenarioDir = Join-Path $OutputRoot "$stamp-$safeScenario"
New-Item -Path $scenarioDir -ItemType Directory -Force | Out-Null

$processInfoPath = Join-Path $scenarioDir "process.txt"
$countersCsvPath = Join-Path $scenarioDir "counters.csv"
$summaryPath = Join-Path $scenarioDir "summary.txt"
$tracePath = Join-Path $scenarioDir "trace.nettrace"

"Scenario: $Scenario" | Out-File $processInfoPath
"Timestamp: $(Get-Date -Format o)" | Out-File $processInfoPath -Append
"PID: $($process.Id)" | Out-File $processInfoPath -Append
Get-Process -Id $process.Id | Format-List Id,ProcessName,CPU,PM,WS,StartTime | Out-File $processInfoPath -Append

$counterList = "System.Runtime[cpu-usage,working-set,gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,alloc-rate,exception-count,threadpool-thread-count]"
dotnet-counters collect --process-id $process.Id --duration ("00:00:{0:00}" -f [Math]::Max(5, $DurationSeconds)) --format csv --output $countersCsvPath --counters $counterList | Out-Null

$rows = Import-Csv $countersCsvPath
$summary = $rows |
    Group-Object "Counter Name" |
    ForEach-Object {
        $values = $_.Group | ForEach-Object { [double]$_."Mean/Increment" }
        [PSCustomObject]@{
            Counter = $_.Name
            Avg = [Math]::Round((($values | Measure-Object -Average).Average), 4)
            Max = [Math]::Round((($values | Measure-Object -Maximum).Maximum), 4)
            Min = [Math]::Round((($values | Measure-Object -Minimum).Minimum), 4)
            Samples = $values.Count
        }
    } |
    Sort-Object Counter

$summary | Format-Table -AutoSize | Out-File $summaryPath

if (-not $SkipTrace) {
    dotnet-trace collect --process-id $process.Id --duration ("00:00:{0:00}" -f [Math]::Max(5, $TraceSeconds)) --output $tracePath | Out-Null
}

Write-Output "Scenario artifacts saved: $scenarioDir"
