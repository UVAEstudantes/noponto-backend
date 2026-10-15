# Etapa 4C.1 — Preparação da release diagnóstica GTFS-RT

Data: 2026-10-11. **BLOQUEADA para aprovação de deploy da árvore atual.** A integridade focal está homologada, mas não há vínculo verificável entre a imagem produtiva informada e uma base de fontes/schema, nem composição produtiva efetiva disponível para certificar fontes/ETA/limites/rollback. A preparação documental foi concluída; nenhum artefato Docker foi construído/publicado e nenhum serviço foi alterado.

## Inventário e procedência

Consultados AGENTS.md, HISTORICO.md, relatórios [11](11-instrumentacao-funil-operacional-20261011.md), [12](12-validacao-integracao-funil-20261011.md), [13](13-homologacao-conectada-funil-20261011.md) e [14](14-fechamento-materializacao-funil-20261011.md). Git local sem fetch: branch **fix/gps**, **sem upstream**, HEAD **6f64538b47cb3987c5bf023d9878931b69228dac**. A referência local origin/main aponta ao mesmo SHA; isso não prova o estado remoto atual ou a imagem implantada. Working tree modificado, com arquivos novos não rastreados; sem commit de candidata.

A base completa proposta para revisão é o conteúdo rastreado desse HEAD, acrescido SOMENTE do delta permitido abaixo. Esse inventário de preparação não é um manifesto de imagem já construída. Um artefato imutável futuro exigirá fontes congeladas, dependências e digest registrados depois de autorização separada.

### Delta exato permitido da instrumentação

Arquivos produtivos modificados em relação ao HEAD:

```text
NoPonto/2-Application/Services/GPS/GpsCicloPerformance.cs
NoPonto/2-Application/Services/GPS/GpsPollingService.cs
NoPonto/2-Application/Services/GPS/RetryOperacionalGps.cs
NoPonto/2-Application/Services/GPS/ViagemObservadaService.cs
NoPonto/2-Application/Services/GPS/ViagemOperacional.Integridade.cs
NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs
NoPonto/2-Application/Services/GPS/ViagemOperacional.cs
NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs
```

Novo fonte produtivo: `NoPonto/2-Application/Services/GPS/GpsCicloPerformance.Operacional.cs`.

Testes modificados:

```text
NoPonto/5-Testes/IntegridadeCircularRegraTests.cs
NoPonto/5-Testes/RetryOperacionalGpsTests.cs
NoPonto/5-Testes/ViagemDuravelPostgresTests.cs
NoPonto/5-Testes/ViagemObservadaServiceTests.cs
NoPonto/5-Testes/ViagemOutboxBatchTests.cs
```

Novos testes: `NoPonto/5-Testes/GpsFunilOperacionalTests.cs` e `NoPonto/5-Testes/GpsFunilLogsTests.cs`. Testes pertencem à revisão/validação e o projeto atual também os compila no mesmo assembly; não há proposta de mudar esse empacotamento.

Delta separado e já autorizado: `NoPonto/3-Domain/Entities/FormaPagamento.cs` contém SOMENTE as duas declarações futuras Cor/Icone comentadas. HEAD não possui propriedades ativas: os comentários preservam a intenção sem ampliar modelo/contrato. Manter comentadas, sem Ignore EF, migration ou ajuste de snapshot.

Documentação de procedência permitida:

```text
NoPonto/HISTORICO.md
docs/00-auditoria/09-passagens-pos-gtfsrt-20261011.md
docs/00-auditoria/09-passagens-pos-gtfsrt-20261011.sql
docs/00-auditoria/10-avanco-operacional-pos-matching-20261011.md
docs/00-auditoria/11-instrumentacao-funil-operacional-20261011.md
docs/00-auditoria/12-validacao-integracao-funil-20261011.md
docs/00-auditoria/13-homologacao-conectada-funil-20261011.md
docs/00-auditoria/14-fechamento-materializacao-funil-20261011.md
docs/00-auditoria/15-preparacao-release-diagnostica-20261011.md
```

Delta preexistente FORA dessa release: `docs/03-funcionalidades/08-schedules-coleta-builder-e-importacao.md`, sobre builder ferroviário/Japeri. Preservado no workspace, excluir de promoção diagnóstica. Não excluir nem sobrescrever esse trabalho para preparar a candidata.

### Arquivos ignorados e contexto de build

Inspeção seletiva, sem ler credenciais: `.env`, bin, obj e lab-output presentes/ignorados; NoPonto/docs contém documentação local, e .env.local.example é rastreado. `.dockerignore` exclui .env/.env.*, bin/obj e arquivos temporários, inclusive o exemplo de ambiente. Não exclui lab-output ou toda documentação local; o csproj exclui lab-output de Compile/Content/None/EmbeddedResource, mas Docker COPY ainda pode transportar esse diretório para o estágio de build. Uma enumeração recursiva dos ignorados encontrou caminhos de laboratório excessivamente longos; foi interrompida, sem limpeza. Não alegada enumeração completa desses artefatos.

