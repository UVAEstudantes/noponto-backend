# Segunda janela híbrida — preflight de 24/10/2026

**Preflight de leitura aprovado; publicação e janela ainda não liberadas.** O código híbrido está validado localmente, mas `7bf00fc` ainda não existe no GitHub e não tem CI remoto/digest publicado. O homeserver executa o binário anterior, somente GTFS. O supervisor novo não está instalado/agendado. Nada foi publicado, implantado ou ativado neste preflight.

## Revisão, workflow e evidências

HEAD `7bf00fcacc6b357aa105cf1056392d0a298b98e1`, branch `feat/its-current-sppo`. Antes deste relatório, nenhuma alteração rastreada pendente; preservados os não rastreados `artifacts/`, `tools/ItsShadowLab/` e `tools/ItsShadowLab.Tests/`. Os três hashes de código operacional e os seis hashes de evidências do [híbrido](../its-hybrid.summary.json) foram recalculados e coincidem.326/326 direcionados,1057/1057 offline sem rede, E2E real descartável 1/1 e 24/24 Python são evidências existentes da revisão, não novas execuções .NET neste preflight.

O workflow remoto de `main` foi comparado ao local: idêntico após normalizar finais de linha. `main` permanece `bb774fffdfa708bb561cfe16347a07f41b5da725`; última [execução 147](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40107661744) concluída com sucesso para esse commit. A consulta GitHub do `7bf00fc` retorna commit inexistente, como esperado para revisão só local. Não há CI remoto novo a apresentar como aprovado.

- `pull_request` para `main`: CI, sem publicação/deploy.
- `push` em `main`: CI → build linux/amd64/publicação GHCR → deploy por `needs.build.outputs.digest`.
- Push da branch de trabalho, isoladamente, não publica imagem. Não há `workflow_dispatch`.
- `NOPONTO_DEPLOY_ENABLED=true` efetivo. **Merge em main pode enfileirar deploy produtivo**, não deve ser tratado como mera publicação de código. Environment `production` exige um reviewer. Não alterar variáveis, proteção ou workflow para evitar essa aprovação.
- Variáveis organizacionais de acesso ao env/state/PhysicalETA correspondem aos paths inspecionados. O override Shadow usa o default versionado dentro do state. Segredos requeridos estão disponíveis por escopos repositório/organização; somente presença/visibilidade foi consultada, nunca valores. Isso não revalida conteúdo de chave/token ou garante disponibilidade futura do registry/Tailscale.
- Workflow, scripts `.deploy`, Dockerfile e migrations não diferem de `origin/main`. O Dockerfile publica todo o projeto NoPonto, incluindo os arquivos híbridos. Em `Production`, `DatabaseStartupPolicy` verifica migrations pendentes e bloqueia startup, sem aplicá-las; o bootstrap Redis normal existente permanece. Nenhum comando administrativo/migration está autorizado para esta janela.

`python_runbook_blocks_syntax 2` significa **dois blocos Python compilados com sucesso para verificar sintaxe**, sem executá-los; não é código de erro. O parser Bash não estava disponível, portanto os comandos shell não foram validados por execução nem por `bash -n`.

Novas verificações locais: sonda/supervisor 24/24; testes `.ci`:6 aprovados/4 ignorados; `.deploy`:2 aprovados/34 ignorados por requisitos de shell/plataforma no Windows. Não reclassificar esses skips como aprovação completa do deploy. CI Ubuntu da nova revisão continua sendo uma condição anterior à implantação. Não foram repetidos .NET/offline/E2E ou benchmarks de frota.

## Produção observada somente com leitura

Inspeções em 18:04 e 18:13–18:14 UTC,15:04/15:13–15:14 local, sábado. SSH via agente e host verificado, sem `IdentitiesOnly=yes`.

