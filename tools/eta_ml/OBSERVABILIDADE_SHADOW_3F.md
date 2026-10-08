# 3F — observabilidade operacional do Shadow histórico

Implementada e validada localmente. Shadow continua desligado por padrão; não há modelo histórico real certificado, avaliação de chegada real ou autorização de produção. Não foram iniciadas API completa, produção, SSH, treino ou migrations. As alterações anteriores do working tree foram preservadas.

## Implementação e limites

`HistoricalEtaShadow` reutiliza seus contadores e sink bounded. `HistoricalEtaOperationalStatistics` acrescenta sete janelas numéricas: resolver, HTTP, processamento, espera na fila, tempo desde captura até término, diferença histórica−pública e diferença absoluta entre previsões. Latências são milissegundos; diferenças são segundos. Não retém identificadores de veículos/observações nessas janelas. As duas janelas de comparação são reiniciadas quando muda o hash de modelo validado; as de latência continuam operacionais.

`OperationalSnapshot`, acessível pela instância DI, usa schema `noponto-eta-shadow-operations-v1`. Contadores são acumulados desde o início da instância; percentis são nearest-rank das últimas N amostras, não uma série temporal. Snapshot concorrente é diagnóstico e não uma transação entre todos os contadores. Amostras ausentes geram percentis nulos. Chaves/rejeições têm cardinalidade fixa, sem agrupamento por linha, veículo, viagem ou modelo.

Contadores incluem candidatos, elegíveis, inelegíveis, motivos de rejeição, enfileirados, fila cheia, batches/observações, resultados válidos, recusas, falhas HTTP/503/timeouts, aberturas e recuperações do circuito. O snapshot inclui profundidade, capacidade, ocupação e maior ocupação observada da fila. `probe_allowed` significa prazo do circuito expirado; recuperação só é contada após resposta integralmente válida. Cancelamento de encerramento não abre circuito. Timeout do resolver não é interpretado como indisponibilidade de modelo.

Taxas: `eligible_rate=eligible/candidate`, `enqueue_rate=enqueued/eligible`, `operational_coverage=success/candidate`, `comparable_prediction_rate=comparable_prediction/success`; denominador zero produz null. São taxas operacionais acumuladas, não qualidade preditiva. `candidate_per_second` usa uptime da instância. `model_status` é a última observação HTTP; `last_validated_model_version` e sua data conservam o último hash validado mesmo após indisponibilidade, sem afirmar que ele segue disponível.

Novas opções da seção já existente `HistoricalEtaShadow`:

| Opção | Default | Limite |
|---|---:|---|
| MetricSampleCapacity | 1024 | 32–4096 por janela |
| MetricsLogEnabled | true | somente com Shadow habilitado |
| MetricsLogSeconds | 60 | 30–3600 |

O worker emite `HistoricalEtaShadowMetrics {SnapshotJson}` em nível Information, por tarefa periódica própria, observada/cancelada no encerramento. Sem logs por GPS; provider desabilitado evita serialização; exceção do provider é contada e contida. A configuração global continua OFF. Dozzle pode filtrar `HistoricalEtaShadowMetrics`; o JSON possui campos estáveis para dashboard futuro. Retenção dos logs depende do limite já configurado no runtime, que esta etapa não altera. Não há endpoint de métricas, container de monitoramento ou nova persistência. Consulta segura atual: DI/testes locais; qualquer exposição HTTP requer desenho/autorização futura.

O sink original permanece 500 resultados/300 segundos por padrão. A fila original continua limitada, captura usa TryWrite e não participa dos locks das janelas estatísticas. Resolver/HTTP continuam fora do polling, com budgets originais de 500/1000 ms e sem retries. Logger e agregação periódica podem consumir CPU/IO, mas não são chamados pelo produtor GPS. ETA público, Redis, cliente público e EtaV2Shadow legado não receberam alterações nesta etapa.

Exemplo reduzido de fixture (não dados ou modelo reais):

