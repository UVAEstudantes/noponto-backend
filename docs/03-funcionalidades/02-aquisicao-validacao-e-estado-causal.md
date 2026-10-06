# 02 — Aquisição, validação e estado causal

**Finalidade:** documentar providers, janelas, normalização, deduplicação e correção temporal.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado de verificação:** implementação e testes inspecionados; produção previamente confirmada para seleção de fontes e flags; providers não consultados nesta etapa.

## 1. Providers e contratos

### 1.1 Zirix direto — ônibus

`ZirixGpsSource` adapta `GpsSppoClient`. A base é configurável; o código documenta o recurso corrente `/sppo/zirix/gps`, com `dataInicial` e `dataFinal` ISO-8601 UTC referentes ao timestamp do servidor. Não se registram parâmetros sensíveis.

Payload sanitizado representativo de `PosicaoApiDto`:

```json
{"id_veiculo":"A00000","servico":"000","latitude":"-22.9000","longitude":"-43.2000","velocidade":"32","direcao":"90","datetime":"2026-10-06T12:00:00Z","datetime_envio":"2026-10-06T12:00:02Z","datetime_servidor":"2026-10-06T12:00:03Z","route_id":"...","trip_id":"...","shape_id":"..."}
```

Identidade e linha são trim/uppercase; coordenadas aceitam string/número e vírgula decimal; velocidade inválida vira `0`; bearing fora de `[0,360]` vira nulo e `360` vira `0`. Falta de veículo, coordenada ou timestamp GPS descarta. Linha vazia é tratada como fora de operação. `datetime_servidor`, se válido e dentro da janela, alimenta watermark.

### 1.2 BRT current

`BrtCurrentGpsSource` adapta `GpsBrtClient`, recurso configurável com default público `/gps/brt`. O payload raiz contém `veiculos`:

```json
{"veiculos":[{"codigo":"00000","linha":"00","latitude":-22.91,"longitude":-43.24,"dataHora":1791288000000,"velocidade":28,"direcao":180,"sentido":"..."}]}
```

`direcao` tolera número ou string. Veículo sem código, linha vazia/`"0"`, coordenada zero/inválida ou epoch inválido é removido. A identidade interna recebe prefixo `BRT-`; `TimestampServidor` é igual ao GPS por ausência de timestamp separado. Erros HTTP são `Falha`; JSON e outros erros caem no catch geral `inesperada`.

### 1.3 Data.Rio agregada

`DatarioGpsSource` usa `GpsDatarioClient.BuscarTodasPaginasAsync(new GpsDatarioQuery())` sobre `https://its.mobilidade.rio/`. O DTO agrega `id_veiculo`, serviço/route/direction/shape/trip, lat/lon, timestamps, velocidade, direção, modo, fornecedor e qualidade (satélites, HDOP/VDOP/PDOP e fontes).

```json
{"id_veiculo":"...","servico":"...","latitude":-22.90,"longitude":-43.20,"timestamp_gps":"...","velocidade":20,"direcao":45,"modo":"ONIBUS","fornecedor":"...","quantidade_satelites":8,"hdop":1.2}
```

O mapper exige identidade, coordenadas e timestamp; normaliza bearing e preserva hints estruturais. Em produção é shadow. O polling operacional não chama `GetShadows`, portanto é diagnóstico/comparação, não fallback comprovado.

## 2. Polling e handoff

### Ônibus

`GpsSppoCollectorService` é worker dedicado, serializado por `SemaphoreSlim(1,1)`. Defaults de `GpsSppoCollectorOptions`: timeout 90 s, janela inicial 20 s, overlap 10 s, catch-up em chunks de 60 s, lag recuperável 300 s e intervalo 10 s.

Planejamento de janela:

- sem watermark: `[agora-20s, agora]`;
- normal/catch-up: início `watermark-10s`, fim `min(watermark+60s, agora)`;
- lag `>300s`: fast-forward para `[agora-20s, agora]`; só confirma o salto se vier watermark de fonte válido.

Falha/timeout não avança watermark nem publica lote. Resultado vazio pode avançar watermark, mas preserva snapshot pendente. O store mantém uma geração até `GpsPollingService` emitir ACK; se houver falha Redis, o ACK é retido para permitir reprocessamento.

### BRT

O polling principal roda a cada `GpsPolling.IntervaloSegundos` (default/config 20 s). `GpsBrtPollingGate.ObterAsync` respeita `IntervaloBrtSegundos` (default 20 s), evita HTTP em excesso e pode reutilizar o último sucesso. São pipelines de aquisição distintos, convergindo em `ProcessarCicloAsync`.

Não há retry HTTP imediato explícito nos clients. A repetição ocorre no próximo ciclo/coleta. Os timeouts são 90 s no coletor Zirix e `GPS.HTTP_TIMEOUT_SECONDS` (default 15 s) no BRT.

## 3. Ordem efetiva de normalização/validação

1. Desserialização tolerante aos campos escalares previstos.
2. Identidade e estado operacional da fonte.
3. coordenada finita, latitude `[-90,90]`, longitude `[-180,180]`, rejeitando `(0,0)`;
4. timestamp maior que Unix epoch e não mais de 2 min no futuro (`GpsLeituraValidator`);
5. normalização de linha, velocidade e bearing;
6. união das fontes primárias e vencedor de maior timestamp por `Ordem`;
7. idade no ciclo `<= MaxIdadeGpsSegundos` (default 300);
8. comparação com `veiculo:{ordem}:ativo`: somente timestamp estritamente maior segue;
9. histórico, bearing, salto e matching;
10. CAS Redis repete a monotonicidade de modo atômico.

