# Homologação manual de desempenho — NoPonto

Kit preparado sobre `audit/gps-bus-brt-pipeline`, base `63da9a9d3cc6fa48e476c0e2eddb13784d75bf40`, em 21/10/2026. **A preparação original não executou cenários; as tentativas manuais posteriores e correções estão registradas em VALIDATION.md.** Nenhum resultado de capacidade ou aprovação produtiva é alegado. Não executar o Compose normal da aplicação para este laboratório.

## Preparação no Windows

- PowerShell **7**, SDK .NET **9**, Docker Desktop em modo Linux e contexto local `desktop-linux` por named pipe. O kit recusa Docker remoto.
- Dependências NuGet do projeto já restauradas. Se necessário, executar manualmente `dotnet restore ./NoPonto/NoPonto.csproj` antes de `Prepare`; isso acessa os feeds configurados. O kit usa `--no-restore` e não altera pacotes.
- Imagens **locais** `postgis/postgis:16-3.4` e `redis:7.4-alpine`. Se ausentes, obter manualmente essas imagens antes do laboratório (`docker --context desktop-linux pull postgis/postgis:16-3.4` e `docker --context desktop-linux pull redis:7.4-alpine`). Downloads são públicos; o kit nunca faz pull e congela os IDs locais no manifesto.
- Reservar inicialmente aproximadamente **2 GB livres de RAM**, além do sistema/Docker Desktop, e 1–2 GB de disco para build, logs e imagens, sem contar o cache NuGet. Estimativa de planejamento, não consumo medido. Fechar cargas concorrentes durante comparações; não encerrar containers alheios.
- Executar no checkout desta branch, sem WIP funcional durante a comparação. `Prepare` compila a árvore atual; `run.json` vincula o HEAD. WIP não é descartado, mas HEAD sozinho não identifica código não commitado.

No PowerShell 7, a partir da raiz do repositório:

```powershell
$kit = Join-Path (Get-Location) 'tools/performance/Lab.ps1'
$lab = Join-Path (Get-Location) ('NoPonto/lab-output/perf-' + [guid]::NewGuid().ToString('N'))
& $kit -Action Prepare -LabDirectory $lab
& $kit -Action Start -LabDirectory $lab
```

`Prepare` apenas compila (limite 180 s), em `build/` e `obj/` exclusivos. Não inicia API, Docker nem testes. `Start` cria dois containers e uma rede com UUID e label `noponto.performance.lab`. Portas aleatórias são vinculadas exclusivamente a `127.0.0.1`. Nenhum endpoint ou connection string externo pode ser informado. Guards idênticos no PostgreSQL e Redis são validados antes de migrations/seed em schema exclusivo. A API usada pelos cenários é Kestrel temporário em loopback, iniciado pelo teste; não é a API de desenvolvimento/produção e sua URL não precisa ser informada.

A conexão Redis construída por este launcher usa `allowAdmin=true` para coletar `INFO`, exclusivamente no endpoint descartável validado. Isso habilita a permissão do cliente StackExchange.Redis, sem alterar servidor, ACL, credenciais ou configuração global. Se `INFO` for bloqueado pelo modo admin do cliente ou explicitamente negado por ACL, apenas essa métrica fica indisponível (`info_admin_disabled`/`info_permission_denied`, com fase no JSON/Markdown). Erros de conexão, timeout, autenticação, persistência ou outros comandos continuam sendo falhas. Após atualizar o kit, criar outro UUID e executar `Prepare` novamente; não reutilizar o build/fingerprint anterior nem regenerar os relatórios da execução falha para substituir a evidência original.

`GpsIntegrationResources.Record` centraliza a escrita de evidências: um lock estático protege somente a abertura/append/fechamento de cada linha completa, entre escritores do mesmo testhost. A serialização JSON e o processamento GPS/SignalR permanecem fora desse lock. O formato `{kind,value}` com uma linha por registro permanece igual. Erros reais de disco ou bloqueio por escritor externo continuam propagando; não há retry silencioso ou descarte de registros. O kit mantém um processo de execução por laboratório com `operation.lock` e diretórios exclusivos; não se promete coordenação com processos externos nem atomicidade diante de crash/disco cheio.

## Executar os cenários

