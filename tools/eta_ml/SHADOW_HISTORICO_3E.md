# 3E — Shadow histórico causal (desligado por padrão)

Implementação técnica preparada, sem ativação, modelo real certificado ou comparação real de previsões. O cliente público `GpsEtaClient`, `ML:BASE_URL`, Redis operacional e o Shadow legado `EtaV2Shadow`/`LONGITUDINAL_SPEED_V0` permanecem independentes. Não usar diagnóstico exploratório como treino. Não habilitar modelo sintético em produção.

## Captura e contrato

`GpsPollingService.ConfirmarPosicaoAsync` chama `HistoricalEtaShadow.TryCapture` depois da aceitação do GPS e da decisão operacional, junto ao ponto do Shadow legado. A captura copia o DTO e verifica timestamp/veículo/linha/sentido/padrão/versão/topologia contra a decisão confiável. Exige próxima ocorrência explícita na mesma volta e à frente; ausência, versão divergente, viagem finalizada e wrap implícito são recusados. Não consulta passagens, journal ou labels.

Contrato `noponto-eta-shadow-history-v1`: lista de até 200 objetos com as 16 features do serviço histórico (`modal`, `codigo_linha`, `linha_id`, `sentido_id`, `padrao_id`, `versao_id`, `ocorrencia_id`, `parada_id`, `topologia`, `posicao_gps`, `posicao_destino`, `distancia_metros`, `velocidade_kmh`, `velocidade_media_causal_kmh`, `hora_dia`, `dia_semana`), mais `shadow_contract` e `shadow_request_id`. IDs estruturais preservam o contrato existente; observação/viagem/veículo não viram features. Velocidade inválida fica nula, permitindo o tratamento de ausência já existente no modelo. Hora/dia vêm de `TimestampGps.ToOffset(-03:00)`, domingo=0. `ObservacaoId` usa a função da telemetria; request ID é SHA256 de contrato/observação/alvo/versão/volta.

A resposta continua um array em ordem. Para Shadow, cada item precisa ecoar `shadow_request_id`; cabeçalhos `X-Model-Version` (SHA256 do artefato) e `X-Model-Data-Kind: real` são obrigatórios. O backend valida o lote inteiro antes de registrar previsões e recusa sintéticos, ordem incorreta, ETA inválido e resposta acima de 256 KiB. O serviço ML mantém sua validação de procedência e artefato antes do carregamento; o hash identifica a versão, não é assinatura criptográfica. Requisições legadas sem identificador Shadow mantêm a resposta anterior.

ETA público só é correlacionado quando a ocorrência usada pela previsão pública é exatamente o alvo operacional capturado. Caso contrário fica nulo; nunca recalcular ou sobrescrever o ETA público para completar a comparação.

## Distância e custo

O worker resolve estrutura e destino por IDs explícitos num único SELECT com `unnest(... WITH ORDINALITY)` para até 200 observações. Calcula `ST_Length(ST_LineSubstring(GeometriaVersionada, posiçãoGPS, posiçãoDestino)::geography)`, preservando a semântica operacional do SQL 3A. Não usa distância direta, histórico de passagem ou geometria de outra versão. Conferência final de estrutura/posição/distância evita completar dados ausentes.

Uma conexão/transação READ ONLY por lote, buscas por PK nas tabelas estruturais e até 200 cálculos de trecho geográfico; nenhuma consulta adicional individual no polling e nenhum scan de telemetrias. Timeout de statement 500 ms, lock 100 ms e prazo conjunto consulta+HTTP de 1000 ms por padrão. Há custo geográfico proporcional à complexidade da rota: latência/CPU reais ainda precisam ser medidas localmente. Se exceder os limites, descarta e abre circuito; não aumentar tolerâncias nem trocar por distância direta. Um worker serial limita concorrência; não existe cache de geometrias ilimitado.

## Isolamento e observabilidade

Seção própria `HistoricalEtaShadow`: `Enabled=false`, `BaseUrl=http://eta-history:5200`, `QueueCapacity=1000`, `BatchSize=200`, `TimeoutMs=1000`, `CircuitSeconds=30`, `MaxAgeSeconds=30`, `SinkCapacity=500`, `SinkTtlSeconds=300`. Valores têm limites máximos validados na inicialização. Cliente HTTP exclusivo `eta-history-shadow`; não reutiliza `GpsEtaClient`. Fila limitada, `TryWrite` sem espera, coalescência de 50 ms, sem retries. Erros de captura são contidos; falhas de consulta/HTTP, timeout e 503 abrem circuito por 30 s. Descartes de fila cheia, observação expirada, circuito aberto e encerramento são contabilizados explicitamente.

