# ETA — visão geral e fluxo operacional

## Finalidade, baseline e estado

Este documento define o problema de ETA e separa os três estados existentes em 2026-10-06: (1) cliente HTTP legado ainda chamado pelo GPS, com serviço Python intencionalmente desligado e fallback fail-open; (2) fundação ETA V2 em C#, implementada para shadow/canary, mas desabilitada em produção; (3) ML supervisionado V2 ainda não demonstrado operacionalmente. Nenhum deles deve ser confundido com a interpolação determinística ferroviária.

## Problema funcional e matemático

Para veículo `v`, viagem operacional `j`, versão de padrão `p`, ocorrência-alvo `o` e instante de previsão `t₀`, sejam:

- `T(v,j,p,o)`: instante em que a passagem por `o` é observada ou inferida;
- `d_route(t₀,o)`: distância restante ao longo do padrão, em metros;
- `ŷ(t₀,o)`: ETA previsto, em segundos;
- `y(t₀,o) = T(v,j,p,o) − t₀`: ETA real, em segundos, válido somente se `T ≥ t₀`;
- `e = ŷ − y`: erro assinado; positivo significa previsão tardia;
- `|e|`: erro absoluto.

O objetivo supervisionado correto é aproximar `f(X(t₀), contexto) ≈ y(t₀,o)`, usando apenas informação disponível até `t₀`. Distância euclidiana não basta: o veículo segue geometria e sentido; há paradas intermediárias, dwell, semáforos, tráfego, variação temporal e estado operacional. Horário, dia, linha, trecho, histórico causal e interação entre veículos são candidatos conceituais, não uma lista de features já implementadas.

### Por que o alvo é uma ocorrência

Uma parada física pode aparecer mais de uma vez no mesmo padrão, em uma rota circular ou em voltas sucessivas. `OcorrenciaParadaPadraoId + PadraoVersaoId + Volta + ViagemId` fixa qual visita futura será avaliada. Usar somente `ParadaId` pode fechar a previsão com a volta anterior, misturar padrões ou rotas alteradas e fabricar labels curtos/negativos. A versão estrutural também impede aplicar posição longitudinal de uma geometria nova a uma previsão criada sobre a antiga.

## Fluxo rodoviário vigente

```text
fonte GPS -> validação/matching -> enriquecimento longitudinal
          -> cliente ETA legado (HTTP, opcional/fail-open) -> DTO operacional
          -> aceite Redis + ViagemObservada
          -> ETA V2 shadow TryRecord (se habilitado)
          -> sampling de telemetria -> channel -> Redis Stream -> PostgreSQL
          -> eventos de passagem -> histórico + fechamento de previsões V2
```

Em `GpsPollingService.ProcessarPosicaoAceitaAsync`, somente uma posição aceita atualiza a viagem; então `EtaV2ShadowService.TryRecord` e a produção de telemetria são chamados de modo não bloqueante. Já `GpsEtaClient.PredizirLoteAsync` ocorre antes, em lote, sobre posições enriquecidas, e seus resultados preenchem `EtaProximaParadaSegundos` e `EtaConfianca`. A indisponibilidade desse serviço não impede matching, Redis, viagem, telemetria ou SignalR.

## Entradas e saídas por caminho

| Caminho | Entrada principal | Saída | Uso pelo passageiro |
|---|---|---|---|
| ETA legado | próxima parada, distância, velocidade média, fração, linha e relógio do servidor | segundos e confiança categórica | pode preencher DTO GPS quando o serviço responde |
| ETA V2 baseline | viagem coerente, ocorrência exata, distância longitudinal restante e velocidade atual | evento shadow persistível | não altera DTO nem mapa |
| ML V2 | dataset/artefato ainda a definir | não operacional | nenhum |
| ferrovia | grade + ETA do provider + expected run | posição/tempos inferidos | snapshot ferroviário; não é este modelo ML |

## Rodoviário versus ferroviário

No rodoviário há coordenada GPS observada, matching, distância longitudinal e passagens por ocorrências; o target pode ser fechado por evento posterior da própria viagem. Na ferrovia não há GPS: o NoPonto associa ETA de pares de estações a expected runs e propaga tempo/posição. Podem compartilhar versionamento de preditor, estados, métricas, eventos e avaliação, mas identidade, fonte, incerteza e ground truth permanecem específicos. O preditor ferroviário atual é regra/interpolação, não ML.

## Semântica de confiança

Precisão, cobertura e freshness são dimensões diferentes. A confiança antiga (`alta`, `media`, `baixa`) depende apenas da distância enviada e não é probabilidade calibrada. O ETA V2 registra preditor/versão, contexto e eventual motivo de ausência, mas ainda não calcula intervalo probabilístico. Uma UI futura deve distinguir fonte, idade, versão, ausência e fallback; nada disso autoriza substituir hoje o contrato público.

## Limitações e pendências

- o legado prevê grandeza semanticamente incompatível com o uso no DTO;
- ETA V2 está OFF (`Enabled=false`, `ShadowEnabled=false`, canary 0% em produção);
- previsão constante por velocidade ignora dwell e tráfego futuro;
- ground truth pode ser interpolado;
- não existe modelo supervisionado V2 treinado/validado no fluxo atual;
- critérios de promoção ainda são propostas, não decisão aprovada.

## Referências e testes

`GpsPollingService.cs` (etapa 4.5 e `ProcessarPosicaoAceitaAsync`), `GpsEtaClient.cs`, `EtaV2Shadow.cs`, `ViagemOperacional.cs`, `EtaV2Repository.cs`, `GpsPerformanceMetricsTests`, `EtaV2FoundationTests`, `EtaV2HardeningTests` e `EtaV2PostgresIntegrationTests`. Testes foram inspecionados, não executados nesta etapa.

