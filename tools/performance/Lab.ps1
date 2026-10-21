#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Start','Run','Interrupt','Stop','Report')][string]$Action,
    [Parameter(Mandatory)][string]$LabDirectory,
    [ValidateSet('A','B','C','D')][string]$Scenario = 'A',
    [ValidateRange(2,1000)][int]$Vehicles = 4,
    [ValidateRange(1,8)][int]$LinesPerModal = 1,
    [ValidateRange(0,64)][int]$Subscribers = 2,
    [ValidateRange(1,10)][int]$Repeats = 1,
    [ValidateRange(0,30)][int]$PaceSeconds = 1,
    [ValidateRange(1,4)][int]$Parallelism = 2,
    [ValidateRange(30,900)][int]$MaxSeconds = 180,
    [ValidateRange(256,1536)][int]$HostMemoryMB = 768,
    [switch]$IndividualMatching,
    [switch]$NaturalExpiry,
    [switch]$AllowLargerLoad
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$labRoot = [IO.Path]::GetFullPath((Join-Path $repo 'NoPonto/lab-output'))
if (-not [IO.Path]::IsPathFullyQualified($LabDirectory)) { throw 'Absolute LabDirectory required.' }
$lab = [IO.Path]::GetFullPath($LabDirectory)
if ([IO.Path]::GetDirectoryName($lab) -ne $labRoot -or [IO.Path]::GetFileName($lab) -notmatch '^perf-[a-f0-9]{32}$') {
    throw 'LabDirectory must be an absolute direct child NoPonto/lab-output/perf-<GUID N>.'
}
# Reject links/junctions in every existing ancestor, including workspace ancestors.
for ($path = $lab; $path; $path = [IO.Path]::GetDirectoryName($path)) {
    if (Test-Path -LiteralPath $path) {
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Reparse points are not accepted in lab paths.'
        }
    }
}
$statePath = Join-Path $lab 'lab.json'
$context = 'desktop-linux'
$label = 'noponto.performance.lab'
$lock = $null
$activeProcess = $null

