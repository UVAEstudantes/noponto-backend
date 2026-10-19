param([switch]$Run, [Parameter(Mandatory=$true)][string]$ResultsDirectory,
      [string]$Filter='FullyQualifiedName~NoPonto.Tests.GpsPipelineDisposableIntegrationTests.')
$ErrorActionPreference='Stop'
if (-not $Run) { throw 'Explicit -Run required for disposable integration.' }
if (-not [IO.Path]::IsPathFullyQualified($ResultsDirectory) -or (Test-Path -LiteralPath $ResultsDirectory)) {
    throw 'A new absolute results directory is required.'
}
$context='desktop-linux'
$endpoint=(docker --context $context context inspect $context --format '{{.Endpoints.docker.Host}}').Trim()
if ($LASTEXITCODE -ne 0 -or $endpoint -notlike 'npipe:*') { throw 'Local Docker Desktop named pipe required.' }
foreach ($image in @('postgis/postgis:16-3.4','redis:7.4-alpine')) {
    docker --context $context image inspect $image --format '{{.Id}}' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Preexisting fixture images required; no pull permitted.' }
}
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
$guard=[guid]::NewGuid().ToString('N')
$network="noponto-gps-$guard"
$owned=[Collections.Generic.List[string]]::new()
$before=@{}
foreach ($id in (docker --context $context ps -q --no-trunc)) {
    $before[$id]=(docker --context $context inspect --format '{{.State.StartedAt}}|{{.RestartCount}}|{{.State.Running}}' $id).Trim()
}
$previous=@{}
$keys=@('NOPONTO_GPS_FIXTURE_POSTGRES','NOPONTO_GPS_FIXTURE_REDIS','NOPONTO_GPS_FIXTURE_GUARD','NOPONTO_GPS_FIXTURE_RESULTS',
        'POSTGIS_TEST_CONNECTION','REDIS_TEST_CONNECTION')