Não construir a partir do workspace sujo: numa etapa autorizada, criar contexto a partir de fontes rastreadas congeladas + allowlist exata de novos arquivos. Não incluir .env, resultados/laboratórios/artefatos ignorados. Nenhuma alteração de .dockerignore/Dockerfile feita aqui. Dados ferroviários rastreados requeridos pelo csproj permanecem na base; não remover assets para diminuir contexto sem verificar referências.

## Diferenças em relação à produção conhecida

Imagem atual informada pelo operador: **sha256:dbe503a480387dc1ab6e54857cc7f052d3524a61991666fc14e022dac833af79**, início 2026-10-10T14:37:46.200180815Z. Não localizada no daemon local por esse ID; nenhum pull ou acesso remoto. Não há revision/manifesto de fontes ou histórico EF certificado para ela. Um Image ID não é automaticamente o digest de manifesto do registry: não converter esse valor diretamente em ghcr.io/...@sha256 sem prova.

Referência histórica verificável no repositório: GTFS-RT local03, Image ID adebd170..., construída de 551b7342138e5a3f982d02ba424c82ea714e6571 COM working tree e consolidada posteriormente (66f2e26 e documentação 2c5323e). Essa referência não é a imagem atual. O inventário exato de paths entre 2c5323e e HEAD está no apêndice; não deve ser apresentado como diff dos bytes da imagem dbe503.

Deltas funcionais preexistentes nesse intervalo, separados da instrumentação:

- Política fail-closed de migrations no startup e pipeline CI/deploy API por digest.
- Deduplicação GPS pelo timestamp Redis, incluindo mudanças em cache/polling e testes.
- MVP tarifas/formas de pagamento: endpoints/DTOs/regras/store/contexto e migration TarifasPagamentosMvp; há remoções/substituições de código legado nesse commit, anteriores à instrumentação.
- Separação do modal BRT nas importações GTFS e respectivas regras/testes.

HEAD já contém esses deltas. Uma imagem da árvore atual só é exclusivamente diagnóstica em relação à produção se todos eles já estiverem na imagem dbe503 e o schema for compatível. Isso é desconhecido; não recomendada promoção integral nem cherry-pick cego para outra base. Primeiro obter o manifesto/revision real; então escolher uma base comprovada e conferir novamente o delta permitido.

## Revisão focal de impacto e configuração

Comparação contra HEAD confirma agregação/contextos/wrappers e hooks diagnósticos: regras internas preservadas, retorno preservado, chamadas internas sem dupla contagem, Interlocked/Volatile e AsyncLocal novas/retry, eventos contados depois de CommitAsync. Nenhum novo valor alimenta matching, TTL, baseline, passagens, mudança operacional, circularidade, decisões de retry, ETA/ML, sampling ou exportação. Nenhum delta de migrations, csproj, Program, Compose ou contratos persistidos contra HEAD. O deslocamento preexistente da medição do stopwatch para depois do retry inicial mantém o denominador legado; o contexto diagnóstico inclui esse retry.

Mantidos 164 contadores/zeros em seis temas, cycle_id e UTC; sem logs novos individuais por GPS/veículo. Warnings/log individual de retry que já existiam foram preservados. Logger desabilitado não altera regras/retornos/transações; os agregados permanecem independentes do logger. Ressalva de custo: EmitirFunilOperacional não testa IsEnabled antes de formatar seis payloads, portanto há formatação/alocações mesmo com Information desabilitado. Não foi introduzida correção nesta preparação nem alegado overhead zero ou benchmark desse caso.

Defaults de GpsEtaOptions: ausência de ML:ETA:Enabled significa TRUE. A imagem não habilita GTFS-RT/desliga ETA por si só. Flags efetivas atuais devem ser confirmadas por whitelist, preservando o estado desejado:

```text
ASPNETCORE_ENVIRONMENT=Production; DOTNET_ENVIRONMENT ausente ou Production
GpsSources__BusPrimarySource=GTFSRT_BUS; GpsSources__BrtPrimarySource=GTFSRT_BRT
GtfsRealtimeGps__BusEnabled=true; GtfsRealtimeGps__BrtEnabled=true
GtfsRealtimeGps__CrosswalkValidated=true
ML__ETA__Enabled=false; EtaV2__Enabled=false; EtaV2__ShadowEnabled=false
GpsPolling__EnriquecerTodasLinhas=true (informado pelo operador)
GpsPolling__MudancaOperacionalHabilitada: obter o valor efetivo, não presumir
```

Também preservar polling/TTL/cadência/unidade de velocidade/fontes shadow/sampling/backpressure atuais. Não alterar flags para satisfazer a whitelist nesta etapa; obter composição vigente e resolver qualquer divergência antes de autorização futura.

Compose validado estaticamente via config JSON com env-file sintético, somente whitelist impressa e variáveis restauradas. Base: latest, Production, fontes ZIRIX_DIRECT/BRT_CURRENT, GTFS-RT false, ML__ETA__Enabled ausente, sem limites de CPU/RAM da API declarados. Base+override: build local, noponto-api:local, Development, mesmos defaults de fontes. Não usar essa composição de desenvolvimento no homeserver: pode aplicar migrations automaticamente e reativar ETA.

