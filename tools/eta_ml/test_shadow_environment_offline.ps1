# Real PowerShell child processes read only one synthetic environment variable.
# The runner body, Docker and dotnet test are never executed.
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'test_shadow_disposable.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ($parseErrors | Out-String)}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-D8PrepareAuthorization'},$false)
if(-not $definition){throw 'Missing environment removal function'}
. ([scriptblock]::Create($definition.Extent.Text))
$key='NOPONTO_SHADOW_FIXTURE_D8_PREPARE'
$original=[Environment]::GetEnvironmentVariable($key,'Process')
$passed=0
function Assert-True($Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Test-Environment([string]$Name,[scriptblock]$Body){& $Body;$script:passed++;Write-Output "PASS $Name"}
function Read-ChildAuthorization {
 $probe=@'
$key='NOPONTO_SHADOW_FIXTURE_D8_PREPARE'
$value=[Environment]::GetEnvironmentVariable($key,'Process')
[pscustomobject]@{present=[Environment]::GetEnvironmentVariables('Process').Contains($key);is_null=($null -eq $value);is_empty=($null -ne $value -and $value.Length -eq 0);expected_token=($value -ceq 'offline-fixture-token');capture_allowed=($null -eq $value)} | ConvertTo-Json -Compress
'@
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probe))
 $json=& ([Environment]::ProcessPath) -NoProfile -EncodedCommand $encoded
 if($LASTEXITCODE -ne 0){throw 'Environment probe child failed'}
 return ($json | ConvertFrom-Json)
}
# Any accidental real invocation is blocked in this script's scope.
function docker {throw 'Docker forbidden in offline environment tests'}
function dotnet {
 $filter=@($args | Where-Object {$_ -like 'FullyQualifiedName=*'})
 if($filter.Count -ne 1){throw 'Unexpected simulated dotnet command'}
 if($filter[0] -like '*D8_PrepareSchemaExplicitly'){$phase='prepare'}
 elseif($filter[0] -like '*D8_CaptureThroughActualGpsCasAndShadow'){$phase='capture'}
 else{throw 'Unexpected test selected'}
 $script:phases.Add([pscustomobject]@{phase=$phase;child=(Read-ChildAuthorization)})
 $global:LASTEXITCODE=0
}
function Confirm-D8Trx {param($Path,$Method)} # TRX content is outside this environment-only test.
$outerTry=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[-1]
$phaseBlock=$ast.Find({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if($CaptureD8)') -and $node.Extent.Text -match 'dotnet test.*D8_PrepareSchemaExplicitly'},$true)
if(-not $phaseBlock){throw 'Missing independent phase sequence'}
$restoreBlock=$outerTry.Finally.Statements[0]
$CaptureD8=$true;$guard='offline-fixture-token';$project='unused-offline';$ResultsDirectory=[IO.Path]::GetTempPath()
try {
 Test-Environment 'TokenIsInheritedAndAuthorizesPreparation' {
  $env:NOPONTO_SHADOW_FIXTURE_D8_PREPARE='offline-fixture-token'
  $child=Read-ChildAuthorization
  Assert-True ($child.present -and $child.expected_token -and -not $child.capture_allowed) 'Preparation token not inherited exactly'
 }
 Test-Environment 'NullSetterReproducesInheritedEmptyVariable' {
  [Environment]::SetEnvironmentVariable($key,$null,'Process')
  $child=Read-ChildAuthorization
  Assert-True ($child.present -and $child.is_empty -and -not $child.is_null -and -not $child.capture_allowed) 'Original call must reproduce present-empty on this runtime'
 }
 Test-Environment 'ExplicitEmptyIsNotAbsence' {
  $env:NOPONTO_SHADOW_FIXTURE_D8_PREPARE=''
  $child=Read-ChildAuthorization
  Assert-True ($child.present -and $child.is_empty -and -not $child.capture_allowed -and -not $child.expected_token) 'Empty must authorize neither phase'
 }
 Test-Environment 'RemovalProducesRealAbsenceInChild' {
  Remove-D8PrepareAuthorization
  $child=Read-ChildAuthorization
  Assert-True (-not $child.present -and $child.is_null -and $child.capture_allowed -and -not $child.expected_token) 'Child must observe absence rather than empty'
  Remove-D8PrepareAuthorization # Idempotent when absent.
 }
 Test-Environment 'ActualPhaseSequencePassesTokenOnlyToPreparation' {
  $script:phases=[Collections.Generic.List[object]]::new()
  . ([scriptblock]::Create($phaseBlock.Extent.Text))
  Assert-True ($script:phases.Count -eq 2 -and $script:phases[0].phase -eq 'prepare' -and $script:phases[1].phase -eq 'capture') 'Phases must remain ordered and independent'
  Assert-True ($script:phases[0].child.expected_token -and -not $script:phases[0].child.capture_allowed) 'Preparation must inherit token'
  Assert-True (-not $script:phases[1].child.present -and $script:phases[1].child.capture_allowed) 'Capture must inherit no preparation authorization'
 }
 foreach($failure in @('throw','silent')){
  Test-Environment "RemovalFailureStopsCapture_$failure" {
   function Remove-Item {param($LiteralPath,$ErrorAction) if($failure -eq 'throw'){throw 'Simulated environment removal failure'}}
   $script:phases=[Collections.Generic.List[object]]::new();$rejected=$false
   try {. ([scriptblock]::Create($phaseBlock.Extent.Text))}catch{$rejected=$true}
   Assert-True ($rejected -and $script:phases.Count -eq 1 -and $script:phases[0].phase -eq 'prepare') 'No capture child may start after removal failure'
   Assert-True ([Environment]::GetEnvironmentVariable($key,'Process') -ceq 'offline-fixture-token') 'Simulated removal must retain authorization for detection'
  }
 }
 foreach($state in @('absent','empty','token')){
  Test-Environment "FinallyRestoresPreviousState_$state" {
   $value=switch($state){'absent'{$null};'empty'{''};'token'{'offline-fixture-token'}}
   $previous=@{$key=$value}
   $env:NOPONTO_SHADOW_FIXTURE_D8_PREPARE='changed-offline-token'
   . ([scriptblock]::Create($restoreBlock.Extent.Text))
   $child=Read-ChildAuthorization
   switch($state){
    'absent' {Assert-True (-not $child.present -and $child.is_null) 'Previous absence was changed into empty'}
    'empty' {Assert-True ($child.present -and $child.is_empty) 'Previous empty state was lost'}
    'token' {Assert-True ($child.present -and $child.expected_token) 'Previous value was lost'}
   }
  }
 }
} finally {
 $value=if($null -eq $original){[NullString]::Value}else{$original}
 [Environment]::SetEnvironmentVariable('NOPONTO_SHADOW_FIXTURE_D8_PREPARE',$value,'Process')
}
Write-Output "Offline environment: $passed passed, 0 failed. Real child probes; no Docker or dotnet tests."