```json
{
  "schema": "noponto-eta-shadow-operations-v1",
  "enabled": true,
  "counters": {"candidate":3,"eligible":2,"enqueued":1,"queue_full":1,"success":1,"http_503_batches":1,"circuit_openings":1,"circuit_recoveries":1},
  "operational_coverage": 0.3333333333333333,
  "queue_occupancy": 1,
  "circuit_state": "closed",
  "aggregation": {
    "sample_capacity":1024,
    "percentiles":{"prediction_difference_seconds":{"sample_count":1,"p50":-20,"p90":-20,"p95":-20}},
    "comparison_kind":"prediction_difference_not_arrival_error",
    "arrival_evaluation_state":"awaiting_trusted_labels"
  }
}
```

## Três tipos de evidência

1. Observabilidade operacional: cobertura, recusas, latência, saturação, circuito e modelo. Funciona sem modelo, com 503 explícito.
2. Comparação entre previsões: somente ETA público capturado para a mesma ocorrência operacional do alvo histórico, com correlação já validada pelo adapter/resposta. Diferença não é erro real; ETA público nulo não é preenchido artificialmente. Não são emitidos MAE/RMSE de chegada.
3. Avaliação contra chegada: ainda não implementada. Exige passagem real confiável, validação do contrato 3A e correlação temporal íntegra. O sink de cinco minutos é insuficiente para reconstrução tardia.

Proposta para etapa separada, dependente de aprovação: writer assíncrono independente, spool local com quota/TTL explícitas (por exemplo 64 MiB/48 h), desligado por padrão, sem alterar journal. Guardar ObservacaoId, timestamp GPS, viagem/volta/versão/alvo, hash do modelo e previsões; fazer join offline com passagem confiável para exatamente o mesmo alvo/execução, aplicando os validadores 3A. Contar perda por quota, atraso, ausência de label e inconsistência; só então calcular MAE/RMSE/P90 e coverage de avaliação. Não implementar armazenamento ilimitado nem certificar AuditadaSemProtecao por inferência. Nenhum spool foi criado nesta etapa.

## Inicialização fria do ML

Causa comprovada: imports eager de numpy/sklearn/pipeline ocorriam antes da liveness, mesmo sem artefato. Perfil exclusivo `network=none`, 512 MiB, 0,5 CPU, sem modelo:

| Medição | Imagem 3E.1 | Protótipo 3F |
|---|---:|---:|
| import eta_history_service | 67,606 s | 3,098 s |
| pico RSS na conclusão desse import | 138.628 KiB | 45.152 KiB |
| numpy/sklearn já importados | sim | não |

Após o import leve, carregar explicitamente numpy/sklearn/pipeline no protótipo levou 0,513/5,973/1,040 s. Outra ordem de medição na imagem antiga deu numpy 1,764 s, sklearn 8,136 s, FastAPI 2,163 s e pipeline 1,734 s. Cache, contenção e ordem afetam o tempo: não atribuir todos os ~101 s anteriores exclusivamente a uma biblioteca. Arquivos locais: `outputs/startup-3f-3552167201964687a1c30f835925d6c9`.

Correção: `inference_contract.py` lê com AST/std lib as declarações literais existentes de features/version do pipeline, sem executar treinamento ou duplicar listas. Declarações inesperadas falham; teste garante igualdade com o pipeline original. Sklearn carrega apenas quando existe artefato, mantendo a validação original de versão antes de unpickle; numpy/matrix carregam na inferência após modelo disponível. Hash, procedência, contrato e synthetic gate permanecem. Os 15 hashes da migração continuam conferidos.

Smoke da imagem final `noponto-eta-history:3f-local`: liveness observada 3,402 s, memória amostrada 32,59 MiB, health 200, ready 503, lote vazio 200 [], lote completo 503 `TrustedCompatibleModelUnavailable`, healthcheck healthy. Digest final local: `sha256:74a613f85aa741270c39d20a7ea134d73458d297d0d53eaf3ebe315886581a8f`.

Liveness só indica processo saudável; readiness exige modelo compatível. Custo de carga de modelo real foi adiado, não eliminado: benchmark/warmup de artefato certificado continua pendente. Não se aumentou timeout de inferência. O harness tolera timeout da sondagem durante startup, sem alterar o timeout do pedido de inferência.

## Validação e custos observados

