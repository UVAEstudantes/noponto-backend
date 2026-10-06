# Datasets, treinamento e validação de ETA

## Finalidade e separação de estados

Há três coisas distintas: `treinar.py`, pipeline legado que gerou o XGBoost histórico; `dataset_v1.py`, fundação offline reproduzível de segmentos na estrutura anterior; e um dataset ETA V2 por eventos de previsão/ground truth, ainda não implementado como exportador/treinador operacional. Esta seção não declara nenhum modelo V2 treinado.

## Pipeline legado de treino

O SQL de `treinar.py` lê campos agregados de `HistoricoPassagens`, aplica filtros fixos e usa split aleatório 80/20. Features são seis colunas e target é duração do trecho. O encoder é mapa ordinal de códigos de linha; modelo/encoder são Joblib separados. Não há manifesto, hash do dataset, imputação explícita, pipeline serializado de transformação ou split por viagem. Os resultados são impressos no console, não vinculados ao artefato.

## Dataset `noponto-segmentos-v1`

`dataset_v1.py` lê passagens com cursor PostgreSQL read-only e memória proporcional a uma viagem. Aceita somente origem `ESTRUTURADO`, viagem/itinerário/sentido/paradas completos, timestamps com timezone, passagem não posterior ao GPS, frações `[0,1]`, comprimento positivo e modal ônibus/BRT.

Segmentos são pares adjacentes na sequência original; passagem inválida interrompe continuidade, sem “pular” a lacuna. Exige mesma viagem, itinerário, sentido, linha e veículo; ordem e tempo progressivos; comprimento consistente. Calcula:

```text
tempo_segmento_s = T_destino − T_origem
distância_segmento_m = (posição_destino − posição_origem) × comprimento_itinerário
```

A viagem inteira recebe split pela última passagem: `TRAIN`, `VALIDATION` ou `TEST`. Outputs Parquet particionados por split, Zstd, e manifesto com versões, critérios, fronteiras, schema e contagens. `FEATURE_COLUMNS` e `TARGET_COLUMNS` são disjuntos por validação explícita.

Limitação crítica: SQL/tipos exigem `ItinerarioId` e `ParadaItinerarioId`, enquanto o V2 vigente usa `PadraoVersaoId`, `OcorrenciaParadaPadraoId` e `Volta`. Logo o pipeline é reutilizável conceitualmente, não executável como dataset V2 sem adaptação comprovada.

## Dataset ETA V2 necessário

A fonte mais semanticamente direta é `PrevisoesEtaV2` em estado `REALIZADA`: cada linha já contém `t₀`, target exato, contexto estrutural, preditor, ETA previsto, `T_pass`, ETA real e erros. Um exportador futuro deve:

1. congelar intervalo e schema;
2. selecionar apenas status/qualidade definidos;
3. distinguir passagem interpolada de observada — hoje essa qualidade não está persistida;
4. preservar viagem inteira em um único split;
5. impedir informação posterior a `TimestampPrevisao` nas features;
6. produzir manifesto, checksums, contagens e rejeições;
7. nunca editar os eventos de origem.

## Classificação de features candidatas

| Feature | Disponibilidade |
|---|---|
| distância longitudinal, velocidade atual, bearing, hora/dia, linha/sentido/padrão/ocorrência, posição, modal/provedor | disponível no evento ETA V2 no instante da previsão |
| velocidade média causal, janelas de velocidade, aceleração, tempo parado | presente/derivável da telemetria causal; join ainda necessário |
| intervalo desde última parada, ordem/volta, estado da viagem | derivável de eventos/estado atuais |
| velocidade histórica por trecho/hora | derivável somente com agregações temporais treinadas no passado |
| headway/distância para veículos vizinhos | potencialmente derivável por snapshots sincronizados; não implementado como feature ETA |
| clima/incidentes/semáforos | requer nova integração |
| congestionamento futuro/chegada real | informação futura proibida |

Todo cálculo de janela deve usar timestamps `≤ t₀`. “Existe no banco” não significa “estava disponível na inferência”.

## Leakage e splits

Riscos concretos:

- amostras do mesmo veículo/viagem em treino e teste após split aleatório;
- velocidade/agregação calculada com observações depois de `t₀`;
- chegada/erro/estado final usados como feature;
- normalização/encoder ajustados no conjunto completo;
- duplicatas da previsão dentro do sampling;
- versões estruturais futuras fornecendo geometria a amostras antigas;
- tuning repetido no holdout até ele deixar de ser independente.

O split deve ser temporal e agrupado por viagem; dependendo da pergunta científica, reservar linhas/veículos também testa generalização. O `validacao_temporal.py` existente isola a janela elegível mais recente e exclui holdout da sensibilidade, mas avalia posição corrigida em metros, não ETA em segundos.

## Métricas

Para ETA: `MAE=mean(|e|)`, `RMSE=sqrt(mean(e²))`, mediana absoluta, viés `mean(e)`, P90/P95, cortes por horizonte/linha/trecho/hora, cobertura, taxa de fechamento e latência. MAPE é instável quando `y≈0`; deve ter limiar ou ser evitada. Precisão deve ser mostrada junto à cobertura: rejeitar quase todos os casos pode reduzir MAE artificialmente.

## Comparação justa

Baseline e ML devem usar os mesmos eventos, `t₀`, ocorrência, ground truth e período. Se elegibilidades diferirem, reportar interseção comparável e cobertura total de cada modelo. Nenhum caso pode ser removido apenas porque desfavorece o candidato. Resultados de posição B0–B3, em metros, não comprovam ETA.

## Treinamento e artefatos futuros

Uma execução reproduzível deve versionar query/exportador, commit, schema, intervalo, splits, transformação, seed, hiperparâmetros, bibliotecas, hardware relevante, métricas por grupo e hashes de dataset/modelo/encoder. Treinamento é offline; inferência deve carregar um bundle compatível e rejeitar schema/versão desconhecidos.

## Referências e testes

ML: `treinar.py`, `dataset_v1.py`, `gerar_dataset.py`, `validacao_temporal.py`, `validar_posicao_temporal.py`, `lab-output/eta-ml-reconciliation-audit.md`, testes Python de dataset/posição/validação. Backend: `PrevisaoEtaV2`, `TelemetriaVeiculoMl`, migrations e testes ETA. Nenhum treino, export ou teste foi executado.

