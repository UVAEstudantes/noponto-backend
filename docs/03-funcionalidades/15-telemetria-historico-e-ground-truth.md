# Telemetria, histórico e ground truth

## Finalidade e estado

Este documento explica como uma posição aceita torna-se telemetria e como uma passagem posterior fecha o target ETA. A coleta `TelemetriasVeiculoMl` está ativa em produção por configuração reconciliada, embora o default versionado de sampling esteja desligado. ETA V2 continua sem linhas persistidas na fotografia de 2026-10-06.

## Fluxo efetivo da telemetria

```text
GPS recebido -> validação/matching/enriquecimento -> aceite operacional
 -> atualização de ViagemObservada
 -> sampling determinístico por linha/bloco
 -> EventoTelemetriaMl -> bounded channel (TryWrite)
 -> publisher -> Redis Stream noponto:ml:telemetria
 -> consumer group -> TelemetriaMlWorker
 -> INSERT idempotente em TelemetriasVeiculoMl
```

Telemetria é produzida apenas dentro do bloco de posição aceita em `GpsPollingService.ProcessarPosicaoAceitaAsync`. Exceção de sampling/factory/publisher é capturada e não reverte GPS nem viagem. O evento guarda coordenada recebida e projetada, velocidade instantânea, bearing, timestamps da fonte/servidor, linha/sentido, viagem, padrão/ocorrência/volta, fração, comprimento, próxima ocorrência, distâncias e velocidade média causal quando disponíveis.

`ObservacaoId` é SHA-256 de modal, provedor, veículo e `TimestampGps` normalizados; serve à deduplicação. Não equivale à identidade da viagem.

## Sampling

`TelemetriaMlSamplingPolicy.ShouldCollect` divide UTC em blocos de `BlockMinutes` e calcula SHA-256 de `v1|seed|blockId|modal|codigoLinha`. A desigualdade sobre o hash seleciona aproximadamente `LinePercentage` das identidades linha/modal por bloco. Isso evita alternar veículos individualmente e torna a escolha reproduzível com seed/configuração. Se sampling está desabilitado ou em 100%, coleta tudo; identidade ausente é fail-open; 0% rejeita identidades válidas.

Essa política controla volume, não qualidade estatística. Mudar seed, porcentagem ou duração altera a população observada e deve constar no manifesto do dataset.

## Channel, Redis e persistência

O publisher usa channel bounded com `FullMode.Wait`, mas o hot path chama `TryWrite`: quando cheio, descarta o evento e incrementa métrica em vez de bloquear GPS. O publisher agrupa e grava Redis Stream. O worker usa consumer group, recupera pendentes, persiste em PostgreSQL e só confirma após sucesso. Payload permanente inválido ou cinco falhas segue para dead-letter. A retenção considera margem temporal, pending/consumer groups e limites; defaults versionados: ciclo 5 min, margem 60 min, DLQ 7 dias, limites de stream configurados. Retenção do stream não define por si só retenção da tabela PostgreSQL.

## Viagem e eventos de passagem

`ViagemOperacional` mantém viagem, padrão, sentido, posição confirmada, cursor de ocorrência e volta. Ao avançar sobre ocorrências, emite `PassagemParada` com EventId determinístico:

```text
passagem:{ViagemId}:{OcorrenciaParadaPadraoId}:{Volta}
```

O evento contém linha, sentido, padrão operacional, versão, ocorrência, parada, ordem, fração, timestamps e velocidades. `HistoricoEventoRepository` valida o payload e, na mesma transação, grava journal `EventosViagem`, `HistoricoPassagens` e fecha previsões ETA V2. A estrutura é revalidada por joins; EventId repetido com payload diferente é conflito, não overwrite.

## Timestamp da passagem

`ViagemOperacional.TimestampPassagem` interpola se duas observações pertencem à mesma versão, há progresso estritamente positivo, a parada está entre as frações e o intervalo é `(0,180]` s:

```text
α = (p_stop − p_prev) / (p_now − p_prev)
T_pass = T_prev + α × (T_now − T_prev)
```

Exemplo: posição 0,20 às 10:00:00, posição 0,30 às 10:01:00 e parada em 0,25 resultam em `α=0,5` e passagem 10:00:30. Se qualquer condição falha, usa `TimestampGps` atual. Assim, o ground truth pode ser interpolado ou conservadoramente atribuído à leitura posterior; a entidade não guarda hoje um campo explícito distinguindo esses dois graus de confiança.

## Label e fechamento

Para previsão em 10:00:10 de 50 s e passagem correspondente em 10:01:00:

```text
EtaRealSegundos = 50
ErroSegundos = 50 − 50 = 0
ErroAbsolutoSegundos = 0
```

Se a previsão fosse 70 s, erro seria `+20 s`. `EtaV2Repository.ClosePassageAsync` atualiza somente `PENDENTE` que coincidam em veículo, viagem, linha, sentido, padrão, versão, ocorrência e volta, e tenham `TimestampPrevisao ≤ passagem`. Essas chaves protegem contra viagem/volta/versão erradas. Previsões antigas da mesma placa/ordem em outra viagem são invalidadas ao persistir uma nova; pendentes sem passagem expiram após o cutoff.

## Qualidade e casos incompletos

| Caso | Tratamento existente | Lacuna |
|---|---|---|
| GPS duplicado | identidade/deduplicação e estado causal | duplicatas semanticamente diferentes exigem auditoria |
| GPS atrasado/regressivo | validação e transição da viagem | regras detalhadas dependem do pipeline GPS |
| sem viagem/padrão/ocorrência | telemetria pode conter nulos; ETA V2 inelegível | dataset deve filtrar explicitamente |
| passagem interpolada | fórmula limitada a 180 s | confiança não persistida |
| mudança de viagem | pendentes anteriores invalidados | evento perdido pode atrasar invalidação |
| Redis/Postgres indisponível | pending/retry/DLQ; GPS fail-open | channel pode descartar antes do stream |
| ocorrência nunca alcançada | expiração | não é label negativo de duração |

## Proveniência reproduzível

Já existem IDs estruturais, timestamps, provedor, parâmetros de sampling, schema/migrations e versões de preditor. Um dataset acadêmico ainda precisa manifesto com commits, intervalo, timezone, fonte, flags, seed/blocos, matcher, regras de aceite/interpolação, schema, versão estrutural, query, contagens/rejeições e hashes de outputs. O mecanismo não registra todos esses elementos automaticamente; parte é requisito futuro.

## Referências e testes

`GpsPollingService.cs`, `TelemetriaMl.cs`, `TelemetriaMlSampling.cs`, `TelemetriaMlStreamPublisher.cs`, `TelemetriaMlWorker.cs`, `TelemetriaMlRepository.cs`, `ViagemOperacional.cs`, `HistoricoPassagemWorker.cs`, `HistoricoEventoRepository.cs`; testes `TelemetriaMl*`, `RedisStreamBoundedTests`, `ViagemOperacional*`, `ViagemOutbox*` e `EtaV2PostgresIntegrationTests`. Não executados nesta etapa.

