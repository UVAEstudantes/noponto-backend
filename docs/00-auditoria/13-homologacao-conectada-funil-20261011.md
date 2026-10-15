# Etapa 4B.2 — Homologação conectada do funil GPS

Data: 2026-10-11. Continuação dos relatórios [11](11-instrumentacao-funil-operacional-20261011.md) e [12](12-validacao-integracao-funil-20261011.md). **BLOQUEADA PARA HOMOLOGAÇÃO completa:** os três testes conectados do funil passaram, assim como os cenários existentes de materialização, mas faltam assertions conectadas de identidade/timestamps completos e ausência de histórico após rollback. Não houve deployment.

## Escopo e preservação

Consultados AGENTS.md, HISTORICO.md, relatórios 11/12, fixtures e estado Git antes de criar serviços. Preservados todos os diffs anteriores, inclusive Cor/Icone comentados e emissão de 164 contadores em seis registros. Nenhuma regra, fonte, teste, snapshot, migration, dependência, Compose ou configuração produtiva foi alterada nesta etapa. Arquivos modificados: este relatório novo e acréscimo em `NoPonto/HISTORICO.md`. Compilações locais atualizaram artefatos de build.

## Isolamento antes de migrations

Contexto Docker local `desktop-linux`, endpoint `npipe:////./pipe/dockerDesktopLinuxEngine`. Imagens locais `postgis/postgis:16-3.4` e `redis:7`, executadas com `--pull=never`. Rede bridge nova por tentativa, sem associação a redes de aplicações anteriores. Banco novo `funil4b2`, usuário exclusivo no novo serviço; schema da fixture `viagem3_<GUID>` com search_path próprio e migrations EXISTENTES. Redis dedicado, database 0 inicialmente vazio (`DBSIZE=0`). Não foram usados containers transporte, autenticação ou bases anteriores; sem homeserver/SSH/túneis/produção.

Execução que completou os testes: **`np4b2-4b57fb9a6cb5`**. Label de rede e containers: `noponto.test.run=np4b2-4b57fb9a6cb5`.

| Serviço | Nome | Binding dinâmico | Armazenamento | Limites | Versão comprovada |
| --- | --- | --- | --- | --- | --- |
| PostgreSQL/PostGIS | `np4b2-4b57fb9a6cb5-pg` | `127.0.0.1:58612` → 5432 | tmpfs `/var/lib/postgresql/data`, 768 MiB | RAM 1 GiB, 2 CPU, 256 PIDs | PostgreSQL 16.4; PostGIS 3.4.3 |
| Redis | `np4b2-4b57fb9a6cb5-redis` | `127.0.0.1:58613` → 6379 | tmpfs `/data`, 128 MiB | RAM 256 MiB, 1 CPU, 128 PIDs | Redis 7.4.8 |

Redis iniciado sem snapshots/AOF. Inspect seletivo confirmou nomes, labels, bindings loopback e somente mounts tmpfs, sem volumes/bind mounts persistentes. O banco foi confirmado por `current_database()` e pela configuração POSTGRES_DB do novo container. Versão PostgreSQL por SQL; PostGIS 3.4.3 disponível em `pg_available_extensions` antes das migrations, também carregado e confirmado por `postgis_lib_version()` no template_postgis do MESMO container novo. Após as migrations, o banco funil4b2 confirmou extensões postgis/postgis_topology/tiger 3.4.3 via pg_extension. Exemplo de schema observado durante execução: `viagem3_f59de0c8df684baf81c07636ae12649b` (34 tabelas).

POSTGIS_TEST_CONNECTION e REDIS_TEST_CONNECTION foram definidas explicitamente no processo executor, somente com loopback/portas acima/banco novo; nenhum fallback utilizado. Senha aleatória temporária passada ao Docker pelo nome de variável de ambiente, sem imprimir senha ou conexão completa. Variáveis anteriores, inclusive POSTGRES_PASSWORD temporária, restauradas em finally.

### Interrupções prévias ao gate

Duas tentativas anteriores não executaram testes nem migrations:

1. `np4b2-deb6595d82f7`: a chamada postgis_lib_version no banco novo falhou porque a extensão ainda não estava instalada nele. Verificação interrompida; nenhum teste iniciado. Corrigido apenas o script temporário de comprovação, usando versão disponível e template do container novo.
2. `np4b2-34506c3fd9e1`: quoting incorreto na consulta do script temporário; erro SQL, gate interrompido. Corrigido quoting antes de nova tentativa.

