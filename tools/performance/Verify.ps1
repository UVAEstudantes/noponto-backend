#requires -Version 7.0
# Fast offline checks only. No Docker, database, GPS, network or load.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Failed: $Name" }
    $checks.Add($Name)
}
foreach ($file in @('Lab.ps1','Report.ps1','Verify.ps1')) {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors) | Out-Null
    Check ($errors.Count -eq 0) "syntax/$file"
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch = Join-Path $repo ("NoPonto/lab-output/perf-kit-verification-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$lab = $scratch
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Lab.ps1'),[ref]$tokens,[ref]$errors)
$connectionAssignment = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$redis' -and $node.Right.Extent.Text -like '*connectTimeout=*'
}, $true)
$ports = @{ RedisPort = 54321 }
. ([scriptblock]::Create($connectionAssignment.Extent.Text))
Check (($redis -split ',')[0] -ceq '127.0.0.1:54321' -and ($redis -split ',') -contains 'allowAdmin=true') 'isolated Redis connection enables INFO admin mode and retains guarded loopback port'
foreach ($name in @('Native','Run-Process')) {
    $node = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    # Load only these local helper definitions, never the lab entry point.
    . ([scriptblock]::Create($node.Extent.Text))
}
$code = Run-Process @('--version') @{} $scratch 15
Check ($code -is [int] -and $code -eq 0) 'child process returns scalar exit code without async output contamination'
Check ((Get-Content -LiteralPath (Join-Path $scratch 'stdout.log') -Raw).Trim() -match '^\d+\.\d+\.') 'child stdout retained separately'
Check ((Native 'dotnet' @('--version') 15) -match '^\d+\.\d+\.') 'bounded native argument dispatch'

foreach ($path in @('.', (Join-Path $scratch 'unsafe'), (Join-Path $repo 'NoPonto'))) {
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Lab.ps1') -Action Run -LabDirectory $path }
    catch { $rejected = $_.Exception.Message -match 'Absolute LabDirectory|direct child' }
    Check $rejected "unsafe path rejected: $path"
}

function Make-Run([string]$Name, [string]$Status, [int]$Passed, [int]$Cycles, [bool]$WithTrx = $true, [string]$Scenario = 'A') {
    $run = Join-Path $scratch "run-$Name"; New-Item -ItemType Directory -Path $run | Out-Null
    @{ scenario = $Scenario; repeats = 1; status = $Status; exit_code = 0; head = 'synthetic-offline-check' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
    $rows = @(@{ kind = 'perf_definition'; value = @{ scenario = $Scenario } })
    for ($i = 0; $i -lt $Cycles; $i++) {
        $rows += @{ kind = 'perf_cycle'; value = @{ supplied = 4; accepted = 2; failures = 0; cycle_ms = 10 * ($i + 1) } }
    }
    $rows += @{ kind = 'perf_pipeline'; value = @{ matching_ms = 5; normal = $true } }
    $rows | ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress } | Set-Content -LiteralPath (Join-Path $run 'observations.jsonl') -Encoding utf8
    if ($WithTrx) {
        $xml = "<TestRun><Results><UnitTestResult testName='NoPonto.Tests.GpsPipelineDisposableIntegrationTests.PerformanceLab_Manual' outcome='Passed'/></Results><ResultSummary><Counters total='$Passed' passed='$Passed' failed='0'/></ResultSummary></TestRun>"
        Set-Content -LiteralPath (Join-Path $run 'scenario.trx') -Value $xml -Encoding utf8
    }
    return $run
}
$good = Make-Run 'complete' 'finished' 1 8
$zero = Make-Run 'zero-tests' 'finished' 0 8
$partial = Make-Run 'partial' 'finished' 1 3
$interrupted = Make-Run 'interrupted' 'interrupted' 1 8
$missing = Make-Run 'missing-trx' 'finished' 1 8 $false
$operational = Make-Run 'operation-only' 'finished' 1 0 $true 'C'
for ($i = 0; $i -lt 16; $i++) {
    @{ kind = 'perf_operation'; value = @{ enabled = $true; step = 'invalidation'; elapsed_ms = 7; durable_commit = $true } } |
        ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $operational 'observations.jsonl') -Encoding utf8
}
$wrong = Make-Run 'wrong-definition' 'finished' 1 8
$wrongConfig = Get-Content -LiteralPath (Join-Path $wrong 'run.json') -Raw | ConvertFrom-Json -AsHashtable
$wrongConfig.scenario = 'B'
$wrongConfig | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $wrong 'run.json') -Encoding utf8
$redisUnavailable = Make-Run 'redis-info-unavailable' 'finished' 1 8
@{ kind = 'perf_unavailable'; value = @{ component = 'redis'; phase = 'before'; reason = 'info_admin_disabled' } } |
    ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $redisUnavailable 'observations.jsonl') -Encoding utf8
