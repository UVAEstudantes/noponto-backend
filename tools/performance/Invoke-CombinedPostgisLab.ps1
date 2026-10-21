param(
    [Parameter(Mandatory=$true)][string]$GtfsFile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$BaselineSource,
    [string]$CandidateSource = 'NoPonto/4-Data/Repositories/GpsItinerarioRepository.CombinedSetBased.cs',
    [switch]$Compare,
    [switch]$RunDifferential
)

# Opt-in laboratory, not a deployment/operational script. Run from repository root.
# One new Docker Desktop container, no persistent volume, no existing database.
$ErrorActionPreference = 'Stop'
$labContext = 'desktop-linux'
$labImage = 'postgis/postgis:16-3.4'
$labGuard = [guid]::NewGuid().ToString('N')
$labName = 'noponto-combined-' + $labGuard.Substring(0,12)
$labDatabase = 'combined_' + $labGuard.Substring(0,12)
$labRoot = Join-Path ([IO.Path]::GetTempPath()) ('noponto-combined-' + [guid]::NewGuid().ToString('N'))
$labId = $null
$oldPassword = $env:POSTGRES_PASSWORD
$oldConnection = $env:POSTGIS_TEST_CONNECTION

function Assert-LabExit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "Laboratory step failed: $Step (exit $LASTEXITCODE)" }
}

function Get-OwnedLab {
    $labMeta = docker --context $labContext inspect $labId | ConvertFrom-Json
    Assert-LabExit 'inspect owned fixture'
    if ($labMeta.Id -ne $labId -or $labMeta.Name -ne ('/' + $labName) -or
        $labMeta.Config.Labels.'noponto.combined.guard' -ne $labGuard -or
        @($labMeta.Mounts | Where-Object { $_ -and $_.Type -ne 'tmpfs' }).Count -ne 0) {
        throw 'Fixture ownership mismatch; refusing mutation'
    }
    return $labMeta
}

try {
    if (-not (Test-Path -LiteralPath $GtfsFile -PathType Leaf)) { throw 'Local GTFS required' }
    if ($Compare -and -not $BaselineSource) { throw 'Comparison requires a separate saved baseline source' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory to preserve earlier evidence' }
    $labEndpoint = docker context inspect $labContext --format '{{.Endpoints.docker.Host}}'
    Assert-LabExit 'local Docker context'
    if ($labEndpoint -ne 'npipe:////./pipe/dockerDesktopLinuxEngine') { throw 'Only local Docker Desktop is allowed' }
    docker --context $labContext image inspect $labImage --format '{{.Id}}' | Out-Null
    Assert-LabExit 'cached PostGIS image (no pull)'
    python -c 'import psycopg2'
    Assert-LabExit 'psycopg2 prerequisite'
    New-Item -ItemType Directory -Path $labRoot | Out-Null
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    $env:POSTGRES_PASSWORD = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $labId = docker --context $labContext run -d --pull never --name $labName `
        --label "noponto.combined.guard=$labGuard" --network bridge --publish '127.0.0.1::5432' `
        --tmpfs '/var/lib/postgresql/data:rw,size=2g' --memory 2g --cpus 2 `
        --env POSTGRES_PASSWORD --env "POSTGRES_DB=$labDatabase" $labImage
    Assert-LabExit 'create disposable fixture'
    $labId = $labId.Trim()
    $labMeta = Get-OwnedLab
    $labBindings = @($labMeta.NetworkSettings.Ports.'5432/tcp')
    if ($labBindings.Count -ne 1 -or $labBindings[0].HostIp -ne '127.0.0.1') { throw 'Loopback binding required' }
    $labPort = [int]$labBindings[0].HostPort
    $labReady = $false
    for ($labAttempt=0; $labAttempt -lt 80; $labAttempt++) {
        docker --context $labContext exec $labId pg_isready -h 127.0.0.1 -U postgres -d $labDatabase *> $null
        if ($LASTEXITCODE -eq 0) { $labReady=$true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $labReady) { throw 'Disposable fixture did not become ready' }
    $labEmpty = docker --context $labContext exec $labId psql -U postgres -d $labDatabase -At -c `
        "SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename<>'spatial_ref_sys'"
    Assert-LabExit 'verify brand-new database'
    if ($labEmpty.Trim() -ne '0') { throw 'Refusing preexisting database' }
    docker --context $labContext exec $labId psql -v ON_ERROR_STOP=1 -U postgres -d $labDatabase -c `
        "CREATE TABLE public.combined_fixture_guard(token text NOT NULL); INSERT INTO public.combined_fixture_guard VALUES('$labGuard')" | Out-Null
    Assert-LabExit 'mark owned fixture'
    $labState = Join-Path $labRoot 'private-state.json'
    @{root=$labRoot;guard=$labGuard;container=$labName;context=$labContext;database=$labDatabase;
      password=$env:POSTGRES_PASSWORD;id=$labId;port=$labPort;createdUtc=[DateTime]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath $labState -Encoding UTF8
    $labArgs = @('tools/performance/combined_postgis.py','--state',$labState,'--output',$OutputDirectory)
    python @labArgs seed --gtfs $GtfsFile
    Assert-LabExit 'seed isolated catalog'
    $labBaseline = if ($BaselineSource) { $BaselineSource } else { $CandidateSource }
    foreach ($labScenario in @('low','high','concentrated')) {
        python @labArgs profile --source $labBaseline --scenario $labScenario
        Assert-LabExit 'baseline parameterized EXPLAIN'
        if ($Compare) {
            python @labArgs profile --source $CandidateSource --scenario $labScenario
            Assert-LabExit 'candidate parameterized EXPLAIN'
        }
    }
    if ($Compare) {
        python @labArgs compare --baseline $BaselineSource --candidate $CandidateSource --pairs 7
        Assert-LabExit 'seven alternating warmed pairs and exact ordered equivalence'
    }
    if ($RunDifferential) {
        $env:POSTGIS_TEST_CONNECTION = "Host=127.0.0.1;Port=$labPort;Database=$labDatabase;Username=postgres;Password=$env:POSTGRES_PASSWORD"
        # Does not start the app; each class creates/drops only its own random test schema.
        dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter `
            'FullyQualifiedName~NoPonto.Tests.GpsCombinedDeferredLengthPostgisTests.|FullyQualifiedName~NoPonto.Tests.GpsMatchingDifferentialHarnessTests.|FullyQualifiedName~NoPonto.Tests.GpsMatchingCombinadoDiferencialPostgisTests.|FullyQualifiedName~NoPonto.Tests.GpsMatchingLotePostgisTests.|FullyQualifiedName~NoPonto.Tests.GpsPinnedVersionProjectionPostgisTests.|FullyQualifiedName~NoPonto.Tests.GpsCircularNextOccurrencePostgisTests.|FullyQualifiedName~NoPonto.Tests.GpsItinerarioRepositoryPostgisTests.' `
            --logger 'trx;LogFileName=postgis.trx' --results-directory $OutputDirectory
        Assert-LabExit 'PostGIS differential tests'
    }
}
finally {
    $env:POSTGRES_PASSWORD = $oldPassword
    $env:POSTGIS_TEST_CONNECTION = $oldConnection
    if ($labId) {
        $null = Get-OwnedLab
        docker --context $labContext rm -f $labId | Out-Null
        Assert-LabExit 'remove only owned disposable fixture'
        Write-Output 'Owned fixture removed; tmpfs data discarded.'
    }
    $labPrivate = Join-Path $labRoot 'private-state.json'
    if (Test-Path -LiteralPath $labPrivate) { Remove-Item -LiteralPath $labPrivate }
}