Não existe filtro global que rejeite velocidade instantânea negativa; valores `<=90 km/h` entram na fila da média, o que constitui uma lacuna defensiva. Valores acima de 90 não entram na média. O salto usa velocidade implícita `d/Δt` e rejeita matching quando excede `2 × 90 = 180 km/h` nos defaults.

## 4. Exemplos derivados do código

- **Nova válida:** GPS `12:00:20`, Redis `12:00:00`, idade 5 s → segue; se o Lua confirma, torna-se aceita.
- **Repetida:** dois itens do mesmo veículo no lote a `12:00:20` → um vence o agrupamento; contra Redis com o mesmo timestamp é descartado/rejeitado.
- **Mais antiga:** GPS `11:59:50` contra ativo `12:00:00` → não chega ao matching; corrida posterior também seria rejeitada pelo CAS.
- **Coordenada impossível:** latitude `95` ou `(0,0)` → descartada no client.
- **Futura:** até 2 min pode passar na validação geral; correção temporal usa tolerância própria default de 5 s para seu cálculo experimental.
- **Salto:** 1.000 m em 10 s implica 360 km/h; como `360 > 180`, bearing é anulado e não há matching atual.
- **Bearing ausente:** deslocamento histórico ≥10 m gera bearing geométrico; abaixo disso usa bearing válido da fonte, depois último bearing confirmado, senão nulo.
- **Histórico insuficiente:** sem deslocamento e sem bearing, a posição pode ser aceita no Redis, mas não ganha rota.

## 5. Estado causal e correção temporal

Em produção a flag estava ativa. A ordem é: matching → preparação C → CAS operacional B → persistência C apenas para B aceito. A chave é `veiculo:{ordem}:posicao-causal`, hash versionado, TTL default 300 s, batches de 100 e no máximo um retry de conflito.

O estado contém contexto (veículo/modal/provider/linha/versão e opcionais), último timestamp/fração/comprimento, amostras de velocidade, sinais de parada e estado de movimento. Ele reinicia por mudança de identidade, linha, versão, sentido/viagem, comprimento incompatível, janela >180 s, entrada inválida ou ausência.

Velocidade causal por par:

`v = ((p_atual - p_anterior) × comprimento / Δt) × 3,6`

Regressão linear é recusada; no máximo 32 amostras em 180 s. “Parado” requer 2 observações com velocidade instantânea `<3 km/h` e deslocamento `<=10 m`; velocidade ≥3 ou deslocamento >10 indica movimento.

O motor B3 adaptativo projetaria:

`p_corrigida = clamp(p_original + (v/3,6 × idade)/comprimento, p_original, 1)`

Ele usa instantânea se idade `<=15 s`, depois mediana híbrida, zera quando parado e não projeta além de 120 s. Contudo, o coordinator atual apenas atualiza/persiste o estado e produz shadow; não há chamada a `Corrigir` substituindo `PosicaoNaRota` no pipeline operacional. A posição publicada permanece a do matching B.

O CAS causal também verifica se `veiculo:{ordem}:ts` ainda é o timestamp B esperado. Conflito relê/recalcula; Redis indisponível é fail-open para B, sem fingir persistência causal.

## 6. Entradas, saídas e erros

Entrada do núcleo: `GpsObservation`/`PosicaoVeiculoDto`. Saída da aquisição: posição normalizada mais metadados `[JsonIgnore]`. Saída causal: estado Redis e, se shadow habilitado/amostrado, evento comparativo. JSON inválido, HTTP, timeout e estados Redis inválidos são classificados/logados; não fabricam timestamps.

## 7. Testes relacionados

- `GpsLeituraValidatorTests`: coordenadas e timestamps.
- `GpsSppoClientTests`, `GpsSppoCollectorTests`, `GpsPollingCadenciaTests`, `GpsPollingFontesTests`, `GpsSourceAbstractionTests`, `GpsDatarioClientTests`.
- `GpsEnriquecimentoServiceTests`: bearing, saltos, média e continuidade.
- `CorrecaoTemporalPosicaoTests`: vetores, parada/retomada, clamps, futuro/stale, contexto e bounds.
- `EstadoCausalPosicaoTests` e `EstadoCausalPosicaoRedisTests`: codec, CAS, conflito, fail-open e shadow.

Não foram executados nesta etapa. Faltam provas E2E atuais combinando provider real, handoff, matching, B e C sob falha/restart.

## 8. Elementos para diagramas e referências

**Atividade de validação:** parse → identidade → coordenada → timestamp → dedupe → idade → comparação Redis → salto/bearing → matching → CAS.  
**Participantes:** `GpsSppoClient`, `GpsSppoCollectorService`, `GpsSppoSnapshotStore`, `GpsBrtClient`, `GpsBrtPollingGate`, `GpsPollingService`, `GpsLeituraValidator`, `CorrecaoTemporalPosicaoCoordinator`, `EstadoCausalPosicaoRepository`.

Referências centrais: `GpsSourceContracts.cs`; `GpsSourceAdapters.cs`; `GpsSppoClient.cs`; `GpsBrtClient.cs`; `GpsDatarioClient.cs`; `GpsSppoCollectorService.cs:76-244`; `GpsPollingService.cs:163-305,446-490`; `MotorCorrecaoTemporalPosicao.cs`; `EstadoCausalPosicaoRepository.cs`.

Pendências: validar velocidade negativa/NaN por provider, documentar esquema Data.Rio com fixture versionada e medir perda no fast-forward em ambiente controlado.
