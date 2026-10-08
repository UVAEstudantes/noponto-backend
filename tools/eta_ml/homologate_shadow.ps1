param([string]$ReportDirectory)
$ErrorActionPreference='Stop'
$root=Resolve-Path (Join-Path $PSScriptRoot '../..')
$suffix=[Guid]::NewGuid().ToString('N')
$container="eta-shadow-3e1-$suffix"
$network="eta-shadow-3e1-$suffix"
$database="eta_shadow_3e1_$suffix"
if (-not $ReportDirectory) { $ReportDirectory=Join-Path $root "tools/eta_ml/outputs/shadow-3e1-$suffix" }
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Report directory must be new' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$ownedContainer=$false
$ownedNetwork=$false
$previousConnection=$env:HISTORICAL_SHADOW_TEST_CONNECTION
try {
    docker network create --driver bridge --opt com.docker.network.bridge.enable_icc=false --label noponto.fixture=shadow-3e1 $network | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create exclusive network' }
    $ownedNetwork=$true
    # Exclusive network, inter-container communication off, published only on loopback.
    # --internal networks suppress published ports on this Docker Desktop; do not attach existing services.
    $probe=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
    $probe.Start(); $port=([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
    docker run -d --name $container --network $network --label noponto.fixture=shadow-3e1 --memory 512m --cpus 1 --tmpfs /var/lib/postgresql/data:rw,nosuid,size=256m -e POSTGRES_HOST_AUTH_METHOD=trust -e "POSTGRES_DB=$database" -p "127.0.0.1:${port}:5432" postgis/postgis:16-3.4 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create exclusive container' }
    $ownedContainer=$true
    $readyTimer=[System.Diagnostics.Stopwatch]::StartNew()
    do {
        docker exec $container psql -h 127.0.0.1 -U postgres -d $database -v ON_ERROR_STOP=1 -At -c 'SELECT 1;' *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ($readyTimer.Elapsed.TotalSeconds -lt 180)
    if ($LASTEXITCODE -ne 0) { throw 'Fixture not ready' }
    # No reuse of operational database or operational migrations.
    "CREATE EXTENSION IF NOT EXISTS postgis; COMMENT ON DATABASE $database IS 'noponto-exclusive-shadow-3e1';" | docker exec -i $container psql -h 127.0.0.1 -U postgres -d $database -v ON_ERROR_STOP=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot mark fixture' }
    $binding=[string](docker port $container 5432/tcp)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect fixture loopback binding' }
    $binding=$binding.Trim()
    if ($binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Fixture must publish loopback only' }
    $env:HISTORICAL_SHADOW_TEST_CONNECTION="Host=127.0.0.1;Port=$($Matches[1]);Database=$database;Username=postgres;Application Name=eta-shadow-3e1;Maximum Pool Size=2"
    dotnet test (Join-Path $root 'NoPonto/NoPonto.csproj') --no-restore --filter 'FullyQualifiedName~HistoricalEtaShadowLocalTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=shadow-3e1.trx' --results-directory $ReportDirectory 2>&1 | Tee-Object -FilePath (Join-Path $ReportDirectory 'test.log')
    $testExit=$LASTEXITCODE
    docker stats --no-stream --format '{{json .}}' $container | Set-Content (Join-Path $ReportDirectory 'postgres-stats.json')
    docker exec $container psql -U postgres -d $database -At -c 'SELECT version(),postgis_full_version();' | Set-Content (Join-Path $ReportDirectory 'versions.txt')
    if ($testExit -ne 0) { throw "Homologation failed, inspect $ReportDirectory" }
    Write-Host "PASS: reports in $ReportDirectory"
} finally {
    $env:HISTORICAL_SHADOW_TEST_CONNECTION=$previousConnection
    # Delete ONLY resources created by this invocation; never prune, drop/reuse or remove existing resources.
    if ($ownedContainer) {
        docker logs $container 2>&1 | Set-Content (Join-Path $ReportDirectory 'fixture-server.log')
        docker rm -f -v $container | Out-Null
    }
    if ($ownedNetwork) { docker network rm $network | Out-Null }
}