| Item | Estado observado |
|---|---|
| Imagem atual/rollback anterior à próxima implantação | `ghcr.io/guilhermedesales/noponto-api@sha256:3ea342210229e489f2e987ee8b919a15ea9d20ded23929905bf2b50200c345a3` |
| Release/estado persistido |147; active.env corresponde à imagem em execução |
| API | Uma escritora; início 15:51:42,235 UTC, zero restart/OOM; rootHTTP 200 e GPS/socket funcionais. Não há Docker healthcheck da API configurado |
| BUS | `GTFSRT_BUS`, fallback vazio; variável BusSupportMode ausente, equivalente aFullFallback por padrão |
| BRT | `GTFSRT_BRT`; habilitações BUS/BRT e crosswalk verdadeiras; ambas velocidadesKilometresPerHour; intervalo HTTP 30 s |
| Cadência e retenção | Polling 15 s, coletor 10 s após aquisição, TTLativo 40 s/recente 180 s; sem alterações |
| Recursos |4 CPUs,3,738 GiB; disponível aproximadamente 2,18 GiB. API limitada a 1 CPU/768 MiB/256 PIDs. Amostras 227,9–248,2 MiB,0,58–5,25%CPU; não são picos garantidos |
| Redis/PostgreSQL | Ambos healthy, RedisPING=PONG; PostgreSQL/PostGIS 16. Sem restart/OOM. Os outros sete containers também running/sem restart/OOM |
| Schema/estrutura |24 migrations, última `20261023014903_FormaPagamentoAparencia`;494 identidades/linhas estruturais únicas. As dez linhas da coorte têm modal validado e direções 0/1 pela SQL seletiva real do adaptador, em transação READ ONLY com limites |
| Supervisão | Cron ativo; crontab do usuário ausente, zero jobs de janela; deploy.lock sem holder na amostra. Supervisor remoto ainda é o histórico da primeira janela |
| Conectividade ITS do host | Um GET SPPO, minuto anterior, limit 1, HTTP 200 em 0,767 s, CONECTA/ZIRIXok, corpo limitado 64 KiB. Não foi aquisição completa nem teste do namespace/TLS da nova imagem |

Compose efetivo, na ordem dos labels atuais:

1. `/opt/stacks/noponto/docker-compose.yml`
2. `/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml`
3. `/home/guilherme/.noponto-deploy/physical-eta.override.yml`
4. `/home/guilherme/.noponto-deploy/historical-eta-shadow.override.yml`
5. `/home/guilherme/.noponto-deploy/its-window-20261024-0e0737c725/gtfs.rollback.override.yml`

Env file `/opt/stacks/noponto/.env`; project directory `/opt/stacks/noponto`, projeto `noponto`, serviço `api`. O render em memória confirmou conexões iguais às usadas pela API e que a proposta muda apenas três chaves BUS, preservando imagem/serviços/limites. **Esse render usa a imagem antiga apenas para comparar configuração; não prova Hybrid nesse binário.** O quinto arquivo contém somente o rollback GPS histórico; não apagá-lo, pois os labels ainda o referenciam. O deploy versionado continua usando seus quatro Compose e guardGTFS; antes da execução autorizada, comparar novamente essa composição com os labels efetivos, descontando somente o override GPS conhecido.

Prévia REST, duas leituras separadas por aproximadamente 40 s. Não substitui o baseline imediatamente anterior à ativação **com o novo binário**:

| Modal/linha | Veículos leitura 1→2 | GPS<=60 s leitura 1→2 |
|---|---:|---:|
| BUS 838 |19→19|19→17|
| BUS 898 |8→8|8→8|
| BUS 006 |0→0|0→0|
| BUS 457 |21→21|21→19|
| BUS 483 |30→30|30→29|
| BUS 355 |4→3|3→3|
| BUS 805 |2→2|2→1|
| BRT 35 |4→4|4→3|
| BRT 60 |11→11|11→5|
| BRT 67 |10→9|8→5|