foreach ($key in $keys) { $previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
$exitCode=1
$createdNetwork=$false
try {
    # Dedicated bridge: Docker Desktop does not publish host ports on internal
    # networks. No existing container is attached to this fixture network.
    docker --context $context network create --label "noponto.gps.fixture=$guard" $network | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture network creation failed.' }
    $createdNetwork=$true
    $pg=(docker --context $context run -d --rm --pull never --name "noponto-gps-pg-$guard" --network $network `
        --label "noponto.gps.fixture=$guard" --memory 512m --cpus 1 --pids-limit 160 `
        --tmpfs /var/lib/postgresql/data:rw,size=536870912 -e POSTGRES_HOST_AUTH_METHOD=trust `
        -e POSTGRES_DB=noponto_gps_fixture -p 127.0.0.1::5432 postgis/postgis:16-3.4).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Fixture PostGIS creation failed.' }; $owned.Add($pg)
    $redis=(docker --context $context run -d --rm --pull never --name "noponto-gps-redis-$guard" --network $network `
        --label "noponto.gps.fixture=$guard" --memory 96m --cpus 0.5 --pids-limit 64 --tmpfs /data:rw,size=33554432 `
        -p 127.0.0.1::6379 redis:7.4-alpine redis-server --save '' --appendonly no).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Redis creation failed.' }; $owned.Add($redis)
    $ready=$false
    for ($i=0; $i -lt 60; $i++) {
        docker --context $context exec $pg pg_isready -h 127.0.0.1 -U postgres -d noponto_gps_fixture 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Fixture readiness timeout.' }
    "CREATE TABLE noponto_gps_fixture_guard(token text NOT NULL); INSERT INTO noponto_gps_fixture_guard VALUES ('$guard');" |
        docker --context $context exec -i $pg psql -U postgres -d noponto_gps_fixture -v ON_ERROR_STOP=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Postgres guard failed.' }
    docker --context $context exec $redis redis-cli SET fixture:guard $guard | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Redis guard failed.' }
    $pgState=(docker --context $context inspect $pg | ConvertFrom-Json)[0]
    $redisState=(docker --context $context inspect $redis | ConvertFrom-Json)[0]
    foreach ($state in @($pgState,$redisState)) {
        if ($state.Config.Labels.'noponto.gps.fixture' -ne $guard -or @($state.Mounts | Where-Object Type -ne tmpfs).Count -gt 0) {
            throw 'Ownership or ephemeral mount validation failed.'
        }
    }
    $pgPort=$pgState.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    $redisPort=$redisState.NetworkSettings.Ports.'6379/tcp'[0].HostPort
    if (-not $pgPort -or -not $redisPort -or [int]$pgPort -le 0 -or [int]$redisPort -le 0 -or
        $pgState.NetworkSettings.Ports.'5432/tcp'[0].HostIp -ne '127.0.0.1' -or
        $redisState.NetworkSettings.Ports.'6379/tcp'[0].HostIp -ne '127.0.0.1') { throw 'Explicit random loopback ports required.' }
    $env:NOPONTO_GPS_FIXTURE_POSTGRES="Host=127.0.0.1;Port=$pgPort;Database=noponto_gps_fixture;Username=postgres;Timeout=5;Command Timeout=5"
    $env:NOPONTO_GPS_FIXTURE_REDIS="127.0.0.1:$redisPort,connectTimeout=5000,syncTimeout=5000"
    $env:NOPONTO_GPS_FIXTURE_GUARD=$guard
    $env:NOPONTO_GPS_FIXTURE_RESULTS=$ResultsDirectory
    $env:POSTGIS_TEST_CONNECTION=$env:NOPONTO_GPS_FIXTURE_POSTGRES
    $env:REDIS_TEST_CONNECTION=$env:NOPONTO_GPS_FIXTURE_REDIS
    [ordered]@{scope='disposable';dedicated_network=$true;network_internal=$false;mounts='tmpfs only';postgres_image=$pgState.Image;
        redis_image=$redisState.Image;preexisting_container_count=$before.Count;ports='random loopback only'} |
        ConvertTo-Json | Set-Content (Join-Path $ResultsDirectory 'isolation.json') -Encoding utf8
    $project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../NoPonto/NoPonto.csproj'))
    dotnet test $project -c Release --no-restore --filter $Filter --logger 'trx;LogFileName=integration.trx' --results-directory $ResultsDirectory --verbosity quiet
    $exitCode=$LASTEXITCODE
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key],'Process') }
    foreach ($id in $owned) {
        $owner=docker --context $context inspect --format '{{index .Config.Labels "noponto.gps.fixture"}}' $id 2>$null
        if ($LASTEXITCODE -eq 0 -and $owner.Trim() -eq $guard) {
            docker --context $context stop --timeout 5 $id | Out-Null
            if ($LASTEXITCODE -ne 0) { $exitCode=1 }
        } else { Write-Warning 'Ownership unavailable; container not touched.'; $exitCode=1 }
    }
    if ($createdNetwork) {
        $owner=docker --context $context network inspect --format '{{index .Labels "noponto.gps.fixture"}}' $network 2>$null
        if ($LASTEXITCODE -eq 0 -and $owner.Trim() -eq $guard) {
            docker --context $context network rm $network | Out-Null
            if ($LASTEXITCODE -ne 0) { $exitCode=1 }
        } else { Write-Warning 'Network ownership unavailable; network not touched.'; $exitCode=1 }
    }
    $unchanged=$true
    foreach ($id in $before.Keys) {
        $after=docker --context $context inspect --format '{{.State.StartedAt}}|{{.RestartCount}}|{{.State.Running}}' $id 2>$null
        if ($LASTEXITCODE -ne 0 -or $after.Trim() -ne $before[$id]) { $unchanged=$false; $exitCode=1 }
    }
    $left=@(docker --context $context ps -aq --filter "label=noponto.gps.fixture=$guard")
    [ordered]@{preexisting_unchanged=$unchanged;preexisting_count=$before.Count;owned_containers_remaining=$left.Count;
        owned_network_removed=(-not $createdNetwork -or (docker --context $context network ls -q --filter "label=noponto.gps.fixture=$guard").Count -eq 0)} |
        ConvertTo-Json | Set-Content (Join-Path $ResultsDirectory 'teardown.json') -Encoding utf8
    if ($left.Count -ne 0) { $exitCode=1 }
}
exit $exitCode
