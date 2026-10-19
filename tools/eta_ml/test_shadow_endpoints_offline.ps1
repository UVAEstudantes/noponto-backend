# Offline only: extract functions, never execute the runner body or the Docker executable.
$ErrorActionPreference='Stop'
$runner=Join-Path $PSScriptRoot 'test_shadow_disposable.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ($parseErrors | Out-String)}
foreach($name in @('Invoke-FixtureDocker','Select-D8BindingMap','Get-D8EndpointDecision','Read-D8Endpoint','Confirm-D8Endpoints')){
 $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
 if(-not $definition){throw "Missing function $name"}
 . ([scriptblock]::Create($definition.Extent.Text))
}
$script:pg='a' * 64;$script:redis='b' * 64
function docker {
 if($args.Count -ne 5 -or $args[0] -ne 'container' -or $args[1] -ne 'inspect' -or $args[2] -ne '--format' -or
  $args[4] -notin @($script:pg,$script:redis)){throw 'Unexpected Docker command'}
 if($args[3] -match '\.Env|json \.Mounts\}\}|json \.\}\}') {throw 'Inspect must not request private fields or full objects'}
 if($args[3] -notmatch 'json \.HostConfig.PortBindings' -or $args[3] -notmatch 'json \.NetworkSettings.Ports'){throw 'Both binding sources must be queried explicitly'}
 $script:queryCount++
 $global:LASTEXITCODE=$script:dockerExit
 return $script:responses[$args[4]]
}
function Assert-True($Condition,[string]$Message){if(-not $Condition){throw $Message}}
function New-State([string]$Id,[string]$Key,[string]$Port){
 return @{
  Id=$Id;Config=@{Labels=@{'noponto.fixture'='guard'};Env=@('SECRET=must-not-be-persisted')}
  HostConfig=@{PortBindings=@{ $Key=@(@{HostIp='127.0.0.1';HostPort=''}) }}
  NetworkSettings=@{Ports=@{ $Key=@(@{HostIp='127.0.0.1';HostPort=$Port}) };Networks=@{exclusive=@{NetworkID='lab-network'}}}
  Mounts=@(@{Type='tmpfs'})
 }
}
function Reset-Responses {
 $script:dockerExit=0;$script:queryCount=0
 $script:responses=@{
  $script:pg=(New-State $script:pg '5432/tcp' '45001' | ConvertTo-Json -Depth 12 -Compress)
  $script:redis=(New-State $script:redis '6379/tcp' '45002' | ConvertTo-Json -Depth 12 -Compress)
 }
}
$passed=0
foreach($case in @(
 @('IPv4DynamicLoopback','safe','ipv4_loopback_confirmed'),
 @('IPv4ZeroDynamicRequest','safe','ipv4_loopback_confirmed'),
 @('IPv4ExplicitMatchingPort','safe','ipv4_loopback_confirmed'),
 @('IPv6LoopbackUnsupported','inconclusive','ipv6_not_supported_by_fixture'),
 @('AllIPv4Interfaces','unsafe','non_loopback_binding'),
 @('AllIPv6Interfaces','unsafe','non_loopback_binding'),
 @('NonlocalAddress','unsafe','non_loopback_binding'),
 @('ConfiguredWildcard','unsafe','non_loopback_binding'),
 @('MissingRuntimeField','missing','endpoint_absent'),
 @('NullRuntimePorts','missing','endpoint_absent'),
 @('EmptyRuntimeBindings','missing','endpoint_absent'),
 @('NullRuntimeBindings','missing','endpoint_absent'),
 @('MissingConfiguredField','missing','endpoint_absent'),
 @('MalformedJson','inconclusive','malformed_inspect_response'),
 @('UnexpectedJsonArray','inconclusive','malformed_inspect_response'),
 @('ScalarBinding','inconclusive','invalid_binding_shape'),
 @('MismatchedAssignedPort','inconsistent','configured_runtime_port_mismatch'),
 @('MissingAssignedPort','inconclusive','invalid_assigned_port'),
 @('OutOfRangePort','inconclusive','invalid_assigned_port'),
 @('ReservedOperationalPort','inconclusive','invalid_assigned_port'),
 @('HiddenUnsafeSecondBinding','unsafe','non_loopback_binding'),
 @('DuplicateSafeBindings','inconclusive','ambiguous_binding_array'),
 @('UnexpectedPublishedPort','inconclusive','unexpected_published_port'),
 @('DockerQueryFailure','query_failed','docker_inspect_failed'),
 @('InspectIdentityMismatch','inconsistent','inspect_identity_mismatch')
)){
 Reset-Responses
 $state=New-State $script:pg '5432/tcp' '45001'
 switch($case[0]){
  'IPv4ZeroDynamicRequest' {$state.HostConfig.PortBindings['5432/tcp'][0].HostPort='0'}
  'IPv4ExplicitMatchingPort' {$state.HostConfig.PortBindings['5432/tcp'][0].HostPort='45001'}
  'IPv6LoopbackUnsupported' {$state.HostConfig.PortBindings['5432/tcp'][0].HostIp='::1';$state.NetworkSettings.Ports['5432/tcp'][0].HostIp='::1'}
  'AllIPv4Interfaces' {$state.NetworkSettings.Ports['5432/tcp'][0].HostIp='0.0.0.0'}
  'AllIPv6Interfaces' {$state.NetworkSettings.Ports['5432/tcp'][0].HostIp='::'}
  'NonlocalAddress' {$state.NetworkSettings.Ports['5432/tcp'][0].HostIp='192.168.1.10'}
  'ConfiguredWildcard' {$state.HostConfig.PortBindings['5432/tcp'][0].HostIp='0.0.0.0'}
  'MissingRuntimeField' {$state.NetworkSettings.Remove('Ports')}
  'NullRuntimePorts' {$state.NetworkSettings.Ports=$null}
  'EmptyRuntimeBindings' {$state.NetworkSettings.Ports['5432/tcp']=@()}
  'NullRuntimeBindings' {$state.NetworkSettings.Ports['5432/tcp']=$null}
  'MissingConfiguredField' {$state.HostConfig.Remove('PortBindings')}
  'ScalarBinding' {$state.NetworkSettings.Ports['5432/tcp']=@{HostIp='127.0.0.1';HostPort='45001'}}
  'MismatchedAssignedPort' {$state.HostConfig.PortBindings['5432/tcp'][0].HostPort='45003'}
  'MissingAssignedPort' {$state.NetworkSettings.Ports['5432/tcp'][0].HostPort=''}
  'OutOfRangePort' {$state.NetworkSettings.Ports['5432/tcp'][0].HostPort='65536'}
  'ReservedOperationalPort' {$state.NetworkSettings.Ports['5432/tcp'][0].HostPort='5432'}
  'HiddenUnsafeSecondBinding' {$state.NetworkSettings.Ports['5432/tcp']+=@{HostIp='0.0.0.0';HostPort='45001'}}
  'DuplicateSafeBindings' {$state.NetworkSettings.Ports['5432/tcp']+=@{HostIp='127.0.0.1';HostPort='45001'}}
  'UnexpectedPublishedPort' {$state.NetworkSettings.Ports['80/tcp']=@(@{HostIp='127.0.0.1';HostPort='45003'})}
  'DockerQueryFailure' {$script:dockerExit=1}
  'InspectIdentityMismatch' {$state.Id=$script:redis}
 }
 $script:responses[$script:pg]=$state | ConvertTo-Json -Depth 12 -Compress
 if($case[0] -eq 'MalformedJson'){$script:responses[$script:pg]='{invalid SECRET=must-not-be-persisted'}
 if($case[0] -eq 'UnexpectedJsonArray'){$script:responses[$script:pg]='['+$script:responses[$script:pg]+']'}
 $read=Read-D8Endpoint $script:pg '5432/tcp'
 Assert-True ($read.Diagnostic.status -eq $case[1] -and $read.Diagnostic.reason -eq $case[2]) "$($case[0]): unexpected decision $($read.Diagnostic | ConvertTo-Json -Depth 8 -Compress)"
 Assert-True ($read.Diagnostic.query_exit_code -eq $script:dockerExit -and $script:queryCount -eq 1) 'Query outcome not preserved'
 Assert-True (($read.Diagnostic | ConvertTo-Json -Depth 12) -notmatch 'SECRET|must-not-be-persisted') 'Private/raw inspect data leaked'
 $passed++;Write-Output "PASS $($case[0])"
}
Reset-Responses
$directory=Join-Path ([IO.Path]::GetTempPath()) ('noponto-d8-endpoints-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$returned=@(Confirm-D8Endpoints $script:pg $script:redis $directory)
$path=Join-Path $directory 'd8-endpoints.json'
$saved=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
Assert-True ($returned.Count -eq 2 -and $saved.resources.Count -eq 2 -and $saved.data_kind -eq 'fixture') 'Both safe states/evidence required'
Assert-True ($saved.resources[0].host_config_port_bindings.'5432/tcp'[0].HostPort -eq '' -and $saved.resources[0].network_settings_ports.'5432/tcp'[0].HostPort -eq '45001') 'Configured dynamic request must stay distinct from assigned port'
$passed++;Write-Output 'PASS SafePairPreservesBothBindingSources'
foreach($failure in @('missing','unsafe','query_failed','malformed')){
 Reset-Responses
 $state=New-State $script:pg '5432/tcp' '45001'
 if($failure -eq 'missing'){$state.NetworkSettings.Ports['5432/tcp']=$null}
 if($failure -eq 'unsafe'){$state.NetworkSettings.Ports['5432/tcp'][0].HostIp='0.0.0.0'}
 $script:responses[$script:pg]=$state | ConvertTo-Json -Depth 12 -Compress
 if($failure -eq 'query_failed'){$script:dockerExit=1}
 if($failure -eq 'malformed'){$script:responses[$script:pg]='invalid SECRET=must-not-be-persisted'}
 $rejected=$false
 try {Confirm-D8Endpoints $script:pg $script:redis $directory | Out-Null}catch{$rejected=$_.Exception.Message -match 'D8 endpoint validation failed'}
 $saved=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
 $expectedStatus=if($failure -eq 'malformed'){'inconclusive'}else{$failure}
 Assert-True ($rejected -and $saved.resources.Count -eq 2 -and $saved.resources[0].status -eq $expectedStatus) 'Rejection must occur after saving both diagnostics'
 Assert-True ((Get-Content -LiteralPath $path -Raw) -notmatch 'SECRET|must-not-be-persisted') 'Full inspect must never be saved'
 $passed++;Write-Output "PASS EvidenceWrittenBeforeEndpointRejection_$failure"
}
Reset-Responses
$rejected=$false
try {Confirm-D8Endpoints $script:pg $script:redis (Join-Path $directory 'not-created') | Out-Null}catch{$rejected=$true}
Assert-True $rejected 'Safe endpoints must not be returned when evidence cannot be written'
$passed++;Write-Output 'PASS EvidenceWriteFailureBlocksSafeEndpoints'
Write-Output "Offline endpoints: $passed passed, 0 failed. No Docker executable invoked."