006 retornou 404 com a mensagem exata de linha vazia nas duas leituras; outras rotas 200. Não é indisponibilidade da API. Um socket real assinando as dez linhas recebeu 27 listas não vazias, uma conexão, zero erros/regressões/IDs duplicados na lista/coordenadas inválidas. Três ciclos com timestamp posterior ao início registrado da inspeção (18:13:24,866 UTC): máximo 7,012 s,1516 aceites CAS e zero falhas CAS de infraestrutura. A inscrição e o polling normais da aplicação continuam produzindo suas escritas operacionais; não houve escrita administrativa, limpeza ou reconfiguração.

## Sequência que depende de nova autorização

1. Autorizar publicação e implantaçãoGTFS separadamente da janela. Publicar a branch, abrir PR para `main`, exigir CI da revisão pretendida e rever a árvore resultante. Merge só com autorização que reconheça o deploy automático habilitado. Aguardar CI/build, registrar SHA do merge, run_id/run_number e digest de `needs.build.outputs.digest`; conferir os arquivos operacionais contra a revisão testada. Nunca promover `latest`/tag parecida como prova.
2. Aprovar o environment `production` apenas com esse digest e escopo. O mecanismo `.deploy/ssh-deploy.sh`/`deploy-api.sh` faz deploy somente da API ainda emGTFSRT_BUS, sob deploy.lock, com rollback de imagem existente. Não remover guardGTFS, não rodar Compose de dependências, migrations ou cleanup. Se houver falha, restaurar a imagem atual acima pelo mecanismo existente e **não iniciar Hybrid**.
3. Confirmar digest novo na API e active.env/active.run, uma escritora,API/Redis/PostgreSQL/GPS/SignalR e BRTGTFS. Confirmar startupProduction/schema sem pendências e limites/TTLs inalterados. A imagem `3ea342…` atualmente instalada não serve para esta janela.
4. Somente com autorização específica da janela, copiar supervisor `7bf00fc`, `probe_http.py` e os dois overrides para **novo** diretório privado. Usar imagem nova já implantada, nunca reutilizar ostate/digest antigo. Registrar e conferir hashes. Preservar o crontab anterior em arquivo privado; instalar somente um job marcado e provar execução independente no modo `proof` antes de armar.
5. Armar `scheduled` depois dessa prova; cron inicia o supervisor fora do SSH. O supervisor valida baselineGTFS, mantém o mesmo lock durante a janela, observa 120 s nas seteBUS e três controlesBRT, troca somenteAPI e exige log `BUS hybrid` com ambas saudáveis e contribuições positivas. Interromper se não conseguir provar o binário híbrido, mesmo queHTTP esteja saudável.
6. Monitorar até 600 s a partir do início da ativação, sem aguardar completar caso degradem critérios. Retorno obrigatório paraGTFSRT_BUS/fallback vazio/FullFallback no mesmo novo digest. Conferir saúde/GPS/socket/estado final antes de remover somente o job marcado, preservando outros jobs. **Não apagar o diretório/override final**, que passa a fazer parte dos labels da API. Arquivar relatório agregado e provar ausência de reativação agendada.

## Comandos futuros de preparação, ativação e rollback

**Não executados.** Executar no homeserver somente após os gates anteriores e autorização. `TRIAL` deve ser um diretório privado novo com os quatro arquivos revisados: `its_window.py`, `probe_http.py`, `its.primary.override.yml`, `gtfs.rollback.override.yml`. `APPROVED_IMAGE` deve conter o digest **novo** publicado; manter esses valores no registro da operação.

```bash
# No homeserver; somente depois da publicação/deploy GTFS autorizados.
set -Eeuo pipefail
umask 077
: "${TRIAL:?Diretório privado novo da janela}"
: "${APPROVED_IMAGE:?Digest novo aprovado e já implantado em GTFS}"
[[ $TRIAL =~ ^/home/guilherme/\.noponto-deploy/hybrid-window-[a-zA-Z0-9_-]+$ ]]
[[ $APPROVED_IMAGE =~ ^ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}$ ]]
test "$(docker inspect --format '{{.Config.Image}}' noponto_api)" = "$APPROVED_IMAGE"
test "$(docker inspect --format '{{.State.Running}}' noponto_api)" = true
sha256sum "$TRIAL/its_window.py" "$TRIAL/probe_http.py" \
  "$TRIAL/its.primary.override.yml" "$TRIAL/gtfs.rollback.override.yml"
# Comparar com os hashes locais da revisão autorizada antes de continuar.
```

