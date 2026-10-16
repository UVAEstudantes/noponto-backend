param([switch]$Run,[Parameter(Mandatory=$true)][string]$ResultsDirectory)
$ErrorActionPreference='Stop'
if(-not $Run){throw 'Use -Run explicitly; this creates only disposable local fixtures.'}
if(-not [IO.Path]::IsPathFullyQualified($ResultsDirectory) -or (Test-Path -LiteralPath $ResultsDirectory)){throw 'Use a new absolute results directory.'}
$context=(docker context show).Trim()
if($LASTEXITCODE -ne 0 -or $context -ne 'desktop-linux'){throw 'Requires local Docker Desktop desktop-linux; remote contexts forbidden.'}
$endpoint=(docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if($endpoint -notlike 'npipe:*'){throw 'Expected local Windows named pipe.'}
foreach($image in @('postgis/postgis:16-3.4','redis:7.4-alpine')){
 docker image inspect $image --format '{{.Id}}' | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Fixture image missing; no pull/build is permitted by this harness.'}
}
$guard=[guid]::NewGuid().ToString('N')
$ids=[Collections.Generic.List[string]]::new()
$previous=@{}
foreach($key in @('NOPONTO_SHADOW_FIXTURE_POSTGRES','NOPONTO_SHADOW_FIXTURE_REDIS','NOPONTO_SHADOW_FIXTURE_GUARD')){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
$testExit=1
try {
 $pg=(docker run -d --rm --pull never --name "noponto-shadow-pg-$guard" --label "noponto.fixture=$guard" --memory 384m --cpus 1 --pids-limit 128 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=noponto_shadow_fixture -p 127.0.0.1::5432 postgis/postgis:16-3.4).Trim()
 if($LASTEXITCODE -ne 0){throw 'PostGIS fixture failed'};$ids.Add($pg)
 $redis=(docker run -d --rm --pull never --name "noponto-shadow-redis-$guard" --label "noponto.fixture=$guard" --memory 96m --cpus 0.5 --pids-limit 64 -p 127.0.0.1::6379 redis:7.4-alpine redis-server --save '' --appendonly no).Trim()
 if($LASTEXITCODE -ne 0){throw 'Redis fixture failed'};$ids.Add($redis)
 $ready=$false
 for($i=0;$i -lt 120;$i++){
  docker exec $pg pg_isready -h 127.0.0.1 -U postgres -d noponto_shadow_fixture 2>$null | Out-Null
  if($LASTEXITCODE -eq 0){$ready=$true;break};Start-Sleep -Seconds 1
 }
 if(-not $ready){New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null; docker logs --tail 30 $pg 2>&1 | Set-Content (Join-Path $ResultsDirectory 'fixture-bootstrap.log'); throw 'PostGIS fixture readiness timeout; only fixture bootstrap log saved.'}
 "CREATE TABLE noponto_shadow_fixture_guard(token text NOT NULL); INSERT INTO noponto_shadow_fixture_guard VALUES ('$guard');" | docker exec -i $pg psql -U postgres -d noponto_shadow_fixture -v ON_ERROR_STOP=1 | Out-Null
 if($LASTEXITCODE -ne 0){throw 'PostGIS guard failed'}
 docker exec $redis redis-cli SET fixture:guard $guard | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Redis guard failed'}
 $pgState=(docker inspect $pg | ConvertFrom-Json)[0]
 $redisState=(docker inspect $redis | ConvertFrom-Json)[0]
 if($pgState.Config.Labels.'noponto.fixture' -ne $guard -or $redisState.Config.Labels.'noponto.fixture' -ne $guard){throw 'Fixture ownership mismatch'}
 $pgPort=$pgState.NetworkSettings.Ports.'5432/tcp'[0].HostPort
 $redisPort=$redisState.NetworkSettings.Ports.'6379/tcp'[0].HostPort
 $env:NOPONTO_SHADOW_FIXTURE_POSTGRES="Host=127.0.0.1;Port=$pgPort;Database=noponto_shadow_fixture;Username=postgres;Timeout=5;Command Timeout=5"
 $env:NOPONTO_SHADOW_FIXTURE_REDIS="127.0.0.1:$redisPort"
 $env:NOPONTO_SHADOW_FIXTURE_GUARD=$guard
 $project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../NoPonto/NoPonto.csproj'))
 dotnet test $project --no-restore --filter FullyQualifiedName~HistoricalEtaDisposableIntegrationTests --logger 'trx;LogFileName=disposable.trx' --results-directory $ResultsDirectory
 $testExit=$LASTEXITCODE
} finally {
 foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}
 foreach($id in $ids){
  $owner=docker inspect --format '{{index .Config.Labels "noponto.fixture"}}' $id 2>$null
  if($LASTEXITCODE -eq 0 -and $owner.Trim() -eq $guard){docker stop --timeout 5 $id | Out-Null}
  else{Write-Warning 'Fixture ownership unavailable; no cleanup attempted for that container.'}
 }
}
exit $testExit
