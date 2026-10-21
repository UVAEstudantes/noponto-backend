# Plano manual de implantação, comparação e rollback GTFS-RT

**NÃO EXECUTADO. NÃO LIBERADO.** O parecer40 contém dois bloqueios de código em304 e um impedimento de build completo. Estes comandos são um roteiro futuro para operador autorizado, após correção/revisão. Nenhuma variável, arquivo ou recurso produtivo foi alterado nesta preparação. O plano usa o contrato existente de `.deploy/deploy-api.sh`, com seis argumentos obrigatórios e quatro Compose; não propõe ativar automação.

## 1. Condições de entrada e procedência

1. Corrigir os achados40 e integrar regressões permanentes; todos os casos atuais201/810 e os novos casos devem passar. Executar `dotnet build NoPonto.sln --no-restore --no-incremental -c Release` em ambiente autorizado e registrar saída/exit code. Um bloqueio da ferramenta não equivale a build aprovado.
2. Congelar o conteúdo candidato por manifesto de fontes, artefato publicado e digest imutável. HEAD43b6a72 sozinho não identifica a árvore com WIP. Nenhum commit/push/merge é autorizado por este plano; a criação/publicação de um artefato futuro precisa de autorização própria. Construir somente de contexto controlado, sem enviar .env, lab-output ou artefatos privados; preservar o workspace/rollback. A .dockerignore atual não exclui lab-output do contexto, embora o csproj o exclua do publish.
3. Comparar a base real da imagem atualmente executada com a candidata. Se não houver procedência suficiente para limitar o delta funcional, parar. Hash/label de Git sozinho não prova a composição do binário: conferir o DLL publicado daquela imagem Linux e a evidência da construção. Não comparar por suposição com DLL Windows de outro build.
4. Confirmar compatibilidade do retorno com schema/codec/estado escrito pela candidata e política de migrations da imagem anterior. Program.cs atual apenas verifica migrations fora de Development, mas uma imagem antiga pode ter outra política. Não executar migration, bootstrap administrativo, importador ou rollback de dados para permitir startup.
5. Confirmar nenhuma promoção concorrente e que o deploy automático permanece desabilitado/ausente em todos os escopos efetivos. Não alterar variáveis do GitHub ou habilitar flags. Se não for possível comprovar a ausência de promoção automática concorrente, suspender a janela manual.

Comandos futuros de consulta, sem habilitar nada:

```bash
: "${GITHUB_REPOSITORY:?owner/repo confirmado pelo operador}"
gh variable get NOPONTO_DEPLOY_ENABLED --repo "$GITHUB_REPOSITORY"
gh variable get NOPONTO_DEPLOY_ENABLED --repo "$GITHUB_REPOSITORY" --env production
gh run list --repo "$GITHUB_REPOSITORY" --workflow deploy.yml --limit 10 \
  --json databaseId,status,conclusion,headSha
```

Ausência, HTTP401/403 ou precedência não esclarecida precisam ser resolvidos pela verificação autorizada na UI, sem inferir false de uma falha de leitura. Não executar variable set, workflow enable, rerun ou push vazio.

## 2. Pré-verificação de servidor, somente leitura

Os caminhos vêm dos arquivos de operação do repositório; confirmar que são os efetivos no homeserver antes de usar. Não recriar arquivo ausente nem copiar o Compose local sobre o produtivo.