O caminho .deploy exige base `/opt/stacks/noponto/docker-compose.yml`, overlay `/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml`, env e estado externos. Esses arquivos efetivos não estão disponíveis nesta árvore. O script exige Production, sem build/command/entrypoint alternativos, BUS/BRT GTFS-RT, ETA legado e V2 OFF, 1 CPU/768 MiB/256 PIDs. São gates do script, não limites observados do homeserver. Não alterar limites nem composição para fazê-los passar sem avaliação autorizada.

## Startup, migrations e compatibilidade

Program carrega ambiente sem sobrescrever variáveis existentes e tem caminhos administrativos separados; revisar argv/entrypoint do artefato para garantir startup normal. Antes de HostedServices/app.Run: cria scope EF e chama DatabaseStartupPolicy com GetPendingMigrationsAsync/MigrateAsync/bootstrapRedis.

- Development explícito: MigrateAsync automático.
- Production/outros: somente consulta de migrations; falha de consulta ou pendência bloqueia startup, sem migration/fallback/bootstrap.
- Após gate aprovado: bootstrap de timestamps Redis pode escrever marcadores, antes do polling. Startup normal não é integralmente read-only mesmo sem migration; preservar esses marcadores no retorno.

Nenhuma migration nova na instrumentação. Última na candidata: **20261010002416_TarifasPagamentosMvp**, posterior a IntegridadeCircularDuravel. Snapshot sincronizado comprovado offline. Contra referência local03, existe delta de migration/snapshot de tarifas. Pendência EF no banco produtivo é **DESCONHECIDA**; testes em banco novo migrado não provam ausência de pendência em produção. Não executar migrations ou iniciar API para descobrir isso.

Consulta manual futura, somente após identificar schema da aplicação e obter autorização de leitura, usando sessão já administrada pelo operador, sem conexão em linha de comando:

```sql
BEGIN READ ONLY;
SET LOCAL statement_timeout = '2s';
SET LOCAL lock_timeout = '500ms';
SELECT "MigrationId" FROM "SCHEMA_CONFIRMADO"."__EFMigrationsHistory" ORDER BY "MigrationId";
ROLLBACK;
```

É tabela pequena de metadados, não scan de histórico/Outbox. Comparar com IDs compilados/manifesto da candidata; não consultar schema presumido e não aceitar ausência da tabela como schema aprovado. Sem qualquer escrita/migration. Não basta conferir somente o último ID se houver lacunas.

## Publicação imutável e retorno

Único workflow local: .github/workflows/deploy.yml. PR para main valida; push para main valida e publica imagem automaticamente depois do CI. Tags latest/data-SHA/build são aliases, não identidade imutável. Deploy automático depende também de NOPONTO_DEPLOY_ENABLED=true e ambiente production/proteções cujo estado remoto é desconhecido. Antes de futura publicação, obter estado real desses controles: merge/push pode implantar, não apenas publicar. Nenhuma ação Git/remota executada aqui.

CI offline usa allowlist .ci/offline-tests.json: contém algumas regressões existentes, mas não seleciona todas as classes novas GpsFunilOperacional/GpsFunilLogs/RetryOperacionalGps; testes conectados também não estão nesse job. Guardar evidência local homologada e exigir validação explícita da candidata congelada, sem presumir cobertura pelo simples CI verde. Nenhum workflow alterado.

Estratégia futura: congelar a base de produção comprovada + delta permitido, revision e hash de conteúdo; repetir validações nesse conjunto; registrar dependências/SDK resolvidos e digest do runtime (Dockerfile já fixa ASP.NET por sha256:0712631...). SDK usa tag mutável 9.0-bookworm-slim: registrar o ID/digest realmente usado e não prometer build reproduzível byte a byte. Publicar somente quando autorizado, guardar digest retornado pelo build-push-action, ID/arquitetura linux/amd64 e manifesto/hash de fontes. Promover por `ghcr.io/guilhermedesales/noponto-api@sha256:DIGEST_DE_REGISTRY_VERIFICADO`, nunca latest. Não foi construído nem publicado artefato nesta etapa.

O script existente usa lock, sequência crescente, snapshot privado de base/overlay/env, guarda a imagem anterior local e recria SOMENTE api com --no-deps/--no-build/--pull never. Health exige ID correto, Running=true, OOMKilled=false, RestartCount=0 e três HTTP 200, timeout padrão 180 s. Rollback restaura active.env/active.run e tenta somente a API anterior. Não desfaz writes/schema. Env snapshot é sensível, não anexar ou imprimir; retorno exige preservar PostgreSQL/Redis/Outbox/histórico/marcadores de integridade e timestamp.

Antes de armar esse script: provar compatibilidade do binário anterior com schema/codec/estados circulares/candidatos/payloads atuais e política de startup. Não escolher local02/local03/build antigo só pela disponibilidade: algumas versões chamam Migrate no startup e podem ignorar estado operacional mais novo. Homologação offline de codecs ajuda, mas não certifica um ID de imagem desconhecido. Rollback deve usar a imagem anterior exata previamente verificada, sem downgrade/migration reversa/apagar chaves/volumes ou reiniciar dependências. Se incompatível, interromper preparação em vez de prometer retorno seguro.