Começar um por vez, com os defaults. O processo do VSTest é iniciado **somente** em `Run`. Cada execução tem diretório `run-<cenário>-<UUID>` novo. Nenhum teste fora de `PerformanceLab_Manual` é descoberto pelo filtro de execução; ele chama explicitamente os pequenos controles de correção descritos abaixo.

```powershell
# A — 4 veículos ao todo, 1 linha BUS + 1 BRT, 2 clientes inscritos nas duas linhas
& $kit -Action Run -LabDirectory $lab -Scenario A

# B — fanout, REST, reconexão/reinscrição e LinhaEsvaziada
& $kit -Action Run -LabDirectory $lab -Scenario B

# B opcional — acrescenta testemunho existente de 60 s com TTL ativo real de 40 s
& $kit -Action Run -LabDirectory $lab -Scenario B -NaturalExpiry

# C — operação durável, passagens, CAS/fencing, concorrência, rollback e Outbox
& $kit -Action Run -LabDirectory $lab -Scenario C

# D — comparação false/true exclusivamente nas opções dos repositórios descartáveis
& $kit -Action Run -LabDirectory $lab -Scenario D
```

| Cenário | Conteúdo | Estimativa inicial, não medida | Limite padrão |
|---|---|---|---|
| A | Oito ciclos: novo, novo, duplicado, fora de ordem, antigo, intermitente, novo, novo. Fontes HTTP protobuf locais, crosswalk e matching PostGIS reais, CAS/viagens/Redis e materialização real. | 20–90 s, 4 veículos; observadores também consomem recursos | 180 s |
| B | Oito ciclos novos; cada cliente assina todas as linhas. Reconecta um cliente no quinto ciclo. Compara payloads/identidades/status/ETA de cada veículo no REST individual e por linha. Remove chaves conhecidas ao final e exige LinhaEsvaziada em cada cliente/linha. | 20–90 s; com NaturalExpiry, mais 60 s | 180 s |
| C | BUS/BRT × linha/sentido, flag local true; sequências curtas. Reutiliza quatro controles existentes: CAS concorrente, conflito durável, rollback BRT e projeção atrasada. | 10–90 s; pequenas transações e seed estrutural | 180 s |
| D | Mesmas quatro sequências para false e true, estados independentes. Candidata → negativa → reload sem Redis → positivo → positivo; depois passagem futura e reentrega. Ordem false/true alterna entre repetições. | 10–90 s; custo de invalidação medido à parte | 180 s |

As estimativas incluem startup do testhost e migrations no schema descartável. Se uma máquina lenta atingir o limite, o relatório permanece **incompleto**, sem relaxar asserções. Diagnosticar primeiro; ampliar somente por parâmetro explícito.

### Comparações controladas e maior duração

```powershell
# Sem assinantes, para comparar custo dos observadores/fanout em A
& $kit -Action Run -LabDirectory $lab -Scenario A -Subscribers 0

# Matching individual versus combinado set-based; manter os demais parâmetros iguais
& $kit -Action Run -LabDirectory $lab -Scenario A -IndividualMatching

# Somente após analisar o pequeno: 16 veículos, 4 linhas no total, 4 clientes
& $kit -Action Run -LabDirectory $lab -Scenario B -Vehicles 16 -LinesPerModal 2 -Subscribers 4 -AllowLargerLoad

# Estabilidade curta: até 10 repetições, com pacing real entre ciclos de A/B
& $kit -Action Run -LabDirectory $lab -Scenario A -Repeats 3 -PaceSeconds 15 -MaxSeconds 600 -AllowLargerLoad

# D com repetição e alternância de ordem, sem executar matriz de integração
& $kit -Action Run -LabDirectory $lab -Scenario D -Repeats 3 -MaxSeconds 600 -AllowLargerLoad
```

Cada repetição A/B contém oito ciclos e novos veículos/linhas. O tempo GPS/cache avança 30 s por ciclo, enquanto o pacing real varia de 0 a 30 s. São dimensões diferentes: **não dividir pelo tempo GPS para obter throughput**. A fonte fornece snapshots construídos deterministicamente; IDs e GUIDs são exclusivos. Cache estrutural/Redis/PostgreSQL não é reiniciado entre repetições. Para comparar cold start, executar `Stop` e preparar outro `$lab`; não fazer flush/reset em serviços existentes. Repetições no mesmo laboratório medem aquecimento e acumulação, não amostras independentes.