function Native([string]$File, [string[]]$Arguments, [int]$Timeout = 20) {
    $pi = [Diagnostics.ProcessStartInfo]::new($File)
    $pi.UseShellExecute = $false; $pi.CreateNoWindow = $true
    $pi.RedirectStandardOutput = $true; $pi.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $pi.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::Start($pi)
    try {
        $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($Timeout * 1000)) { $p.Kill($true); throw "$File timed out." }
        $stdout = $out.GetAwaiter().GetResult(); $stderr = $err.GetAwaiter().GetResult()
        if ($p.ExitCode -ne 0) { throw "$File exited $($p.ExitCode): $stderr $stdout" }
        return $stdout.Trim()
    } finally { $p.Dispose() }
}
function Docker([string[]]$Arguments) { Native 'docker' (@('--context',$context) + $Arguments) }
function Source-Fingerprint {
    $files = @((Native 'git' @('-C',$repo,'-c','core.quotepath=false','ls-files','--','NoPonto','tools/performance')) -split "`n" | Where-Object { $_ })
    $files += @(Get-ChildItem -LiteralPath $PSScriptRoot -File | ForEach-Object { 'tools/performance/' + $_.Name })
    $rows = foreach ($file in ($files | Sort-Object -Unique)) {
        $digest = (Get-FileHash -LiteralPath (Join-Path $repo $file) -Algorithm SHA256).Hash
        "$file=$digest"
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Save-State { $script:state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $statePath -Encoding utf8 }
function Check-Docker {
    $endpoint = Docker @('context','inspect',$context,'--format','{{.Endpoints.docker.Host}}')
    if ($endpoint -notmatch '^npipe:////\./pipe/[a-zA-Z0-9_.-]+$') { throw 'Local Docker Desktop named pipe required.' }
    $os = Docker @('info','--format','{{.OSType}}')
    if ($os -ne 'linux') { throw 'Docker Desktop Linux containers required.' }
}
function Inspect-Owned([string]$Name, [string]$Kind = 'container') {
    $argsList = if ($Kind -eq 'network') { @('network','inspect',$Name) } else { @('inspect',$Name) }
    $item = (Docker $argsList | ConvertFrom-Json)[0]
    $owner = if ($Kind -eq 'network') { $item.Labels.$label } else { $item.Config.Labels.$label }
    if ($owner -ne $state.guard) { throw 'Ownership mismatch; resource will not be touched.' }
    return $item
}
function Check-Services {
    $pg = Inspect-Owned $state.pg; $redis = Inspect-Owned $state.redis
    foreach ($item in @($pg,$redis)) {
        if (-not $item.State.Running -or $item.State.OOMKilled -or $item.HostConfig.Memory -le 0 -or
            @($item.Mounts | Where-Object Type -ne 'tmpfs').Count -gt 0) { throw 'Ephemeral service safety check failed.' }
        $netNames = @($item.NetworkSettings.Networks.PSObject.Properties.Name)
        if ($netNames.Count -ne 1 -or $netNames[0] -ne $state.network) { throw 'Unexpected service network.' }
    }
    $pgBind = @($pg.NetworkSettings.Ports.'5432/tcp'); $redisBind = @($redis.NetworkSettings.Ports.'6379/tcp')
    if ($pgBind.Count -ne 1 -or $redisBind.Count -ne 1 -or $pgBind[0].HostIp -ne '127.0.0.1' -or
        $redisBind[0].HostIp -ne '127.0.0.1' -or [int]$pgBind[0].HostPort -in @(0,5432) -or
        [int]$redisBind[0].HostPort -in @(0,6379,6380)) { throw 'Random loopback bindings required.' }
    $token = Docker @('exec',$state.pg,'psql','-X','-U','postgres','-d','noponto_gps_fixture','-Atc','SELECT token FROM public.noponto_gps_fixture_guard')
    $redisToken = Docker @('exec',$state.redis,'redis-cli','--raw','GET','fixture:guard')
    if ($token -ne $state.guard -or $redisToken -ne $state.guard) { throw 'Data guards do not match.' }
    return @{ PgPort = $pgBind[0].HostPort; RedisPort = $redisBind[0].HostPort }
}
function Run-Process([string[]]$Arguments, [hashtable]$Environment, [string]$Directory, [int]$Budget, [switch]$Monitor) {
    $pi = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $pi.UseShellExecute = $false; $pi.CreateNoWindow = $true; $pi.WorkingDirectory = $repo
    $pi.RedirectStandardOutput = $true; $pi.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $pi.ArgumentList.Add($arg) }
    foreach ($key in $Environment.Keys) { $pi.Environment[$key] = [string]$Environment[$key] }
    $script:activeProcess = [Diagnostics.Process]::Start($pi)
    $p = $script:activeProcess
    $stdout = [IO.File]::Create((Join-Path $Directory 'stdout.log'))
    $stderr = [IO.File]::Create((Join-Path $Directory 'stderr.log'))
    $outTask = $p.StandardOutput.BaseStream.CopyToAsync($stdout)
    $errTask = $p.StandardError.BaseStream.CopyToAsync($stderr)
    $watch = [Diagnostics.Stopwatch]::StartNew(); $nextSample = 0
    try {
        while (-not $p.WaitForExit(250)) {
            if ($watch.Elapsed.TotalSeconds -ge $Budget) { throw 'Duration limit exceeded; process tree interrupted.' }
            if (Test-Path -LiteralPath (Join-Path $lab 'interrupt')) { throw 'Operator interruption requested.' }
            if ($Monitor -and $watch.Elapsed.TotalSeconds -ge $nextSample) {
                $nextSample = $watch.Elapsed.TotalSeconds + 5
                $stats = Docker @('stats','--no-stream','--format','{{json .}}',$state.pg,$state.redis)
                foreach ($line in ($stats -split "`n" | Where-Object { $_ })) {
                    @{ elapsed_s = $watch.Elapsed.TotalSeconds; docker = ($line | ConvertFrom-Json) } |
                        ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $Directory 'resources.jsonl') -Encoding utf8
                }
                foreach ($name in @($state.pg,$state.redis)) {
                    $item = Inspect-Owned $name
                    if (-not $item.State.Running -or $item.State.OOMKilled -or $item.RestartCount -ne 0) { throw 'Service stopped, restarted or OOM; interrupted.' }
                }
                if ($watch.Elapsed.TotalSeconds -ge $Budget) { throw 'Duration limit exceeded during sampling.' }
            }
        }
        return $p.ExitCode
    } finally {
        if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit(5000) | Out-Null }
        [void]$outTask.GetAwaiter().GetResult(); [void]$errTask.GetAwaiter().GetResult()
        $stdout.Dispose(); $stderr.Dispose(); $p.Dispose(); $script:activeProcess = $null
    }
}

if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $lab) { throw 'Prepare requires a new lab directory.' }
    New-Item -ItemType Directory -Path $lab -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $lab)) { throw 'Lab directory missing. Run Prepare first.' }
