# Janela controlada ITS + GTFS-RT — preparação atualizada em24/10/2026

Estado mais recente em26/10: [tentativa com handoff corrigido e rollback concluído](hybrid-window3-handoff-20261026.md). **Híbrido INCONCLUSIVO; NO-GO para repetir antes de revisar/testar localmente os gates durante a inicialização.** A captura única foi adotada e reteve as duas recriações sem gaps; ITS teve timeout de headers e não contribuiu nas composições observadas. A espera de Hybrid saudável impediu amostras durante boot e o rollback começou aos92,75s, sem prova conjunta898até90s. O prazo não foi flexibilizado. Produção retornou à release151 emGTFSRT_BUS/fallback vazio/FullFallback/tracefalse, BRTGTFSRT_BRT; cron removido e lock livre. Não armar nova tentativa automaticamente.

Atualização de26/10: [prova Debian do handoff em `phase=proof`](capture-handoff-debian-preflight-20261026.md) aprovada com cron real e mesma captura/PID/inode, arquivo completo e produção GTFS preservada. **GO somente para solicitar autorização separada de nova janela**, mediante novo preflight imediato; esta prova não entrou em `scheduled`, `baseline` ou ITS. O modo `scheduled` pode ativar ITS e não deve ser usado em provas GTFS-only. O parágrafo histórico abaixo registra o NO-GO anterior à prova Debian.

Atualização local anterior de26/10: [handoff da captura corrigido, testes e cinco hashes novos](capture-handoff-fix-20261026.md). Naquele checkpoint, o preflight Debian ainda estava pendente. Numa futura janela **autorizada**, iniciar um único capturador em `proof`, conferir que `state.capture` contém PID/nascimento, sessão, container e inode do arquivo, e só então passar `phase` a `scheduled`. O supervisor adota esse PID no `baseline`; nunca iniciar segundo capturador nem apagar `capture/`. Após retorno seguro, verifica arquivo uma vez fora do deploy lock, inclusive no caminho do watchdog. O hash anterior `d166f170…` de `its_window.py` ficou obsoleto; usar `75bfe9a9…` e os outros quatro hashes do relatório novo. Não reutilizar state, diretório ou scripts de execução de26/10.

Estado histórico em26/10: [tentativa interrompida no preflight](hybrid-window3-retry-20261026.md). Naquela tentativa houve NO-GO: a prova iniciou uma captura passiva, e o supervisor tentou iniciar outra no mesmo diretório exclusivo; `CaptureFailure` ocorreu antes do baseline formal/ITS. A [prova GTFS corrigida de364,86s](log-capture-burst-debian-preflight-20261026.md) passou, mas não cobria esse handoff. O estado posterior consta na prova Debian acima.

Correção local após NO-GO26/10: [captura tolerante a bursts, testes e novos hashes](log-capture-burst-fix-20261026.md). **Nova provaGTFS no Debian ainda obrigatória; não armar ITS.**

Estado26/10: [terceira janela bloqueada no preflight — captura com input_queue_overflow](hybrid-window3-20261026.md). **NO-GO para nova ativação** até correção local/testes e nova provaGTFS. ITS não foi ativada; produção permanece GTFS-only/traceOFF. Este resultado posterior substitui o GO limitado da prova de24/10.

Prova operacional limitada no Debian concluída, sem ITS: [cron, captura passiva, recursos e limpeza](log-capture-debian-preflight-20261024.md). GO para solicitar autorização separada; preflight e prova cron frescos continuam obrigatórios na futura janela.

Atualização local após release151: [captura dedicada e preflight da terceira janela](log-capture-preflight-20261024.md). Este procedimento prevalece sobre comandos/hashes históricos abaixo: cinco arquivos operacionais, captura independente obrigatória antes do baseline, digest151 exato, gate89890s e rollback GTFS/FullFallback/trace=false. Nenhuma terceira janela autorizada/instalada/executada.

Estado após publicação autorizada: [release151 implantada em GTFS-only, trace OFF e pendências da terceira janela](instrumented-release-deploy-20261024.md). Nenhum supervisor/cron da terceira janela foi instalado. Antes de armá-la, provar retenção dos logs por página e obter nova autorização.

Investigação posterior e instrumentação local, ainda sem nova publicação/ativação: [hybrid-window2-investigation-20261024.md](hybrid-window2-investigation-20261024.md). Os templates atuais acrescentam somente o toggle diagnóstico GpsPipelineTrace ao escopo BUS; exigem o novo binário e hashes, não a release149. Gate898 permanece90s; usar a parada verificada --request-stop no supervisor novo.