- Backend offline final: 72 testes aprovados, zero falhas/ignorados; runner 3,923 s. Cobrem concorrência, janelas/percentis, reset por modelo, logs OFF/erro/timer/stop, counters, fila, circuito/recuperação, 503/timeout, causalidade, correlação e preservação do ETA fictício público.
- ML: 82 testes offline aprovados, 4,613 s, incluindo contrato leve, ausência de modelo, correlação/headers e hashes históricos. Sem treino real.
- Build incremental final aprovado em 2,52 s, zero warnings/erros; recompilação da suíte apresentou cinco warnings preexistentes. `git diff --check` aprovado nos dois repositórios; arquivos novos conferidos separadamente. Nenhum commit/push/deploy. O working tree contém também alterações anteriores, não atribuídas à 3F.
- Custo local da agregação: 100.000 samples em 10,126 ms; 100 snapshots em 26,845 ms; delta de heap retido após GC 10.096 bytes. Não é limite de RSS nem prova de ausência de vazamento por dias. Payload numérico máximo derivado: 7×1024×8=57.344 bytes default; 229.376 bytes no máximo configurável, além de objetos/cópias transitórias.
- Regressão conectada exclusiva com instrumentação 3F: um teste composto/17 cenários passou; runner ~20,08 s. Medianas resolver para lotes 1/10/50/200: 5,588/9,005/11,390/10,728 ms; P90 7,074/12,479/14,337/12,144 ms; máximos 7,293/102,842/20,344/14,455 ms. Memória de contexto DB ~2,07–2,09 MB, não RSS/pico global. Fixture fictícia, READ ONLY, sem tabelas operacionais.
- Worker fixture 1000 observações/sink8: heap retido 446.456 bytes; fila cheia/captura 1,450 ms; pico observado uma consulta/um HTTP. SQL timeout ~537,788 ms, pool cancelado ~1013,033 ms e HTTP lento ~1009,020 ms. Cancelamento não é deadline wall-clock rígido. Primeira consulta fria terminou por timeout em 1421,627 ms; tentativa seguinte do harness passou em 559,519 ms. Não foi adicionado retry ao worker. Evidência: `outputs/shadow-3e1-6842f8e0c60f47e0b7c1303b9271ac90`.

Os cenários conectados antecederam os últimos ajustes apenas estatísticos (espera/fim-a-fim/reset da janela), cobertos pelos 72 testes finais. O pool público permanece compartilhado: worker serial limita a um lease; fairness com carga operacional real não foi medida. Modelo real/rotas reais/uso prolongado permanecem fora desta homologação.

## Reprodução PowerShell — somente local

Arquivos desta etapa: backend `HistoricalEtaShadow.cs`, novo `HistoricalEtaOperationalStatistics.cs`, `HistoricalEtaShadowTests.cs`, `profile_shadow_startup.ps1`, ajuste de sondagem em `smoke_shadow_ml.ps1`, este relatório, guia 3E, documentação 3B e HISTORICO (somente acréscimo). ML: `eta_history_service.py`, novo `inference_contract.py`, novo `tests/test_inference_contract.py`, `Dockerfile.eta-history` e `README_ETA_3C.md`. DI/polling/cliente público não receberam alterações nesta etapa.

```powershell
Set-Location D:/repositorio_github/NoPonto/noponto-backend
dotnet build NoPonto/NoPonto.csproj --no-restore
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsPollingCadenciaTests|FullyQualifiedName~TelemetriaMlIdentidadeTests' --logger 'console;verbosity=detailed'
git diff --check

Set-Location D:/repositorio_github/NoPonto/ml
python -m unittest discover -s tests -p 'test_*.py' -q
docker build -f Dockerfile.eta-history -t noponto-eta-history:3f-local .
git diff --check

Set-Location D:/repositorio_github/NoPonto/noponto-backend
pwsh -NoProfile -File tools/eta_ml/smoke_shadow_ml.ps1 -Image noponto-eta-history:3f-local
# Perfil pareado requer imagem histórica já disponível; cria apenas containers exclusivos:
pwsh -NoProfile -File tools/eta_ml/profile_shadow_startup.ps1
# Opcional: PostGIS exclusivo com fixture fictícia e cleanup dos próprios recursos:
pwsh -NoProfile -File tools/eta_ml/homologate_shadow.ps1
```

Não ligar a API completa com configurações existentes para reproduzir esses testes. Próxima etapa: resolver certificação/volume do dataset e produzir primeiro modelo real offline; depois homologar carga/readiness e comparação local, e aprovar separadamente correlação bounded com passagem real. Ativação produtiva e retreinamento periódico continuam sem execução/autorização.