Containers/rede dessas duas tentativas removidos após nova conferência de nome e label. Não se suprimiram erros nem se executaram migrations em isolamento não comprovado.

## Três resultados individuais e contabilidade

Filtro selecionou exatamente três casos da mesma classe xUnit, executados sequencialmente (sem paralelismo entre seus métodos), com fixture real PostgreSQL/Redis. `dotnet test --no-restore`, sem `--no-build`; nenhum restore/download. Bateria: **3 aprovados, 0 falhos/ignorados**, testes 36,91 segundos; comando 39,84 segundos, abaixo de cinco minutos.

| Caso | Resultado individual | Esperado e observado pelas assertions |
| --- | --- | --- |
| Funil_CommitRealSeparaInicioPassagensEFim | APROVADO, 82 ms | estado Finalizada e persistência durável; CommitInicio=1, CommitPassagem=3, CommitFim=1, ViagemOutboxEvents=5, CatchupPassagensTotal=3; SQL real conta 3 PassagemParada no Outbox |
| Funil_FalhaAntesCommitNaoConfirmaPassagens(false) | APROVADO, 62 ms | falha após escrita de estado, antes do INSERT Outbox; InfrastructureFailure; SelecionadoPassagem=2, CommitPassagem=0, ViagemOutboxEvents=0, CatchupPassagensTotal=2; SQL real conta 0 PassagemParada no Outbox |
| Funil_FalhaAntesCommitNaoConfirmaPassagens(true) | APROVADO, aproximadamente 1 s | falha após INSERT Outbox e antes de commit; mesmas expectativas/observações 2 selecionadas, 0 confirmadas, 0 eventos Outbox contabilizados, catch-up 2; SQL real conta 0 PassagemParada no Outbox |

Todos os valores listados acima são assertions executadas, não valores inferidos de logs. O caso de sucesso não afirma numericamente todos os contadores SelecionadoInicio/Fim/Passagem, nem consulta tipos início/fim individualmente no SQL: esses complementos não estão no teste atual. Cada falha prepara a baseline ANTES do escopo métrico, portanto o zero de ViagemOutboxEvents corresponde à tentativa com falha, não à ausência absoluta de eventos de início no banco. O SQL filtra ordem exclusiva do teste e tipo PassagemParada. Fixture limpa registros/schema ao terminar.

