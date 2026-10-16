#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
# Optional --configure-shadow: same active digest/sequence, no pull, same rollback/lock.
[[ $# == 6 || ( $# == 7 && ( $7 == --check || $7 == --configure-shadow ) ) ]] || { echo 'Deploy: seis argumentos e modo opcional --check/--configure-shadow.' >&2; exit 2; }
candidate=$1 sequence=$2 env_file=$3 state=$4 physical_override=$5 shadow_override=$6
managed_shadow=${NOPONTO_SHADOW_OVERRIDE:-$state/historical-eta-shadow.override.yml}
check_only=0; configure_only=0
[[ ${7:-} != --check ]] || check_only=1
[[ ${7:-} != --configure-shadow ]] || configure_only=1
[[ $physical_override == /* && $shadow_override == /* && $managed_shadow == /* ]] || exit 2
[[ $candidate =~ ^ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}$ ]] || exit 2
[[ $sequence =~ ^[1-9][0-9]{0,8}$ && $env_file == /* && $state == /* ]] || exit 2
base=${NOPONTO_BASE_COMPOSE:-/opt/stacks/noponto/docker-compose.yml}
overlay=${NOPONTO_OVERLAY_COMPOSE:-/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml}
project_dir=$(dirname "$base")
# Congelado antes de trocar base/overlay pelos snapshots no rollback.
readonly project_dir
health_timeout=${NOPONTO_HEALTH_TIMEOUT:-180}
[[ $health_timeout =~ ^[1-9][0-9]{0,3}$ ]] || exit 2
for tool in docker flock curl python3; do command -v "$tool" >/dev/null || exit 2; done
[[ -r $base && -r $overlay && -r $env_file && -r $physical_override && -r $shadow_override ]] || exit 2
if (( ! check_only )); then
mkdir -p "$state"
exec 9>"$state/deploy.lock"
flock -n 9 || { echo 'Deploy já em andamento.' >&2; exit 1; }
if [[ -f $state/active.run ]]; then
    read -r last < "$state/active.run"
    [[ $last =~ ^[0-9]+$ ]] || exit 2
    if (( configure_only )); then
        [[ $sequence == "$last" && -f $state/active.env ]] || exit 1
        [[ $(cat "$state/active.env") == "GTFSRT_API_IMAGE=$candidate" ]] || exit 1
    else
        (( sequence > last )) || { echo 'Release antiga ou já processada recusada.' >&2; exit 1; }
    fi
elif (( configure_only )); then
    echo 'Reconfiguracao exige release ativa registrada.' >&2; exit 1
fi
fi
compose() {
    GTFSRT_API_IMAGE=$1 docker compose --project-name noponto --project-directory "$project_dir" \
        --env-file "$env_file" -f "$base" -f "$overlay" -f "$physical_override" -f "$shadow_override" "${@:2}"
}
# Não publicar configuração renderizada nem erros do provider/Compose.
previous_shadow=$(python3 -I - "$candidate" "$env_file" "$base" "$overlay" "$physical_override" "$shadow_override" "$project_dir" "$state" "$configure_only" "$managed_shadow" <<'PY'
import copy,json,os,stat,subprocess,sys
try:
 image,env_file,base,overlay,physical,shadow,project,state,configure,managed=sys.argv[1:]
 command=['docker','compose','--project-name','noponto','--project-directory',project,
          '--env-file',env_file,'-f',base,'-f',overlay]
 env=dict(os.environ,GTFSRT_API_IMAGE=image)
 def render(extra):
  result=subprocess.run(command+extra+['config','--format','json'],env=env,capture_output=True,check=True,timeout=30)
  return json.loads(result.stdout)
 original=render([]); rendered=render(['-f',physical])
 # The third file may change exactly the two PhysicalEta settings, no other value/service.
 normalized=copy.deepcopy(rendered)
 original_env=original['services']['api'].get('environment',{})
 for key in ('PhysicalEta__Enabled','PhysicalEta__MaxAgeSeconds'):
  if key in original_env: normalized['services']['api']['environment'][key]=original_env[key]
  else: normalized['services']['api']['environment'].pop(key,None)
 assert normalized==original
 physical_rendered=rendered
 rendered=render(['-f',physical,'-f',shadow])
 prefix='HistoricalEtaShadow__'
 fixed={'MlEnabled':'false','HistoricalEnabled':'false','QueueCapacity':'64','BatchSize':'16',
        'SinkCapacity':'128','TimeoutMs':'500','MaxAgeSeconds':'90',
        'OfflineExport__DirectoryPath':'/var/lib/noponto-shadow',
        'OfflineExport__Fixture':'false','OfflineExport__QueueCapacity':'128',
        'OfflineExport__MaxFileBytes':'1048576','OfflineExport__MaxTotalBytes':'16777216',
        'OfflineExport__MaxFiles':'32','OfflineExport__RetentionHours':'24'}
 keys={prefix+k:v for k,v in fixed.items()}
 root=prefix+'Enabled'; export=prefix+'OfflineExport__Enabled'
 keys.update({root:'false',export:'false'})
 def shadow_environment(environment,partial=False):
  # Configuration is case-insensitive; aliases/duplicates could silently override a gate.
  selected={k:str(v) for k,v in environment.items() if k.replace(':','__').lower().startswith(prefix.lower())}
  assert set(selected)<=set(keys)
  mode=selected.get(root,'false')
  assert mode in ('false','true') and selected.get(export,'false')==mode
  expected=dict(keys,**{root:mode,export:mode})
  assert selected==expected if not partial else all(v==expected[k] for k,v in selected.items())
  if mode=='true': assert selected==expected
  return selected,mode
 e=rendered['services']['api'].get('environment',{})
 selected,mode=shadow_environment(e)
 host=os.path.join(state,'shadow-capture-v2'); target='/var/lib/noponto-shadow'
 volume={'type':'bind','source':host,'target':target,'bind':{'create_host_path':False}}
 normalized=copy.deepcopy(rendered); a=normalized['services']['api']
 old_env=physical_rendered['services']['api'].get('environment',{})
 for k in keys:
  if k in old_env: a['environment'][k]=old_env[k]
  else: a['environment'].pop(k,None)
 old_volumes=physical_rendered['services']['api'].get('volumes',[])
 assert not any(v.get('target')==target for v in old_volumes)
 expected_volumes=old_volumes+([volume] if mode=='true' else [])
 # Compose may sort mounts by target; do not accept any additional mount/options.
 def canonical_mounts(values):
  values=copy.deepcopy(values)
  for v in values:
   if v.get('bind',{}).get('create_host_path') is False: v['bind'].pop('create_host_path')
  return sorted(values,key=lambda v:v['target'])
 assert canonical_mounts(a.get('volumes',[]))==canonical_mounts(expected_volumes)
 if 'volumes' in physical_rendered['services']['api']: a['volumes']=old_volumes
 else: a.pop('volumes',None)
 assert normalized==physical_rendered
 if mode=='true':
  assert os.path.realpath(host)==host and os.path.isdir(host)
  info=os.stat(host)
  assert stat.S_IMODE(info.st_mode)==0o700 and info.st_uid==os.geteuid()
  assert os.access(host,os.W_OK|os.X_OK)
 # Freeze the mode actually running, never the requested candidate mode.
 def inspect(fmt):
  return json.loads(subprocess.run(['docker','inspect','--format',fmt,'noponto_api'],capture_output=True,check=True,timeout=15).stdout)
 current_env={}
 for item in inspect('{{json .Config.Env}}'):
  key,_,value=item.partition('=')
  assert key not in current_env
  current_env[key]=value
 previous_env,previous_mode=shadow_environment(current_env,partial=True)
 # Explicit false gates also protect return from defaults/provider values in the base files.
 previous_env=dict(keys,**previous_env)
 previous_env[root]=previous_env[export]=previous_mode
 mounts=[m for m in inspect('{{json .Mounts}}') if m.get('Destination')==target]
 assert len(mounts)<=1
 if mounts:
  m=mounts[0]
  assert m['Type']=='bind' and m['Source']==host and m['RW'] is True
 if previous_mode=='true': assert mounts
 if configure=='1':
  managed_rendered=render(['-f',physical,'-f',managed])
  ma=managed_rendered['services']['api']
  _,managed_mode=shadow_environment(ma.get('environment',{}))
  assert managed_mode==previous_mode
  assert canonical_mounts(ma.get('volumes',[]))==canonical_mounts(old_volumes+([volume] if mounts else []))
  for k in keys:
   if k in old_env: ma['environment'][k]=old_env[k]
   else: ma['environment'].pop(k,None)
  if 'volumes' in physical_rendered['services']['api']: ma['volumes']=old_volumes
  else: ma.pop('volumes',None)
  assert managed_rendered==physical_rendered
 previous_api={'environment':previous_env}
 if mounts: previous_api['volumes']=[volume]
 a=rendered['services']['api']; e=a.get('environment',{})
 assert a['image']==image
 assert e.get('ASPNETCORE_ENVIRONMENT')=='Production'
 assert e.get('DOTNET_ENVIRONMENT','Production')=='Production'
 assert not a.get('build') and not a.get('command') and not a.get('entrypoint')
 assert e.get('GpsSources__BusPrimarySource')=='GTFSRT_BUS'
 assert e.get('GpsSources__BrtPrimarySource')=='GTFSRT_BRT'
 assert float(a.get('cpus',0))==1 and int(a.get('mem_limit',0))==805306368 and int(a.get('pids_limit',0))==256
 assert str(e.get('PhysicalEta__Enabled')).lower()=='true'
 assert str(e.get('PhysicalEta__MaxAgeSeconds'))=='90'
 for k in ('GtfsRealtimeGps__BusEnabled','GtfsRealtimeGps__BrtEnabled','GtfsRealtimeGps__CrosswalkValidated'):
  assert str(e.get(k)).lower()=='true'
 for k in ('ML__ETA__Enabled','EtaV2__Enabled','EtaV2__ShadowEnabled'):
  assert str(e.get(k)).lower()=='false'
 print(json.dumps({'services':{'api':previous_api}}))
except Exception:
 print('Composicao produtiva invalida; valores omitidos.',file=sys.stderr)
 sys.exit(1)
PY
)
previous_id=$(docker inspect --format '{{.Image}}' noponto_api 2>/dev/null)
[[ $previous_id =~ ^sha256:[a-f0-9]{64}$ ]] || exit 1
[[ $(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}/{{index .Config.Labels "com.docker.compose.service"}}' noponto_api 2>/dev/null) == noponto/api ]] || exit 1
if (( check_only )); then
    echo 'Preflight READ ONLY aprovado: digest, quatro Compose, PhysicalEta=true/90, Shadow OFF ou captura sem inferencia.'
    exit 0
fi
if (( configure_only )); then
    [[ $(docker image inspect --format '{{.Id}}' "$candidate" 2>/dev/null) == "$previous_id" ]] || exit 1
    [[ -r $managed_shadow ]] || exit 1
fi
previous="noponto-api:rollback-${previous_id#sha256:}"
docker image tag "$previous_id" "$previous" >/dev/null 2>&1
mkdir -p "$state/previous"
cp "$base" "$state/previous/base.yml"
cp "$overlay" "$state/previous/overlay.yml"
cp "$physical_override" "$state/previous/physical-eta.override.yml"
cp "$shadow_override" "$state/previous/candidate-shadow.override.yml"
printf '%s\n' "$previous_shadow" > "$state/previous/shadow.override.json"
if (( configure_only )); then cp "$managed_shadow" "$state/previous/managed-shadow.override.yml"; fi
cp "$env_file" "$state/previous/runtime.env"
printf '%s\n' "$previous" > "$state/previous/image"
if [[ -f $state/active.env ]]; then
    cp "$state/active.env" "$state/previous/active.env"
else
    printf 'GTFSRT_API_IMAGE=%s\n' "$previous" > "$state/previous/active.env"
fi
healthy() {
    local expected=$1 good=0 deadline=$((SECONDS + health_timeout)) status code
    while (( SECONDS < deadline )); do
        status=$(docker inspect --format '{{.Image}}/{{.State.Running}}/{{.State.OOMKilled}}/{{.RestartCount}}' noponto_api 2>/dev/null) || status=''
        code=$(curl --silent --output /dev/null --write-out '%{http_code}' --connect-timeout 2 --max-time 5 http://127.0.0.1:8080/ 2>/dev/null) || code=''
        if [[ $status == "$expected/true/false/0" && $code == 200 ]]; then
            good=$((good + 1)); (( good >= 3 )) && return 0
        else good=0; fi
        sleep 2
    done
    return 1
}
shadow_healthy() {
    compose "$1" config --format json 2>/dev/null | python3 -I -c '
import json,subprocess,sys
try:
 a=json.load(sys.stdin)["services"]["api"]
 def inspect(fmt):
  return json.loads(subprocess.run(["docker","inspect","--format",fmt,"noponto_api"],capture_output=True,check=True,timeout=15).stdout)
 def selected(env):
  return {k:v for k,v in env.items() if k.replace(":","__").lower().startswith("historicaletashadow__")}
 actual=dict(item.split("=",1) for item in inspect("{{json .Config.Env}}"))
 assert selected(actual)==selected(a.get("environment",{}))
 expected=[v for v in a.get("volumes",[]) if v["target"]=="/var/lib/noponto-shadow"]
 mounted=[v for v in inspect("{{json .Mounts}}") if v["Destination"]=="/var/lib/noponto-shadow"]
 assert len(expected)==len(mounted)
 if expected:
  assert mounted[0]["Type"]=="bind" and mounted[0]["Source"]==expected[0]["source"] and mounted[0]["RW"] is True
except Exception:
 print("Estado Shadow do container diverge da composicao; valores omitidos.",file=sys.stderr)
 sys.exit(1)
'
}
# Pull não altera API; rollback já está disponível localmente, sem registry.
if (( ! configure_only )); then
    docker pull "$candidate" >/dev/null 2>&1 || { echo 'Pull falhou; API preservada.' >&2; exit 1; }
fi
candidate_id=$(docker image inspect --format '{{.Id}}' "$candidate" 2>/dev/null)
[[ $candidate_id =~ ^sha256:[a-f0-9]{64}$ ]] || exit 1
# Preparar ambos antes de substituir API, sem publicar estado prematuramente.
printf 'GTFSRT_API_IMAGE=%s\n' "$candidate" > "$state/active.env.tmp"
printf '%s\n' "$sequence" > "$state/active.run.tmp"
armed=1
finish() {
    local result=$?
    trap - EXIT HUP INT TERM
    if [[ ${armed:-0} == 1 ]]; then
        echo 'Candidata falhou; tentando rollback da API.' >&2
        base="$state/previous/base.yml" overlay="$state/previous/overlay.yml" env_file="$state/previous/runtime.env"
        physical_override="$state/previous/physical-eta.override.yml"
        shadow_override="$state/previous/shadow.override.json"
        if compose "$previous" up -d --no-deps --no-build --pull never api >/dev/null 2>&1 && healthy "$previous_id" && shadow_healthy "$previous"; then
            echo 'Rollback confirmado.' >&2
        else echo 'ROLLBACK FALHOU: intervenção do operador necessária.' >&2; fi
        if (( configure_only )); then
            if ! { cp "$state/previous/managed-shadow.override.yml" "$managed_shadow.tmp" && mv "$managed_shadow.tmp" "$managed_shadow"; }; then
                echo 'Falha ao restaurar override Shadow: intervencao necessaria.' >&2
            fi
        fi
        if ! { cp "$state/previous/active.env" "$state/active.env.tmp" && mv "$state/active.env.tmp" "$state/active.env"; }; then
            echo 'Falha ao restaurar active.env: intervenção necessária.' >&2
        fi
        # Reverter também o marcador caso um sinal interrompa a conclusão.
        if [[ -n ${last:-} ]]; then
            if ! { printf '%s\n' "$last" > "$state/active.run.tmp" && mv "$state/active.run.tmp" "$state/active.run"; }; then
                echo 'Falha ao restaurar active.run: intervenção necessária.' >&2
            fi
        elif ! rm -f "$state/active.run"; then
            echo 'Falha ao remover marcador de promoção: intervenção necessária.' >&2
        fi
        result=1
    fi
    exit "$result"
}
trap finish EXIT
trap 'exit 1' HUP INT TERM
compose "$candidate" up -d --no-deps --no-build --pull never api >/dev/null 2>&1
healthy "$candidate_id"
shadow_healthy "$candidate"
if (( configure_only )); then
    cp "$shadow_override" "$managed_shadow.tmp"
    chmod 600 "$managed_shadow.tmp"
    mv "$managed_shadow.tmp" "$managed_shadow"
fi
mv "$state/active.env.tmp" "$state/active.env"
# Última gravação é o marcador: nenhuma sequência promovida antes da referência.
mv "$state/active.run.tmp" "$state/active.run"
armed=0
echo 'Deploy da API confirmado.'