`Vehicles`, `LinesPerModal`, `Subscribers`, `Parallelism`, `PaceSeconds` e matching individual afetam **A/B**. C/D usam a sequência fixa BUS/BRT × linha/sentido, controlada por `Repeats`; aumentar Vehicles não aumenta sua carga. Pacing não é aplicado a C/D. C/D usam checkpoint de 0 s para observar cada commit semântico; não representam diretamente a taxa produtiva com checkpoint de 60 s.

Limites de entrada: 2–1.000 veículos totais, 1–8 linhas **por modal** (mínimo de um veículo por linha), 0–64 clientes, paralelismo 1–4, 1–10 repetições, 30–900 s por execução. Mais de oito veículos, uma linha por modal, quatro clientes, uma repetição, 180 s ou heap de 768 MiB exigem `-AllowLargerLoad`. O teto não é uma recomendação para a máquina de 4 GB. REST é uma verificação concorrente por assinante, sequencial por veículo; não é gerador dedicado de RPS aberto nem evita coordinated omission.

## Isolamento, recursos e interrupção

| Componente | Limite configurado | Observação |
|---|---|---|
| PostGIS | 1 CPU, 512 MiB RAM, sem swap, 160 PIDs, 40 conexões | PGDATA em tmpfs até 512 MiB, compete com RAM; não é teste de I/O de disco durável |
| Redis | 0,5 CPU, 96 MiB RAM, sem swap, 64 PIDs | maxmemory 64 MiB/noeviction; /data tmpfs, sem AOF/snapshot |
| Testhost .NET no Windows | heap gerenciado 768 MiB por processo, processador lógico configurado 2 por default | `HostMemoryMB` controla GC (256–1.536 MiB), **não** é limite de working set; CPU do host não é limitada por cgroup |
| Execução | watchdog 180 s e limite individual de chamadas Docker 20 s | coleta Docker a cada ~5 s; watchdog pode exceder o orçamento por até uma chamada em andamento; `Kill(true)` encerra árvore do processo |

Abortar ao primeiro erro/assertion, serviço parado/reiniciado/OOM, guard inválido, falta de memória Redis, limite de duração ou pedido do operador. Falhas de consulta/latência acima dos timeouts existentes também interrompem o caso; resultados parciais não viram sucesso. Verificar pressão de memória do Windows e espaço livre antes de ampliar. Não há ajuste automático de regras para fazer uma carga passar.

Para interromper, usar **Ctrl+C** no terminal de execução; o `finally` encerra a árvore filha. Para preparar a alternativa em outro terminal, antes de iniciar `Run`, imprimir este comando no terminal que conhece `$kit` e `$lab`, e copiar a linha gerada para **outro PowerShell 7**:

```powershell
"& '$kit' -Action Interrupt -LabDirectory '$lab'"
```

Aguardar `Run` retornar antes de limpar. O arquivo `operation.lock` impede Start/Run/Stop simultâneos. Após Interrupt, o laboratório não aceita novas cargas; preservar relatórios, limpar e preparar outro UUID. Se o terminal for encerrado à força, os relatórios podem ficar sem TRX; `Report` os marca incompletos. Containers permanecem disponíveis para inspeção local até `Stop` (inclusive em falha parcial de `Start`).

```powershell
# Recriar resumos de execuções terminadas ou interrompidas, sem Docker nem testes
& $kit -Action Report -LabDirectory $lab

# Limpeza idempotente: somente containers/rede próprios, sem apagar relatórios
& $kit -Action Stop -LabDirectory $lab
```

`Stop` exige manifesto válido, UUID/nome/label coincidentes e mounts exclusivamente tmpfs. Não executa prune, down genérico, remoção de volumes/imagens nem exclusão recursiva de diretórios. Containers preexistentes são inventariados por ID/estado (inclusive parados) e comparados no `cleanup.json`. Um estado alterado por outro processo é reportado, sem tentar restaurá-lo/reiniciá-lo. A rede só é removida se estiver vazia. Não editar o manifesto/labels para forçar limpeza. O kit não acessa homeserver, produção, GPS real ou cloud.

