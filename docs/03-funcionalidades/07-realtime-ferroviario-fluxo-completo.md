# Realtime ferroviário — fluxo completo

**Data de corte:** 2026-10-06 · **Escopo:** implementação vigente de `TremRealtime`, grade ferroviária e frontend. A implementação antiga em `2-Application/Trem` não integra este fluxo.

## Visão ponta a ponta

```text
grade observada (/trips/plan) -> builder -> importação versionada -> ExpectedRun
                                                       |
scanner/gate -> /trips/next -> normalização -> tracker -> vínculo ExpectedRun
                                                       |
                                  âncoras ETA -> atraso/posição longitudinal
                                                       |
                         merge schedule/realtime -> GET /rail/vehicles/snapshot
                                                       |
                              geometria V2 -> interpolação e mapa no app
```

Não existe telemetria GPS ferroviária nesse caminho. O provedor informa partidas/ETA para pares de estações; o NoPonto associa evidências de um mesmo `trainCode`, projeta o instante de passagem em estações e estima a posição ao longo de um padrão operacional publicado.

## Fontes e identidades

- `/trips/plan` é a fonte offline usada pelo coletor para reconstruir a grade; não é chamado pelo runtime do mapa.
- `/trips/next?originId&destinationId` alimenta o runtime. Os campos da partida são autoritativos; linha e sentido do envelope não são herdados quando ausentes na partida.
- Linha, sentido, estações, padrão e ocorrências são resolvidos contra a estrutura V2 publicada.
- A identidade física transitória é `provider + data local de tracking + trainCode`. A identidade operacional durável é o `ExpectedRunId`, derivado da versão da grade, corrida e data de serviço.

## Estados e significado

`Unresolved`, `AwaitingDeparture`, `Dwell`, `InSegment`, `TerminalHold` e `Ended` descrevem o estado espacial. Para apresentação há `Scheduled`, `Estimated` e `Live`. **Live significa estimativa sustentada por evidência recente do provedor, não localização GPS.** `Scheduled` é apenas grade; `Estimated` combina grade e evidência já envelhecida ou outra projeção estimada.

## Limites operacionais

O cliente possui orçamento local, concorrência limitada, single-flight por par OD, timeout, backoff, cooldown de `NoService` e circuit breaker. O canário seleciona probes sob orçamento; o gate evita sondagem fora da janela de serviço. Esses controles são por processo: em múltiplas réplicas, o teto global depende de coordenação distribuída ainda não identificada.

O estado de tracker, bindings, estimador e publicação é em memória. Reinício perde correlações e exige nova aquisição. A grade e a topologia permanecem no PostgreSQL.

## Estado verificado

Em produção foram confirmados runtime de grade, probes schedule-aware, estimação espacial, publicação estimada e schedule-first habilitados, com uma versão ativa de Santa Cruz: 23 padrões, 246 corridas e 7.433 paradas programadas. As flags efetivamente distribuídas no frontend não foram verificadas. Japeri tem coleta FULL local concluída, mas ainda não equivale a grade construída, auditada, importada ou ativada.

## Evidências principais

`Program.cs`; `Services/TremRealtime/**`; `Services/TremSchedule/**`; `RailVehiclesController.cs`; `src/services/railRealtime.ts`; `src/hooks/useRailRealtime.ts`; `src/utils/railGeometry.ts`.