## Checklist antes de solicitar aprovação de deploy

- [ ] Vincular dbe503 a fontes/manifesto/artefato e identificar seu digest de registry quando houver; decidir base da candidata sem novos deltas funcionais.
- [ ] Confirmar migrations aplicadas e compatibilidade da candidata/retorno; não preparar migration para resolver esse gate automaticamente.
- [ ] Congelar allowlist e hashes; excluir documentação ferroviária alheia e artefatos ignorados sem remover trabalho local.
- [ ] Obter composição efetiva redigida, ambientes Production, GTFS-RT BUS/BRT e ETA OFF explícitos, flags atuais preservadas, limites/driver/logging reais.
- [ ] Confirmar acionadores CI/deploy/proteções e prever indisponibilidade da API única/SignalR durante recriação; não executar scripts de implantação nesta etapa.
- [ ] Capturar baseline real de recursos/ciclos/erros, identificar reserva mínima do host com operador e certificar imagem de rollback disponível/compatível.
- [ ] Conferir loglevel Information/filtros e capacidade do driver/coletor; seis partes por ciclo, 164 contadores, sem interpretar parte ausente como zero.
- [ ] Autorizar separadamente build/publicação/deploy e medição curta; ter operador/retorno e critérios de interrupção definidos.

## Roteiro manual read-only de 10–15 minutos

Comandos abaixo são PROPOSTOS para execução futura PELO OPERADOR no host, não executados pelo agente. Nenhum SSH, serviço novo, driver/Dozzle alterado. Repetir uma janela de 10–15 min antes e outra depois de deployment separadamente autorizado, em condições comparáveis de oferta/cadência; preservar flags. Registrar UTC, imagem/uptime, frio de startup separado e baseline mediana/p95, não usar um ciclo isolado como causa.

### Identidade, saúde e recursos (saída limitada)

```bash
# Bash no host; nomes devem ser confirmados pelo operador, sem listar segredos.
api=noponto_api; pg=transporte_postgres; redis=transporte_redis
for c in "$api" "$pg" "$redis"; do
  docker inspect --format '{{.Name}} image={{.Image}} running={{.State.Running}} oom={{.State.OOMKilled}} restarts={{.RestartCount}} started={{.State.StartedAt}} health={{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} memory={{.HostConfig.Memory}} swap={{.HostConfig.MemorySwap}} cpu_nano={{.HostConfig.NanoCpus}} cpu_quota={{.HostConfig.CpuQuota}} cpu_period={{.HostConfig.CpuPeriod}} pids={{.HostConfig.PidsLimit}} logdriver={{.HostConfig.LogConfig.Type}}' "$c"
done
image_id=$(docker inspect --format '{{.Image}}' "$api")
docker image inspect --format 'id={{.Id}} arch={{.Architecture}} digests={{json .RepoDigests}} revision={{index .Config.Labels "org.opencontainers.image.revision"}}' "$image_id"
free -b
df -h / /var/lib/docker    # ajustar ao DockerRootDir real, não executar du/scans
cat /proc/pressure/memory /proc/pressure/io /proc/pressure/cpu
```

Limites zero/ausentes não significam recursos livres; considerar limites cgroup/host. Não imprimir docker inspect completo, Config.Env, compose config completo, env files ou logs de healthcheck. Saúde ausente não é saudável: nesse caso usar sinais já existentes, sem inventar probes pesadas. Swap/cpu_percent precisam ser interpretados com quota/número de cores.

Whitelist para flags existentes sem expor o restante do ambiente:

```bash
docker inspect --format '{{json .Config.Env}}' "$api" | python3 -c '
import json,sys
keys={"ASPNETCORE_ENVIRONMENT","DOTNET_ENVIRONMENT","GpsSources__BusPrimarySource","GpsSources__BrtPrimarySource","GtfsRealtimeGps__BusEnabled","GtfsRealtimeGps__BrtEnabled","GtfsRealtimeGps__CrosswalkValidated","GtfsRealtimeGps__BusSpeedUnit","GtfsRealtimeGps__BrtSpeedUnit","GtfsRealtimeGps__IntervalSeconds","ML__ETA__Enabled","EtaV2__Enabled","EtaV2__ShadowEnabled","GpsPolling__EnriquecerTodasLinhas","GpsPolling__MudancaOperacionalHabilitada"}
env=dict(x.split("=",1) for x in json.load(sys.stdin) if "=" in x)
for k in sorted(keys): print(k+"="+env.get(k,"<ausente: conferir configuração/default>"))
'
```

Whitelist mostra variáveis, não resolve appsettings/argumentos/providers automaticamente. Não classificar flag ausente como OFF. Se composição efetiva continuar incerta, parar antes do deploy.

Coleta para arquivos pequenos, diretório NOVO com modo privado, sem saída contínua no terminal:

```bash
umask 077
out=$(mktemp -d /tmp/noponto-diag-XXXXXXXX)
inicio=$(date -u +%Y-%m-%dT%H:%M:%SZ)
# Só leitura; timeout encerra a coleta, não containers.
timeout 900s docker stats --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.MemPerc}} {{.NetIO}} {{.BlockIO}} {{.PIDs}}' "$api" "$pg" "$redis" > "$out/stats.txt" & stats_pid=$!
timeout 905s vmstat -w 10 91 > "$out/vmstat.txt"
wait "$stats_pid" || true  # 124 é término de timeout esperado, registrar outros erros
fim=$(date -u +%Y-%m-%dT%H:%M:%SZ)
printf 'inicio=%s fim=%s\n' "$inicio" "$fim" > "$out/janela.txt"
free -b > "$out/mem-final.txt"
df -h / /var/lib/docker > "$out/disco-final.txt"
```

Confirmar previamente capacidade para poucos MiB de coleta e retenção do driver; conferir timestamps inicial/final realmente capturados. vmstat primeira linha é média desde boot, separar das amostras da janela. Opcional iostat se JÁ instalado, sem instalar agente: `iostat -xz 10 91`, para arquivo limitado. I/O BlockIO Docker é acumulado, comparar deltas, não atribuir picos ao PostgreSQL só pelo total.

### Logs seletivos com limite e detecção de incompletude

Depois da janela, executar o pequeno coletor Python abaixo no mesmo shell. Ele não usa --tail (evita perder silenciosamente o começo), limita janela/tempo/volume lido e imprime SOMENTE sumário de contagens. Salva somente tokens numéricos e metadados do funil/performance, sem mensagens de erro/payloads/IDs de GPS. Um limite excedido torna a coleta inconclusiva, não comprova perda no coletor. Não despejar o arquivo bruto no terminal.

```bash
API_DIAG="$api" DIAG_START="$inicio" DIAG_END="$fim" DIAG_OUT="$out" python3 - <<'PY'
import collections, json, os, re, subprocess, time
p=subprocess.Popen(["docker","logs","--timestamps","--since",os.environ["DIAG_START"],"--until",os.environ["DIAG_END"],os.environ["API_DIAG"]],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
deadline=time.monotonic()+60
read_bytes=0; read_lines=0; limited=False; cycles={}; perf=0; malformed=0
numeric=re.compile(r"\b([A-Za-z_][A-Za-z_0-9]*)=(-?\d+(?:\.\d+)?)\b")
meta=re.compile(r"cycle_id=([a-f0-9]{32}) inicio_utc=(\S+) parte=(\d+) partes=(\d+) tema=([a-z_]+) contadores=(.*)")
themes=["resultados","decisoes","movimento","passagens","predecessor_nova","predecessor_retry"]
errors=collections.Counter()
# Timeout do processo evita bloquear indefinidamente numa leitura de pipe.
import threading
def expire():
    global limited
    if p.poll() is None: limited=True; p.kill()
timer=threading.Timer(60,expire); timer.start()
try:
 with open(os.path.join(os.environ["DIAG_OUT"],"agregados.jsonl"),"x",encoding="utf-8") as output:
  while True:
   raw=p.stdout.readline(65537)
   if not raw: break
   read_bytes+=len(raw); read_lines+=1
   if len(raw)>65536 or read_bytes>8*1024*1024 or read_lines>20000 or time.monotonic()>deadline:
    limited=True; p.kill(); break
   line=raw.decode("utf-8",errors="replace")
   if "Performance GPS:" in line:
    perf+=1; output.write(json.dumps({"kind":"performance","numbers":numeric.findall(line)})+"\n")
   elif "Funil GPS:" in line:
    # Formato textual local. JSON/escape/multiline desconhecido exige parser revisado.
    m=meta.search(line)
    if not m: malformed+=1; continue
    cid,utc,part,total,theme,payload=m.groups(); part=int(part); total=int(total)
    pairs=re.findall(r"\b(op_[A-Za-z_0-9]+)=(-?\d+)\b",payload)
    record={"cycle_id":cid,"inicio_utc":utc,"parte":part,"partes":total,"tema":theme,"fields":pairs}
    cycles.setdefault(cid,[]).append(record); output.write(json.dumps(record)+"\n")
   else:
    # Apenas contagem indicativa; não exportar textos de exceção/segredos.
    for label in ("Startup bloqueado:","fault injection","Lease do batch Outbox foi perdido", "Worker do outbox de viagens indisponivel"):
     if label in line: errors[label]+=1
 p.wait(timeout=5)
finally:
 timer.cancel()
 if p.poll() is None: p.kill(); p.wait()
complete=0; duplicates=0
for cid,records in cycles.items():
 parts=[r["parte"] for r in records]; keys=[k for r in records for k,v in r["fields"]]
 duplicates+=len(parts)-len(set(parts))
 if (sorted(parts)==list(range(1,7)) and all(r["partes"]==6 and 1<=r["parte"]<=6 and r["tema"]==themes[r["parte"]-1] for r in records)
     and len({r["inicio_utc"] for r in records})==1 and len(keys)==164 and len(set(keys))==164): complete+=1
print(json.dumps({"limited":limited,"docker_exit":p.returncode,"performance_records":perf,"cycles_seen":len(cycles),"complete_cycles":complete,"incomplete_cycles":len(cycles)-complete,"duplicate_parts":duplicates,"malformed":malformed,"selected_error_counts":errors}))
PY
```