```bash
set -euo pipefail
umask 077
STATE=/home/guilherme/.noponto-deploy
BASE=/opt/stacks/noponto/docker-compose.yml
GTFS=/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml
RUNTIME_ENV=/opt/stacks/noponto/.env
PHYSICAL="$STATE/physical-eta.override.yml"
SHADOW="$STATE/historical-eta-shadow.override.yml"
DEPLOY_SCRIPT="$STATE/shadow-tools/deploy-api.sh"
PROJECT_DIR=/opt/stacks/noponto
for file in "$BASE" "$GTFS" "$RUNTIME_ENV" "$PHYSICAL" "$SHADOW" \
            "$DEPLOY_SCRIPT" "$STATE/active.env" "$STATE/active.run"; do
  test -r "$file" || { printf 'Preflight: arquivo requerido indisponivel.\n' >&2; exit 1; }
done
docker inspect --format '{{.Image}} {{.State.Running}} {{.State.OOMKilled}} {{.RestartCount}} {{.State.StartedAt}}' noponto_api
docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}/{{index .Config.Labels "com.docker.compose.service"}}' noponto_api
CURRENT_ID=$(docker inspect --format '{{.Image}}' noponto_api)
docker image inspect --format '{{json .RepoDigests}}' "$CURRENT_ID"
docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$CURRENT_ID"
docker exec noponto_api sha256sum /app/NoPonto.dll
sha256sum "$DEPLOY_SCRIPT"
IFS= read -r current_ref < "$STATE/active.env"
case "$current_ref" in
  GTFSRT_API_IMAGE=ghcr.io/guilhermedesales/noponto-api@sha256:*) CURRENT_REF=${current_ref#GTFSRT_API_IMAGE=} ;;
  *) printf 'Referencia ativa nao identificada; investigar sem promover.\n' >&2; exit 1 ;;
esac
test "$(docker image inspect --format '{{.Id}}' "$CURRENT_REF")" = "$CURRENT_ID"
IFS= read -r LAST_SEQUENCE < "$STATE/active.run"
[[ $LAST_SEQUENCE =~ ^[0-9]+$ ]]
curl --silent --output /dev/null --write-out 'root_http=%{http_code}\n' \
  --connect-timeout 2 --max-time 5 http://127.0.0.1:8080/
```

Resultado exigido: projeto/serviço noponto/api; imagem ativa e container correspondentes; Running=true/OOM=false; contador de reinícios estável; raiz HTTP200. Caso active.env legitimamente contenha tag de rollback, comprovar seu Image ID/procedência e revisar este gate em vez de aceitar automaticamente qualquer tag.

Comparar o hash do script remoto com o hash da cópia local revisada, registrado na evidência40. O preflight de quatro arquivos existe apenas nessa versão do contrato; parar em divergência. Não imprimir docker inspect completo, .Config.Env completo ou compose config na tela: podem conter segredos. Comparar renderização e configuração real em memória/área privada, incluindo env efetivo, command/entrypoint, mounts, limites, redes e portas; todos os deltas devem se limitar à imagem. Exigir Production, coleta10s, polling15s e opções GTFS inalterados, inclusive unidades de velocidade e crosswalk. Preservar exatamente os modos PhysicalEta/Shadow já usados; não permitir reconfiguração sob pretexto de implantação.

Depois de ter uma candidata aprovada, executar somente o preflight read-only do mecanismo existente:

```bash
: "${CANDIDATE:?ghcr.io/guilhermedesales/noponto-api@sha256:digest aprovado}"
: "${NEW_SEQUENCE:?sequencia aprovada maior que a ativa, sem conflito com CI}"
[[ $CANDIDATE =~ ^ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}$ ]]
[[ $NEW_SEQUENCE =~ ^[1-9][0-9]{0,8}$ ]]
(( NEW_SEQUENCE > LAST_SEQUENCE ))
NOPONTO_BASE_COMPOSE="$BASE" NOPONTO_OVERLAY_COMPOSE="$GTFS" NOPONTO_SHADOW_OVERRIDE="$SHADOW" \
  bash "$DEPLOY_SCRIPT" "$CANDIDATE" "$NEW_SEQUENCE" "$RUNTIME_ENV" "$STATE" "$PHYSICAL" "$SHADOW" --check
```

`--check` não faz pull/up, snapshot ou alteração de estado e não comprova saúde do binário candidato. Ele é necessário, mas não substitui a comparação do estado real ou a homologação. Não inventar número alto para vencer o gate de sequência.

## 3. Baseline e congelamento de retorno

Antes da troca, concluir as amostras de baseline da seção6 e o diagnóstico42. Se as transferências completas estiverem instáveis, a análise de ganho deve ficar suspensa até existir janela comparável; não aumentar timeout para tornar a amostra aparentemente saudável.

Bloco futuro que grava somente evidência privada de uma janela autorizada, sem tocar na API. O lock deve ser o mesmo do deploy. Não exportar esta pasta: poderá conter configuração sensível.

