param()
$ErrorActionPreference='Stop'
$suffix=[Guid]::NewGuid().ToString('N')
$container="eta-coverage-redis-$suffix"
$network="eta-coverage-redis-$suffix"
$previousConnection=$env:ETA_COVERAGE_REDIS_CONNECTION
$previousMarker=$env:ETA_COVERAGE_REDIS_MARKER
$ownedContainer=$false
$ownedNetwork=$false
try {
    docker network create --driver bridge --opt com.docker.network.bridge.enable_icc=false --label noponto.fixture=coverage-3g3b2a $network | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Exclusive Redis network failed' }
    $ownedNetwork=$true
    $probe=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
    $probe.Start();$port=([System.Net.IPEndPoint]$probe.LocalEndpoint).Port;$probe.Stop()
    docker run -d --name $container --network $network --label noponto.fixture=coverage-3g3b2a --memory 128m --cpus 1 -p "127.0.0.1:${port}:6379" redis:7 redis-server --appendonly no --save '' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Exclusive Redis container failed' }
    $ownedContainer=$true
    $timer=[System.Diagnostics.Stopwatch]::StartNew()
    do {
        $savedPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
        $pong=docker exec $container redis-cli ping 2>$null
        $probeExit=$LASTEXITCODE;$ErrorActionPreference=$savedPreference
        if ($probeExit -eq 0 -and $pong -eq 'PONG') { break }
        Start-Sleep -Milliseconds 300
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    if ($pong -ne 'PONG') { throw 'Exclusive Redis not ready' }
    $binding=([string](docker port $container 6379/tcp)).Trim()
    if ($binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Loopback required' }
    $env:ETA_COVERAGE_REDIS_CONNECTION="127.0.0.1:$($Matches[1]),abortConnect=true,name=eta-coverage-$suffix"
    $env:ETA_COVERAGE_REDIS_MARKER=$suffix
    # The existing harness creates a separate exclusive PostGIS fixture and validates its identity.
    & (Join-Path $PSScriptRoot 'homologate_evidence.ps1')
} finally {
    $env:ETA_COVERAGE_REDIS_CONNECTION=$previousConnection
    $env:ETA_COVERAGE_REDIS_MARKER=$previousMarker
    if ($ownedContainer) { docker rm -f -v $container | Out-Null }
    if ($ownedNetwork) { docker network rm $network | Out-Null }
}