O parser não é alteração produtiva nem prova prévia de Dozzle. Se houver formato estruturado não reconhecido, malformed/partes ausentes ou limite/erro Docker, interromper interpretação e adaptar o leitor na cópia local; não reconfigurar driver/coletor. Exigir conjunto de nomes também compatível com o manifesto de 164 campos homologado, além da contagem de nomes únicos; presença de 164 nomes errados não é validação. Corrigir a contagem de erro pelo período e ciclo, sem usar warnings selecionados como taxa total de falhas.

Um ciclo pode atravessar a fronteira da janela; confirmar inicio_utc dentro da janela e tratar fronteiras explicitamente. Contagem Performance vs cycles_seen detecta possível perda TOTAL de segmentos, respeitando filtro da janela. Parte ausente nunca equivale a zero; conjuntos duplicados não devem ser somados. Uma comparação por inicio_utc liga a linha legada às partes novas; validar relógio/formato. Logs pós-deploy frios são estrato separado, não apagar seus dados.

Análise offline: duração/excesso/start-to-start contra intervalo configurado, p50/p95 por ciclo, número de chamadas/resultados nova/retry, InfrastructureFailure/Conflict/InvalidState, selecionado vs confirmado por tipo, Outbox e catch-up com seus denominadores. Normal sem cruzamento pode ser legítimo. BUS/BRT só onde os contadores atuais permitem: GPS/matching legado e predecessor/idade/plausibilidade por nova/retry; demais decisões operacionais são agregadas por origem, não inventar divisão por modal. Worker/journal/histórico são estágio separado; métricas de commit não medem materialização. Não fazer COUNT/scan de tabelas grandes, EXPLAIN ANALYZE, VACUUM ou manutenção para completar o roteiro.

## Interrupção e retorno: sinais objetivos

Registrar primeiro a linha de base e reserva mínima de RAM/disco/rede aprovada pelo operador. Sem baseline/reserva/retorno verificado, não iniciar deployment. Comparações devem usar janelas/cadência/carga próximas e métricas da mesma camada; percentuais universais não são propostos.

- Novo OOM, restart não planejado ou health de PostgreSQL/Redis degradado: interromper medição imediatamente e priorizar dependências/SO/rede. Não reiniciar/remover dependências para manter API disponível.
- Queda progressiva de MemAvailable abaixo da reserva acordada, swap-in/out/pressão de memória ou I/O sustentados acima do baseline com piora da saúde: parar coleta onerosa e encaminhar retorno SOMENTE da API se compatível e autorizado. Avaliar conjunto de sinais, não percentual MemUsage isolado.
- API fora de saúde/circuito de restart, falha de startup por schema, erro operacional novo, backlog/falhas materialização crescendo ou ciclos com excesso/atraso consistentemente superiores ao p95 basal: suspender promoção/medição; aplicar o timeout/health já definido do caminho de deploy, sem inventar valor de desempenho aceitável.
- Segmentar logs sem completude, truncamento/malformed, mudança de fontes/ETA/flags ou erro/limite de coleta: parar interpretação e classificar janela inconclusiva. Não é justificativa automática para relaxar matching/proteção ou retornar binário incompatível.

Não executar compose down, prune, rm de volumes, FLUSHDB, limpeza de Outbox/histórico ou migrations reversas. Se retorno não for seguro, manter dependências intactas e escalar ao operador; disponibilidade da API não prevalece sobre integridade de dados. Nenhuma dessas ações foi executada nesta preparação.

## Validação e decisão final

Build Release `dotnet build NoPonto/NoPonto.csproj -c Release --no-restore`: aprovado, 23,40 s, 0 erros/5 avisos preexistentes. Bateria Release offline focada: **406 aprovados, 0 falhos/ignorados**, runner aproximadamente 2 s, incluindo instrumentação/logs/retry, snapshot, startup, ETA gate, fontes/cadência GTFS-RT, sampling e codec. Não repetidos os seis casos conectados nesta etapa; evidência permanece no relatório 14. Nenhuma fixture externa/serviço/container novo iniciado. Compose base/base+override renderizados só com variáveis sintéticas e saída whitelist. git diff --check sem erros.

**BLOQUEADA**, por procedência da imagem atual/base e migrations aplicadas desconhecidas, composição efetiva ausente e retorno compatível não certificado. Integridade funcional segue aprovada no escopo 4B.3; isso não supre esses gates de release. Coletor/performance reais são pendências de medição controlada, não requisitos para acessar produção sem autorização. Fechar os itens de procedência/schema/composição/retorno e congelar o manifesto antes de solicitar aprovação de deploy; não houve build/push de imagem, deployment, configuração, flag, migration, acesso externo ou operação Git de publicação nesta etapa.

