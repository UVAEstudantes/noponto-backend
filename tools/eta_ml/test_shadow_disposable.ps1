param([switch]$Run,[Parameter(Mandatory=$true)][string]$ResultsDirectory,
 [switch]$CaptureD8,[switch]$PrepareCaptureSchema)
$ErrorActionPreference='Stop'
if(-not $Run){throw 'Use -Run explicitly; this creates only disposable local fixtures.'}
if($PrepareCaptureSchema -and -not $CaptureD8){throw '-PrepareCaptureSchema requires -CaptureD8.'}
if($CaptureD8 -and -not $PrepareCaptureSchema){throw 'D8 needs separately authorized -PrepareCaptureSchema; no resources started.'}
if(-not [IO.Path]::IsPathFullyQualified($ResultsDirectory) -or (Test-Path -LiteralPath $ResultsDirectory)){throw 'Use a new absolute results directory.'}
if($CaptureD8){
 if($ResultsDirectory.StartsWith('\\')){throw 'D8 output must be local.'}
 for($parent=[IO.DirectoryInfo]::new($ResultsDirectory).Parent;$null -ne $parent;$parent=$parent.Parent){
  if($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'D8 output cannot traverse links.'}
 }
}
function Confirm-D8Trx([string]$Path,[string]$Method){
 [xml]$trx=Get-Content -LiteralPath $Path -Raw
 $counts=$trx.TestRun.ResultSummary.Counters
 $results=@($trx.TestRun.Results.UnitTestResult)
 $definition=@($trx.TestRun.TestDefinitions.UnitTest)
 if($counts.total -ne '1' -or $counts.passed -ne '1' -or $results.Count -ne 1 -or $results[0].outcome -ne 'Passed' -or $definition.Count -ne 1){throw 'D8 TRX rejected: expected exactly one passed test.'}
 if($definition[0].TestMethod.className.Split(',')[0] -ne 'NoPonto.Tests.HistoricalEtaCaptureDisposableTests' -or $definition[0].TestMethod.name -ne $Method){throw 'D8 TRX rejected: unexpected test.'}
}
function Save-D8Resources {
 if($CaptureD8){
  @{schema='noponto-d8-resources-v1';data_kind='fixture';guard=$guard;containers=@($ids.ToArray());container_names=@($containerNames.ToArray());network=$network;network_name=$networkName} |
   ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'd8-resources.json') -Encoding utf8
 }
}
function Invoke-FixtureDocker([string[]]$Arguments){
 # Native failures are data here, including when the caller enables native error promotion.
 $ErrorActionPreference='Continue'
 $PSNativeCommandUseErrorActionPreference=$false
 try {
  $output=@(& docker @Arguments 2>&1)
  [pscustomobject]@{Code=$LASTEXITCODE;Output=$output}
 } catch { [pscustomobject]@{Code=-1;Output=@($_.Exception.Message)} }
}
function Get-FixturePresence([string]$Kind,[string]$Reference){
 $format=if($Kind -eq 'container'){'{{.ID}}|{{.Names}}'}else{'{{.ID}}|{{.Name}}'}
 $arguments=@($Kind,'ls','--no-trunc','--format',$format)
 if($Kind -eq 'container'){$arguments+= '--all'}
 $query=Invoke-FixtureDocker $arguments
 if($query.Code -ne 0){throw 'Resource inventory failed; absence cannot be proved.'}
 foreach($line in $query.Output){if(([string]$line).Split('|') -ccontains $Reference){return $true}}
 return $false
}
function Complete-FixtureCleanup([string[]]$Containers,[string]$Network,[string]$Guard){
 $resources=@($Containers | ForEach-Object { @{Kind='container';Reference=$_} })
 if($Network){$resources+=@{Kind='network';Reference=$Network}}
 foreach($resource in $resources){
  $kind=$resource.Kind;$reference=$resource.Reference
  $status='ownership_inconclusive';$detail=$null;$success=$false
  try {
   if(-not (Get-FixturePresence $kind $reference)){$status='absent';$success=$true}
   else {
    $format=if($kind -eq 'container'){'{{index .Config.Labels "noponto.fixture"}}'}else{'{{index .Labels "noponto.fixture"}}'}
    $owner=Invoke-FixtureDocker @($kind,'inspect','--format',$format,$reference)
    if($owner.Code -ne 0 -or $owner.Output.Count -ne 1 -or ([string]$owner.Output[0]).Trim() -cne $Guard){
     # Auto-remove may race with inspect. A successful inventory must prove absence.
     if(-not (Get-FixturePresence $kind $reference)){$status='absent';$success=$true}
     else {$detail='Ownership unproved; no mutation attempted.'}
    }else{
     $status='state_unverified'
     $operation=if($kind -eq 'container'){@('container','stop','--timeout','5',$reference)}else{@('network','rm',$reference)}
     $result=Invoke-FixtureDocker $operation
     if($result.Code -ne 0){$status='cleanup_failed';$detail="Docker cleanup exit $($result.Code)"}
     $present=Get-FixturePresence $kind $reference
     if($result.Code -ne 0){$status='cleanup_failed';$detail="Docker cleanup exit $($result.Code); present=$present"}
     elseif(-not $present){$status='removed';$success=$true}
     elseif($kind -eq 'container'){
      $state=Invoke-FixtureDocker @('container','inspect','--format','{{.State.Running}}',$reference)
      if($state.Code -eq 0 -and $state.Output.Count -eq 1 -and ([string]$state.Output[0]).Trim() -ceq 'false'){$status='stopped';$success=$true}
      else {$status='active_or_unverified';$detail='Container termination not proved.'}
     }else{$status='still_present';$detail='Network removal not proved.'}
    }
   }
  }catch{$detail=(@($detail,$_.Exception.Message) | Where-Object {$_}) -join '; '}
  [pscustomobject]@{kind=$kind;reference=$reference;status=$status;success=$success;detail=$detail}
 }
}
function Get-FixtureExitCode([int]$TestExit,$ExperimentError,[object[]]$Cleanup){
 if($ExperimentError -or @($Cleanup | Where-Object { -not $_.success }).Count -gt 0){return 1}
 return $TestExit
}
$context=(docker context show).Trim()
if($LASTEXITCODE -ne 0 -or $context -ne 'desktop-linux'){throw 'Requires local Docker Desktop desktop-linux; remote contexts forbidden.'}
$endpoint=(docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if($endpoint -notlike 'npipe:*'){throw 'Expected local Windows named pipe.'}
foreach($image in @('postgis/postgis:16-3.4','redis:7.4-alpine')){
 docker image inspect $image --format '{{.Id}}' | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Fixture image missing; no pull/build is permitted by this harness.'}
}
$guard=[guid]::NewGuid().ToString('N')
$database=if($CaptureD8){"noponto_d8_$guard"}else{'noponto_shadow_fixture'}
$network=$null
$ids=[Collections.Generic.List[string]]::new()
$containerNames=[Collections.Generic.List[string]]::new()
$networkName=$null
$previous=@{}
foreach($key in @('NOPONTO_SHADOW_FIXTURE_POSTGRES','NOPONTO_SHADOW_FIXTURE_REDIS','NOPONTO_SHADOW_FIXTURE_GUARD','NOPONTO_SHADOW_FIXTURE_D8_OUTPUT','NOPONTO_SHADOW_FIXTURE_D8_PREPARE')){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
$testExit=1
$experimentError=$null
$cleanup=@()
try {
 $pgIsolation=@();$redisIsolation=@()
 if($CaptureD8){
  New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
  $acl=Get-Acl -LiteralPath $ResultsDirectory
  $acl.SetAccessRuleProtection($true,$false)
  $user=[Security.Principal.WindowsIdentity]::GetCurrent().User
  $acl.SetOwner($user)
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
  Set-Acl -LiteralPath $ResultsDirectory -AclObject $acl
  Save-D8Resources
  $networkName="noponto-shadow-net-$guard"
  Save-D8Resources
  $network=(docker network create --internal --label "noponto.fixture=$guard" $networkName).Trim()
  if($LASTEXITCODE -ne 0){throw 'D8 isolated network creation failed'}
  Save-D8Resources
  $pgIsolation=@('--network',$network,'--tmpfs','/var/lib/postgresql/data:rw,nosuid,size=256m')
  $redisIsolation=@('--network',$network,'--tmpfs','/data:rw,nosuid,size=64m')
 }
 $containerNames.Add("noponto-shadow-pg-$guard")
 Save-D8Resources
 $pg=(docker run -d --rm --pull never --name "noponto-shadow-pg-$guard" --label "noponto.fixture=$guard" --memory 384m --cpus 1 --pids-limit 128 @pgIsolation -e POSTGRES_HOST_AUTH_METHOD=trust -e "POSTGRES_DB=$database" -p 127.0.0.1::5432 postgis/postgis:16-3.4).Trim()
 if($LASTEXITCODE -ne 0){throw 'PostGIS fixture failed'};$ids.Add($pg)
 Save-D8Resources
 $containerNames.Add("noponto-shadow-redis-$guard")
 Save-D8Resources
 $redis=(docker run -d --rm --pull never --name "noponto-shadow-redis-$guard" --label "noponto.fixture=$guard" --memory 96m --cpus 0.5 --pids-limit 64 @redisIsolation -p 127.0.0.1::6379 redis:7.4-alpine redis-server --save '' --appendonly no).Trim()
 if($LASTEXITCODE -ne 0){throw 'Redis fixture failed'};$ids.Add($redis)
 Save-D8Resources
 $ready=$false
 for($i=0;$i -lt 120;$i++){
  docker exec $pg pg_isready -h 127.0.0.1 -U postgres -d $database 2>$null | Out-Null
  if($LASTEXITCODE -eq 0){$ready=$true;break};Start-Sleep -Seconds 1
 }
 if(-not $ready){if(-not (Test-Path -LiteralPath $ResultsDirectory)){New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null}; docker logs --tail 30 $pg 2>&1 | Set-Content (Join-Path $ResultsDirectory 'fixture-bootstrap.log'); throw 'PostGIS fixture readiness timeout; only fixture bootstrap log saved.'}
 "CREATE TABLE noponto_shadow_fixture_guard(token text NOT NULL); INSERT INTO noponto_shadow_fixture_guard VALUES ('$guard');" | docker exec -i $pg psql -U postgres -d $database -v ON_ERROR_STOP=1 | Out-Null
 if($LASTEXITCODE -ne 0){throw 'PostGIS guard failed'}
 docker exec $redis redis-cli SET fixture:guard $guard | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Redis guard failed'}
 $pgState=(docker inspect $pg | ConvertFrom-Json)[0]
 $redisState=(docker inspect $redis | ConvertFrom-Json)[0]
 if($pgState.Config.Labels.'noponto.fixture' -ne $guard -or $redisState.Config.Labels.'noponto.fixture' -ne $guard){throw 'Fixture ownership mismatch'}
 if($CaptureD8){
  foreach($state in @($pgState,$redisState)){
   if(@($state.Mounts | Where-Object { $_.Type -ne 'tmpfs' }).Count -ne 0){throw 'D8 refuses volumes or host mounts.'}
   if(@($state.NetworkSettings.Networks.PSObject.Properties).Count -ne 1){throw 'D8 refuses shared networks.'}
  }
  if($pgState.NetworkSettings.Ports.'5432/tcp'[0].HostIp -ne '127.0.0.1' -or $redisState.NetworkSettings.Ports.'6379/tcp'[0].HostIp -ne '127.0.0.1'){throw 'D8 refuses non-loopback published endpoints.'}
 }
 $pgPort=$pgState.NetworkSettings.Ports.'5432/tcp'[0].HostPort
 $redisPort=$redisState.NetworkSettings.Ports.'6379/tcp'[0].HostPort
 $env:NOPONTO_SHADOW_FIXTURE_POSTGRES="Host=127.0.0.1;Port=$pgPort;Database=$database;Username=postgres;Timeout=5;Command Timeout=5"
 $env:NOPONTO_SHADOW_FIXTURE_REDIS="127.0.0.1:$redisPort"
 $env:NOPONTO_SHADOW_FIXTURE_GUARD=$guard
 $project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../NoPonto/NoPonto.csproj'))
 if($CaptureD8){
  $env:NOPONTO_SHADOW_FIXTURE_D8_OUTPUT=Join-Path $ResultsDirectory "capture-$guard"
  $env:NOPONTO_SHADOW_FIXTURE_D8_PREPARE=$guard
  dotnet test $project --no-restore --filter FullyQualifiedName=NoPonto.Tests.HistoricalEtaCaptureDisposableTests.D8_PrepareSchemaExplicitly --logger 'trx;LogFileName=d8-prepare.trx' --results-directory $ResultsDirectory
  if($LASTEXITCODE -ne 0){throw 'D8 explicit schema preparation failed; capture not started.'}
  Confirm-D8Trx (Join-Path $ResultsDirectory 'd8-prepare.trx') 'D8_PrepareSchemaExplicitly'
  [Environment]::SetEnvironmentVariable('NOPONTO_SHADOW_FIXTURE_D8_PREPARE',$null,'Process')
  dotnet test $project --no-build --no-restore --filter FullyQualifiedName=NoPonto.Tests.HistoricalEtaCaptureDisposableTests.D8_CaptureThroughActualGpsCasAndShadow --logger 'trx;LogFileName=d8-capture.trx' --results-directory $ResultsDirectory
  if($LASTEXITCODE -ne 0){throw 'D8 capture failed; evidence retained and owned containers will stop.'}
  Confirm-D8Trx (Join-Path $ResultsDirectory 'd8-capture.trx') 'D8_CaptureThroughActualGpsCasAndShadow'
 }else{
  dotnet test $project --no-restore --filter FullyQualifiedName~HistoricalEtaDisposableIntegrationTests --logger 'trx;LogFileName=disposable.trx' --results-directory $ResultsDirectory
 }
 $testExit=$LASTEXITCODE
} catch {
 $experimentError=$_
} finally {
 foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}
 # Planned exclusive names also cover a creation failure before Docker returned an ID.
 $cleanup=@(Complete-FixtureCleanup $containerNames.ToArray() $networkName $guard)
 if($CaptureD8 -and (Test-Path -LiteralPath $ResultsDirectory)){
  try {
   @{data_kind='fixture';guard=$guard;experiment_error=$(if($experimentError){$experimentError.Exception.Message});resources=$cleanup} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'd8-cleanup.json') -Encoding utf8
  }catch{
   $cleanup+= [pscustomobject]@{kind='evidence';reference='d8-cleanup.json';status='write_failed';success=$false;detail=$_.Exception.Message}
  }
 }
}
if($experimentError){Write-Error -ErrorRecord $experimentError -ErrorAction Continue}
foreach($failure in @($cleanup | Where-Object { -not $_.success })){
 Write-Error "Fixture cleanup: $($failure.kind) $($failure.reference): $($failure.status); $($failure.detail)" -ErrorAction Continue
}
exit (Get-FixtureExitCode $testExit $experimentError $cleanup)
