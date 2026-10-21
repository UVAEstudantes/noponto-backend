#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$LabDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Distribution($Values) {
    $sorted = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return @{ available = $false; samples = 0 } }
    $result = [ordered]@{ available = $true; samples = $sorted.Count; min = $sorted[0]; max = $sorted[-1] }
    foreach ($q in @(50,95,99)) { $result["p$q"] = $sorted[[Math]::Max(0,[Math]::Ceiling($sorted.Count * $q / 100) - 1)] }
    return $result
}
function Read-JsonLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ($line) { $line | ConvertFrom-Json -AsHashtable }
    }
}

foreach ($dir in (Get-ChildItem -LiteralPath $LabDirectory -Directory -Filter 'run-*')) {
    $errors = [Collections.Generic.List[string]]::new()
    $run = Get-Content -LiteralPath (Join-Path $dir.FullName 'run.json') -Raw | ConvertFrom-Json -AsHashtable
    $samples = @()
    try { $samples = @(Read-JsonLines (Join-Path $dir.FullName 'observations.jsonl')) }
    catch { $errors.Add('observations.jsonl is truncated or invalid; metrics incomplete.') }
    $trx = $null
    try {
        [xml]$xml = Get-Content -LiteralPath (Join-Path $dir.FullName 'scenario.trx') -Raw
        $counters = $xml.SelectSingleNode("//*[local-name()='Counters']")
        $test = $xml.SelectSingleNode("//*[local-name()='UnitTestResult']")
        $trx = @{ total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed;
            test = [string]$test.testName; outcome = [string]$test.outcome }
        if ($trx.total -ne 1 -or $trx.passed -ne 1 -or $trx.failed -ne 0 -or $trx.test -notlike '*PerformanceLab_Manual*') {
            $errors.Add('TRX must contain exactly one passing PerformanceLab_Manual test.')
        }
    } catch { $errors.Add('TRX missing or invalid; execution cannot be certified.') }
    function Values([string]$Kind, [string]$Field) {
        @($samples | Where-Object kind -eq "perf_$Kind" | ForEach-Object { $_.value[$Field] })
    }
    $cycles = @($samples | Where-Object kind -eq 'perf_cycle')
    $accepted = [double](Values 'cycle' 'accepted' | Measure-Object -Sum).Sum
    $supplied = [double](Values 'cycle' 'supplied' | Measure-Object -Sum).Sum
    $cycleTime = [double](Values 'cycle' 'cycle_ms' | Measure-Object -Sum).Sum
    $failures = [double](Values 'cycle' 'failures' | Measure-Object -Sum).Sum
    $definitions = @($samples | Where-Object kind -eq 'perf_definition')
    if ($definitions.Count -ne 1) { $errors.Add('Scenario definition missing or duplicated.') }
    elseif ($definitions[0].value.scenario -ne $run.scenario) { $errors.Add('Scenario definition does not match run.json.') }
    if ($run.scenario -in @('A','B') -and $cycles.Count -ne 8 * $run.repeats) { $errors.Add('Cycle count incomplete.') }
    $operations = @($samples | Where-Object kind -eq 'perf_operation')
    $expectedOperations = if ($run.scenario -eq 'C') { 16 * $run.repeats } elseif ($run.scenario -eq 'D') { 32 * $run.repeats } else { 0 }
    if ($operations.Count -ne $expectedOperations) { $errors.Add('Operational sample count incomplete.') }
    $groups = @($operations | Group-Object { "$($_.value.enabled)/$($_.value.step)" } | ForEach-Object {
        @{ enabled_step = $_.Name; elapsed_ms = (Distribution @($_.Group | ForEach-Object { $_.value.elapsed_ms }));
            durable_commits = @($_.Group | Where-Object { $_.value.durable_commit }).Count }
    })
    $resourceSamples = @()
    try { $resourceSamples = @(Read-JsonLines (Join-Path $dir.FullName 'resources.jsonl')) }
    catch { $errors.Add('Container resource samples incomplete.') }
    $components = @($resourceSamples | Group-Object { $_.docker.Name -replace '-[a-f0-9]{32}$','' } | ForEach-Object {
        $values = @($_.Group | ForEach-Object { [double]::Parse(($_.docker.CPUPerc -replace '%',''),[Globalization.CultureInfo]::InvariantCulture) })
        @{ component = $_.Name; cpu_percent = (Distribution $values);
            memory = @($_.Group | ForEach-Object { @{ elapsed_s = $_.elapsed_s; usage = $_.docker.MemUsage; percent = $_.docker.MemPerc } }) }
    })
    $numericPipeline = @($samples | Where-Object kind -eq 'perf_pipeline' | ForEach-Object { $_.value })
    $pipeline = [ordered]@{}
    $keys = @($numericPipeline | ForEach-Object { $_.Keys } | Sort-Object -Unique)
    foreach ($key in $keys) { $pipeline[$key] = Distribution @($numericPipeline | ForEach-Object { if ($_.ContainsKey($key) -and $_[$key] -isnot [bool]) { $_[$key] } }) }
    $complete = $run.status -eq 'finished' -and $run.exit_code -eq 0 -and $errors.Count -eq 0
    $summary = [ordered]@{
        schema = 1; scenario = $run.scenario; complete = $complete; run = $run; trx = $trx; validation_errors = @($errors)
        availability = @{ container_resources = $resourceSamples.Count -gt 0; postgres = @(Values 'database' 'measured').Count -gt 0;
            redis = @(Values 'redis' 'measured').Count -gt 0; process = @(Values 'process' 'cpu_ms').Count -gt 0;
            cycle_latency = $cycles.Count -gt 0; operational_latency = $operations.Count -gt 0;
            runtime_unavailable = @($samples | Where-Object kind -eq 'perf_unavailable' | ForEach-Object value) }
        measured = [ordered]@{
            supplied_positions = $(if ($cycles.Count) { $supplied } else { $null }); accepted_positions = $(if ($cycles.Count) { $accepted } else { $null }); cycle_count = $cycles.Count
            accepted_per_processing_second = $(if ($cycleTime -gt 0) { $accepted / ($cycleTime / 1000) } else { $null })
            trip_infrastructure_failures = $(if ($cycles.Count) { $failures } else { $null })
            trip_failure_fraction_per_supplied_position = $(if ($supplied -gt 0) { $failures / $supplied } else { $null })
            cycle_ms = (Distribution (Values 'cycle' 'cycle_ms'))
            rest_ms = (Distribution (Values 'rest' 'rest_ms'))
            cycle_start_to_signalr_observation_ms = (Distribution (Values 'delivery' 'cycle_start_to_observation_ms'))
            rest_signalr_divergences = $(if (@(Values 'rest' 'divergences').Count) { (Values 'rest' 'divergences' | Measure-Object -Sum).Sum } else { $null })
            operations = $groups
            recovery = @(Values 'recovery' 'sequence_wall_ms')
            recovery_samples = @($samples | Where-Object kind -eq 'perf_recovery' | ForEach-Object value)
            reload_ms = (Distribution (Values 'reload' 'reload_ms'))
            outbox = @($samples | Where-Object kind -eq 'perf_outbox' | ForEach-Object value)
            database = @($samples | Where-Object kind -eq 'perf_database' | ForEach-Object value)
            redis = @($samples | Where-Object kind -eq 'perf_redis' | ForEach-Object value)
            process = @($samples | Where-Object kind -eq 'perf_process' | ForEach-Object value)
            memory = @($samples | Where-Object kind -eq 'perf_memory' | ForEach-Object value)
            failures = @($samples | Where-Object kind -eq 'perf_failure' | ForEach-Object value)
            natural_expiry = @($samples | Where-Object kind -eq 'perf_natural_expiry' | ForEach-Object value)
            container_resources = $components; pipeline_numeric = $pipeline
        }
        unavailable = @('Isolated server publication-to-wire latency (cycle-to-observer upper bound measured instead)',
            'Exact SQL statements/writes per GPS vehicle/cycle (durable semantic commits measured in C/D)',
            'PostGIS-only query latency distribution (pipeline matching stage and whole-DB SQL totals available)',
            'Full API process CPU separate from provider/load generator (same testhost)',
            'Real scheduler drift, production snapshot fidelity, multiple API replicas/backplane, arbitrary crash recovery',
            'Full telemetry sampling/stream and ETA/ML workers (outside this harness)',
            'Production acceptance or homeserver capacity: not established by synthetic desktop run')
        limitations = @('Percentiles use nearest rank; small sample p95/p99 often equal maximum; no statistical significance implied',
            'PostgreSQL counters are cumulative across this lab and asynchronously updated; do not interpret them as per-vehicle writes',
            'Observers and docker stats impose overhead; compare runs on same hardware/settings/order',
            'No pass/fail performance thresholds enforced yet; complete means scenario assertions and evidence passed')
    }
    $summary | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath (Join-Path $dir.FullName 'summary.json') -Encoding utf8
    $md = [Collections.Generic.List[string]]::new()
    $md.Add("# NoPonto — cenário $($run.scenario)")
    $md.Add(''); $md.Add("Execução completa e validada: **$complete**. HEAD: ``$($run.head)``. Estado: $($run.status).")
    $md.Add(''); $md.Add('| Métrica medida | Resultado |'); $md.Add('|---|---|')
    if ($cycles.Count) { $md.Add("| Posições fornecidas / aceitas | $supplied / $accepted |") }
    else { $md.Add('| Posições fornecidas / aceitas | indisponível neste cenário |') }
    $md.Add("| Aceitas por segundo de processamento | $($summary.measured.accepted_per_processing_second) |")
    foreach ($key in @('cycle_ms','rest_ms','cycle_start_to_signalr_observation_ms','reload_ms')) {
        $d = $summary.measured[$key]
        if ($d.available) { $md.Add("| $key (n=$($d.samples)), p50 / p95 / p99 | $($d.p50) / $($d.p95) / $($d.p99) ms |") }
        else { $md.Add("| $key | indisponível neste cenário |") }
    }
    $md.Add("| Divergências REST/SignalR | $($summary.measured.rest_signalr_divergences) (vazio = indisponível) |")
    if ($summary.availability.redis) { $md.Add("| Redis INFO | $($summary.measured.redis.Count) amostra(s) medida(s); consultar indisponibilidades por fase |") }
    else { $md.Add('| Redis INFO | indisponível; nenhuma amostra medida |') }
    $md.Add(''); $md.Add('Operação por flag/etapa, backlog, PostgreSQL, Redis, CPU/RAM e tempos internos: consultar summary.json e observations.jsonl. Dados brutos de containers: resources.jsonl. Logs separados: stdout.log/stderr.log; resultado de asserções: scenario.trx.')
    $md.Add(''); $md.Add('## Validação')
    foreach ($errorText in $errors) { $md.Add("- $errorText") }
    $md.Add(''); $md.Add('## Métricas indisponíveis')
    foreach ($entry in $summary.unavailable) { $md.Add("- $entry") }
    foreach ($entry in $summary.availability.runtime_unavailable) {
        $md.Add("- $($entry.component) (fase: $($entry.phase)): $($entry.reason)")
    }
    $md.Add(''); $md.Add('## Limitações')
    foreach ($entry in $summary.limitations) { $md.Add("- $entry") }
    [IO.File]::WriteAllLines((Join-Path $dir.FullName 'summary.md'), $md)
    Write-Output "$($dir.Name): complete=$complete"
}
