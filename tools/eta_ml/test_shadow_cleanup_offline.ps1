# Offline behavioral tests: load only function ASTs, never execute the runner.
$ErrorActionPreference='Stop'
$runner=Join-Path $PSScriptRoot 'test_shadow_disposable.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ($parseErrors | Out-String)}
foreach($name in @('Invoke-FixtureDocker','Get-FixturePresence','Complete-FixtureCleanup','Get-FixtureExitCode')){
 $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
 if(-not $definition){throw "Missing runner function $name"}
 . ([scriptblock]::Create($definition.Extent.Text))
}

# Any unrecognised invocation throws; this function shadows Docker completely.
function docker {
 $call=@($args);$script:calls.Add(($call -join ' '));$global:LASTEXITCODE=0
 $kind=$call[0];$verb=$call[1];$reference=$call[-1]
 if($verb -eq 'ls'){
  if($script:scenario -eq 'inventory_failure' -or ($script:scenario -in @('stop_failure_inventory','final_inventory_failure') -and @($script:calls | Where-Object {$_ -like 'container stop *'}).Count -gt 0)){$global:LASTEXITCODE=1;return 'daemon unavailable'}
  if($kind -eq 'container'){
   foreach($protected in @('transporte_redis','transporte_postgres','auth-postgres','auth-redis')){"protected-id|$protected"}
   if($script:containerPresent){'fixture-id|fixture-container'}
  }elseif($kind -eq 'network'){if($script:networkPresent){'network-id|fixture-network'}}
  else{throw 'Unexpected resource kind'}
 }elseif($verb -eq 'inspect'){
  if($call[3] -eq '{{.State.Running}}'){return ([string]$script:running).ToLowerInvariant()}
  if($script:scenario -eq 'ownership_inconclusive'){$global:LASTEXITCODE=1;return 'inspect unavailable'}
  if($script:scenario -eq 'foreign_owner'){return 'different-guard'}
  return 'fixture-guard'
 }elseif($kind -eq 'container' -and $verb -eq 'stop'){
  if($reference -ne 'fixture-container'){throw 'Attempt to stop protected resource'}
  if($script:scenario -in @('stop_failure','stop_failure_inventory')){$global:LASTEXITCODE=1;return 'stop failed'}
  if($script:scenario -eq 'stop_failure_removed'){$script:containerPresent=$false;$global:LASTEXITCODE=1;return 'failed despite removal'}
  if($script:scenario -ne 'still_active'){$script:running=$false}
  if($script:scenario -notin @('still_active','stopped')){$script:containerPresent=$false}
  return $reference
 }elseif($kind -eq 'network' -and $verb -eq 'rm'){
  if($reference -ne 'fixture-network'){throw 'Attempt to remove foreign network'}
  if($script:scenario -eq 'network_failure'){$global:LASTEXITCODE=1;return 'network removal failed'}
  if($script:scenario -ne 'network_remains'){$script:networkPresent=$false}
  return $reference
 }else{throw "Unexpected Docker invocation: $($call -join ' ')"}
}
function Assert-True($Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Reset-Fixture([string]$Scenario){
 $script:scenario=$Scenario;$script:containerPresent=$true;$script:networkPresent=$true;$script:running=$true
 $script:calls=[Collections.Generic.List[string]]::new()
}
$passed=0
function Test-Cleanup([string]$Name,[scriptblock]$Body){
 & $Body
 $script:passed++
 Write-Output "PASS $Name"
}
Test-Cleanup 'CleanupSuccessRemovesOnlyOwnedResources' {
 Reset-Fixture 'success'
 $result=@(Complete-FixtureCleanup @('fixture-container') 'fixture-network' 'fixture-guard')
 Assert-True ($result.Count -eq 2 -and @($result | Where-Object status -ne 'removed').Count -eq 0) 'Both resources must be removed'
 Assert-True ((Get-FixtureExitCode 0 $null $result) -eq 0) 'Clean success must exit zero'
 Assert-True (-not $script:containerPresent -and -not $script:networkPresent) 'Mock resources remained'
 Assert-True (@($script:calls | Where-Object {$_ -match 'stop| rm '}).Count -eq 2) 'Expected exactly two owned mutations'
}
Test-Cleanup 'ProvenAbsenceDoesNotMutate' {
 Reset-Fixture 'absent';$script:containerPresent=$false;$script:networkPresent=$false
 $result=@(Complete-FixtureCleanup @('fixture-container') 'fixture-network' 'fixture-guard')
 Assert-True (@($result | Where-Object status -ne 'absent').Count -eq 0) 'Absence must be proved by inventory'
 Assert-True ($script:calls.Count -eq 2) 'Absent resources must not be inspected or mutated'
}
Test-Cleanup 'StoppedContainerMeetsTerminationContract' {
 Reset-Fixture 'stopped'
 $result=@(Complete-FixtureCleanup @('fixture-container') $null 'fixture-guard')
 Assert-True ($result[0].status -eq 'stopped' -and $result[0].success) 'Stopped state must be verified'
}
foreach($case in @(
 @('stop_failure','cleanup_failed'),@('stop_failure_removed','cleanup_failed'),
 @('network_failure','cleanup_failed'),@('still_active','active_or_unverified'),
 @('network_remains','still_present'),@('ownership_inconclusive','ownership_inconclusive'),
 @('foreign_owner','ownership_inconclusive'),@('inventory_failure','ownership_inconclusive')
 @('stop_failure_inventory','cleanup_failed'),@('final_inventory_failure','state_unverified')
)){
 Test-Cleanup $case[0] {
  Reset-Fixture $case[0]
  $result=@(Complete-FixtureCleanup @('fixture-container') 'fixture-network' 'fixture-guard')
  Assert-True (@($result | Where-Object status -eq $case[1]).Count -gt 0) "Missing status $($case[1])"
  Assert-True ((Get-FixtureExitCode 0 $null $result) -ne 0) 'Cleanup uncertainty must override test success'
  Assert-True ($result.Count -eq 2) 'One failure must not prevent checking the next resource'
  if($case[0] -eq 'stop_failure_inventory'){
   Assert-True ($result[0].detail -match 'Docker cleanup exit 1' -and $result[0].detail -match 'absence cannot be proved') 'Command failure and failed final verification must both survive'
  }
  if($case[0] -in @('foreign_owner','ownership_inconclusive','inventory_failure')){
   Assert-True (@($script:calls | Where-Object {$_ -match 'stop| rm '}).Count -eq 0) 'Unproved ownership must never mutate'
  }
 }
}
Test-Cleanup 'OriginalFailureSurvivesFinallyAndCleanupFailure' {
 Reset-Fixture 'stop_failure'
 $outerTry=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[-1]
 $previous=@{};$containerNames=[Collections.Generic.List[string]]::new();$containerNames.Add('fixture-container')
 $networkName='fixture-network';$guard='fixture-guard';$CaptureD8=$false;$experimentError=$null;$testExit=0
 # Execute the actual catch/finally blocks, substituting only the operational try body.
 . ([scriptblock]::Create('try { throw "original experiment failure" } ' + $outerTry.CatchClauses[0].Extent.Text + ' finally ' + $outerTry.Finally.Extent.Text))
 Assert-True ($experimentError.Exception.Message -eq 'original experiment failure') 'Original exception was masked'
 Assert-True (@($cleanup | Where-Object status -eq 'cleanup_failed').Count -eq 1) 'Cleanup failure lost'
 Assert-True ((Get-FixtureExitCode $testExit $experimentError $cleanup) -eq 1) 'Combined failure must exit nonzero'
 Assert-True ($cleanup.Count -eq 2) 'Cleanup must continue after a stop failure'
 $reportStatements=@($ast.EndBlock.Statements | Where-Object {$_.Extent.StartOffset -gt $outerTry.Extent.EndOffset -and $_ -isnot [Management.Automation.Language.ExitStatementAst]})
 $reported=@(. ([scriptblock]::Create(($reportStatements.Extent.Text -join "`n"))) 2>&1)
 Assert-True (@($reported | Where-Object {"$_" -match 'original experiment failure'}).Count -eq 1) 'Original error must be reported'
 Assert-True (@($reported | Where-Object {"$_" -match 'Fixture cleanup: container fixture-container: cleanup_failed'}).Count -eq 1) 'Cleanup error must also be reported'
}
Test-Cleanup 'PartialCreationWithoutIdRetainsReceiptAndCleanupEvidence' {
 Reset-Fixture 'ownership_inconclusive'
 $outerTry=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[-1]
 $previous=@{};$ids=[Collections.Generic.List[string]]::new()
 $containerNames=[Collections.Generic.List[string]]::new();$containerNames.Add('fixture-container')
 $network=$null;$networkName='fixture-network';$guard='fixture-guard';$CaptureD8=$true;$experimentError=$null;$testExit=0
 $ResultsDirectory=Join-Path ([IO.Path]::GetTempPath()) ('noponto-d8-cleanup-tests-' + [guid]::NewGuid().ToString('N'))
 [IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
 $save=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Save-D8Resources'},$false)
 . ([scriptblock]::Create($save.Extent.Text))
 Save-D8Resources
 . ([scriptblock]::Create('try { throw "creation returned no ID" } ' + $outerTry.CatchClauses[0].Extent.Text + ' finally ' + $outerTry.Finally.Extent.Text))
 $receipt=Get-Content -LiteralPath (Join-Path $ResultsDirectory 'd8-resources.json') -Raw | ConvertFrom-Json
 $evidence=Get-Content -LiteralPath (Join-Path $ResultsDirectory 'd8-cleanup.json') -Raw | ConvertFrom-Json
 Assert-True ($receipt.containers.Count -eq 0 -and $receipt.container_names[0] -eq 'fixture-container' -and $receipt.network_name -eq 'fixture-network') 'Pre-creation identifiers lost'
 Assert-True ($evidence.experiment_error -eq 'creation returned no ID' -and $evidence.resources.Count -eq 2) 'Evidence must retain original error and both cleanup results'
 Assert-True (@($evidence.resources | Where-Object status -ne 'ownership_inconclusive').Count -eq 0) 'Unproved resources must be reported'
 Assert-True ((Get-FixtureExitCode 0 $experimentError $cleanup) -eq 1) 'Partial creation cannot succeed'
}
Test-Cleanup 'NonzeroTestExitSurvivesSuccessfulCleanup' {
 Reset-Fixture 'success'
 $result=@(Complete-FixtureCleanup @('fixture-container') 'fixture-network' 'fixture-guard')
 Assert-True ((Get-FixtureExitCode 7 $null $result) -eq 7) 'Original nonzero exit lost'
}
Write-Output "Offline cleanup: $passed passed, 0 failed. No Docker executable invoked."