No futuro i3-2120/4 GB: iniciar exclusivamente com defaults e paralelismo 1, sem outros serviços concorrentes de carga. Reservar memória para SO e eventuais serviços existentes; os limites dos containers somam 608 MiB, mas não incluem heap/working set .NET nem a VM do Docker Desktop. Windows+Docker Desktop pode não caber com folga em 4 GB; Linux/Docker nativo reduz essa parcela, mas **este launcher recusa hosts remotos e não é portável para Linux sem revisão**. Compilar antes em máquina adequada seria uma proposta futura; não há acesso/preparação do homeserver nesta entrega. Dados em tmpfs e limites artificiais impedem extrapolar IOPS/capacidade de produção.

## Métricas e leitura dos relatórios

Arquivos por execução em `$lab/run-*/`:

- `summary.json` e `summary.md`: resumo, disponibilidade, parâmetros, commit, IDs de imagens, limites, estado e validação do TRX.
- `observations.jsonl`: tempos/counters internos e controles sintéticos, sem payload GPS, coordenadas, SQL textual ou connection strings.
- `resources.jsonl`: amostras de CPU/memória/IO/rede dos **dois containers próprios**; nenhum inventário de environment alheio.
- `scenario.trx`, `stdout.log`, `stderr.log`: asserções e diagnóstico separados. TRX pode conter IDs/valores **sintéticos** em falhas; revisar antes de compartilhar.
- No diretório pai: `lab.json` (proveniência/isolamento, sem senha), logs de compilação e `cleanup.json` após Stop.

| Métrica | Medida disponível | Limitação |
|---|---|---|
| Throughput | aceites CAS / soma dos tempos reais de ciclo; fornecidas e aceitas separadas | inclui rejeições deliberadas na entrada; não é GPS por segundo produtivo nem taxa de chegada aberta |
| p50/p95/p99 | ciclo, verificação REST (duas requisições + assert), ciclo→observador SignalR, reload, operação por flag/etapa | nearest rank; oito amostras dão p95/p99 semelhantes ao máximo; sem alegar confiança estatística |
| SignalR | tempos internos `signalr_preparacao_ms`, `signalr_broadcast_ms`, `signalr_ms`, clientes/linhas e observação no fio | ciclo→observador inclui processamento e fila do leitor, é limite superior; não mede publicação→recepção isolada |
| Erros/divergências | falhas de infraestrutura da viagem; asserções REST/SignalR e TRX | timeout/cancelamento não é descarte funcional esperado; execução parcial é incompleta, mesmo que amostras anteriores tenham divergência zero |
| PostgreSQL | conexões, commits/rollbacks, tuplas, blocks e soma de calls/tempo SQL em pg_stat_statements | cumulativos da base isolada, amostrados, atualização assíncrona; incluiram seed/observadores. Não equivalem a statements por veículo |
| Matching | estágios/counters produtivos e matching combinado real em A/B | geometria curta/retas sintéticas; não mede variedade/complexidade de toda a malha; sem distribuição SQL exclusivamente PostGIS |
| Redis | memória, clientes, comandos, hits/misses/evictions e métricas internas de CAS/commit | processo compartilhado com runtime, índices e observadores; sem perfil por comando |
| Outbox | backlog antes/depois e duração de drain/materialização | worker real chamado explicitamente, sem atraso do scheduler e cleanup; em C/D inclui reentrega e leitura do journal |
| Operação D | duração por flag/etapa e commits semânticos duráveis nas quatro observações; reload; confirmação; span GPS entre dois positivos | não é contagem exata de SQL/escrita física; sequência usa mesmo padrão de dados, estados novos; seed e montagem da evidência fora da duração da chamada |
| CPU/RAM | containers via Docker; CPU/working set/peak/heap do testhost | testhost agrega carga/fontes/REST/SignalR; pico de memória cobre toda a vida do processo, CPU começa depois da inicialização da fixture |