if ($Action -eq 'Interrupt') {
    [IO.File]::WriteAllText((Join-Path $lab 'interrupt'), 'operator request')
    Write-Output 'Interruption requested; wait for Run to return before Stop.'; return
}
try {
    $lock = [IO.File]::Open((Join-Path $lab 'operation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if ($Action -eq 'Prepare') {
        $guard = [IO.Path]::GetFileName($lab).Substring(5)
        $script:state = [ordered]@{ version = 1; guard = $guard; repo = $repo; head = (Native 'git' @('-C',$repo,'rev-parse','HEAD'));
            pg = "noponto-perf-pg-$guard"; redis = "noponto-perf-redis-$guard"; network = "noponto-perf-$guard";
            prepared = $false; started = $false; preexisting = @(); created_utc = [DateTime]::UtcNow.ToString('o');
            source_sha256 = (Source-Fingerprint); git_status = (Native 'git' @('-C',$repo,'status','--porcelain=v1')) }
        Save-State
        $build = Join-Path $lab 'build'; New-Item -ItemType Directory -Path $build | Out-Null
        $target = Join-Path $PSScriptRoot 'PerformanceLab.targets'
        $code = Run-Process @('build',(Join-Path $repo 'NoPonto/NoPonto.csproj'),'-c','Release','--no-restore',
            "-p:CustomAfterMicrosoftCommonTargets=$target","-p:OutputPath=$build/","-p:IntermediateOutputPath=$lab/obj/",'--verbosity','quiet') @{} $lab 180
        if ($code -ne 0) { throw 'Lab build failed; see stdout.log/stderr.log. Restore dependencies separately if needed.' }
        $state.prepared = $true; Save-State
        Write-Output "Prepared at $lab (no Docker services started)."; return
    }
    $script:state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
    if ($state.version -ne 1 -or $state.repo -ne $repo -or $state.guard -cne [IO.Path]::GetFileName($lab).Substring(5) -or
        $state.pg -cne "noponto-perf-pg-$($state.guard)" -or $state.redis -cne "noponto-perf-redis-$($state.guard)" -or
        $state.network -cne "noponto-perf-$($state.guard)") { throw 'Invalid manifest.' }
    if ($Action -eq 'Report') { & (Join-Path $PSScriptRoot 'Report.ps1') -LabDirectory $lab; return }
    Check-Docker
    if ($Action -eq 'Start') {
        if (-not $state.prepared -or $state.started) { throw 'Prepare once, then Start once.' }
        # Fail on collisions before creating any resource; never adopt a resource.
        $existingNames = Docker @('ps','-a','--format','{{.Names}}')
        $existingNetworks = Docker @('network','ls','--format','{{.Name}}')
        if ($state.pg -in ($existingNames -split "`n") -or $state.redis -in ($existingNames -split "`n") -or
            $state.network -in ($existingNetworks -split "`n")) { throw 'Resource name collision. Use a new lab.' }
        $state.preexisting = @()
        foreach ($id in ((Docker @('ps','-aq','--no-trunc')) -split "`n" | Where-Object { $_ })) {
            $snapshot = Docker @('inspect','--format','{{.State.StartedAt}}|{{.RestartCount}}|{{.State.Running}}',$id)
            $state.preexisting += @{ id = $id; state = $snapshot }
        }
        $state.images = @{}
        foreach ($image in @('postgis/postgis:16-3.4','redis:7.4-alpine')) {
            $state.images[$image] = Docker @('image','inspect',$image,'--format','{{.Id}}')
        }
        Save-State
        Docker @('network','create','--label',"$label=$($state.guard)",$state.network) | Out-Null
        # Image IDs frozen by inspect; no pull/build, no bind mounts, no persistent volumes.
        Docker @('run','-d','--pull','never','--name',$state.pg,'--network',$state.network,'--label',"$label=$($state.guard)",
            '--memory','512m','--memory-swap','512m','--cpus','1','--pids-limit','160',
            '--tmpfs','/var/lib/postgresql/data:rw,size=536870912','-e','POSTGRES_HOST_AUTH_METHOD=trust','-e','POSTGRES_DB=noponto_gps_fixture',
            '-p','127.0.0.1::5432',$state.images['postgis/postgis:16-3.4'],
            '-c','shared_preload_libraries=pg_stat_statements','-c','pg_stat_statements.track=all','-c','track_io_timing=on','-c','max_connections=40') | Out-Null
        Docker @('run','-d','--pull','never','--name',$state.redis,'--network',$state.network,'--label',"$label=$($state.guard)",
            '--memory','96m','--memory-swap','96m','--cpus','0.5','--pids-limit','64','--tmpfs','/data:rw,size=33554432',
            '-p','127.0.0.1::6379',$state.images['redis:7.4-alpine'],'redis-server','--save','','--appendonly','no',
            '--maxmemory','64mb','--maxmemory-policy','noeviction') | Out-Null
        $ready = $false
        $readyWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($readyWatch.Elapsed.TotalSeconds -lt 45) {
            try { Docker @('exec',$state.pg,'pg_isready','-h','127.0.0.1','-U','postgres','-d','noponto_gps_fixture') | Out-Null; $ready = $true; break }
            catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw 'PostGIS readiness timeout. Run Stop to clean partially created resources.' }
        $sql = "CREATE TABLE public.noponto_gps_fixture_guard(token text NOT NULL); INSERT INTO public.noponto_gps_fixture_guard VALUES ('$($state.guard)'); CREATE EXTENSION IF NOT EXISTS pg_stat_statements;"
        Docker @('exec',$state.pg,'psql','-X','-U','postgres','-d','noponto_gps_fixture','-v','ON_ERROR_STOP=1','-c',$sql) | Out-Null
        Docker @('exec',$state.redis,'redis-cli','SET','fixture:guard',$state.guard) | Out-Null
        Check-Services | Out-Null
        $state.started = $true; Save-State
        Write-Output 'Started guarded PostGIS/Redis. Nothing was fetched from external GPS providers.'; return
    }
    if ($Action -eq 'Stop') {
        # Enumerate by exact unique ownership label; revalidate names and labels before removal.
        $owned = @((Docker @('ps','-aq','--filter',"label=$label=$($state.guard)")) -split "`n" | Where-Object { $_ })
        foreach ($id in $owned) {
            $item = Inspect-Owned $id
            if ($item.Name.TrimStart('/') -notin @($state.pg,$state.redis) -or
                @($item.Mounts | Where-Object Type -ne 'tmpfs').Count -gt 0) { throw 'Unexpected owned resource; refusing cleanup.' }
            Docker @('rm','-f',$id) | Out-Null
        }
        $networks = @((Docker @('network','ls','-q','--filter',"label=$label=$($state.guard)")) -split "`n" | Where-Object { $_ })
        foreach ($id in $networks) {
            $network = Inspect-Owned $id 'network'
            if ($network.Name -ne $state.network -or @($network.Containers.PSObject.Properties).Count -gt 0) { throw 'Network not empty or name mismatch.' }
            Docker @('network','rm',$id) | Out-Null
        }
        $preserved = $true
        foreach ($entry in $state.preexisting) {
            try { $after = Docker @('inspect','--format','{{.State.StartedAt}}|{{.RestartCount}}|{{.State.Running}}',$entry.id)
                if ($after -ne $entry.state) { $preserved = $false } }
            catch { $preserved = $false }
        }
        @{ utc = [DateTime]::UtcNow.ToString('o'); preexisting_unchanged = $preserved; preexisting_count = @($state.preexisting).Count;
            owned_containers_remaining = ((Docker @('ps','-aq','--filter',"label=$label=$($state.guard)")) -split "`n" | Where-Object { $_ } | Measure-Object).Count;
            owned_networks_remaining = ((Docker @('network','ls','-q','--filter',"label=$label=$($state.guard)")) -split "`n" | Where-Object { $_ } | Measure-Object).Count } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $lab 'cleanup.json') -Encoding utf8
        $state.started = $false; Save-State
        if (-not $preserved) { throw 'Preexisting resource changed externally or disappeared; inspect cleanup.json.' }
        Write-Output 'Only owned containers/network removed. Reports retained; no volume or image deletion.'; return
    }
    if ($Action -eq 'Run') {
        if (-not $state.started) { throw 'Start the lab first.' }
        if (Test-Path -LiteralPath (Join-Path $lab 'interrupt')) { throw 'Interrupted labs must be cleaned with Stop. Prepare a new lab to resume.' }
        if ($Vehicles -lt 2 * $LinesPerModal -or ($Scenario -eq 'B' -and $Subscribers -eq 0)) { throw 'One vehicle per modal/line and B subscriber required.' }
        if ($NaturalExpiry -and $Scenario -ne 'B') { throw 'NaturalExpiry is a B-only optional witness.' }
        if (-not $AllowLargerLoad -and ($Vehicles -gt 8 -or $LinesPerModal -gt 1 -or $Subscribers -gt 4 -or $Repeats -gt 1 -or $MaxSeconds -gt 180 -or $HostMemoryMB -gt 768)) {
            throw 'Larger loads require explicit -AllowLargerLoad.'
        }
        $ports = Check-Services
        if ((Native 'git' @('-C',$repo,'rev-parse','HEAD')) -ne $state.head) { throw 'HEAD changed since Prepare; create a fresh lab/build.' }
        if ((Source-Fingerprint) -ne $state.source_sha256) { throw 'Source changed since Prepare; create a fresh lab/build. No WIP was modified.' }
        $run = Join-Path $lab ("run-$Scenario-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run | Out-Null
        $settings = @{ scenario = $Scenario; vehicles = $Vehicles; lines_per_modal = $LinesPerModal; subscribers = $Subscribers; repeats = $Repeats;
            cycles_per_repeat = 8; pace_seconds = $PaceSeconds; parallelism = $Parallelism; max_seconds = $MaxSeconds; host_memory_mb = $HostMemoryMB;
            matching_batch = (-not $IndividualMatching); natural_expiry = [bool]$NaturalExpiry; head = $state.head; images = $state.images; cpu_count = [Environment]::ProcessorCount;
            host_ram_bytes = (Get-CimInstance -ClassName Win32_ComputerSystem).TotalPhysicalMemory;
            os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; dotnet = (Native 'dotnet' @('--version')); pwsh = $PSVersionTable.PSVersion.ToString();
            source_sha256 = $state.source_sha256; git_status = $state.git_status;
            status = 'running'; exit_code = $null; interruption = $null }
        $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
        $pg = "Host=127.0.0.1;Port=$($ports.PgPort);Database=noponto_gps_fixture;Username=postgres;Timeout=5;Command Timeout=5"
        $redis = "127.0.0.1:$($ports.RedisPort),connectTimeout=5000,syncTimeout=5000"
        $envs = @{ NOPONTO_GPS_FIXTURE_POSTGRES = $pg; NOPONTO_GPS_FIXTURE_REDIS = $redis; NOPONTO_GPS_FIXTURE_GUARD = $state.guard;
            NOPONTO_GPS_FIXTURE_RESULTS = $run; POSTGIS_TEST_CONNECTION = $pg; REDIS_TEST_CONNECTION = $redis;
            NOPONTO_PERF_RUN = '1'; NOPONTO_PERF_SCENARIO = $Scenario; NOPONTO_PERF_VEHICLES = $Vehicles; NOPONTO_PERF_LINES = $LinesPerModal;
            NOPONTO_PERF_SUBSCRIBERS = $Subscribers; NOPONTO_PERF_REPEATS = $Repeats; NOPONTO_PERF_CYCLES = 8; NOPONTO_PERF_PACE = $PaceSeconds;
            NOPONTO_PERF_PARALLELISM = $Parallelism; NOPONTO_PERF_BATCH = [int](-not $IndividualMatching);
            NOPONTO_PERF_NATURAL_EXPIRY = [int][bool]$NaturalExpiry;
            DOTNET_GCHeapHardLimit = ('0x{0:X}' -f ($HostMemoryMB * 1MB)); DOTNET_PROCESSOR_COUNT = $Parallelism }
        try {
            $settings.exit_code = Run-Process @('test',(Join-Path $repo 'NoPonto/NoPonto.csproj'),'-c','Release','--no-build','--no-restore',
                "-p:OutputPath=$lab/build/",'--filter','FullyQualifiedName=NoPonto.Tests.GpsPipelineDisposableIntegrationTests.PerformanceLab_Manual',
                '--logger','trx;LogFileName=scenario.trx','--results-directory',$run,'--verbosity','quiet') $envs $run $MaxSeconds -Monitor
            $settings.status = if ($settings.exit_code -eq 0) { 'finished' } else { 'failed' }
        } catch {
            $settings.status = 'interrupted'; $settings.interruption = $_.Exception.Message
        } finally {
            $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
            & (Join-Path $PSScriptRoot 'Report.ps1') -LabDirectory $lab
        }
        Write-Output "Reports: $run"
        $summary = Get-Content -LiteralPath (Join-Path $run 'summary.json') -Raw | ConvertFrom-Json
        if ($settings.status -ne 'finished' -or -not $summary.complete) { throw 'Run failed, incomplete or interrupted; reports retained.' }
    }
} finally {
    if ($null -ne $activeProcess -and -not $activeProcess.HasExited) { $activeProcess.Kill($true) }
    if ($null -ne $lock) { $lock.Dispose() }
}