Preparar `state.json` privado com `image=APPROVED_IMAGE`, `phase=proof`, marker único, hash do supervisor, obrigatório `mandatory_final_source=GTFSRT_BUS`, snapshot/hash do crontab e sua existência original. Instalar **um** job marcado, preservando todos os demais:

```text
* * * * * /usr/bin/python3 /home/guilherme/.noponto-deploy/hybrid-window-<RUN>/its_window.py --tick >/dev/null 2>&1 # noponto-hybrid-window-<RUN>
```

O modo `proof` não ativa fonte. Esperar `cron-proof.json` novo, confirmar `independent_cron=true`, horário posterior à preparação e ancestralidade cron. O JSON de prova contém horário, ancestrais e `independent_cron`; conferir separadamente caminho/hash dos arquivos e processo correto, pois esses campos não constam da prova. Não chamar `--tick` manualmente para simular prova do cron. A combinação supervisor/watchdog reutiliza o mecanismo já comprovado na primeira janela, mas **a instalação nova precisa de sua própria prova**.

```bash
# Ainda GTFS: só leitura, antes de armar.
python3 "$TRIAL/its_window.py" --validate
# Somente após cron proof e autorização: armar; próximo tick começa o baseline.
python3 - "$TRIAL" "$APPROVED_IMAGE" <<'PY'
import json,pathlib,sys,time,os
root=pathlib.Path(sys.argv[1]); target=root/'state.json'
s=json.loads(target.read_text()); proof=json.loads((root/'cron-proof.json').read_text())
assert s['phase']=='proof' and s['image']==sys.argv[2]
assert proof['independent_cron'] and 0 <= time.time()-proof['time'] <= 120
s.update(phase='scheduled',scheduled=time.time(),heartbeat=time.time(),deadline=time.time()+960)
temp=root/'state.tmp';temp.write_text(json.dumps(s));os.chmod(temp,0o600);os.replace(temp,target)
PY
```

O supervisor faz a ativação, parada antes de recriar, checagem de nenhuma API remanescente e rollback obrigatório; não executar outro `compose up` em paralelo. Cron avalia heartbeat/prazo independentemente do SSH, verificaPID/nascimento/cmdline antes de encerrar o grupo travado, toma o mesmo lock e repete rollback se necessário. A resolução de um minuto e os timeouts dos comandos podem atrasar o retorno de contingência; não é garantia de retorno instantâneo com Docker/host indisponível. O prazo normal de 600 s inicia a reversão; manter acesso alternativo e supervisão humana.

Rollback controlado antecipado: pedir parada ao supervisor peloPID **verificado** e manter cron ativo até `complete`; se ele estiver sem resposta, usar a mesma verificação dePID/nascimento/cmdline do watchdog, sem liberar/remover lock artificialmente. Com o supervisor encerrado e o lock livre, o comando abaixo reutiliza o rollback/health do controlador:

```bash
# Só após confirmar que o supervisor anterior encerrou. Falha se outro detiver lock.
python3 - "$TRIAL" "$APPROVED_IMAGE" <<'PY'
import importlib.util,pathlib,sys
root=pathlib.Path(sys.argv[1]).resolve();sys.path.insert(0,str(root))
spec=importlib.util.spec_from_file_location('manual_window',root/'its_window.py')
w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
s=w.load();assert s['image']==sys.argv[2]
lock=w.take_lock()
try: w.rollback(s,'operator_rollback')
finally: lock.close()
PY
```

