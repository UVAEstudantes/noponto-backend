# 03 — Matching V2 e viagem operacional

**Finalidade:** explicar associação geoespacial, bearing, próxima ocorrência e identidade da viagem.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Verificação:** SQL e regras vigentes inspecionados; batch/set-based confirmados ativos em produção. Testes apenas inventariados.

## 1. Estrutura e entrada

O matching não escolhe um “itinerário” mutável. Ele associa a observação à cadeia:

`Modal → Linha → Sentido → PadraoOperacional → PadraoVersao → OcorrenciaParadaPadrao → Parada`.

Uma linha pode ter sentidos e padrões de serviço distintos; cada padrão possui uma versão publicada com geometria própria. A ocorrência representa a presença ordenada de uma parada física naquela versão, permitindo repetir a mesma `Parada` e representar voltas. A viagem fixa `PadraoVersaoId`: mudanças estruturais futuras não reinterpretam seu progresso.

Entradas principais: código da linha, lat/lon, bearing confiável, distância máxima (default 250 m), versão anterior/faixa de progresso e, quando existente, contexto da viagem. Saída `EnriquecimentoRotaDto`: IDs estruturais, topologia, fração `[0,1]`, comprimento, distância à rota, ponto projetado, bearing local e próxima ocorrência.

## 2. Seleção PostGIS

No caminho set-based, um JSON vira CTE `entradas`; `pontos` cria `geometry(4326)` e `geography`; `contexto_global` reúne somente versões atuais dos padrões da linha. O algoritmo, por entrada, é:

1. `ST_Distance(ponto::geography, geometria::geography)` mede distância métrica à rota.
2. Mantém candidatos a até `dist_max`.
3. `ST_LineLocatePoint(geometria, ponto)` projeta o ponto na LineString e devolve fração longitudinal `p`.
4. `ST_LineInterpolatePoint` em `clamp(p−0,025)` e `clamp(p+0,025)` cria dois pontos locais.
5. `ST_Azimuth` entre eles fornece o bearing da geometria.
6. Diferença circular: `|mod(b_local − b_gps + 540, 360) − 180|`.
7. Rejeita diferença `>=80°`.
8. Ordena pelo score `diferença/80 + distância/dist_max`; UUID desempata de modo determinístico.
9. O vencedor é enriquecido com ponto projetado e próxima ocorrência.

O modo combinado materializa, no mesmo comando, três ramos: `GLOBAL`, `ANTERIOR` e `OPERACIONAL`. O anterior restringe a geometria com `ST_LineSubstring(fracao_min, fracao_max)` e reconverte a fração local à global. O operacional restringe a versão pinada e só é válido sem regressão. Matching batch agrupa entradas em chunks (internamente 100), reduzindo comandos; consultas podem degradar/fazer fallback conforme proteção do estágio. Isso reduz round-trips, mas não constitui medição de throughput.

Índices espaciais podem reduzir candidatos para operações como `ST_DWithin`; as consultas também fazem joins por códigos/IDs. O plano real depende de estatísticas e cardinalidade. Não foi executado `EXPLAIN ANALYZE`, portanto não se atribuem tempos nem complexidade assintótica ao banco.

## 3. Bearing e histórico

Precedência implementada:

1. deslocamento desde o último ativo ≥10 m → bearing geodésico calculado;
2. senão, bearing validado do provider;
3. senão, último bearing confirmado no singleton do enriquecedor;
4. senão, nulo e consulta global não ocorre.

Fórmula C#:

`θ = (atan2(sin Δλ cos φ₂, cos φ₁ sin φ₂ − sin φ₁ cos φ₂ cos Δλ) em graus + 360) mod 360`.

O bearing local considera uma vizinhança de 5% da fração (`p±0,025`), suavizando o eixo de uma curva, mas ainda pode ser ambíguo em laços ou geometrias muito curtas. Bearing não determina sozinho o sentido: candidatos precisam também pertencer à linha, estar próximos e vencer score/histerese.

Velocidade média usa até 3 leituras `<=90 km/h`. “Parado” para histerese significa média (ou instantânea) `<3 km/h`. Se o global propõe outra versão, a troca só vence a anterior quando a melhora de distância é `>30 m` com veículo não parado/bearing ou `>100 m` independentemente disso.

## 4. Continuidade e validação temporal

O singleton guarda por veículo último match, bearing, timestamp e ciclos sem rota. O orçamento de deslocamento é:

`limite_m = (VelocidadeMaximaKmh × 2 / 3,6) × Δt + ToleranciaProjecaoMetros`.

Defaults: 90 km/h e 50 m. A faixa é `p_anterior ± limite/comprimento`, clampada em `[0,1]`. Depois do resultado, o mesmo orçamento valida avanço. Em rota linear usa `|p_novo−p_antigo|`. Em circular e delta negativo usa `1+delta`, interpretando fim→início; uma regressão ampla não é convertida pela distância circular mínima.

Match inválido ou incompatível zera campos estruturais no DTO atual. O estado antigo fica somente como ajuda por até `MaxCiclosSemRota=3`; ele não é copiado como se fosse posição atual. Mudança de comprimento reinicia referência. Após três ausências o cache é removido.

## 5. Próxima ocorrência

Para o vencedor, a consulta busca `OcorrenciasParadasPadroes` da mesma versão:

- linear: primeira ocorrência com `PosicaoTracado > p`;
- circular: aceita também ocorrências antes de `p`, mas ordena primeiro as que ainda estão à frente; depois faz wrap;
- desempate: `PosicaoTracado`, então `Ordem`.