Catch-up mede ocorrências selecionadas mesmo quando há rollback: seus 2 não são commits. Os 5 eventos de sucesso incluem início e fim, além de 3 passagens. Nenhum desses contadores comprova materialização. Hooks de contagem após CommitAsync não foram alterados.

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~ViagemDuravelPostgresTests.Funil_CommitRealSeparaInicioPassagensEFim|FullyQualifiedName~ViagemDuravelPostgresTests.Funil_FalhaAntesCommitNaoConfirmaPassagens' --logger 'console;verbosity=normal' --verbosity minimal
```

Esse comando exige repetir o gate e fornecer explicitamente as conexões de NOVOS serviços: as portas desta execução já foram liberadas e não devem ser reutilizadas como evidência de isolamento.

## Materialização independente: evidências e lacunas

Usados somente cenários existentes que invocam o worker/repositório produtivos; nenhum pipeline de teste inventado e nenhum worker alterado.

| Cenário | Resultado | Evidência real |
| --- | --- | --- |
| WorkerParado_PreservaOutbox_ReinicioDrena_Idempotente | 1/1 aprovado, 7,01 s de execução total | repositório operacional cria 4 eventos pendentes; worker ClaimAsync/ProcessarAsync drena; Outbox pendente=0, journal EventosViagem=4, HistoricoPassagens=3; reexecução de cada payload pelo repositório produtivo não duplica histórico |
| Passagens_SaoMaterializadasEConcluidasEmUmBatch, contagens 1/10/61/100 | 4/4 aprovados | worker ClaimAsync/ProcessarLoteAsync: journal, histórico, conclusões e métricas de inserção iguais à contagem em cada variante |
| EventoDuplicadoIdentico_EIdempotente | 1/1 aprovado | PersistirAsync duas vezes para o mesmo evento: 1 journal e 1 histórico |
| LeasePerdidoDepoisDaMaterializacao_FazRollbackCompleto | 1/1 aprovado | lease alterado antes do processamento; worker falha e rollback deixa journal=0, conclusões=0, BatchFailures=1 |

Bateria adicional de batch: **6/6 aprovados**, 16,15 segundos. Total conectado nesta etapa: **10 casos aprovados**, nenhum falho/ignorado. O cenário de lease usa eventos de início, não passagens: NÃO constitui evidência de zero HistoricoPassagens após rollback de uma passagem. A reentrega existente percorre PersistirAsync diretamente depois de processamento pelo worker; não é uma nova entrega de lease pelo worker.

Os cenários de contagem demonstram passagem confirmada materializada e idempotência, mas não comparam integralmente ViagemId/Volta/OcorrenciaParadaPadraoId/TimestampPassagem/TimestampGps entre Outbox, journal e histórico. A consulta de histórico por ViagemId fornece evidência parcial de viagem, sem certificar os demais campos. Revisão do SQL produtivo mostra mapeamento explícito e transação journal/histórico conjunta, mas leitura de código não substitui essas assertions conectadas. Não se observou divergência quantitativa no cenário de drenagem: 4 eventos → 4 journal, dos quais 3 passagens → 3 históricos. Diferenças entre os estágios são esperadas para tipos início/fim.

**Menor complemento proposto, não implementado:** estender WorkerParado_PreservaOutbox_ReinicioDrena_Idempotente para conservar os payloads reivindicados, comparar esses cinco campos por evento no journal/histórico, reenviar a mesma OutboxItem pelo worker e exigir contagens estáveis. Estender as duas variantes de falha do funil para executar o worker depois da tentativa e exigir explicitamente zero journal/histórico de passagem daquela ordem; a baseline de início pode ser materializada sem produzir histórico. Para rollback de materialização, adaptar o cenário existente de lease para usar PassageEvents(1) e exigir também CountHistory=0. Tudo com a mesma fixture/worker existentes, sem alterações de regras/pipeline.

## Integridade dos logs e custo

Reutilizados os testes GpsFunilLogsTests da 4B.1: **4/4 aprovados**, 2,34 segundos. Captura pelo ILogger de teste recompõe seis segmentos do mesmo cycle_id, valida os 164 campos/zeros/ausência de duplicação, timestamp UTC, estabilidade por ciclo, distinção entre ciclos e detecção de qualquer parte ausente/duplicada. Nenhuma configuração de logging alterada. Console de testes contém os resultados de tamanho; não foi iniciado harness separado nem API completa. A captura ILogger não é prova de Dozzle/coletor produtivo.

| Contadores sintéticos | Maior registro UTF-8 | Soma de seis |
| --- | ---: | ---: |
| zeros | 1.228 bytes | 6.193 bytes |
| 10.000 | 1.336 bytes | 6.849 bytes |
| long.MaxValue | 1.714 bytes | 9.145 bytes |

Ensaio local com 100 aquecimentos/1.000 emissões: 55,373 ms e 66.176.040 bytes alocados, incluindo captura/renderização/dicionários do logger de teste. Não é benchmark de API, banco ou sink produtivo. Não medidos throughput sustentado, cadência GTFS real, locks/latência sob frota real, pressão de memória de ciclo, truncamento por envelopes/coletores, entrega completa em produção ou elegibilidade do dataset ETA. A linha legada de performance continua com tamanho anterior; não foi reformatada.

## Limpeza, conclusão e próximo gate

finally restaurou variáveis e, antes de cada remoção, reinspecionou nome exato e label exclusiva de container/rede. Removidos apenas os dois containers e a rede de cada uma das três tentativas. tmpfs desapareceu com os containers; nenhum volume persistente criado. Inspeção final mostrou apenas recursos antigos de outra execução com label semelhante, que foram integralmente preservados. Nenhuma operação sobre transporte_postgres/transporte_redis/autenticação. `git diff --check` sem erros (avisos LF/CRLF preexistentes).

**Não aprovada para implantação neste gate.** O bloqueio de conectividade/commit real da 4B está fechado e a emissão local continua íntegra. A homologação exigida de materialização está parcialmente comprovada; faltam os checks explícitos identificados acima. Primeiro fechar esses pequenos complementos nos cenários existentes e repetir em novos serviços descartáveis. Depois será possível preparar a medição curta de 10–15 minutos descrita no relatório 11, com aprovação e implantação em etapa separada; nenhum deployment ou ajuste operacional ocorreu aqui.
