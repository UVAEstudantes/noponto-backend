# Configuration/verification only. No Docker executable, runner body or resources.
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'test_shadow_disposable.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ($parseErrors | Out-String)}
foreach($name in @('Invoke-FixtureDocker','Get-D8NetworkCreateArguments','Confirm-D8BridgeNetwork')){
 $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
 if(-not $definition){throw "Missing function $name"}
 . ([scriptblock]::Create($definition.Extent.Text))
}
function Assert-True($Condition,[string]$Message){if(-not $Condition){throw $Message}}
$script:guard='1' * 32;$script:network='a' * 64
$passed=0
function Test-Network([string]$Name,[scriptblock]$Body){& $Body;$script:passed++;Write-Output "PASS $Name"}
function docker {
 if($args.Count -ne 5 -or $args[0] -ne 'network' -or $args[1] -ne 'inspect' -or $args[2] -ne '--format' -or $args[4] -ne $script:network){throw 'Unexpected Docker invocation'}
 $global:LASTEXITCODE=$script:dockerExit
 return $script:response
}
Test-Network 'ExclusiveBridgePlanKeepsGuardAndLoopbackDefault' {
 $plan=@(Get-D8NetworkCreateArguments $script:guard "noponto-shadow-net-$script:guard")
 Assert-True (($plan -join '|') -ceq "network|create|--driver|bridge|--opt|com.docker.network.bridge.host_binding_ipv4=127.0.0.1|--label|noponto.fixture=$script:guard|noponto-shadow-net-$script:guard") 'Network arguments differ from the isolated lab plan'
 Assert-True ($plan -notcontains '--internal' -and $plan -notcontains '--network' -and $plan -notcontains 'host') 'No internal-only, host or existing network allowed'
}
Test-Network 'NetworkPlanRejectsForeignName' {
 $rejected=$false
 try {Get-D8NetworkCreateArguments $script:guard 'noponto_default' | Out-Null}catch{$rejected=$true}
 Assert-True $rejected 'Existing network name accepted'
}
Test-Network 'NetworkPlanRejectsInvalidGuard' {
 $rejected=$false
 try {Get-D8NetworkCreateArguments 'invalid' 'noponto-shadow-net-invalid' | Out-Null}catch{$rejected=$true}
 Assert-True $rejected 'Invalid ownership guard accepted'
}
Test-Network 'EgressAcknowledgementRequiredBeforeAnyDockerCommand' {
 $gate=$ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text -match 'if\(\$CaptureD8 -and -not \$AllowD8BridgeEgress\)'}
 Assert-True ($null -ne $gate) 'Missing explicit egress opt-in'
 $CaptureD8=$true;$AllowD8BridgeEgress=$false;$rejected=$false
 try {. ([scriptblock]::Create($gate.Extent.Text))}catch{$rejected=$_.Exception.Message -match 'outbound traffic'}
 Assert-True $rejected 'D8 accepted without acknowledgement'
 $AllowD8BridgeEgress=$true
 . ([scriptblock]::Create($gate.Extent.Text))
 $firstDocker=$ast.EndBlock.Statements | Where-Object {$_.Extent.Text -match '^\$context=\(docker'}
 Assert-True ($gate.Extent.EndOffset -lt $firstDocker.Extent.StartOffset) 'Opt-in must precede Docker'
}
$directory=Join-Path ([IO.Path]::GetTempPath()) ('noponto-d8-network-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
foreach($scenario in @('safe','internal','host_driver','foreign_owner','foreign_name','foreign_id','remote_scope','all_interfaces','missing_default','missing_internal','query_failure','malformed','array')){
 Test-Network "NetworkVerification_$scenario" {
  $state=@{Id=$script:network;Name="noponto-shadow-net-$script:guard";Driver='bridge';Scope='local';Internal=$false;Owner=$script:guard;Options=@{'com.docker.network.bridge.host_binding_ipv4'='127.0.0.1'}}
  $script:dockerExit=0
  switch($scenario){
   'internal' {$state.Internal=$true}
   'host_driver' {$state.Driver='host'}
   'foreign_owner' {$state.Owner='different'}
   'foreign_name' {$state.Name='noponto_default'}
   'foreign_id' {$state.Id='b' * 64}
   'remote_scope' {$state.Scope='swarm'}
   'all_interfaces' {$state.Options.'com.docker.network.bridge.host_binding_ipv4'='0.0.0.0'}
   'missing_default' {$state.Options=@{}}
   'missing_internal' {$state.Remove('Internal')}
   'query_failure' {$script:dockerExit=1}
  }
  $script:response=$state | ConvertTo-Json -Depth 5 -Compress
  if($scenario -eq 'malformed'){$script:response='invalid'}
  if($scenario -eq 'array'){$script:response='['+$script:response+']'}
  $rejected=$false
  try {Confirm-D8BridgeNetwork $script:network $script:guard $directory}catch{$rejected=$true}
  $saved=Get-Content -LiteralPath (Join-Path $directory 'd8-network.json') -Raw | ConvertFrom-Json
  Assert-True ($saved.network.query_exit_code -eq $script:dockerExit -and $saved.network.policy -eq 'exclusive-bridge-loopback-outbound-possible') 'Network evidence must state query outcome and outbound risk'
  if($scenario -eq 'safe'){Assert-True (-not $rejected -and $saved.network.status -eq 'confirmed' -and $saved.network.internal -eq $false) 'Expected confirmed non-internal bridge'}
  else{Assert-True ($rejected -and $saved.network.status -ne 'confirmed') 'Unexpected network must be rejected with evidence'}
 }
}
Write-Output "Offline network: $passed passed, 0 failed. No Docker executable invoked."