```bash
REVIEW_DIR=$(mktemp -d "$STATE/gps-acquisition.XXXXXX")
chmod 700 "$REVIEW_DIR"
(
  exec 9>"$STATE/deploy.lock"
  flock -n 9 || exit 1
  test "$(docker inspect --format '{{.Image}}' noponto_api)" = "$CURRENT_ID"
  cp -- "$STATE/active.env" "$REVIEW_DIR/pre-active.env"
  cp -- "$STATE/active.run" "$REVIEW_DIR/pre-active.run"
  printf '%s\n' "$CURRENT_ID" > "$REVIEW_DIR/pre-image-id"
  printf '%s\n' "$CANDIDATE" > "$REVIEW_DIR/candidate"
  printf '%s\n' "$NEW_SEQUENCE" > "$REVIEW_DIR/sequence"
)
```

O deploy existente captura sob seu próprio lock a imagem local anterior e os quatro insumos/env em previous/, antes de puxar/trocar a candidata; reconstrói também o Shadow realmente em execução. Não usar uma pasta previous/ de outra release como retorno desta janela. Preservar a imagem local e disco disponível; não executar prune, down, limpeza de volumes/streams/chaves ou remoção da imagem antiga.

## 4. Troca manual futura — não executar enquanto houver bloqueio40

```bash
# Somente após aprovação explícita do artefato corrigido e da janela.
NOPONTO_BASE_COMPOSE="$BASE" NOPONTO_OVERLAY_COMPOSE="$GTFS" NOPONTO_SHADOW_OVERRIDE="$SHADOW" \
  bash "$DEPLOY_SCRIPT" "$CANDIDATE" "$NEW_SEQUENCE" "$RUNTIME_ENV" "$STATE" "$PHYSICAL" "$SHADOW"
```

O script promove somente api, com --no-deps/--no-build/--pull never após pull explícito da candidata por digest. Não chamar ssh-deploy.sh/Actions nem --configure-shadow. O retorno automático em falha tratável é do próprio script, não certifica recuperação após SIGKILL/queda do host. Health requer três HTTP200, imagem correta, sem OOM/restarts, até180s; não basta para aprovar GPS.

Após sucesso, congelar a evidência de retorno da release atual, ainda sob exclusão mútua. Isso evita que previous/ seja sobrescrita por uma promoção posterior. Se houver divergência, parar e investigar; não sobrescrever estado alheio.

```bash
CANDIDATE_ID=$(docker image inspect --format '{{.Id}}' "$CANDIDATE")
(
  exec 9>"$STATE/deploy.lock"
  flock -n 9 || exit 1
  test "$(docker inspect --format '{{.Image}}' noponto_api)" = "$CANDIDATE_ID"
  test "$(cat "$STATE/active.env")" = "GTFSRT_API_IMAGE=$CANDIDATE"
  test "$(cat "$STATE/active.run")" = "$NEW_SEQUENCE"
  IFS= read -r previous_image < "$STATE/previous/image"
  test "$(docker image inspect --format '{{.Id}}' "$previous_image")" = "$CURRENT_ID"
  cp -a -- "$STATE/previous" "$REVIEW_DIR/rollback"
  printf '%s\n' "$CANDIDATE_ID" > "$REVIEW_DIR/candidate-image-id"
)
```

Verificar também que os arquivos congelados refletem os insumos do preflight, sem alterações externas durante a captura. O retorno usa shadow.override.json, que preserva o modo anterior efetivo, e não candidate-shadow.override.yml.

## 5. Critérios de aprovação, interrupção e reversão

Critérios propostos para aprovação do operador, não SLAs medidos:

