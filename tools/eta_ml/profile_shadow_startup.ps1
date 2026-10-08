param([string[]]$Images=@('noponto-eta-history:3e1-local','noponto-eta-history:3f-local'),[string]$ReportDirectory)
$ErrorActionPreference='Stop'
if (-not $ReportDirectory) { $ReportDirectory=Join-Path $PSScriptRoot ('outputs/startup-3f-'+[Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Report directory must be new' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
foreach ($image in $Images) {
    $name='eta-history-profile-'+[Guid]::NewGuid().ToString('N')
    $wall=[System.Diagnostics.Stopwatch]::StartNew()
    @'
import importlib,time,resource,json,sys
for name in ['eta_history_service','numpy','sklearn','pipeline','collection_profiles']:
    started=time.perf_counter();importlib.import_module(name)
    print(json.dumps(dict(stage=name,seconds=time.perf_counter()-started,max_rss_kib=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss,numpy_loaded='numpy' in sys.modules,sklearn_loaded='sklearn' in sys.modules)),flush=True)
'@ | docker run --rm -i --name $name --network none --label noponto.fixture=shadow-3f-profile --memory 512m --cpus 0.5 --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp:rw,noexec,nosuid,size=32m $image python - 2>&1 | Tee-Object -FilePath (Join-Path $ReportDirectory "$name.jsonl")
    if ($LASTEXITCODE -ne 0) { throw "Profiling failed for $image" }
    "image=$image wall_seconds=$($wall.Elapsed.TotalSeconds)" | Tee-Object -FilePath (Join-Path $ReportDirectory "$name.summary.txt")
}
Write-Host "Reports: $ReportDirectory"
