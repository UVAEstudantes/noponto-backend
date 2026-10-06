# Inventário de testes e execuções

**Data:** 2026-10-06. **Ambiente:** Windows, .NET SDK 9.0.306, Node 20.20.2, pnpm 10.17.1. **Commit executado:** `2d57ea8`; código backend sem diferenças contra `53567bd`. Nenhum container/serviço foi iniciado.

## Inventário e classificação

O backend possui 92 arquivos em `5-Testes`, 101 arquivos com classes `*Tests` e 1.434 casos descobertos. O próprio projeto web contém xUnit e os testes, o que mistura produção/teste na mesma assembly. Busca estática encontrou pelo menos 20 arquivos com conexão PostGIS, 13 com Redis e 11 com HTTP/auditoria; números são indicadores, não categorias mutuamente exclusivas.

| Domínio | Suítes/arquivos representativos | Tipo/dependência | Execução segura nesta etapa |
|---|---|---|---|
| estrutura/importação | EstruturaTransporteV21, EstruturaFinal, GTFS, ArcGis* | unidade + integração PostGIS; alguns HTTP fake | unidades filtráveis; PostGIS bloqueado |
| PostGIS | *PostgisTests, PostgresEstabilidade | schema/transação e escrita isolada | `BLOQUEADO_POR_AMBIENTE` |
| GPS/validação | GpsLeituraValidator, Sources, SppoCollector, Datario | unidade; alguns HTTP fake/audits opt-in | unidades permitidas; collectors/audits não |
| causal/correção | EstadoCausalPosicao, CorrecaoTemporal | unidade; parte instancia polling | executada; falhas de fixture encontradas |
| matching/bearing | GpsItinerario*, Matching*, ArcGisParadasReconciliador | unidades + PostGIS + benchmarks | unidades permitidas; DB/bench bloqueados |
| viagem/ocorrências | ViagemOperacional*, OcorrenciaParada* | unidade + PostGIS/Redis conforme classe | regra pura executada; Ocorrencia bloqueada |
| Redis/CAS | PosicaoVeiculoCache, EstadoCausalRedis, RedisStream* | fake e Redis real; escreve chaves | real bloqueado; não executar sem instância descartável |
| SignalR/HTTP | controller/compatibility/polling orchestration | unitário com mocks e integração | parcialmente coberto; E2E ausente |
| telemetria | TelemetriaMl*, ShadowPosicao* | unidade, Redis/Postgres e retention | unidades possíveis; integrações bloqueadas |
| ferrovia | TremRealtime phases, RailSchedule*, Topology | maioria determinística; alguns PostGIS | runtime/tracker executados |
| ETA V2 | Foundation, Hardening, PostgresIntegration | unidade/timing + Postgres opt-in | hardening executado; integração bloqueada |
| desempenho | *BenchmarkTests, representative/audits | opt-in, DB/Redis/carga | não executado |

Testes com `POSTGIS_TEST_CONNECTION`, `ESTRUTURA_V2_TEST_CONNECTION`, `ETA_V2_TEST_CONNECTION` ou `REDIS_TEST_CONNECTION` podem criar schema/chaves e não foram autorizados sem destino isolado comprovado. Flags “enabled” não provam isolamento.

## Execuções reais

### Descoberta

`dotnet test NoPonto.sln --no-build --list-tests`: exit 0; 1.434 casos descobertos. A primeira listagem realizou build porque usou `--no-restore`; houve apenas warnings de migration minúscula e entry point de test SDK.

### Filtro inicial controlado

Filtro de oito classes selecionadas; duração reportada 7 s: **203 total, 150 aprovados, 53 falhos, 0 ignorados**.

- 43 falhas de `OcorrenciaParadaTests`: fixture recusou executar sem `POSTGIS_TEST_CONNECTION`; resultado deve ser classificado `BLOQUEADO_POR_AMBIENTE`, não regressão funcional.
- 10 falhas de `EstadoCausalPosicaoTests`: `NullReferenceException` no construtor de `GpsPollingService` linha 77; fixture não fornece dependência vigente. É dívida de teste/regressão de fixture, não bloqueio externo.

### Filtro puramente local refinado

Classes: `GpsLeituraValidator`, `CorrecaoTemporalPosicao`, `ViagemOperacionalRegra`, `RailScheduleRuntime`, `TremRealtimePhase3Tracker`, `EtaV2Hardening`. Resultado: **140 total, 139 aprovados, 1 falho**, duração 1 s.

`EtaV2HardeningTests.Worker_FlushesAtMaximumDelay` falhou porque a coleção estava vazia. Reexecução isolada: **1/1 aprovado em 57 ms**. Classificação: instabilidade temporal/flakiness provável; investigar relógio/scheduling e não contar como aprovação estável.

### Frontend

- `pnpm exec tsc --noEmit`: exit 0, sem diagnóstico.
- `pnpm lint`: exit 0, **0 erros e 1 warning**: `mapControls.tsx:13`, `useRef` importado e não usado.
- existem 10 arquivos `*.test.ts`, mas `package.json` não define runner/script de teste; não executados.

## Efeitos e limitações

Build/test gerou somente artefatos em `bin/obj`; nenhum arquivo versionado, DB, Redis ou rede externa foi alterado. A suíte completa permanece `BLOQUEADO_POR_AMBIENTE`. Não se pode somar bloqueios a falhas funcionais nem extrapolar os filtros para 1.434 casos.

Relacionados: [planos experimentais](03-plano-experimental-gps-e-matching.md) e [consolidado](09-relatorio-consolidado-de-validacao.md).
