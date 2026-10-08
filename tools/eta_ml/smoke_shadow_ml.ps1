param([string]$Image='noponto-eta-history:3e1-local')
$ErrorActionPreference='Stop'
$name='eta-history-3e1-'+[Guid]::NewGuid().ToString('N')
$created=$false
try {
    docker run -d --name $name --network none --label noponto.fixture=shadow-3e1-smoke --memory 512m --cpus 0.5 --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp:rw,noexec,nosuid,size=32m $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create exclusive smoke container' }
    $created=$true
    @'
import json, urllib.request, urllib.error, time
base='http://127.0.0.1:5200'
def call(path,body=None):
    req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
    try:
        with urllib.request.urlopen(req,timeout=2) as r: return r.status,json.load(r)
    except urllib.error.HTTPError as e: return e.code,json.load(e)
started=time.monotonic()
while time.monotonic()-started<180:
    try:
        if call('/health')[0]==200: break
    except (urllib.error.URLError, TimeoutError): pass
    time.sleep(.5)
else: raise AssertionError('Health unavailable after 180s')
print('startup_observed_seconds=%.3f'%(time.monotonic()-started),flush=True)
try:
    from inference_contract import FEATURES
except ModuleNotFoundError:
    from pipeline import FEATURES  # Compatibility with historical 3E.1 image.
assert call('/health')[0]==200
assert call('/ready')[0]==503
assert call('/eta/batch',[])==(200,[])
row={k:'exclusive-fixture' for k in FEATURES}
row.update(modal='ONIBUS',topologia='LINEAR',posicao_gps=.1,posicao_destino=.8,distancia_metros=100,velocidade_kmh=20,velocidade_media_causal_kmh=None,hora_dia=12,dia_semana=3,shadow_contract='noponto-eta-shadow-history-v1',shadow_request_id='fixture')
status,body=call('/eta/batch',[row])
assert status==503 and body['detail']=='Trusted compatible model unavailable',(status,body)
print('PASS Docker: health=200 ready=503 empty=200[] nonempty=no-model503; no synthetic or real model loaded')
'@ | docker exec -i $name python -
    if ($LASTEXITCODE -ne 0) { throw 'HTTP smoke assertions failed' }
    # Run the image's own liveness probe and inspect its real Docker health result.
    for ($attempt=0;$attempt -lt 35;$attempt++) {
        $health=[string](docker inspect $name --format '{{.State.Health.Status}}')
        if ($health.Trim() -eq 'healthy') { break }
        Start-Sleep -Seconds 1
    }
    if ($health.Trim() -ne 'healthy') { throw "Docker healthcheck: $health" }
    docker stats --no-stream --format '{{json .}}' $name
    docker inspect $name --format 'health={{.State.Health.Status}} image={{.Image}} network={{.HostConfig.NetworkMode}}'
} finally {
    if ($created) { docker rm -f $name | Out-Null }
}
