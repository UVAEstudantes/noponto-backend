# Etapa 4B.3 — Fechamento das assertions conectadas do funil GPS

Data: 2026-10-11. Complemento focal dos relatórios [11](11-instrumentacao-funil-operacional-20261011.md), [12](12-validacao-integracao-funil-20261011.md) e [13](13-homologacao-conectada-funil-20261011.md). **As pendências funcionais específicas da 4B.2 estão fechadas:** identidade/horários entre etapas, rollback operacional sem histórico, rollback da materialização de passagem e processamento posterior foram explicitamente verificados. Não foi realizada nova auditoria geral nem deployment.

## Escopo e diff efetivo

AGENTS.md, histórico, relatórios e estado Git consultados antes de ações. Alterações anteriores preservadas, incluindo os testes da 4A, Cor/Icone comentados e emissão de 164 contadores em seis segmentos. Mudanças efetivas desta etapa:

- `NoPonto/5-Testes/ViagemDuravelPostgresTests.cs`: duas variantes de falha agora drenam a baseline pelo worker e verificam ausência de passagem no journal/histórico; cenário de drenagem compara os cinco campos em Outbox/journal/histórico antes e depois de reentrega pelo próprio worker; helper privado de assertions SQL.
- `NoPonto/5-Testes/ViagemOutboxBatchTests.cs`: cenário existente de lease convertido em Theory com início e passagem; assertions de zero histórico, item não concluído e processamento posterior após expiração simulada do lease somente nos itens do teste.
- Este relatório novo e acréscimo em `NoPonto/HISTORICO.md`.

Nenhum fonte produtivo, worker, lease/configuração produtiva, regra, contrato, snapshot, migration ou dependência modificado. O diff do Git contra HEAD em ViagemDuravelPostgresTests também contém a preparação anterior da 4A, que não deve ser atribuída integralmente à 4B.3. Nenhum commit/stage/push/reset; `git diff --check` sem erros, somente avisos LF/CRLF preexistentes. Artefatos locais de build atualizados.

## Isolamento e limpeza

Procedimento do relatório 13 repetido com NOVOS recursos. Contexto local desktop-linux, endpoint npipe do Docker Desktop. Imagens locais postgis/postgis:16-3.4 e redis:7, `--pull=never`. Identificador da execução aprovada no gate: **np4b3-53c28a3e50f0**, label `noponto.test.run=np4b3-53c28a3e50f0`.

| Recurso | Evidência |
| --- | --- |
| PostgreSQL | `np4b3-53c28a3e50f0-pg`, `127.0.0.1:58716` → 5432, RAM 1 GiB, 2 CPU, 256 PIDs |
| Redis | `np4b3-53c28a3e50f0-redis`, `127.0.0.1:58717` → 6379, RAM 256 MiB, 1 CPU, 128 PIDs |
| Rede | `np4b3-53c28a3e50f0-net`, bridge nova/exclusiva, nome/label conferidos; cada container associado somente a ela |
| Armazenamento | somente tmpfs, PG `/var/lib/postgresql/data` 768 MiB; Redis `/data` 128 MiB, sem AOF/snapshots; sem volumes persistentes/bind mounts |
| Banco e schema | banco novo funil4b3 confirmado por current_database/POSTGRES_DB; schemas viagem3_GUID da fixture, search_path dedicado |
| Versões | PostgreSQL 16.4, PostGIS 3.4.3 disponível antes de migrations e carregado no template do MESMO novo container; postgis 3.4.3 confirmado em pg_extension do banco de testes após migrations; Redis 7.4.8, inicialmente DBSIZE=0 |

Antes de migrations: nomes, labels, rede, mounts, bindings loopback e versões conferidos. Consulta a pg_class/pg_depend confirmou ausência de tabelas de aplicação em public, distinguindo objetos pertencentes à extensão. Conexões POSTGIS_TEST_CONNECTION/REDIS_TEST_CONNECTION explicitamente definidas para esses serviços, sem fallback e sem imprimir senha/conexão completa. Somente migrations EXISTENTES aplicadas pela fixture. Sem uso de transporte/autenticação, banco preexistente, homeserver, produção, SSH ou túnel.

Uma tentativa preliminar `np4b3-1b6bbc2c79f5` interrompeu o gate antes de testes/migrations: a exigência adicional de public sem qualquer tabela contabilizava tabelas legítimas da extensão inicializada pela imagem. Ajustada exclusivamente a consulta temporária para verificar propriedade pela extensão e ausência de tabelas de aplicação, mantendo a exigência de serviço/banco novo. Recursos da tentativa foram limpos; nova tentativa com identificador distinto executou os testes.

finally restaurou as variáveis anteriores, inclusive POSTGRES_PASSWORD temporária. Antes da remoção de cada recurso, nome e label foram reinspecionados. Removidos somente containers/redes das duas tentativas; tmpfs desapareceu, nenhum volume persistente criado. Inspeção final pelos dois IDs retornou zero containers/redes; recursos anteriores preservados. Portas acima já foram liberadas, não servem como conexão reutilizável.

