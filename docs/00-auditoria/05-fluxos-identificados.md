# 05 — Fluxos identificados

**Data:** 2026-10-06 · **Ambientes:** código e produção · **Estado:** reconstruído estaticamente; ponta a ponta não executado

## Realtime rodoviário

1. `GpsSppoCollectorService` coleta janelas com overlap/catch-up; `GpsPollingService` também orquestra fontes conforme modal e cadência.
2. `IGpsSourceResolver` escolhe fonte primária e shadows entre Zirix, BRT current e Data.Rio. DTOs são convertidos no contrato comum `GpsSourceContracts`.
3. `GpsLeituraValidator` rejeita idade/coordenadas/velocidade inválidas; estado causal e `CorrecaoTemporalPosicao` lidam com leituras fora de ordem e movimentos implausíveis.
4. `GpsEnriquecimentoService` usa hints estruturais e `GpsItinerarioRepository` para candidatos PostGIS/set-based. Determina padrão/versão, sentido, posição longitudinal, distância à rota, bearing e próxima ocorrência de parada.
5. `ViagemObservadaService`/`ViagemOperacionalRepository` mantêm identidade e progresso da viagem. Histórico, outbox, shadow de posição, telemetria ML e ETA V2 são alimentados conforme flags/amostragem.
6. `PosicaoVeiculoCacheRepository` publica snapshots/índices no Redis; `GpsHub` envia atualizações. REST `/veiculos/linha/{codigoLinha}` oferece leitura alternativa.
7. No frontend, `gpsHub.ts` atualiza estado; `veiculosMapa.ts`, `app/index.tsx` e `mapScript.ts` filtram e enviam marcadores à WebView.

Falhas previstas: provider indisponível, leitura velha, salto temporal, ausência de estrutura/candidato, versão não fixada, Redis/DB indisponível e ML fora do ar. TTLs removem veículos desaparecidos. Os nomes de chaves e TTLs exatos ficam centralizados nos codecs/repositórios e opções; não foram reproduzidos aqui para evitar documentação rapidamente obsoleta.

## Realtime ferroviário

1. Schedules são coletados fora da API, normalizados e construídos por `rail_plan_collector.py` e `rail_schedule_builder.py`.
2. O import cria `RailScheduleVersion`, patterns, runs e stops; `RailScheduleGate` seleciona a janela operacional.
3. Scanner/sentinelas consultam o provider com orçamento, concorrência, single-flight, retries/backoff e normalização.
4. `ExpectedRunService` produz viagens esperadas; `ExpectedRunBindingService` associa evidências temporais às runs.
5. Tracker mantém confiança/staleness; `RailTemporalPredictor` e `RailPositionEstimator` interpolam posição ao longo da geometria entre estações. É posição estimada, não GPS real.
6. `RailRealtimeEngine` publica snapshot em `/rail/vehicles/snapshot`; `useRailRealtime.ts` faz polling e o frontend projeta os veículos no mapa com apresentação de confiança.

Sem evidência recente, o estado degrada de confiável para stale/unavailable conforme opções. Schedule-first permite publicar runs estimadas mesmo sem observação, quando habilitado.

## ETA atual

O caminho legado prepara dados no backend e chama o serviço Flask (`GpsEtaClient` → endpoint Python), que carrega encoder/modelo Joblib e retorna previsão. Como o container está parado, esse caminho não está operacional em produção. O ETA V2 executa shadow/amostragem, enfileira no `EtaV2Channel`, persiste em lote e expira pendências; a tabela `PrevisoesEtaV2` estava vazia, portanto resultado produtivo não foi comprovado. Fallbacks mantêm o fluxo GPS sem depender obrigatoriamente do ML.

## Estrutura e importações

GTFS/ArcGIS passam por parser/client, projeção/reconciliação, plano/dry-run e persistência versionada. A modelagem `FonteEstrutural` + `ImportacaoEstrutural` + identidades externas + `PadraoVersao` preserva proveniência e hashes. Parte do pipeline legado está excluída da compilação; comandos ativos devem ser confirmados antes de qualquer execução. Ferrovia segue arquivo normalizado → comando de importação → versão/pattern/run/stop.

## Referências e pendências

`Services/GPS/*`; `Repositories/GpsItinerarioRepository.*`; `GpsHub.cs`; `Services/TremRealtime/*`; `Services/TremSchedule/*`; frontend `gpsHub.ts`, `railRealtime.ts`, `useRailRealtime.ts`; extrator ferroviário. Registrar chaves Redis em apêndice gerado do código na Etapa 2.