Preflight somente leitura atualizado da segunda janela, estado exato e gates ainda pendentes: [second-window-preflight-20261024.md](second-window-preflight-20261024.md). Os comandos desse relatório são futuros; não houve nova publicação/deploy/ativação.

**Segunda janela ainda não autorizada.** A proposta atual usa `BusSupportMode=Hybrid`, complementação por veículo ITS_CURRENT + GTFSRT_BUS, conforme [implementação e validação local](../its-hybrid.md). O padrão permanece `FullFallback`. Publicar uma nova imagem validada exige autorização; a imagem da primeira janela não implementa híbrido. Manter uma única API e watchdog independente do SSH, com retorno obrigatório aGTFS após10min. Os overrides deste diretório agora incluem a terceira chave de modo, inclusive no rollback. Nenhuma alteração produtiva foi realizada nesta preparação.

A inspeção abaixo é histórica de23/10; o estado posterior e o quinto override efetivo constam em [primeira janela](production-window-20261024.md). Antes de qualquer deploy autorizado, inspecionar novamente os labels/configuração efetiva, preservar todos os overrides não relacionados e renderizar candidata/rollback. Não copiar uma composição histórica sem essa conferência.

## Inspeção efetiva, somente leitura

SSH via agente, host verificado. Projeto `noponto`, serviço `api`, container `noponto_api`, uma instância. Composição efetiva:

1. `/opt/stacks/noponto/docker-compose.yml`
2. `/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml`
3. `/home/guilherme/.noponto-deploy/physical-eta.override.yml`
4. `/home/guilherme/.noponto-deploy/historical-eta-shadow.override.yml`

Diretório `/opt/stacks/noponto`, env `/opt/stacks/noponto/.env`. O render com um quinto arquivo em stdin comprovou que a proposta altera **somente as duas chaves de fonte BUS**; imagem, recursos e demais serviços ficaram iguais. Render não foi aplicado. O binário atualmente implantado não é apresentado como contendo ITS.

Fonte atual `GTFSRT_BUS`; BRT `GTFSRT_BRT`. Opt-ins BUS/BRT/crosswalk GTFS já `true`, velocidades `KilometresPerHour`, aquisição GTFS 30 s. Polling 15 s, coletor 10 s após aquisição, TTL ativo/recente 40/180 s. `EnriquecerTodasLinhas=true`, matching batch e combinado set-based ativos. Manter essas opções; não desligar enriquecimento nem ajustar TTLs para fazer a janela passar.

Host: quatro CPUs, 3.738 GiB de RAM; ~2,15–2,19 GiB disponíveis nas amostras. API limitada a uma CPU/768 MiB/256 PIDs, sem OOM/restarts. Amostras API 275–285 MiB e 5,26–103,89% CPU; PostgreSQL 898–903 MiB e 4,21–91,97%; Redis ~97 MiB/640 MiB e 1,06–28,21%. Percentual próximo de 100 é uma CPU; são amostras, não médias/capacidade garantida. Swap já utilizada ~325 MiB, sem atribuir seu uso a ITS.

Crosswalk consultado com **a mesma SQL do adaptador**, em transação READ ONLY, statement timeout 3 s e lock timeout 500 ms: 494 identidades únicas, 312 BUS e 32 BRT validadas, 150 sem validação modal; todas com direções cadastradas. 838, 898 e 006 são BUS validadas nas direções 0/1. As 150 identidades permanecem recusadas pelo GTFS-RT, como hoje; a flag global não torna todas as linhas elegíveis. Não corrigir/importar dados estruturais durante a janela. Schema tem 24 migrations, última `20261023014903_FormaPagamentoAparencia`; esta revisão não cria migration.

Baseline dos logs, janela limitada a 10 min/12.000 linhas: 48 ciclos completos, p95 11,822 s, máximo 13,424 s; matching p95 7,513 s; zero falhas CAS/viagem/crosswalk/timeout nos agregados. Leituras REST em instantes diferentes: 838 com 17–18 veículos, 898 com 12–13 e 006 com um. O frescor já oscila: na 838, 0–11 até60 s por amostra; até234,8 s no veículo mais antigo observado. Usar baseline imediatamente anterior à ativação, não esses números como SLA permanente.

## Publicação do binário, após autorização específica

O código/testes estão na branch de trabalho; falta a autorização para push/merge/publicação/deploy. Obter o digest imutável da **mesma revisão testada** no pipeline aprovado, sem usar `latest`. Registrar commit, digest, origem do build e imagem anterior. Não escolher uma tag de data parecida como prova de revisão.

O `.deploy/deploy-api.sh` atual exige BUS=GTFSRT_BUS. **Não remover esse guard.** A sequência recomendada é publicar/implantar primeiro o binário novo com as quatro composições e GTFS-RT atual, pelo mecanismo existente. Conferir health e GPS antes da janela ITS. Assim, o teste de fonte usa a imagem já implantada e seu rollback por configuração é imediato. Se esse deploy não passar, usar o rollback de imagem já existente; não iniciar ITS.