| Gate | Critério |
| --- | --- |
| Código/artefato | Achados40 corrigidos com RED/GREEN; build completo aprovado; suíte oficial e regressões aprovadas; mesma imagem testada e promovida por digest. |
| Boot/contrato | Imagem esperada, configuração preservada, três root HTTP200 dentro180s, sem OOM/restart. Qualquer violação: interromper e acionar retorno. |
| Integridade GPS | Zero regressão de timestamp aceito/identidade; zero corpo parcial promovido; zero 304 sem representação correspondente; zero tentativa antes do prazo calculado. Qualquer ocorrência confirmada: retorno imediato. |
| Coleta | Após aquecimento2min e fonte completa disponível: cada modal deve registrar avanço de GPS/snapshot dentro120s. Fonte avançando em duas aquisições completas e API838 sem avançar por120s: interromper análise e reverter se atribuível à candidata. |
| Falhas/rate limit | Primeiro429 novo: suspender sondas adicionais e respeitar Retry-After. Dois429 em5min da aplicação: interromper rollout e reverter a candidata. Falhas/timeout da aplicação >20% em5min e aumento ≥10 pontos percentuais com fonte/rota estáveis: reverter. Sem estabilidade da fonte, marcar inconclusivo e diagnosticar; não declarar sucesso. |
| Recursos | OOM/restart: retorno imediato. CPU >80% do limite ou memória >85% do limite por5min, ou ciclo GPS p95>15s com piora≥25% frente à baseline: pausar e avaliar retorno. Não aumentar limites. |
| Ganho comparável | Nas janelas saudáveis definidas abaixo, mediana do atraso adicional cai≥10s e p95 não piora>10s. Se baseline já tiver mediana≤15s, exigir não piora>5s e funcionamento das correções de recuperação; relatar ganho não demonstrável nessa janela. |

Falta de dados mínimos não é aprovação. Saúde da raiz isolada, status Sucesso, contagem de veículos igual ou aumento de GET não comprovam melhoria. Um incidente de conectividade independente não é automaticamente regressão da candidata; se não for possível separar causalmente, manter rollback pronto e decisão conservadora do operador.

## 6. Comparativo antes/depois: GTFS-RT, REST838 e logs

Definição do experimento manual; nenhuma coleta foi executada. Usar o mesmo homeserver, rotas e ferramentas antes/depois. Conferir sincronização UTC via timedatectl, sem corrigir relógio. Baseline20min; troca; aquecimento2min excluído; depois20min, estendendo até30min apenas se necessário para amostras mínimas. Não mudar flags, timeout, intervalo, assinatura SignalR ou carga de teste.

- Fonte somente `https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions`; REST somente `http://127.0.0.1:8080/veiculos/linha/838` (registrar acesso público separado se quiser medir proxy, sem misturar séries).
- REST a cada15s; fonte nunca antes de30s entre inícios, obedecendo Cache-Control/Date/Age e Retry-After recebidos. Um coletor de observação apenas, sem retries em cascata ou cache-busting. Se aparecer cache maior, espaçar a sonda e admitir menor resolução. Não executar diagnóstico42 simultaneamente: ele somaria GETs e distorceria a janela.
- Parear inícios fonte/REST com diferença≤1s e conclusões≤2s para a análise saudável. Download lento/falho continua na taxa de falhas/latências; não entra artificialmente como idade zero nem é omitido do denominador. Registrar quantas rodadas foram excluídas e por quê.
- Ler no máximo2MB por corpo,5.000 entidades e2.000 posições REST; limite15s para a requisição total da fonte,5s para REST. Ler integralmente antes de decodificar. Ignorar corpo parcial para idade GPS; registrar bytes recebidos e falha. Protobuf deve ser decodificado por VehiclePosition.timestamp e VehicleDescriptor.id, não header timestamp ou FeedEntity.id. As helpers de wire já existem em gps-source-observation.py, mas o script tem loop de quatro fontes no nível de módulo: não o importar/executar sem adaptar o escopo. Não usar gps-readonly-probe/runtime-observation, pois acessam dependências adicionais.
- Associar em memória `vehicle.id.Trim().ToUpperInvariant()` a `ordem` REST, preservando zeros iniciais. Considerar apenas o conjunto comum com posições REST da838; não comparar mediana da frota inteira com a linha. Não persistir identidades, coordenadas ou corpos; saída só agregados, limites e motivo de indisponibilidade. Máximo5.000 entradas de estado por fase, descartadas ao final.
- REST404 é rodada sem posições; erro, nunca idade zero. Emitir totalAtivos/totalSemSinal e cobertura do conjunto comum separadamente, sem chamar retenção SemSinal de aquisição nova.