Não usar esse comando para disputar lock com o supervisor nem forçar um segundo escritor. O watchdog também usa esse rollback, com imagem já local e `--pull never`, sem registry ou sessãoSSH. Antes de remover o job, exigir `phase=complete`, fonte realGTFSRT_BUS, modo FullFallback, BRTGTFSRT_BRT, imagem aprovada eAPI única. EstadoJSON sozinho não basta. Não restaurar cegamente um crontab antigo sobre jobs acrescentados por outros trabalhos.

## Monitoramento e interrupção

Preservar todos os critérios da [activation.md](activation.md): integridade GPS/socket, restart/OOM, três ciclos>24 s ou um>60 s, ACK pendente>90 s, falhas CAS, três aquisições novas com suporte falho/duas com ambas falhas, fallback contínuo>5 min, memória/host/swap/CPU e degradação de autenticação/BRT. Usar baseline recém-coletado para cobertura/frescor; 006 legitimamente vazia não é falha deAPI, nem permite dividir por zero. Perda completa/envelhecimento integral de linha relevante por três observações continua fatal. Critérios de CPU, swap e degradação de outros serviços também dependem da supervisão humana; não assumir que todos estão automatizados no controlador.

898 deve mostrar **novos GPS físicos durante Hybrid**, e não só posições retidas do baseline. Os DTOs públicos omitem proveniência interna; configuração, contagem global `selected_gtfs` e uma posição ainda em cache não provam isoladamente o caminho de cada veículo. Correlacionar em memória identidades/timestamps/coordenadas da 898 no feedGTFS normalizado e na publicação, junto da saúde/contribuiçãoGTFS e da fronteiraSPPO validada; persistir só agregados. Se não houver renovação física comprovada em até 90 s apesar de baseline relevante ou ocorrer perda de frescor sustentada, interromper. Não declarar prova de suporte por veículo só porqueHTTP 200 retornou.

Logs `BUS hybrid` fornecem contribuições/conflitos por fonte em agregado; coletor/polling fornecem geração/consumo/ACK/CAS; Redis/REST/socket confirmam aceitação e visibilidade. Registrar separadamente aquisições, aceites e publicações, sem exigir promover toda a frota. Não alterarTTLs, filtros ou banco para melhorar números.

## Gates restantes

Faltam autorização para publicação/merge/deployGTFS, CI remoto do candidato, digest novo e bootGTFS aprovado; depois autorização da janela, instalação/prova do cron novo e baseline novo com evidência 898. Conferência curta de estado/lock deve ser refeita imediatamente antes de qualquer mutação autorizada. A capacidade sob carga híbrida real e a prova produtiva da 898 ainda pertencem à janela, não a este preflight.

[Resumo verificável](second-window-preflight-20261024.summary.json). Artefatos agregados locais em `artifacts/hybrid-preflight-20261024/`, sem credenciais ou posições/identidades individuais. Nenhuma rotina de publicação, aprovaçãoGitHub, cron, controle deAPI ou rollback foi executada nesta etapa.

## Finalização local após interrupção

Documentos e hashes conferidos novamente, sem repetir coletas ou acesso SSH. Corrigida a contagem textual de ciclos: são três após o timestamp inicial exato; o evento às 18:13:24,009 UTC precede esse limite e foi excluído, como já constava no JSON. Máximo de 7,012 s e 1516 aceites CAS permanecem iguais. Supervisor 24/24 e dois blocos Python de comandos aprovados em verificação curta; nenhum comando operacional dos blocos foi executado. Workflow remoto novamente idêntico ao local, deploy habilitado e environment com um reviewer. O estado produtivo descrito continua sendo a observação arquivada às 18:27:37 UTC, não uma coleta nova.

**GO para solicitar autorização de publicação/implantação inicial GTFS; NO-GO para executar a janela agora.** Após autorização, ainda são obrigatórios CI remoto, digest imutável, deploy GTFS saudável e, antes de Hybrid, autorização da janela, prova independente do novo supervisor e baseline imediato. Sem push, merge, deploy, instalação de cron ou alteração produtiva nesta finalização.
