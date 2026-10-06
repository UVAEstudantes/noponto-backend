# ETA V2 — baseline, shadow e canary

## Finalidade, baseline e estado produtivo

ETA V2 é uma fundação de avaliação causal interna em C#. Em 2026-10-06 seus componentes estavam registrados, porém produção mantinha `Enabled=false`, `ShadowEnabled=false`, `CanaryPercent=0` e `PrevisoesEtaV2` vazia. Shadow não publica ETA ao passageiro; canary seleciona quais veículos geram eventos experimentais, não usuários que recebem uma nova previsão.

## Elegibilidade e ordem real

Após GPS aceito, `ViagemObservadaService.AtualizarAsync` retorna estado criado/atualizado e próxima ocorrência operacional. `EtaV2ShadowService.TryRecord` exige:

1. flags enabled e shadow;
2. viagem em `Created` ou `Updated`;
3. próxima ocorrência conhecida;
4. versão do enriquecimento igual à versão pinada da viagem;
5. próxima ocorrência do DTO igual ao target da viagem;
6. linha, sentido e padrão não vazios;
7. fração longitudinal finita;
8. distância restante finita e não negativa.

O candidato elegível passa pelo canary; então o baseline é calculado mesmo quando resulta em “sem previsão”. O request captura IDs, volta, ordem da ocorrência, `TimestampGps`, `TimestampPrevisao=UtcNow`, distância, velocidade, bearing, modal/provedor, preditor/versão e sampling. `TryWrite` não bloqueia o GPS.

## Baseline `EtaV2LongitudinalSpeedV0`

Entradas: distância longitudinal restante `d` em metros; velocidade atual `v` e mínima `v_min` em km/h. Para valores válidos e `v ≥ v_min`:

```text
v_mps = v / 3,6
ETA_s = d / v_mps = 3,6d / v
```

Preditor=`LONGITUDINAL_SPEED`, versão=`V0`. Não há clamp máximo/mínimo do ETA. Distância inválida, velocidade não finita, mínimo inválido, velocidade zero ou abaixo do mínimo produzem `EtaSegundos=null` e um `MotivoSemPrevisao`; ainda podem ser persistidos para medir cobertura.

Exemplos comprováveis pela fórmula:

- 1.000 m a 10 km/h → 360 s;
- 500 m a 10 km/h → 180 s;
- 250 m a 10 km/h → 90 s;
- 10 m a 0 km/h → sem previsão (`VELOCIDADE_ZERO`);
- 10 m a 2,9 km/h com mínimo 3 → sem previsão.

O baseline não adiciona dwell, aceleração, sinais ou histórico. Em congestionamento, velocidade instantânea pode superestimar o tempo se a parada for momentânea ou subestimá-lo antes de uma fila. Distância pequena a alta velocidade gera ETA pequeno sem clamp. Sua virtude é semântica simples, baixo custo e referência reproduzível.

## Canary determinístico

`EtaV2Canary.Includes` normaliza `OrdemVeiculo`, calcula SHA-256 e lê 32 bits big-endian. Inclui quando `hash mod 10.000 < percent×100`; 0 exclui todos e 100 inclui todos. Para percentual fixo, o mesmo veículo permanece no mesmo grupo. Alterar o percentual amplia/reduz o conjunto de forma determinística; alterar identidade redistribui. A unidade é veículo, não viagem, linha ou usuário.

## Channel e backpressure

`EtaV2Channel` tem capacidade configurável (default 5.000), single reader e múltiplos writers. Embora o modo seja `Wait`, a entrada usa `TryWrite`: cheio ou concluído significa drop imediato e métrica `DroppedQueueFull`. Isso protege o hot path, à custa de perda explícita de cobertura. No shutdown, o writer é completado e o worker recebe até 10 s para drenar.

## Batch e persistência

`EtaV2BatchWorker` agrega até 250 itens ou 500 ms. Em erro PostgreSQL, retenta indefinidamente a cada 1.000 ms enquanto o serviço estiver ativo; o lote permanece em memória e bloqueia consumo adicional, permitindo que o canal encha e descarte novos eventos. A transação ordena requests e adquire advisory locks por veículo e target.

`EtaV2Repository.PersistBatchAsync`:

- invalida pendentes do mesmo veículo em outra viagem;
- impede nova previsão para mesma viagem/ocorrência/volta dentro de `SamplingSeconds` (default 15 s);
- persiste previsões válidas e ausências de ETA;
- usa constraints para contexto, status, previsão e ground truth.

Não existe retry count máximo nem armazenamento intermediário durável antes do PostgreSQL; o channel é memória.

## Estados e manutenção

| Estado | Entrada | Saída |
|---|---|---|
| `PENDENTE` | evento persistido | `REALIZADA`, `EXPIRADA` ou `INVALIDADA` |
| `REALIZADA` | passagem exata posterior | timestamps, ETA real e erros preenchidos |
| `EXPIRADA` | pendente mais antigo que 60 min | não recebe ground truth posterior |
| `INVALIDADA` | nova viagem do mesmo veículo | evita associar mudança operacional |

`EtaV2MaintenanceWorker`, a cada minuto, expira lotes de até 5.000 e periodicamente conta pendentes. Loga elegibilidade, canary, queue, batches, persistidos, sem ETA, realizados, expirados, invalidados, falhas, latências acumuladas e cobertura `(persisted-withoutEta)/persisted`. Falhas são fail-open em relação ao GPS.

## Shadow e promoção

Shadow calcula e mede sem alterar `EtaProximaParadaSegundos`. Ele consome CPU mínima no baseline, memória do channel e I/O PostgreSQL. Canary reduz volume experimental, mas não constitui rollout público. Uma promoção futura exige comparação estatística, cobertura, latência, estabilidade, fallback e contrato de frontend; não basta habilitar flags.

## Limitações e pendências

O baseline supõe velocidade constante, o channel não é durável, retry prolongado pode saturar a fila, não há confiança probabilística e a origem interpolada do ground truth não fica registrada na previsão. Antes de habilitar shadow são necessários orçamento operacional, observabilidade de motivos e drops e uma janela de avaliação aprovada.

## Referências e testes

`EtaV2Shadow.cs`, `GpsPollingService.ProcessarPosicaoAceitaAsync`, `EtaV2BatchWorker`, `EtaV2MaintenanceWorker`, `EtaV2Repository`, `previsaoEtaV2.cs`, migration `EtaV2Foundation`; `EtaV2FoundationTests`, `EtaV2HardeningTests` e `EtaV2PostgresIntegrationTests`. Testes não executados nesta etapa.