Somente após essa autorização e implantação, colocar os dois pequenos overrides desta pasta em diretório privado do operador, por exemplo `/home/guilherme/.noponto-deploy/its-window`. Não modificar os quatro arquivos efetivos, `.env`, bancos, Redis, BRT ou limites de recursos. Antes de mudar a fonte, observar por2min as mesmas linhas no aplicativo/SignalR ainda em GTFS-RT, para obter um baseline comparável com as assinaturas abertas.

## Troca de fonte e rollback — terminal supervisionado

Comandos abaixo são **futuros**, executados no homeserver após autorização. O operador deve manter o lock durante a janela e supervisionar os critérios de interrupção. Não executar outro deploy em paralelo; nenhuma segunda API, processo de laboratório ou fonte shadow deve escrever no Redis produtivo.

```bash
set -Eeuo pipefail
umask 077
state=/home/guilherme/.noponto-deploy
trial=$state/its-window
exec 9>"$state/deploy.lock"
flock -n 9 || { echo 'Outro deploy em andamento'; exit 1; }
export GTFSRT_API_IMAGE
GTFSRT_API_IMAGE=$(docker inspect --format '{{.Config.Image}}' noponto_api)
[[ $GTFSRT_API_IMAGE =~ ^ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}$ ]]
# Conferir esse digest com a revisão ITS aprovada, já implantada em modo GTFS.
base=(docker compose --project-name noponto --project-directory /opt/stacks/noponto
  --env-file /opt/stacks/noponto/.env
  -f /opt/stacks/noponto/docker-compose.yml
  -f /home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml
  -f "$state/physical-eta.override.yml"
  -f "$state/historical-eta-shadow.override.yml")
test -r "$trial/its.primary.override.yml"
test -r "$trial/gtfs.rollback.override.yml"

switch_source() {
  local override=$1 running
  "${base[@]}" -f "$override" stop --timeout 30 api || return 1
  # Uma API antiga ainda em execução impede iniciar outra escritora.
  running=$(docker ps -q --filter label=com.docker.compose.project=noponto --filter label=com.docker.compose.service=api) || return 1
  test -z "$running" || return 1
  "${base[@]}" -f "$override" up -d --no-deps --no-build --pull never api
}
rollback() { switch_source "$trial/gtfs.rollback.override.yml"; }
# Em erro, saída ou interrupção, voltar uma vez a GTFS, mantendo o lock.
armed=1
finish() {
  local rc=$?
  trap - EXIT HUP INT TERM
  if [[ $armed == 1 ]]; then
    if ! rollback; then echo 'ROLLBACK FALHOU: intervenção necessária' >&2; rc=1; fi
  fi
  exit "$rc"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM
switch_source "$trial/its.primary.override.yml"

# Conferir HTTP, origem no log, snapshot/ACK, CAS e socket; HTTP sozinho não aprova GPS.
curl --fail --silent --output /dev/null --connect-timeout 2 --max-time 5 http://127.0.0.1:8080/
docker stats --no-stream noponto_api transporte_postgres transporte_redis auth_api
# Executar as sondas de leitura descritas abaixo durante até10 minutos.
# Ao final do primeiro ensaio, retornar a GTFS, salvo nova aprovação explícita de permanência:
rollback
armed=0
trap - EXIT HUP INT TERM
# Liberar o lock apenas após conferir GTFS ativo/health/GPS e nenhuma API duplicada.
```

Antes do `switch_source`, renderizar em memória baseline, candidata e rollback e comparar: candidata deve diferir apenas nas três chaves BUS primária/fallback/modo; rollback deve retornar GTFSRT_BUS/fallback vazio/FullFallback. Conferir opt-ins GTFS, paths efetivos, imagem nova e limites. Não imprimir `compose config` nem ambientes completos. O render só prova configuração; o supervisor atualizado exige também log `BUS hybrid` com duas fontes saudáveis e contribuições positivas de ambas, impedindo considerar um binário antigo como híbrido só porque recebeu a variável. `tools/gps/inspect_its_homeserver.py` renderiza em memória, sem gravar/aplicar configuração.

O bloco mostra os comandos, não automatiza a observação. Não colar o bloco completo e considerar isso um ensaio de 10 min: manter o terminal aberto entre ativação e rollback. Em encerramento brusco/queda do SSH, SIGKILL ou daemon indisponível, a trap não é garantia; manter acesso alternativo e o comando `rollback` pronto. Para retorno emergencial em outro terminal, reconstruir `base` com os mesmos paths e digest aprovado e usar o override GTFS, parando API antes de recriar. Nenhum `down`, `prune`, `rm` de cache ou restart de dependências.

