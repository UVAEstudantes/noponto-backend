#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
# Usage: candidate digest-reference, workflow run number, real env file, state directory.
[[ $# == 4 ]] || { echo 'Deploy: quatro argumentos obrigatórios.' >&2; exit 2; }
candidate=$1 sequence=$2 env_file=$3 state=$4
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
[[ -r $base && -r $overlay && -r $env_file ]] || exit 2
mkdir -p "$state"
exec 9>"$state/deploy.lock"
flock -n 9 || { echo 'Deploy já em andamento.' >&2; exit 1; }
if [[ -f $state/active.run ]]; then
    read -r last < "$state/active.run"
    [[ $last =~ ^[0-9]+$ ]] || exit 2
    (( sequence > last )) || { echo 'Release antiga ou já processada recusada.' >&2; exit 1; }
fi
compose() {
    GTFSRT_API_IMAGE=$1 docker compose --project-name noponto --project-directory "$project_dir" \
        --env-file "$env_file" -f "$base" -f "$overlay" "${@:2}"
}
# Não publicar configuração renderizada nem erros do provider/Compose.
compose "$candidate" config --format json 2>/dev/null | python3 -c '
import json,sys
try:
 a=json.load(sys.stdin)["services"]["api"]; e=a.get("environment",{})
 assert a["image"]==sys.argv[1]
 assert e.get("ASPNETCORE_ENVIRONMENT")=="Production"
 assert e.get("DOTNET_ENVIRONMENT","Production")=="Production"
 assert not a.get("build") and not a.get("command") and not a.get("entrypoint")
 assert e.get("GpsSources__BusPrimarySource")=="GTFSRT_BUS"
 assert e.get("GpsSources__BrtPrimarySource")=="GTFSRT_BRT"
 assert float(a.get("cpus",0))==1 and int(a.get("mem_limit",0))==805306368 and int(a.get("pids_limit",0))==256
 for k in ("GtfsRealtimeGps__BusEnabled","GtfsRealtimeGps__BrtEnabled","GtfsRealtimeGps__CrosswalkValidated"):
  assert str(e.get(k)).lower()=="true"
 for k in ("ML__ETA__Enabled","EtaV2__Enabled","EtaV2__ShadowEnabled"):
  assert str(e.get(k)).lower()=="false"
except Exception: sys.exit(1)
' "$candidate" || { echo 'Composição produtiva inválida.' >&2; exit 1; }
previous_id=$(docker inspect --format '{{.Image}}' noponto_api 2>/dev/null)
[[ $previous_id =~ ^sha256:[a-f0-9]{64}$ ]] || exit 1
[[ $(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}/{{index .Config.Labels "com.docker.compose.service"}}' noponto_api 2>/dev/null) == noponto/api ]] || exit 1
previous="noponto-api:rollback-${previous_id#sha256:}"
docker image tag "$previous_id" "$previous" >/dev/null 2>&1
mkdir -p "$state/previous"
cp "$base" "$state/previous/base.yml"
cp "$overlay" "$state/previous/overlay.yml"
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
# Pull não altera API; rollback já está disponível localmente, sem registry.
docker pull "$candidate" >/dev/null 2>&1 || { echo 'Pull falhou; API preservada.' >&2; exit 1; }
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
        if compose "$previous" up -d --no-deps --no-build --pull never api >/dev/null 2>&1 && healthy "$previous_id"; then
            echo 'Rollback confirmado.' >&2
        else echo 'ROLLBACK FALHOU: intervenção do operador necessária.' >&2; fi
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
mv "$state/active.env.tmp" "$state/active.env"
# Última gravação é o marcador: nenhuma sequência promovida antes da referência.
mv "$state/active.run.tmp" "$state/active.run"
armed=0
echo 'Deploy da API confirmado.'