Distância física à parada usa `ST_Distance(ponto GPS, Localizacao da parada)`. Distância restante **na rota** usa comprimentos de `ST_LineSubstring`. Se a ocorrência está à frente, subtrai o comprimento acumulado atual; no wrap circular, soma restante até o fim e início até a ocorrência.

Exemplo didático linear: veículo em `p=0,40`, ocorrências em `0,20`, `0,55`, `0,80`; a próxima é `0,55`. Exemplo didático circular: veículo em `0,90`, ocorrências `0,10` e `0,70`; nenhuma está à frente, então seleciona `0,10` na volta seguinte. Paradas físicas repetidas são distinguidas pelo `OcorrenciaParadaPadraoId`, não por `ParadaId`.

Em fim linear não há próxima ocorrência. Matching sem versão/geometria/bearing/candidato válido também não produz próxima parada.

## 6. Viagem operacional

`ViagemObservadaService` só atua depois do aceite B. Ele recebe o enriquecimento e usa PostgreSQL durável + projeção quente Redis. `ViagemOperacionalRegra.Decidir` exige transição de ocorrências válida, versão coincidente e fração finita.

Estados: `Ativa → PossivelFim → Finalizada`. Uma viagem nova ganha GUID e evento `ViagemIniciada`. Em linha não circular, alcançar/permanecer no terminal move a `PossivelFim`; duas confirmações posteriores no terminal finalizam e emitem `ViagemFinalizada`. Circularidade mantém `Volta` e progresso absoluto `volta × comprimento + p × comprimento`, sem encerramento terminal equivalente.

Cada ocorrência ultrapassada gera `PassagemParada`, com ID idempotente `viagem + ocorrência + volta`. O timestamp de passagem é interpolado entre observações conforme posição longitudinal. O cursor nunca regride por jitter.

Depois de finalizada, nova viagem requer sentido inequívoco e candidato compatível em até 180 s, com evidência espacial mínima de 10 m. Mudança súbita de linha/versão/sentido não reinicia automaticamente uma viagem ativa: preserva estado/atualiza timestamp ou passa pelo candidato conforme estado. Assim, veículo físico (`Ordem`) e instância de viagem (`ViagemId`) são identidades diferentes.

### Fixação e persistência

O contexto quente de viagem informa a versão fixada ao matching; o ramo operacional tenta reprojetar nessa versão dentro do orçamento e não aceita regressão. PostgreSQL persiste estado durável com versão de concorrência, eventos e outbox numa transação/advisory lock. Redis mantém projeção quente em `veiculo:{ordem}:viagem`. Persistência durável ocorre por mudança semântica/evento ou checkpoint default 60 s; não em toda observação.

Falha de viagem não desfaz o GPS já aceito. Falha antes do CAS de posição não atualiza viagem.

## 7. Situações inconclusivas

- sem bearing confiável: sem consulta espacial;
- código vazio, distância >250 m ou diferença angular ≥80°: sem candidato;
- erro PostGIS: `InfrastructureFailure` onde o contrato diferencia; ramo legado global pode retornar nulo e logar;
- global e anterior discordam: histerese/consulta direcionada decide;
- versão anterior inconsistente: troca não autorizada;
- salto temporal acima do orçamento: match rejeitado;
- ambiguidade estrutural: score escolhe determinística, mas isso não prova correção semântica; viagem exige condições adicionais para novo início.

## 8. Testes

`GpsItinerarioRepositoryPostgisTests`, `GpsMatchingLotePostgisTests`, `GpsMatchingCombinadoDiferencialPostgisTests`, `GpsMatchingBatchOrquestracaoTests`, `GpsPinnedVersionProjectionPostgisTests`, `GpsCircularNextOccurrencePostgisTests`, `CompetidorDirecionalV2Tests`, `GpsEnriquecimentoServiceTests`, `ViagemOperacionalRegraTests`, `ViagemObservadaServiceTests`, `ViagemOperacionalIntegracaoTests` e `ViagemDuravelPostgresTests` cobrem partes descritas. Benchmarks existentes são evidência experimental do ambiente registrado, não SLA produtivo. Nenhum foi executado agora.

Lacunas de teste: trajetórias reais rotuladas em curvas/rotas sobrepostas, troca de linha/sentido E2E, restart de instância com caches em memória vazios e validação quantitativa de falsos matches.

## 9. Preparação para diagramas e referências

**Atividade do matching:** bearing → global/anterior/operacional → filtro espacial → fração → bearing local → score → histerese → validação temporal → ocorrência.  
**Estados da viagem:** inexistente → ativa → possível fim → finalizada → candidato → nova ativa.  
**Participantes:** `GpsEnriquecimentoService`, `GpsPadraoRepository`, `ViagemObservadaService`, `ViagemOperacionalRepository`, `OcorrenciaParadaRepository`, Redis e PostgreSQL.

Referências: `GpsEnriquecimentoService.cs:85-505,516-740`; `GpsItinerarRepository.cs`; `GpsItinerarioRepository.Batch.cs`; `GpsItinerarioRepository.CombinedSetBased.cs`; `ViagemOperacional.cs`; `ViagemObservadaService.cs`; `ViagemOperacionalRepository.cs`.

Pendências: executar plano de consulta em réplica controlada, medir acurácia com ground truth e esclarecer a política desejada para mudança de padrão durante viagem ativa.