| Medida | Definição e relatório |
| --- | --- |
| Idade física fonte/API | Para o mesmo instante UTC da rodada t e veículo comum i: idadeFonte=t−gpsFonte_i; idadeREST=t−gpsREST_i. Não subtrair Age HTTP do GPS. p50/p95/max e quantidade por rodada; depois distribuição das medianas de rodada, para não dar mais peso a lotes grandes. |
| Atraso adicional | gpsFonte_i−gpsREST_i, em segundos, por veículo pareado. Positivo significa REST atrás da fonte; preservar valores negativos e explicar skew/avanço, sem clamp. |
| Movimento temporal | Quantidade de veículos com timestamp maior, igual ou regressivo entre rodadas; contagem de fonte avançando enquanto REST não avança. Não inferir movimento espacial de timestamp. |
| Cadência | Deltas de início de requests da sonda; separadamente, deltas de snapshots HTTP Downloaded do backend, 304 e LocalCache. Nenhum contador do gate prova GET no socket. |
| Falhas/latência | HTTP status, curl/transport result, bytes completos/parciais, DNS/TCP/TLS/TTFB/total, p50/p95, HTTP200/304/429/503, timeout e fonte sem amostra. |
| Backend | Por modal: Downloaded/NotModified/LocalCache/Failed com success/failure; acquisition_timeout separado porque pode ocorrer sem log de ReadCore. Ciclo15s, total_ms, novas, commits_aceitos/infra/rejeitados, snapshot pendente/ACK, linhas_publicadas/veiculos_enviados e recursos agregados. |

Limite de observabilidade: os eventos de aquisição são emitidos após o resultado; não existe neste patch um timestamp explícito de início de todo GET. Deltas de Downloaded/NotModified são intervalos entre conclusões e variam com a latência. Não chamá-los de start-to-start exato. Failed identifica tentativa que retornou falha ao source; um acquisition_timeout externo também pode ter ocorrido na fila/crosswalk e não deve ser somado cegamente como outro GET. A sonda tem seus próprios inícios/headers, que não certificam os do backend. Ausência de violação do prazo não pode ser provada apenas com esses logs; usar as regressões de relógio controlado e registrar o limite da observação produtiva. Uma futura instrumentação adicional exigiria escopo/revisão próprios.

Mínimo para comparar ganho:30 rodadas pareadas saudáveis por fase e ao menos5 veículos comuns em≥80% dessas rodadas. Pelo menos90% das tentativas de fonte devem completar e oscilação da mediana de idade da fonte entre fases deve ser≤10s, com diferença da taxa de falhas≤5 pontos percentuais. Senão, estratificar por qualidade de fonte/rede ou declarar inconclusivo. Não atribuir ao patch uma melhora originada só na fonte.

Comandos pontuais de validação e logs, futuros, sem imprimir posições brutas:

```bash
curl --silent --show-error --fail --max-time 5 --max-filesize 2000000 http://127.0.0.1:8080/veiculos/linha/838 |
python3 -c 'import json,sys; p=json.load(sys.stdin); print(json.dumps({k:p.get(k) for k in ("totalVeiculos","totalAtivos","totalSemSinal")}))'
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' noponto_api
docker logs --timestamps --since 20m --tail 20000 noponto_api 2>&1 |
python3 -c 'import collections,re,sys; c=collections.Counter();
for s in sys.stdin:
 m=re.search(r"GTFSRT source=(GTFSRT_BUS|GTFSRT_BRT) acquisition=(Downloaded|NotModified|LocalCache|Failed) success=(True|False|true|false)",s)
 if m: c["/".join(m.groups())]+=1
 if "failure=acquisition_timeout" in s: c["source_budget_timeout"]+=1
print(dict(c))'
```

Registrar se o tail atingiu20.000 linhas; truncamento impede estimar taxa/intervalos sem outra janela completa. Antes da candidata, o log antigo não tem todos os campos: métricas indisponíveis ficam explicitamente ausentes. O teste de idade deve usar posições em memória e as fórmulas acima; o comando pontual de contagem não mede idade nem substitui o experimento completo.

## 7. Rollback manual posterior a um deploy autorizado — comandos não executados