## Apêndice — hashes do delta de fontes/testes

SHA-256 dos bytes locais desta preparação (incluindo normalização de linha atual). Conferir novamente na candidata congelada; hashes não identificam uma imagem nem as dependências resolvidas. Documentação/histórico podem receber acréscimos e não fazem parte desta tabela de fontes executáveis.

| Arquivo | SHA-256 |
| --- | --- |
| NoPonto/2-Application/Services/GPS/GpsCicloPerformance.cs | 976ac43b4f2666af827a06cfa5465d12de4c4875a23b913e756e51269e776de3 |
| NoPonto/2-Application/Services/GPS/GpsCicloPerformance.Operacional.cs | 0aaa9fcd49dfe78c05faf45e98891b46ebb1fbfae1d749a4a78c9cff3d38d504 |
| NoPonto/2-Application/Services/GPS/GpsPollingService.cs | 62d1be22c6ea5cc5661bf5e727e8b66fc9e5cc81e38de421a2ba48dfb2772174 |
| NoPonto/2-Application/Services/GPS/RetryOperacionalGps.cs | 31e02cf5e51252fa1e5ab5356cd10ff084015b8173a2add65543c130cce0f7b1 |
| NoPonto/2-Application/Services/GPS/ViagemObservadaService.cs | 96bc3a6c253ff0f8ffd310fd7f2e6148b503cf9f9df7e59aa440f563e5563b4f |
| NoPonto/2-Application/Services/GPS/ViagemOperacional.Integridade.cs | f68142b828b422f251e66c8068a8e1171e1daa6c9b169fef451312c37289c0fd |
| NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs | 2ca0db3a232b128e6533da3c7fbec53cc2f9b3433318eda01fc9638cf77a2794 |
| NoPonto/2-Application/Services/GPS/ViagemOperacional.cs | fcbecac4f49e39f36c80ccf2eaba286a83bafe615c7ca984b939ae2f73a1d08e |
| NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs | 85de67d5312af8226819950ed7a7131ea358a51733270b9e100beb72aa934e5c |
| NoPonto/3-Domain/Entities/FormaPagamento.cs | 66cc018bab9be7f9948e5f644c2b38722443805f75a61af1a33ec0566a81f16f |
| NoPonto/5-Testes/IntegridadeCircularRegraTests.cs | f74211a4ae1f251efc2abdcde4006009eb3f4ede93a17cc20445bd192063d110 |
| NoPonto/5-Testes/RetryOperacionalGpsTests.cs | 6496d814636d6b05d83c2f44c297109788b090293c5f137c541513e72e600aa4 |
| NoPonto/5-Testes/ViagemDuravelPostgresTests.cs | 745bcfcf72b47635faf719f302dafe1f081d4dbf78325c6eba5faa0c85753419 |
| NoPonto/5-Testes/ViagemObservadaServiceTests.cs | 5040899cdb55b0d3600f3411ad13f94edcc68ebc16cf41049370ad10a25afe37 |
| NoPonto/5-Testes/ViagemOutboxBatchTests.cs | 4278eeb57981d9e3d8e813e23509b572459bcd481e450766dc9e45b1156f7598 |
| NoPonto/5-Testes/GpsFunilOperacionalTests.cs | 3068749a9d0e63a524bca7cb048152ec724d585c51cfc588a9871846b2f6a6d2 |
| NoPonto/5-Testes/GpsFunilLogsTests.cs | 6250608b806825d0160455b2af21880b8c0028e8df61adbe0285e8dde3579c77 |

## Apêndice — paths entre referência histórica e HEAD

Resultado local de git diff --name-status 2c5323e HEAD -- NoPonto .deploy .github .ci. NÃO é diff da imagem produtiva dbe503; working tree diagnóstico deve ser aplicado adicionalmente conforme allowlist.

