# ETA legado e migração para V2

## Finalidade e estado

Este documento reconstrói o caminho legado e demonstra por que seus artefatos não podem ser promovidos como ETA V2. Baseline: backend `53567bd`, ML `08c493b`, 2026-10-06. O `GpsEtaClient` continua no hot path; FastAPI/Uvicorn e os Joblib constituem legado contratual, porém o serviço está intencionalmente parado em produção.

## Backend: `GpsEtaClient`

`GpsPollingService` envia as posições enriquecidas a `PredizirLoteAsync`. Elegibilidade:

- `10 < DistanciaProximaParadaMetros < 5000`;
- `VelocidadeMedia` presente e não negativa;
- `0 < PosicaoNaRota < 1`;
- `CodigoLinha` não vazio.

Até 200 veículos formam cada chunk. O cliente faz `POST /eta/batch`; o `HttpClient` é configurado no `Program.cs` com base URL externa por configuração e timeout de 5 s. O payload contém `linha`, hora local do servidor `[0,23]`, `DayOfWeek` `[0,6]`, distância em metros, velocidade média em km/h, fração longitudinal e IDs estruturais extras. O Python ignora os IDs extras porque não pertencem ao schema Pydantic.

A resposta deve manter exatamente a cardinalidade e ordem do chunk. Para cada veículo, o backend usa `eta_segundos` e `confianca`; ignora `linha_conhecida` e `eta_minutos`. Qualquer exceção externa inicia cooldown process-local de 30 s e retorna dicionário vazio. Veículos sem resultado preservam o DTO anterior/sem ETA. Cancelamento pelo chamador é respeitado; timeout é medido separadamente nos contadores.

## Serviço Python real

`server.py` usa FastAPI 0.115.12/Uvicorn 0.34.2, não Flask. Ao importar, carrega `modelo_eta.joblib` e `linha_encoder.joblib`. Endpoints:

- `GET /health`: linhas do encoder;
- `POST /eta`: uma entrada;
- `POST /eta/batch`: até 500 entradas;
- `POST /retreinar`: inicia `treinar.py` em subprocesso — capacidade administrativa legada, não acionada pelo polling.

O vetor tem ordem fixa: `[hora_dia, dia_semana, distancia_metros, velocidade_media, posicao_na_rota, linha_cod]`. Linha desconhecida recebe código `0`, que pode colidir com a primeira linha conhecida; apenas a flag de resposta revela isso. A predição é limitada a `[10,3600]` s. Confiança é `alta` abaixo de 300 m, `media` abaixo de 800 m e `baixa` além disso: é heurística por distância, não erro empiricamente calibrado.

O código e dependências identificam a família real: `XGBRegressor`, 400 árvores, profundidade 6, learning rate 0,05, subsample/colsample 0,8, `min_child_weight=5` e método `hist`. Os Joblib rastreados não contêm manifesto suficiente para provar dataset, commit, métricas e ambiente que os geraram; não se inferiu família apenas por dependência.

## Treinamento anterior

`treinar.py` lê `HistoricoPassagens` e filtra:

- duração entre paradas: 30–1.200 s;
- distância do trecho: 50–8.000 m;
- velocidade: 0–90 km/h;
- campos obrigatórios não nulos.

Target: `TempoDesdeParadaAnteriorSegundos`, isto é, duração completa de um trecho já percorrido. Features: hora, dia, `DistanciaTrechoMetros`, velocidade média na passagem, posição na rota e linha com encoding ordinal. O split é aleatório 80/20 (`random_state=42`), não temporal nem agrupado por viagem. Métricas impressas: MAE, mediana absoluta e porcentagem dentro de 60/120 s. Não há relatório versionado que autorize tratá-las como resultado final.

Há ainda `dataset_v1.py`, posterior e independente, que produz segmentos estruturados e split temporal por viagem; ele não treina nem alimenta automaticamente o Joblib legado.

## Incompatibilidade semântica

No treino, para paradas consecutivas `A→B`:

```text
target_train = T_B − T_A
feature_distance_train = distância total A→B
```

Na inferência feita entre as paradas, em `t₀`:

```text
target_needed = T_B − t₀
feature_distance_runtime = distância da posição atual até B
```

Se o trecho leva 300 s e o ônibus já percorreu 80% em 240 s, o target correto é 60 s. Um modelo treinado para a duração total do trecho aprende algo próximo de 300 s, embora receba uma distância residual cuja distribuição nunca representou o mesmo target. Publicar essa saída como “tempo restante” é uma mudança de variável aleatória, não simples ruído.

Divergências adicionais:

| Dimensão | Treino | Inferência | Consequência |
|---|---|---|---|
| distância | trecho entre passagens | próxima parada, descrita historicamente como linha reta no relatório ML | distribuição/semântica distintas |
| velocidade | média na passagem | média causal no GPS atual | instante e processo distintos |
| relógio | campos históricos | relógio local do servidor, não timestamp GPS | possível timezone/atraso |
| identidade | código e encoder | código atual; desconhecido→0 | colisão e extrapolação |
| estrutura | IDs legados | IDs V2 enviados mas ignorados | padrão/ocorrência não condicionam modelo |

## Justificativa da migração

ETA V2 fixa a ocorrência, versão, volta e viagem; usa distância longitudinal restante; registra o instante da previsão; persiste preditor/versão; fecha contra a passagem correspondente; mede erro no mesmo target; e isola experimentos em shadow. Isso resolve auditabilidade e semântica antes de buscar complexidade estatística.

## Falhas, limitações, testes e pendências

FastAPI falha ao iniciar sem os Joblib. `treinar.py` importa `dotenv`, ausente de `requirements.txt`, portanto retreino na imagem não está garantido. Não foi localizado teste Python de contrato do endpoint/artefato; no backend, `GpsPerformanceMetricsTests` cobre sucesso, timeout e falha/cooldown. É necessário arquivar hashes de modelo, encoder, dataset, schema, código e métricas antes de qualquer reutilização.

## Referências

Backend: `GpsEtaClient.cs`, `GpsPollingService.cs`, `Program.cs` e `GpsPerformanceMetricsTests.cs`. Repositório ML: `server.py`, `treinar.py`, `dataset_v1.py`, `requirements.txt`, `modelo_eta.joblib`, `linha_encoder.joblib` e `lab-output/eta-ml-reconciliation-audit.md`.