Usar a pasta privada congelada da seção4. Exigir que a candidata ainda seja a release ativa; não reverter uma promoção alheia. Preservar o lock até health e restauração dos marcadores. Retorno somente do binário/configuração da API, sem restaurar dados.

```bash
(
  set -euo pipefail
  exec 9>"$STATE/deploy.lock"
  flock -n 9 || exit 1
  RB="$REVIEW_DIR/rollback"
  IFS= read -r expected_candidate < "$REVIEW_DIR/candidate-image-id"
  IFS= read -r expected_previous < "$REVIEW_DIR/pre-image-id"
  IFS= read -r rollback_image < "$RB/image"
  IFS= read -r approved_candidate < "$REVIEW_DIR/candidate"
  IFS= read -r approved_sequence < "$REVIEW_DIR/sequence"
  test "$(docker inspect --format '{{.Image}}' noponto_api)" = "$expected_candidate"
  test "$(cat "$STATE/active.env")" = "GTFSRT_API_IMAGE=$approved_candidate"
  test "$(cat "$STATE/active.run")" = "$approved_sequence"
  test "$(docker image inspect --format '{{.Id}}' "$rollback_image")" = "$expected_previous"
  GTFSRT_API_IMAGE="$rollback_image" docker compose --project-name noponto \
    --project-directory "$PROJECT_DIR" --env-file "$RB/runtime.env" \
    -f "$RB/base.yml" -f "$RB/overlay.yml" \
    -f "$RB/physical-eta.override.yml" -f "$RB/shadow.override.json" \
    up -d --no-deps --no-build --pull never api >/dev/null 2>&1
  good=0
  deadline=$((SECONDS + 180))
  while (( SECONDS < deadline )); do
    state=$(docker inspect --format '{{.Image}}/{{.State.Running}}/{{.State.OOMKilled}}/{{.RestartCount}}' noponto_api)
    code=$(curl --silent --output /dev/null --write-out '%{http_code}' \
      --connect-timeout 2 --max-time 5 http://127.0.0.1:8080/) || code=000
    if [[ $state == "$expected_previous/true/false/0" && $code == 200 ]]; then
      good=$((good + 1))
      if (( good >= 3 )); then break; fi
    else good=0; fi
    sleep 2
  done
  (( good >= 3 )) || { printf 'Rollback sem health; intervencao manual necessaria.\n' >&2; exit 1; }
  printf 'Imagem anterior com health; conferir configuracao antes de finalizar marcadores.\n'
)
```

Depois de conferir os modos/env/mounts efetivos privados contra o snapshot, incluindo PhysicalEta e Shadow, finalizar separadamente sob lock. O intervalo entre os blocos tem marcadores ainda apontando para a candidata; manter a janela manual exclusiva e não repetir a primeira recriação. Se a conferência falhar, interromper para intervenção e conservar a evidência.

```bash
(
  set -euo pipefail
  exec 9>"$STATE/deploy.lock"
  flock -n 9 || exit 1
  IFS= read -r expected_previous < "$REVIEW_DIR/pre-image-id"
  IFS= read -r approved_candidate < "$REVIEW_DIR/candidate"
  IFS= read -r approved_sequence < "$REVIEW_DIR/sequence"
  test "$(docker inspect --format '{{.Image}}' noponto_api)" = "$expected_previous"
  test "$(cat "$STATE/active.env")" = "GTFSRT_API_IMAGE=$approved_candidate"
  test "$(cat "$STATE/active.run")" = "$approved_sequence"
  cp -- "$REVIEW_DIR/pre-active.env" "$STATE/active.env.tmp"
  mv -- "$STATE/active.env.tmp" "$STATE/active.env"
  cp -- "$REVIEW_DIR/pre-active.run" "$STATE/active.run.tmp"
  mv -- "$STATE/active.run.tmp" "$STATE/active.run"
)
```

Health ou conferência falhando mantém marcadores não certificados e exige intervenção; não repetir comandos cegamente nem fazer pull de latest. Depois do retorno, repetir observação GPS/REST, conservar todos os artefatos e registrar o evento. Não desfazer dados persistidos pela API, migrations, Redis, Outbox, journal ou histórico.