```text
A	.ci/README.md
A	.ci/check-volume-permissions.sh
A	.ci/offline-tests.json
A	.ci/offline_tests.py
A	.ci/test-offline.sh
A	.ci/test_offline_tests.py
A	.ci/test_volume_permissions.py
A	.deploy/README.md
A	.deploy/deploy-api.sh
A	.deploy/ssh-deploy.sh
A	.deploy/test_deploy.py
M	.github/workflows/deploy.yml
A	NoPonto/1-API/Configuration/DatabaseStartupPolicy.cs
D	NoPonto/1-API/Controllers/Admin/AdminTarifasController.cs
A	NoPonto/1-API/Controllers/FormasPagamentoController.cs
M	NoPonto/1-API/Controllers/TarifasController.cs
D	NoPonto/2-Application/DTOs/Admin/Tarifas/AdminTarifaCreateDto.cs
D	NoPonto/2-Application/DTOs/Admin/Tarifas/AdminTarifaListItemDto.cs
D	NoPonto/2-Application/DTOs/Admin/Tarifas/AdminTarifaUpdateDto.cs
M	NoPonto/2-Application/DTOs/Linhas/LinhaDetalhesDTO.cs
D	NoPonto/2-Application/DTOs/Tarifas/TarifaConsultaDTO.cs
D	NoPonto/2-Application/DTOs/Tarifas/TarifaCriarDTO.cs
D	NoPonto/2-Application/DTOs/Tarifas/TarifaResumoDTO.cs
A	NoPonto/2-Application/DTOs/Tarifas/TarifasContratos.cs
D	NoPonto/2-Application/Interfaces/ITarifaService.cs
M	NoPonto/2-Application/LegacyCompatibility/DTOs/FrontendLegacyDtos.cs
M	NoPonto/2-Application/Services/GPS/GpsPollingService.cs
A	NoPonto/2-Application/Services/GPS/GpsTimestampDeduplication.cs
M	NoPonto/2-Application/Services/GTFS/EstruturaFinalRebuildService.cs
M	NoPonto/2-Application/Services/GTFS/GtfsEstruturalV22Service.cs
A	NoPonto/2-Application/Services/GTFS/GtfsLinhaModal.cs
M	NoPonto/2-Application/Services/ModalService.cs
M	NoPonto/2-Application/Services/TarifaService.cs
A	NoPonto/2-Application/Services/Tarifas/ArcGisTarifasClient.cs
A	NoPonto/2-Application/Services/Tarifas/ArcGisTarifasImportador.cs
A	NoPonto/2-Application/Services/Tarifas/TarifasImportCommand.cs
A	NoPonto/3-Domain/Entities/FormaPagamento.cs
M	NoPonto/3-Domain/Entities/tarifa.cs
A	NoPonto/3-Domain/Tarifas/RegrasTarifarias.cs
M	NoPonto/4-Data/Context/DbContext.cs
A	NoPonto/4-Data/Context/TransporteDesignTimeFactory.cs
M	NoPonto/4-Data/Interfaces/IPosicaoVeiculoCacheRepository.cs
D	NoPonto/4-Data/Interfaces/ITarifaRepository.cs
M	NoPonto/4-Data/Repositories/GtfsDatarioPlanPersister.cs
M	NoPonto/4-Data/Repositories/LinhaRepository.cs
M	NoPonto/4-Data/Repositories/PosicaoVeiculoCacheRepository.cs
D	NoPonto/4-Data/Repositories/TarifaRepository.cs
A	NoPonto/4-Data/Tarifas/TarifasModelConfiguration.cs
A	NoPonto/4-Data/Tarifas/TarifasStore.cs
A	NoPonto/5-Testes/DatabaseStartupPolicyTests.cs
M	NoPonto/5-Testes/EstadoCausalPosicaoTests.cs
M	NoPonto/5-Testes/FrontendLegacyCompatibilityTests.cs
A	NoPonto/5-Testes/GpsTimestampDeduplicationTests.cs
M	NoPonto/5-Testes/GtfsDatarioPersistenceIntegrationTests.cs
A	NoPonto/5-Testes/GtfsLinhaModalTests.cs
M	NoPonto/5-Testes/PosicaoVeiculoCacheRepositoryTests.cs
M	NoPonto/5-Testes/PostgresEstabilidadeTests.cs
A	NoPonto/5-Testes/TarifasPostgresTests.cs
A	NoPonto/5-Testes/TarifasRegrasTests.cs
M	NoPonto/5-Testes/ViagemObservadaServiceTests.cs
M	NoPonto/HISTORICO.md
A	NoPonto/Migrations/20261010002416_TarifasPagamentosMvp.Designer.cs
A	NoPonto/Migrations/20261010002416_TarifasPagamentosMvp.cs
M	NoPonto/Migrations/TransporteDbContextModelSnapshot.cs
M	NoPonto/NoPonto.csproj
M	NoPonto/Program.cs
```

## Apêndice — IDs EF da candidata

Extraídos dos atributos Migration em designers e classes de migrations manuais rastreadas locais. Comparar o conjunto completo com a pequena tabela de metadados; nenhum banco consultado nesta preparação.

```text
20260420171304_InitialMigration
20260420190554_AddParadaItinerarioRelacionamento
20260422030346_PoiItinerarioNovosCampos
20260422035538_SubstituirPoiItinerarioPorPoiParada
20260422154814_PrioridadePOI
20260425231629_HistoricoLinhas
20260504210322_tarifas
20260510013245_AddTipoRotaETempoRealLinha
20260510023924_AddConsorcioLinha
20260914180000_ViagemOperacionalOutbox
20260916002042_TelemetriaMlContinua
20260921144201_PositionCorrectionShadowOrigins
20260923002208_GtfsParadaItinerarioMvp
20260924013121_EstruturaTransporteV21
20260924190000_ViagemDuravelPostgres
20260925175931_EstruturaFinalEtapas1e2
20260926011413_RuntimeEstruturalEtapa3
20260926023401_RemoverLegadoEstruturalBusBrt
20260927220700_EtaV2Foundation
20260930180000_RemoverIndiceAtualizadoEmUtcViagensOperacionais
20261003211227_RailScheduleFoundation
20261006180000_IntegridadeCircularDuravel
20261010002416_TarifasPagamentosMvp
```