`complete=true` exige saída zero, exatamente um teste correto aprovado no TRX, definição e quantidade esperada de amostras. **Não significa desempenho homologado.** Ausência de métrica resulta em `availability=false`, array vazio, distribuição `available=false` ou null, conforme campo; não preencher com zeros presumidos. `unavailable` e `limitations` discriminam as lacunas. Verificar essas seções antes de comparar números.

Para enviar ao ChatGPT: `summary.json`, `summary.md`, `run.json`, `cleanup.json` e `lab.json`. Para diagnóstico: `observations.jsonl`, `resources.jsonl`, `scenario.trx` e os dois logs da execução/compilação. Informar modelo de CPU, RAM total/livre, limites da VM Docker, outros serviços ativos, ordem das execuções e se o laboratório foi recriado. Não enviar `.env`, dumps, inspect completo de serviços alheios ou credenciais. Relatórios reais ficam ignorados em `NoPonto/lab-output/`; não versionar dados de execução como resultados do kit.

## Critérios de aceitação propostos

Não são SLOs aprovados. A primeira rodada pequena serve para verificar o kit; decidir envelopes finais depois dos relatórios e repetir três vezes sob os mesmos parâmetros.

1. **Isolamento obrigatório:** UUID/guards/loopback/tmpfs válidos, zero OOM/restarts, limpeza própria sem sobras e containers anteriores preservados. Nenhuma fonte externa ou configuração global alterada.
2. **Correção obrigatória:** todos os assertions/TRX aprovados, zero divergência REST/SignalR nas observações comparadas, nenhum aceite duplicado/antigo, CAS/fencing preservados, nenhuma passagem na invalidação/baseline, uma passagem futura inédita sem duplicação por reentrega.
3. **Cadência inicial:** propor p95 de ciclo abaixo de 7,5 s e máximo abaixo de 15 s com a carga-alvo, deixando metade do polling de 15 s livre. O kit não mede drift do scheduler: validá-lo separadamente antes de transformar esse envelope em SLO.
4. **Distribuição local:** propor p95 da verificação REST abaixo de 500 ms e do ciclo→observador abaixo de 2 s na carga pequena; revisar custo do observador antes de reprovar o pipeline. Zero timeouts/erros de infraestrutura não deliberados.
5. **Persistência:** backlog pós-drain zero em cada ponto; sem crescimento persistente entre repetições. Propor drain p95 abaixo de 5 s na carga pequena; métrica de fila em regime permanente ainda pendente.
6. **Flag D:** true confirma somente no segundo positivo elegível após a invalidação; false não adota a mudança. Reportar delta de tempo e commits da invalidação (sem limite percentual arbitrário até baseline). Invalidação não deve gerar passagem; reload preserva a barreira e confirmação cria nova execução. Não usar este teste como autorização para habilitar a flag.
7. **Recursos:** sem evictions/OOM; propor pico abaixo de 80% do limite de cada container e memória sem tendência de crescimento após aquecimento em repetições equivalentes. CPU saturada e pausas GC exigem correlacionar com latência, não aprovação automática por média baixa. Considerar limite pequeno do Redis e PGDATA tmpfs.

Fora da certificação: frota de produção, corpus real rotulado, geometria complexa, TLS/proxy/autenticação, backplane/réplicas, crash arbitrário, durabilidade em disco, kernel/CPU do homeserver, importadores, telemetria completa, scheduler contínuo e ETA/ML. Qualquer harness para inicialização completa/rede bloqueada ou hooks novos de instrumentação deve ser proposto separadamente antes de alteração funcional/arquitetural.

## Inventário e plano de reutilização