$redisPartial = Make-Run 'redis-info-partial' 'finished' 1 8
@{ kind = 'perf_redis'; value = @{ phase = 'before'; measured = @{ used_memory = '123456' } } } |
    ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $redisPartial 'observations.jsonl') -Encoding utf8
@{ kind = 'perf_unavailable'; value = @{ component = 'redis'; phase = 'after'; reason = 'info_permission_denied' } } |
    ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $redisPartial 'observations.jsonl') -Encoding utf8
& (Join-Path $PSScriptRoot 'Report.ps1') -LabDirectory $scratch | Out-Null
$a = Get-Content -LiteralPath (Join-Path $good 'summary.json') -Raw | ConvertFrom-Json
Check $a.complete 'complete evidence accepted'
Check ($a.measured.cycle_ms.p50 -eq 40 -and $a.measured.cycle_ms.p95 -eq 80 -and $a.measured.cycle_ms.p99 -eq 80) 'nearest-rank percentiles'
Check ([Math]::Abs($a.measured.accepted_per_processing_second - 16 / .36) -lt .0001) 'throughput uses total time'
Check (-not $a.measured.rest_ms.available -and $null -eq $a.measured.rest_signalr_divergences) 'missing REST is unavailable, never zero'
Check (-not $a.availability.container_resources) 'absent container metrics unavailable'
$op = Get-Content -LiteralPath (Join-Path $operational 'summary.json') -Raw | ConvertFrom-Json
Check $op.complete 'operational-only report accepts the expected sample count'
Check ($null -eq $op.measured.supplied_positions -and $null -eq $op.measured.trip_infrastructure_failures) 'operational-only report does not invent GPS counters'
$mismatch = Get-Content -LiteralPath (Join-Path $wrong 'summary.json') -Raw | ConvertFrom-Json
Check (-not $mismatch.complete) 'mismatched scenario definition rejected'
$unavailable = Get-Content -LiteralPath (Join-Path $redisUnavailable 'summary.json') -Raw | ConvertFrom-Json
Check ($unavailable.complete -and -not $unavailable.availability.redis -and $unavailable.measured.redis.Count -eq 0 -and
    $unavailable.availability.runtime_unavailable[0].reason -eq 'info_admin_disabled') 'INFO unavailable keeps valid run complete without inventing Redis values'
$markdown = Get-Content -LiteralPath (Join-Path $redisUnavailable 'summary.md') -Raw
Check ($markdown -match 'Redis INFO.*indisponível' -and $markdown -match 'redis.*before.*info_admin_disabled') 'Markdown explicitly identifies unavailable Redis INFO and phase/reason'
$partialInfo = Get-Content -LiteralPath (Join-Path $redisPartial 'summary.json') -Raw | ConvertFrom-Json
Check ($partialInfo.complete -and $partialInfo.availability.redis -and $partialInfo.measured.redis.Count -eq 1 -and
    $partialInfo.measured.redis[0].measured.used_memory -ceq '123456' -and
    $partialInfo.availability.runtime_unavailable[0].reason -eq 'info_permission_denied') 'partial INFO availability retains only measured values and denial reason'
foreach ($path in @($zero,$partial,$interrupted,$missing)) {
    $s = Get-Content -LiteralPath (Join-Path $path 'summary.json') -Raw | ConvertFrom-Json
    Check (-not $s.complete) "incomplete evidence rejected: $([IO.Path]::GetFileName($path))"
}
@{ kind = 'perf_operation'; value = @{ enabled = $true; step = 'invalidation'; elapsed_ms = 7; durable_commit = $true } } |
    ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $good 'observations.jsonl') -Encoding utf8
& (Join-Path $PSScriptRoot 'Report.ps1') -LabDirectory $scratch | Out-Null
$a = Get-Content -LiteralPath (Join-Path $good 'summary.json') -Raw | ConvertFrom-Json
Check (-not $a.complete -and $a.measured.operations[0].elapsed_ms.p50 -eq 7) 'unexpected operations rejected and flag grouping measured'
$checks | ForEach-Object { Write-Output "PASS $_" }
Write-Output "Offline checks passed: $($checks.Count). Synthetic artifacts: $scratch"
