# 08 — ETA, ML e evolução

**Data:** 2026-10-06 · **Commits:** backend `53567bd`, ML `08c493b` · **Estado:** código verificado; produção não operacional para serviço ML

## Implementação atual

O repositório ML usa Python com Flask, pandas, NumPy, scikit-learn, psycopg e Joblib. `server.py` carrega `modelo_eta.joblib` e `linha_encoder.joblib`, valida a entrada, monta features e entrega inferência HTTP. `dataset_v1.py` extrai/segmenta telemetria e grava Parquet com manifesto/schema; `treinar.py` treina e persiste artefatos; `validacao_temporal.py` separa holdout temporal para reduzir leakage.

O backend possui dois caminhos:

- `GpsEtaClient`: integração HTTP com o serviço Python e fallback quando indisponível.
- ETA V2: `EtaV2ShadowService`, canal bounded, batch worker, repositório PostgreSQL, maintenance/expiração e métricas, inicialmente adequado a shadow/canary.

Há telemetria contínua com amostragem determinística, backpressure, stream, worker de persistência e retenção. Isso fornece base para evolução, mas não comprova qualidade preditiva.

## Features e avaliação

O pipeline local trabalha com atributos derivados de linha/veículo/tempo/movimento e targets temporais construídos de segmentos. A lista autoritativa está em `dataset_v1.py` (`FEATURE_COLUMNS` e `FEATURE_SCHEMA_VERSION`) e deve ser reproduzida automaticamente na Etapa 2 para evitar divergência. Existem scripts de avaliação de ETA e de posição corrigida, além de testes Python.

Não foi encontrada evidência suficiente nesta auditoria para declarar métricas finais generalizáveis, comparação formal com baseline ou validação externa. Artefatos Joblib existentes não identificam, por si só, o dataset e a execução que os produziram.

## Produção

O container `noponto_ml` estava parado com exit code 137 e estado unhealthy. A API permanecia ativa porque o fluxo possui desacoplamento/fallback. `PrevisoesEtaV2` tinha estimativa zero de linhas, enquanto `TelemetriasVeiculoMl` tinha ~19,3 milhões; logo, coleta de dados está implantada, mas previsão persistida V2 não foi demonstrada.

## Evolução

Próximos passos fundamentados: versionar manifesto do modelo junto ao artefato; registrar métricas e baseline; controlar drift; reter dataset reproduzível; restaurar o serviço dentro do orçamento de memória; promover ETA V2 de shadow para canary somente com gates mensuráveis. DCRNN/GNN e modelos espaço-temporais são possibilidades futuras, não capacidades implementadas.

## Referências e pendências

`ml/server.py`, `dataset_v1.py`, `treinar.py`, `validacao_temporal.py`, `requirements.txt`; backend `GpsEtaClient.cs`, `EtaV2Shadow.cs`, workers/repository e testes `EtaV2*`. Associar hashes de modelo, encoder, dataset e métricas antes de uso acadêmico quantitativo.