O próximo deploy automático com os quatro arquivos volta à política GTFS. Durante o ensaio o lock impede concorrência; para permanência ITS depois do ensaio é necessária decisão/configuração operacional adicional. Não alterar silenciosamente o contrato do deploy nesta etapa.

## Observação e condições de interrupção

Antes da troca e a cada ~30 s, observar 838, 898 e 006 no aplicativo conectado a `/hub/gps`, com uma inscrição por linha. Registrar somente agregados: veículos únicos, até40/60/180 s de GPS físico, regressões/duplicatas na mesma lista, status ativo/sem sinal e próxima parada. REST auxilia baseline; não substitui confirmação do socket. Não abrir um segundo backend apontando para o Redis produtivo.

Usar as métricas já emitidas para aquisição/completude, fonte efetiva, duração, geração/ACK, entrada/válidas/novas, CAS aceito/rejeitado/infra, matching, viagens e publicações. Executar a inspeção com `python -B tools/gps/run_readonly_ssh.py inspect`: inclui `probe_http.py` em memória por stdin, sem instalar/escrever scripts no servidor. `python -B tools/gps/run_readonly_ssh.py observe` observa sete linhas BUS e três BRT por seis rodadas de45s, exige GTFS-RT ativo e não arma janela. A sonda `docs/00-auditoria/gps-readonly-probe.py` continua independente. As ferramentas filtram dados sensíveis. CPU/RAM de todos os serviços e HTTP da autenticação também precisam acompanhamento pelo operador. Não forçar falhas no provedor/infraestrutura produtiva.

Interromper e voltar a GTFS se ocorrer qualquer um:

- OOM/restart da API, falha de boot/HTTP persistente por30 s, regressão de GPS aceito/publicado, identidade duplicada na mesma lista ou coordenada inválida aceita.
- Três ciclos consecutivos acima24 s (aprox. duas vezes o p95 anterior), um ciclo acima60 s, ou geração pendente sem ACK por90 s.
- Duas falhas consecutivas de ambas as fontes, falha CAS de infraestrutura recorrente em dois ciclos, ou fallback contínuo por5 min. Uma falha ITS com fallback saudável é comportamento previsto, não perda de ônibus.
- Memória API acima90% de768 MiB por60 s, RAM disponível do host abaixo512 MiB por60 s, aumento rápido de swap, ou CPU saturada com degradação dos demais serviços. A API já pode atingir100% de uma CPU em picos; não tratar um pico isolado como falha.
- Na 838/898 com baseline de pelo menos cinco veículos, queda de quantidade maior30% junto com queda de pelo menos20 pontos percentuais na fração até60 s por três observações consecutivas. Mais da metade dos itens publicados acima180 s em três observações também interrompe; não aumentar TTLs para mascarar isso.
- Autenticação ou BRT apresenta erros/latência maior que duas vezes o baseline por60 s, ou qualquer efeito nos outros serviços. Interromper mesmo que ITS esteja adquirindo mais veículos.

O primeiro erro ITS pode consumir até90 s antes de consultar o suporte, cujo deadline externo padrão é10 s; há risco transitório de `SemSinal`. ITS saudável pode entregar menos GPS até40 s em alguns ciclos. Ambas as fontes podem compartilhar falhas de infraestrutura do fornecedor; fallback não é redundância independente garantida. Os filtros de timestamp, matching e retenção continuam obrigatórios. Não esperar que toda posição adquirida apareça nas linhas observadas.

Na segunda janela, acompanhar BUS838/898/006/457/483/355/805 e BRT35/60/67 apenas como controles, mantidos emGTFSRT_BRT. O supervisor separa `lines` de `controls`, registra status/latência HTTP por linha e assina as dez no socket. Três leituras novas consecutivas com suporte GTFS falho, duas com ambas falhas, ou a degradação por linha já definida interrompem o ensaio. Logs repetidos não contam como novas falhas. Não modificar TTLs/cadência. Usar o supervisor versionado com cron/watchdog comprovado; o trecho shell acima é ilustrativo e não substitui supervisão independente. A segunda janela requer nova autorização explícita e publicação do novo digest; esta preparação não arma nem instala o supervisor.

## Decisão e evidências

Os resultados finais do ensaio conectado, testes e hashes estão no [relatório desta continuação](validation.md). Prontidão funcional é diferente de autorização: a janela só começa após aprovação específica do artefato e da mudança produtiva. Não foi feito push, merge, deploy, aplicação de override ou escrita/migration/limpeza de dados produtivos.
