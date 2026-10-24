#requires -Version 7.0
param([Parameter(Mandatory)][string]$GtfsZip, [Parameter(Mandatory)][string]$ResultsDirectory,
      [ValidateRange(1,459)][int]$SubscribedLines = 1, [switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run -or -not [IO.Path]::IsPathFullyQualified($GtfsZip) -or -not (Test-Path -LiteralPath $GtfsZip)) {
    throw 'Explicit -Run and an existing absolute public GTFS ZIP path are required.'
}
$keys = @('NOPONTO_ITS_E2E_RUN', 'NOPONTO_ITS_E2E_GTFS', 'NOPONTO_ITS_E2E_SUBSCRIBED_LINES', 'CustomAfterMicrosoftCommonTargets')
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
    $env:NOPONTO_ITS_E2E_RUN = '1'
    $env:NOPONTO_ITS_E2E_GTFS = $GtfsZip
    $env:NOPONTO_ITS_E2E_SUBSCRIBED_LINES = $SubscribedLines.ToString()
    $env:CustomAfterMicrosoftCommonTargets = Join-Path $PSScriptRoot 'ItsE2e.targets'
    & (Join-Path $PSScriptRoot 'test_pipeline_disposable.ps1') -Run -ResultsDirectory $ResultsDirectory `
        -Filter 'FullyQualifiedName~NoPonto.Tests.GpsPipelineDisposableIntegrationTests.ItsEndToEnd_Real'
    $result = $LASTEXITCODE
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
exit $result