## Resultados conectados individuais

Execuções sequenciais, filtros focados, recompilação habilitada e `--no-restore`. **6/6 aprovados**, zero falhos/ignorados, nenhuma seleção vazia.

| Caso | Resultado individual | Evidência observada |
| --- | --- | --- |
| Funil_CommitRealSeparaInicioPassagensEFim | aprovado, 83 ms | CommitInicio=1, CommitPassagem=3, CommitFim=1, Outbox contabilizado=5, catch-up=3; SQL Outbox PassagemParada=3; estado Finalizada/persistência durável |
| Funil_FalhaAntesCommitNaoConfirmaPassagens(false) | aprovado, 80 ms | selecionadas=2, confirmadas=0, Outbox contabilizado=0, catch-up=2; Outbox de passagem=0; após worker journal de início=1, journal de passagem=0, histórico=0 |
| Funil_FalhaAntesCommitNaoConfirmaPassagens(true) | aprovado, 598 ms | mesmas assertions, incluindo rollback do INSERT Outbox antes de commit; após worker nenhum journal/histórico de passagem |
| WorkerParado_PreservaOutbox_ReinicioDrena_Idempotente | aprovado, 596 ms | 4 itens drenados, journal=4, histórico=3; comparação de cinco campos em cada uma das três passagens; repetição pelo worker preserva contagens/valores |
| LeasePerdidoDepoisDaMaterializacao_FazRollbackCompleto(false) | aprovado, 517 ms | 10 inícios: journal=0/histórico=0/concluídos=0, BatchFailures=1 e 10 itens não concluídos; nova reivindicação após expiração simulada processa 10 eventos |
| LeasePerdidoDepoisDaMaterializacao_FazRollbackCompleto(true) | aprovado, 57 ms | passagem válida: journal=0/histórico=0/concluídos=0, BatchFailures=1 e item não concluído; nova reivindicação processa 1 journal, 1 histórico e conclui 1 item |

Tempos dos grupos reportados pelo runner: três casos do funil 26,72 s (comando com compilação 67,99 s), drenagem 11,20 s, lease 22,43 s. Todos os grupos concluíram abaixo de cinco minutos. Diferenças entre duração de métodos e de grupos incluem descoberta, fixture/migrations e teardown; não são medidas de latência produtiva.

## Identidade e precisão temporal entre as etapas

OutboxViagens e EventosViagem guardam o contrato EventoViagem em Payload JSONB, ligados por EventId. HistoricoPassagens possui colunas próprias; não foi suposta coluna EventId no histórico. Cada assertion localiza o histórico por ViagemId, Volta, OcorrenciaParadaPadraoId e Ordem exclusiva e exige exatamente uma linha correspondente.

| Campo do contrato preservado | Outbox/journal | Coluna do histórico | Comparação executada |
| --- | --- | --- | --- |
| ViagemId | Payload viagem_id | ViagemId | GUID exato |
| Volta | Payload volta | Volta | inteiro exato |
| OcorrenciaParadaPadraoId | propriedade deserializada do contrato | OcorrenciaParadaPadraoId | GUID exato |
| TimestampPassagem | Payload timestamp_passagem | TimestampPassagem | instante exato em JSON; instante UTC truncado a microssegundo no timestamptz |
| TimestampGps | Payload timestamp_gps | TimestampGps | mesmo critério temporal |

O evento original preservado é o payload do item reivindicado do Outbox, mantido em memória antes da materialização. Os payloads efetivamente persistidos no Outbox e journal são relidos e deserializados usando o contrato real. JSONB conserva os instantes de 100 ns do .NET; comparação sem tolerância. Para colunas timestamptz, a expectativa é `UtcTicks - UtcTicks % 10`, refletindo o truncamento do Npgsql/PostgreSQL para microssegundos. Não foi usada tolerância em milissegundos/segundos nem arredondamento arbitrário. Comparação ocorreu antes e depois da reentrega, em todas as três passagens.

A fixture contém ocorrências distintas da mesma parada física: a assertion por ocorrência impede confundir ParadaId com OcorrenciaParadaPadraoId. EventId conecta Outbox ao journal, e a identidade de viagem/volta/ocorrência conecta o histórico. O teste exige três históricos totais para a ordem exclusiva, além de uma linha por passagem. Reentrega percorre `worker.ProcessarAsync` novamente, sem chamar um segundo pipeline ou o repositório de histórico diretamente.

## Rollbacks e denominadores

Nas falhas operacionais, a baseline de início é persistida ANTES do escopo de métricas da tentativa. Após falha, ClaimAsync do worker encontra exatamente esse início, que é materializado por ProcessarLoteAsync. SQL filtrado pela ordem exclusiva comprova 1 início no journal e zero passagens no journal/histórico. Não se exige tabela globalmente vazia nem se confunde o início com a tentativa de passagem. CommitPassagem continua zero depois da drenagem. Seleção/catch-up=2 apesar do rollback é a semântica esperada; não é passagem confirmada.

