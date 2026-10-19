param([switch]$Run,[Parameter(Mandatory=$true)][string]$ResultsDirectory,
 [switch]$CaptureD8,[switch]$PrepareCaptureSchema,[switch]$AllowD8BridgeEgress)
$ErrorActionPreference='Stop'
if(-not $Run){throw 'Use -Run explicitly; this creates only disposable local fixtures.'}
if($PrepareCaptureSchema -and -not $CaptureD8){throw '-PrepareCaptureSchema requires -CaptureD8.'}
if($CaptureD8 -and -not $PrepareCaptureSchema){throw 'D8 needs separately authorized -PrepareCaptureSchema; no resources started.'}
if($AllowD8BridgeEgress -and -not $CaptureD8){throw '-AllowD8BridgeEgress requires -CaptureD8.'}
if($CaptureD8 -and -not $AllowD8BridgeEgress){throw 'D8 requires explicit -AllowD8BridgeEgress: exclusive non-internal bridge permits outbound traffic; no resources started.'}
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
  @{schema='noponto-d8-resources-v1';data_kind='fixture';guard=$guard;containers=@($ids.ToArray());container_names=@($containerNames.ToArray());network=$network;network_name=$networkName;network_policy='exclusive-bridge-loopback-outbound-possible'} |
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
function Get-D8NetworkCreateArguments([string]$Guard,[string]$Name){
 if($Guard -cnotmatch '^[0-9a-f]{32}$' -or $Name -cne "noponto-shadow-net-$Guard"){throw 'D8 requires its exclusive network name and guard.'}
 # Host-side tests require actual published ports. This is NOT an egress-isolated network.
 return @('network','create','--driver','bridge',
  '--opt','com.docker.network.bridge.host_binding_ipv4=127.0.0.1',
  '--label',"noponto.fixture=$Guard",$Name)
}
function Confirm-D8BridgeNetwork([string]$Reference,[string]$Guard,[string]$Directory){
 $format='{"Id":{{json .Id}},"Name":{{json .Name}},"Driver":{{json .Driver}},"Scope":{{json .Scope}},"Internal":{{json .Internal}},"Owner":{{json (index .Labels "noponto.fixture")}},"Options":{{json .Options}}}'
 $query=Invoke-FixtureDocker @('network','inspect','--format',$format,$Reference)
 $diagnostic=[ordered]@{network=$Reference;policy='exclusive-bridge-loopback-outbound-possible';query_exit_code=$query.Code;status='query_failed';driver=$null;scope=$null;internal=$null;default_host_ip=$null}
 if($query.Code -eq 0){
  try {
   $state=($query.Output -join "`n") | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
   $diagnostic.status='rejected'
   if($state -is [array] -or $state -isnot [pscustomobject]){throw 'Unexpected network inspect shape'}
   $diagnostic.driver=$(if($state.Driver -eq 'bridge'){'bridge'}else{'<unexpected>'})
   $diagnostic.scope=$(if($state.Scope -eq 'local'){'local'}else{'<unexpected>'})
   $diagnostic.internal=$(if($state.Internal -is [bool]){$state.Internal}else{$null})
   $diagnostic.default_host_ip=$(if($state.Options.'com.docker.network.bridge.host_binding_ipv4' -eq '127.0.0.1'){'127.0.0.1'}else{'<unexpected>'})
   if($state.Id -ceq $Reference -and $state.Name -ceq "noponto-shadow-net-$Guard" -and $state.Owner -ceq $Guard -and
    $diagnostic.driver -eq 'bridge' -and $diagnostic.scope -eq 'local' -and $state.Internal -is [bool] -and -not $state.Internal -and $diagnostic.default_host_ip -eq '127.0.0.1'){$diagnostic.status='confirmed'}
  }catch{$diagnostic.status='malformed'}
 }
 @{schema='noponto-d8-network-v1';data_kind='fixture';network=$diagnostic} | ConvertTo-Json -Depth 5 |
  Set-Content -LiteralPath (Join-Path $Directory 'd8-network.json') -Encoding utf8
 if($diagnostic.status -ne 'confirmed'){throw "D8 exclusive bridge verification failed: $($diagnostic.status)"}
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
function Select-D8BindingMap($Map){
 if($null -eq $Map){return $null}
 if($Map -is [array] -or $Map -isnot [pscustomobject]){return @{invalid_shape=$true}}
 $selected=[ordered]@{}
 foreach($property in $Map.PSObject.Properties){
  if($property.Name -notmatch '^\d{1,5}/(tcp|udp|sctp)$'){return @{invalid_shape=$true}}
  if($null -eq $property.Value){$selected[$property.Name]=$null;continue}
  if($property.Value -isnot [array]){return @{invalid_shape=$true}}
  $selected[$property.Name]=@(foreach($binding in $property.Value){
   $ip=$binding.HostIp;$port=$binding.HostPort
   # Persist only binding fields, never raw inspect, stderr, environment or arbitrary strings.
   [ordered]@{
    HostIp=$(if($ip -is [string] -and $ip -match '^[0-9a-fA-F:.]+$'){$ip}else{'<invalid>'})
    HostPort=$(if($port -is [string] -and $port -match '^\d{0,5}$'){$port}else{'<invalid>'})
   }
  })
 }
 return $selected
}
function Get-D8EndpointDecision($Configured,$Published,[string]$PortKey){
 if(($Configured -and $Configured.Contains('invalid_shape')) -or ($Published -and $Published.Contains('invalid_shape'))){return @{status='inconclusive';reason='invalid_binding_shape'}}
 if($null -eq $Configured -or $null -eq $Published -or -not $Configured.Contains($PortKey) -or -not $Published.Contains($PortKey) -or
  @($Configured[$PortKey]).Count -eq 0 -or @($Published[$PortKey]).Count -eq 0 -or $null -eq $Configured[$PortKey] -or $null -eq $Published[$PortKey]){return @{status='missing';reason='endpoint_absent'}}
 foreach($map in @($Configured,$Published)){
  foreach($key in $map.Keys){
   if($key -ne $PortKey -and @($map[$key] | Where-Object {$null -ne $_}).Count -gt 0){return @{status='inconclusive';reason='unexpected_published_port'}}
  }
  foreach($binding in $map[$PortKey]){
   if($binding.HostIp -eq '<invalid>' -or $binding.HostPort -eq '<invalid>'){return @{status='inconclusive';reason='invalid_binding_value'}}
   if($binding.HostIp -eq '::1'){return @{status='inconclusive';reason='ipv6_not_supported_by_fixture'}}
   if($binding.HostIp -cne '127.0.0.1'){return @{status='unsafe';reason='non_loopback_binding'}}
  }
 }
 if($Configured[$PortKey].Count -ne 1 -or $Published[$PortKey].Count -ne 1){return @{status='inconclusive';reason='ambiguous_binding_array'}}
 $requested=$Configured[$PortKey][0].HostPort;$actual=$Published[$PortKey][0].HostPort
 if($actual -notmatch '^\d+$' -or [int]$actual -lt 1024 -or [int]$actual -gt 65535 -or [int]$actual -in @(5432,6379)){
  return @{status='inconclusive';reason='invalid_assigned_port'}
 }
 # Empty/zero HostConfig port requests dynamic allocation; only runtime Ports proves it exists.
 if($requested -notin @('','0') -and $requested -cne $actual){return @{status='inconsistent';reason='configured_runtime_port_mismatch'}}
 return @{status='safe';reason='ipv4_loopback_confirmed';port=$actual}
}
function Read-D8Endpoint([string]$Reference,[string]$PortKey){
 # Deliberately query no environment, credentials, coordinates or GPS payload.
 $format='{"Id":{{json .Id}},"Config":{"Labels":{"noponto.fixture":{{json (index .Config.Labels "noponto.fixture")}}}},"HostConfig":{"PortBindings":{{json .HostConfig.PortBindings}}},"NetworkSettings":{"Ports":{{json .NetworkSettings.Ports}},"Networks":{{json .NetworkSettings.Networks}}},"Mounts":[{{range $i,$m := .Mounts}}{{if $i}},{{end}}{"Type":{{json $m.Type}}}{{end}}]}'
 $query=Invoke-FixtureDocker @('container','inspect','--format',$format,$Reference)
 $diagnostic=[ordered]@{resource=$Reference;container_port=$PortKey;query_exit_code=$query.Code;query_result='failed';host_config_port_bindings=$null;network_settings_ports=$null;status='query_failed';reason='docker_inspect_failed'}
 $state=$null
 if($query.Code -eq 0){
  try {
   $state=($query.Output -join "`n") | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
   if($state -is [array] -or $state -isnot [pscustomobject]){throw 'Unexpected inspect shape'}
   $diagnostic.query_result='parsed'
   $diagnostic.host_config_port_bindings=Select-D8BindingMap $state.HostConfig.PortBindings
   $diagnostic.network_settings_ports=Select-D8BindingMap $state.NetworkSettings.Ports
   $decision=Get-D8EndpointDecision $diagnostic.host_config_port_bindings $diagnostic.network_settings_ports $PortKey
   $diagnostic.status=$decision.status;$diagnostic.reason=$decision.reason
   if($state.Id -cne $Reference){$diagnostic.status='inconsistent';$diagnostic.reason='inspect_identity_mismatch'}
  } catch {$diagnostic.query_result='malformed';$diagnostic.status='inconclusive';$diagnostic.reason='malformed_inspect_response'}
 }
 [pscustomobject]@{State=$state;Diagnostic=$diagnostic}
}
function Confirm-D8Endpoints([string]$Postgres,[string]$Redis,[string]$Directory){
 $pgEndpoint=Read-D8Endpoint $Postgres '5432/tcp'
 $redisEndpoint=Read-D8Endpoint $Redis '6379/tcp'
 $diagnostics=@($pgEndpoint.Diagnostic,$redisEndpoint.Diagnostic)
 # Evidence must be written successfully before rejection or use of any endpoint.
 @{schema='noponto-d8-endpoints-v1';data_kind='fixture';resources=$diagnostics} |
  ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Directory 'd8-endpoints.json') -Encoding utf8
 if(@($diagnostics | Where-Object {$_.status -ne 'safe'}).Count -gt 0){
  throw ('D8 endpoint validation failed: ' + (($diagnostics | ForEach-Object {"$($_.container_port)=$($_.status)/$($_.reason)"}) -join '; '))
 }
 return @($pgEndpoint.State,$redisEndpoint.State)
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
function Remove-D8PrepareAuthorization {
 $key='NOPONTO_SHADOW_FIXTURE_D8_PREPARE'
 if(Test-Path -LiteralPath "Env:$key" -ErrorAction Stop){Remove-Item -LiteralPath "Env:$key" -ErrorAction Stop}
 # Empty is still present to the C# capture guard. Verify the actual inherited process environment.
 if($null -ne [Environment]::GetEnvironmentVariable($key,'Process') -or [Environment]::GetEnvironmentVariables('Process').Contains($key)){
  throw 'D8 preparation authorization removal not proved; capture not started.'
 }
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
  $networkArguments=Get-D8NetworkCreateArguments $guard $networkName
  $network=(docker @networkArguments).Trim()
  if($LASTEXITCODE -ne 0){throw 'D8 isolated network creation failed'}
  Save-D8Resources
  Confirm-D8BridgeNetwork $network $guard $ResultsDirectory
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
 if($CaptureD8){
  $pgState,$redisState=Confirm-D8Endpoints $pg $redis $ResultsDirectory
 }else{
  $pgState=(docker inspect $pg | ConvertFrom-Json)[0]
  $redisState=(docker inspect $redis | ConvertFrom-Json)[0]
 }
 if($pgState.Config.Labels.'noponto.fixture' -ne $guard -or $redisState.Config.Labels.'noponto.fixture' -ne $guard){throw 'Fixture ownership mismatch'}
 if($CaptureD8){
  foreach($state in @($pgState,$redisState)){
   if(@($state.Mounts | Where-Object { $_.Type -ne 'tmpfs' }).Count -ne 0){throw 'D8 refuses volumes or host mounts.'}
   $attachedNetworks=@($state.NetworkSettings.Networks.PSObject.Properties)
   if($attachedNetworks.Count -ne 1 -or $attachedNetworks[0].Value.NetworkID -cne $network){throw 'D8 refuses shared or unexpected networks.'}
  }
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
  Remove-D8PrepareAuthorization
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
 foreach($key in $previous.Keys){
  $restoreValue=$previous[$key]
  if($key -eq 'NOPONTO_SHADOW_FIXTURE_D8_PREPARE' -and $null -eq $restoreValue){$restoreValue=[NullString]::Value}
  [Environment]::SetEnvironmentVariable($key,$restoreValue,'Process')
 }
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