Sink em memória independente, no máximo 500 registros/5 minutos por padrão, com correlação observação/veículo/alvo/versão/volta/timestamp/modelo/ETA público/ETA histórico/latência/estado. Não escreve Redis, arquivos, tabelas ou logs por GPS. TTL é aplicado ao escrever/consultar; limite físico permanece mesmo sem consultas. Contenção descarta o registro sem bloquear produtor. Reinício perde resultados, por escolha. `HistoricalEtaShadow.Results` e `.Metrics` são snapshots consultáveis via DI; não se expõem em endpoint público nesta etapa. Counters incluem candidate, ineligible, enqueued, queue_depth, queue_full, circuit_open, geometry_refused, expired, success, model_unavailable, http_failure, timeout, failure, capture_failure, stopped, sink_evicted e sink_contention_drop. Cobertura técnica = success/candidate; cobertura comparável exige success com PublicEta não nulo. Contadores são locais à instância, não estimam desempenho real.

## Homologação manual exclusivamente local

Não executada pelo agente; verificar isolamento dos serviços backend/PostGIS/Redis antes de iniciar o backend. Não carregar configuração/conexões de produção. O container ML abaixo não acessa banco nem treina, e expõe somente loopback. Sem modelo, health=200, ready=503 e lote vazio retorna []; lotes técnicos válidos retornam 503, registrado como model_unavailable/circuit_open. A primeira comparação real depende de artefato certificado montado read-only conforme README_ETA_3C.md do ML.

```powershell
Set-Location D:/repositorio_github/NoPonto/ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests -p test_eta_history_service.py -q
docker build -f Dockerfile.eta-history -t noponto-eta-history:3e-local .
docker run --rm --name eta-history-3e-local --memory 512m --cpus 0.5 --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp:rw,noexec,nosuid,size=32m -p 127.0.0.1:5201:5200 noponto-eta-history:3e-local
# Em outro terminal:
Invoke-RestMethod http://127.0.0.1:5201/health
try { Invoke-RestMethod http://127.0.0.1:5201/ready } catch { $_.Exception.Response.StatusCode }
Invoke-RestMethod http://127.0.0.1:5201/eta/batch -Method Post -ContentType application/json -Body '[]'
Set-Location D:/repositorio_github/NoPonto/noponto-backend
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsPollingCadenciaTests|FullyQualifiedName~TelemetriaMlIdentidadeTests' --verbosity quiet
# SOMENTE após verificar que a configuração completa do backend é local/descartável:
$env:HistoricalEtaShadow__Enabled='true'
$env:HistoricalEtaShadow__BaseUrl='http://127.0.0.1:5201'
dotnet run --project NoPonto/NoPonto.csproj --no-launch-profile
# Ao terminar:
Remove-Item Env:HistoricalEtaShadow__Enabled, Env:HistoricalEtaShadow__BaseUrl
```

O operador deve observar os snapshots do sink/counters via debugger local e confirmar que indisponibilidade/fila cheia preservam polling e ETA público. Não há exportador de métricas/dashboards nesta etapa. Validar também consulta real contra geometria versionada, p95/RSS, pressão no pool e recuperação após circuito. Nenhum teste conectado/PostGIS/Docker foi executado automaticamente. Defaults permanecem desligados; ativação em produção exige autorização futura, benchmark e modelo real certificado.

## Atualização 3E.1 — homologação local executada

Componentes aprovados em fixture PostgreSQL 16/PostGIS 3.4 exclusiva e HTTP loopback controlado; smoke Docker do serviço ML real passou sem modelo. Captura após encerramento corrigida de queue_full para stopped; cliente HTTP exclusivo liberado no Dispose. Defaults e ETA público preservados. Resultados medidos, falhas de inicialização fria, limites de cancelamento/pool, memória e comandos reproduzíveis estão em [HOMOLOGACAO_SHADOW_3E_1.md](HOMOLOGACAO_SHADOW_3E_1.md). Usar os runners dessa homologação antes de qualquer execução local da API: eles não iniciam API nem acessam providers operacionais. Aprovação exclusivamente técnica/local, sem artefato certificado, comparação real ou ativação em produção.
# Evolução 3F

Agregação bounded, logs estruturados periódicos, custos e comandos atualizados estão em [OBSERVABILIDADE_SHADOW_3F.md](OBSERVABILIDADE_SHADOW_3F.md). Shadow continua OFF. Comparações entre previsões não constituem avaliação de chegada; a persistência temporal futura requer aprovação separada. Para smoke atualizado usar imagem `noponto-eta-history:3f-local`, com liveness leve e readiness 503 sem modelo certificado.