Na falha de materialização, o cenário altera somente BloqueadoPor dos EventIds exclusivos do teste, como já fazia. O worker tenta materializar na transação de batch, perde o lease na conclusão e faz rollback. Assertions comprovam zero journal/histórico e ausência de conclusão. A consulta de itens ainda não concluídos usa ProcessadoEmUtc IS NULL: o helper preexistente CountPending filtra BloqueadoPor IS NULL, significando itens não reivindicados, portanto não foi usado para interpretar o lease que pertence a outro proprietário.

Para provar recuperabilidade sem aguardar dois minutos, o teste simula expiração de BloqueadoAteUtc nos próprios itens, mantendo worker/lease/configuração produtivos intactos. ClaimAsync reivindica exatamente os mesmos IDs e ProcessarLoteAsync materializa/conclui com sucesso. Na variante de passagem, o resultado posterior é 1 evento e 1 histórico; na variante de início, histórico permanece zero. Isso valida contrato de recuperação, não espera temporal real nem falha de processo do homeserver.

## Regressão e logs

Bateria relevante **204/204 aprovada**, zero falhos/ignorados, runner 3,97 segundos, com recompilação e sem restore. Inclui funil, performance, retry, serviço observado, regras de viagem/mudança/integridade e Modelo_SnapshotSincronizado. Total nesta etapa: **210 casos distintos aprovados** (6 conectados + 204 locais). Avisos de compilação preexistentes CS8981/CS7022/xUnit2031 preservados.

Preservados 164 contadores/zeros e seis segmentos correlacionados, sem duplicação; testes detectam partes ausentes/duplicadas. Cenários de concorrência e retry continuam aprovados e preservam partições de observações novas/retry. Nenhuma alteração nas regras de GPS, matching, viagem, passagem, ETA/ML ou pontos de contagem. Selecionado, confirmado no commit e materializado têm denominadores distintos, agora com evidência conectada das transições e dos rollbacks.

Maior mensagem local: 1.228 bytes com zeros, 1.336 com cinco dígitos, 1.714 com long.MaxValue, antes de envelopes. Ensaio exploratório nesta bateria concorrente: 1.000 emissões, 195,016 ms/66.176.040 bytes incluindo captura/renderização/dicionários do logger de teste. Variação frente às execuções anteriores não estabelece regressão ou custo produtivo; não é benchmark controlado de sink/API. API completa e coletor real não foram iniciados.

Filtros executados (todos com `dotnet test NoPonto/NoPonto.csproj --no-restore --filter ...`):

```text
FullyQualifiedName~ViagemDuravelPostgresTests.Funil_CommitRealSeparaInicioPassagensEFim|FullyQualifiedName~ViagemDuravelPostgresTests.Funil_FalhaAntesCommitNaoConfirmaPassagens
FullyQualifiedName~ViagemDuravelPostgresTests.WorkerParado_PreservaOutbox_ReinicioDrena_Idempotente
FullyQualifiedName~ViagemOutboxBatchTests.LeasePerdidoDepoisDaMaterializacao_FazRollbackCompleto
FullyQualifiedName~GpsFunilLogsTests|FullyQualifiedName~GpsFunilOperacionalTests|FullyQualifiedName~GpsPerformanceMetricsTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalIntegracaoLogicaTests
```

As conexões temporárias precisam ser definidas para NOVOS serviços comprovadamente descartáveis antes dos três primeiros filtros. Não executar suíte global ou usar as portas liberadas desta execução como fallback.

## Classificações independentes e próximo passo

| Avaliação | Classificação | Fundamentação e limites |
| --- | --- | --- |
| A. Integridade funcional da instrumentação | **APROVADA no escopo solicitado** | contagem/commit/rollback/materialização, identidade/horários, idempotência, recuperação e regressões verificadas; pendências específicas da 4B.2 fechadas |
| B. Prontidão para release diagnóstica | **APROVADA COM RESSALVAS para preparação** | evidências funcionais suficientes para preparar release exclusivamente diagnóstica e plano de medição curto; entrega real de logs e capacidade do destino ainda não certificadas; não equivale a liberação incondicional para implantar |
| C. Entrega dos logs no coletor real | **NÃO VALIDADA** | captura local garante formato/recomposição/tamanho, não Dozzle/coletor/envelopes/entrega de todos os segmentos |
| D. Performance e segurança de implantação no homeserver | **NÃO VALIDADA** | nenhum acesso ao destino, load test real, avaliação de capacidade/rollout/reversão ou deployment nesta etapa |

A aprovação de A resulta das assertions executadas; B considera também o escopo restrito e as limitações C/D. Nenhuma aprovação automática desses itens. Próximo passo: preparar, em etapa separada e autorizada, uma medição operacional de 10–15 minutos com versão/flags efetivas registradas, verificação de completude por cycle_id/partes e acompanhamento de custo/falhas. Não alterar flags, TTL, matching ou proteções para aumentar passagens. Nenhuma nova migration ou correção operacional é necessária para fechar as assertions desta etapa.