| Existente | Uso/decisão |
|---|---|
| `tools/gps/test_pipeline_disposable.ps1` | Padrão reutilizado: Docker Desktop local, IDs exclusivos, imagens sem pull, tmpfs, loopback, guards PostgreSQL/Redis e preservação de containers. Script original intacto; launcher novo permite Start/Run/Stop separados e watchdog. |
| `GpsPipelineDisposableIntegrationTests.Infrastructure.cs` e `ViagemOperacionalFixture` | Reutilização direta por classe parcial opt-in: fonte HTTP protobuf local, estrutura V2 mínima, crosswalk, PostGIS, Redis, Kestrel/REST e protocolo WebSocket SignalR reais. Migrations apenas no schema protegido. |
| Parciais `Status`, `Removals`, `OperationalRecovery`, `EvidenceContinuity`, `OperationalEnd` | Contratos/regras existentes consultados; controles específicos de CAS/recovery/fencing reutilizados, sem reimplementar regras de mudança. |
| `GpsMatchingBenchmarkTests`, `EstadoCausalPosicaoBenchmarkTests`, `ShadowPosicaoBenchmarkTests`, `GtfsRealtimePerformanceTests` | Experimentos já existentes com opt-in, dependências/fixtures próprias; não executar diretamente nem importar seus fallbacks localhost. Úteis depois para investigar um gargalo delimitado; não são o entrypoint seguro deste kit. |
| `GpsPerformanceMetricsTests` e `GpsCicloPerformance*.cs` | Contadores/tempos produtivos disponíveis, coletados pelo logger do harness sem mexer no pipeline. |
| `tools/eta_ml/test_shadow_disposable.ps1` e `tools/eta_ml/tcc/Lab.targets` | Precedente para import explícito de laboratório, sem alterar build normal; sem executar experimentos ETA/ML. |
| `NoPonto/lab-output` (profiler PostGIS, RedisPhase3LoadRunner, performance.csv, runners locais) | Artefatos locais/ignorados, alguns diretórios com acesso negado. Não assumidos como dependência reproduzível, nem alterados. |
| `NoPonto/docker-compose*.yml` | Infraestrutura da aplicação inspecionada como referência de componentes; não reutilizada para evitar serviços externos/nomes/volumes existentes. Nenhum Compose existente modificado. |

Fluxo exercitado A/B: `GTFS-RT HTTP local → parser/validação/crosswalk → collector BUS/handoff e gate BRT/cache HTTP → polling/dedup/watermark → matching/enriquecimento PostGIS → CAS/fencing Redis → viagem PostgreSQL + projeção Redis + Outbox → índice de linha → REST/SignalR → materialização explícita de eventos/passagens`. O ciclo é chamado diretamente, sem iniciar todos os BackgroundServices. C/D começam na fronteira do repositório operacional com testemunho de matching construído a partir da geometria real.

Custos a observar: lookup/crosswalk e matching individual versus set-based; leitura de predecessor/watermark; ida/volta Redis/Lua/índices e projeção; consultas estruturais PostgreSQL; advisory locks/transação/checkpoint/Outbox nas viagens; claim/SKIP LOCKED/materialização/idempotência; leitura/serialização e fanout por linha/assinante; custos dos loops de coleta/gates/cache e repetição de snapshots. Invalidações sem passagem podem aumentar commits em D. Contadores do logger ajudam a distinguir esses custos; telemetria amostrada não mede a cadência integral do GPS.

Precisam ser reais para representar esses custos: PostgreSQL/PostGIS com schema/índices/migrations atuais, Redis/Lua, repositórios, matcher, materializador, Kestrel/REST e WebSocket/SignalR. Podem ser controlados: origem GPS HTTP, geometria/rotas sintéticas, relógio da fonte e falhas/ausências. Faltam para representar a implantação completa: scheduler real contínuo, todos os workers/telemetria, malha/corpus representativos, transporte de borda, durabilidade em disco e réplicas. Nenhuma alteração arquitetural foi implementada para cobrir essas lacunas.

Referências: [auditoria do pipeline](../../docs/00-auditoria/24-auditoria-gps-bus-brt-20261019.md), [laboratório integrado](../../docs/00-auditoria/27-homologacao-integrada-gps-20261019.md), [continuidade das evidências](../../docs/00-auditoria/34-correcao-continuidade-evidencias-20261020.md), [motivo de encerramento](../../docs/00-auditoria/36-motivo-fim-mudanca-operacional-20261020.md) e [plano experimental anterior](../../docs/09-validacao/03-plano-experimental-gps-e-matching.md).

## Verificações desta entrega

Comandos/resultados exatos e a lista do que não foi executado estão em [VALIDATION.md](VALIDATION.md). É possível repetir somente os checks offline (não usam Docker/carga):

```powershell
pwsh -NoProfile -File ./tools/performance/Verify.ps1
```

Não iniciar os cenários automaticamente após esses checks. A execução manual e a análise dos relatórios são a próxima etapa.
