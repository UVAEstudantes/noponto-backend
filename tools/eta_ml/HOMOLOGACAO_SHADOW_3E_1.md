# 3E.1 — homologação local do Shadow histórico

Decisão: **APROVADO para habilitação local do Shadow técnico**, exclusivamente com serviços descartáveis/fixtures. Não certifica modelo, dataset, desempenho de ETA ou capacidade de produção. Shadow permanece desligado por padrão; nenhuma API foi iniciada, nenhum Redis operacional foi usado e nenhuma configuração de produção foi alterada.

## Ambiente e isolamento

PostgreSQL 16.4/PostGIS 3.4.3, imagem `postgis/postgis:16-3.4` já disponível. Cada execução cria container, banco e rede exclusivos com nomes aleatórios, label de fixture, porta publicada somente em 127.0.0.1, comunicação entre containers desabilitada, memória 512 MiB e CPU 1. Dados fictícios em tmpfs de 256 MiB; nenhuma migration operacional, snapshot ou dado real é carregado. Autenticação trust restrita a esse ambiente descartável sem credenciais operacionais. Runner só remove recursos que ele próprio criou; não usa prune, drop de banco existente ou nomes operacionais.

O teste exige env específica, host loopback, porta diferente de 5432, prefixo de banco de fixture, application_name próprio, comentário identificador em catálogo compartilhado (`shobj_description`), versões corretas e ausência das tabelas da fixture antes do DDL. Sem env, o teste conectado é explicitamente ignorado. Não herda POSTGIS_TEST_CONNECTION nem connection strings da API.

Schema mínimo exclusivo com PKs/FKs das relações consultadas e duas geometrias fictícias: LINEAR com três vértices, CIRCULAR fechada com quatro. Uma view de teste expõe os campos de PadroesVersoes e uma função que falha se a consulta não estiver realmente READ ONLY, com statement_timeout=500ms e lock_timeout=100ms. Essa instrumentação existe só na fixture; não altera SQL do resolver nem schema operacional.

HTTP controlado via HttpListener em loopback, sockets reais, tarefas observadas no encerramento. Resposta de sucesso usa stub explicitamente de teste, não modelo real/sintético promovido. Smoke separado executa o serviço ML verdadeiro em container novo, sem rede externa, modelo, volume ou treinamento; verifica disponibilidade e recusa de modelo ausente.

## Resultados efetivamente observados

Execução final: `outputs/shadow-3e1-a213e4da3ea14f75b2785743da52f3dd/`, contendo test.log, TRX, versões, stats e log da fixture. Dez repetições por tamanho após aquecimento limitado; tempos são de ponta a ponta do resolver, incluindo pool/transação/leitura/rollback, sem EXPLAIN ANALYZE. Não são tempos de CPU isolados do servidor.

| Candidatos | Mediana ms | P90 ms | Máximo ms | Contextos de memória backend após lote (bytes) |
|---:|---:|---:|---:|---:|
| 1 | 6,322 | 7,332 | 7,827 | 2.307.344 |
| 10 | 5,155 | 5,924 | 43,704 | 2.290.960 |
| 50 | 5,203 | 6,088 | 6,503 | 2.290.960 |
| 200 | 8,637 | 14,634 | 155,417 | 2.290.960 |

Mesmo backend PID reutilizado pelo pool nos quatro tamanhos. Contextos de memória são amostras pós-lote de `pg_backend_memory_contexts`, não pico/RSS completo do processo. RSS do testhost foi 128,58/125,50/122,46/122,05 MiB respectivamente; inclui toda a assembly de testes, driver e servidor HTTP, não representa memória isolada do worker. Alocação gerenciada total durante dez repetições+validações: 150.112/641.496/2.832.784/11.042.248 bytes. Deltas de heap após GC podem ser negativos por coleta de temporários; não são estimativas de tamanho do modelo.

Container PostgreSQL ao final: **135,5 MiB de 512 MiB**, incluindo tmpfs/caches; amostra, não pico. Worker processou 1.000 observações em cinco ondas de 200, com sink limitado a oito registros: delta de heap retido 311.752 bytes. Concorrência observada: uma consulta Shadow ativa e uma requisição HTTP ativa. Monitor periódico usa segundo lease exclusivamente na fixture. Uma segunda prova, com atraso controlado de 50ms na função da fixture e batch=1/20 observações, confirmou pico de uma consulta ativa. Esse atraso não compõe a tabela de latências acima.

| Falha/cenário | Tempo observado ms | Resultado |
|---|---:|---|
| pg_sleep(700ms) dentro da consulta, statement_timeout 500ms | 933,775 | SQLSTATE 57014, mensagem statement timeout |
| Pool de dois leases inteiramente ocupado, orçamento worker 1000ms | 1.016,330 | timeout; leases liberados e nova consulta válida |
| HTTP 503 | 78,766 | model_unavailable; circuito e recuperação conferidos |
| HTTP 200 com JSON inválido | 10,487 | failure; sem previsão registrada |
| HTTP lento | 1.009,671 | timeout; sem previsão registrada |
| Duas capturas com fila de capacidade 1 | 0,830 | primeira aceita, segunda queue_full, sem esperar consulta/HTTP |

Os 500ms são limite do statement PostgreSQL: o tempo acima inclui abrir novo backend, executar configurações, entregar erro e fazer cleanup. Prazo total usa cancelamento em 1000ms; conclusão observada tem sobrecarga de ~10–16ms nos testes de timeout final. Não é garantia de wall-clock rígido em 1000ms sob qualquer carga. Houve outliers e uma tentativa anterior cuja primeira consulta fria excedeu o limite e terminou com NpgsqlException/TimeoutException; não foi ocultada nem tratada como sucesso. Relatório dessa tentativa permanece em `outputs/shadow-3e1-ce35a6d358ce436fac62e70d885a7fbe/`. Aquecimento do harness tem até três tentativas limitadas e registra cada status/tempo; o worker de produção não ganhou retry. Na execução final, todos os aquecimentos passaram na primeira tentativa (108,372/7,108/6,930/10,211ms).

Dados ausentes/IDs incompatíveis/posição inválida/alvo inexistente foram recusados. Linear/circular, mesma versão/linha/sentido/padrão/ocorrência/parada, distância contra referência PostGIS independente, ausência de wrap, ordem do resultado e correlação do batch passaram. Consulta funciona sem tabelas TelemetriasVeiculoMl/HistoricoPassagens, e EXPLAIN seletivo não as referencia. O otimizador pode usar scan sequencial nas poucas linhas estruturais da fixture; não é scan global de telemetria.

503, JSON inválido, timeout, circuito aberto, recuperação após intervalo de teste, fila cheia, cancelamento, dreno no StopAsync e ExecuteTask concluída foram validados. ETA público fictício permaneceu em 80s e DTO não foi alterado. Regressões de polling/cadência/identidade continuaram aprovadas. Não equivale a teste da API inteira com fontes GPS/Redis reais; essa execução não foi feita para evitar integrações operacionais.

## Smoke Docker ML

Build `noponto-eta-history:3e1-local` aprovado, imagem SHA256 `a0df2956b15d24833ed4fef8501d8ab33a4f0eebcbd205bc7e1306019774fc91`. Container exclusivo, network=none, 512 MiB, CPU 0,5, readonly, user da imagem não root, capabilities removidas, tmpfs /tmp 32 MiB.

Resultado: health 200, ready 503, batch vazio 200/[], batch completo 503 `Trusted compatible model unavailable`, Docker health=healthy; memória amostrada **92,24 MiB**. Tempo observado até primeira resposta saudável: **100,995s**, contado após início do probe, não benchmark preciso do cold-start completo. Uma tentativa anterior não atingiu readiness no prazo do harness; healthcheck da imagem chegou a unhealthy durante importação fria. Probe foi ajustado para importar urllib uma vez e aguardar até 180s, sem alterar o healthcheck/imagem ou prazo de inferência. Inicialização fria e healthcheck precisam de revisão/medição antes de qualquer implantação; não esconder essa limitação aumentando o orçamento do worker.

## Correção mínima e testes

Regressão `StoppedWorkerClassifiesLateCaptureAsStopped` falhou antes: captura após StopAsync era `queue_full`. Corrigido com estado de aceitação e classificação `stopped`, incluindo corrida com fechamento. Dispose libera o HttpClient exclusivo; cliente público não é afetado.

Resultado final: **65 testes C# offline aprovados**, incluindo 17 casos Shadow e regressões de polling/cadência/telemetria; **um teste conectado composto aprovado** (~13s de cenários/16,861s do runner de testes); **seis testes de serviço ML e um de hashes de migração aprovados**. Build incremental final passou (1,54s/zero erros); recompilações mostraram cinco warnings preexistentes. git diff --check nos dois repos e whitespace dos arquivos novos conferidos. Nenhum teste de treino/modelo real foi executado.

Falhas do preparo foram corrigidas no harness: Docker Desktop não publicou porta de network --internal; pg_isready aceitou servidor temporário antes de o banco/extensão estar pronto; inicialização da imagem precisa terminar antes da conexão TCP; extensão pode já existir; comentários de bancos estão no catálogo compartilhado. Recursos de cada tentativa removidos somente pelo runner proprietário. Dados da fixture em tmpfs evitam depender da durabilidade/disco do volume; não são benchmark de I/O operacional.

## Limites e risco residual

- Source Npgsql continua compartilhado com o backend. Worker serial usa no máximo um lease em execução normal; teste de exaustão comprova prazo/cancelamento e recuperação, não fairness nem ausência de pressão em um pool de produção saturado. Não houve alteração do pool/configuração pública. Monitorar utilização antes de ampliar volume; um pool próprio limitado pode ser discutido se evidência de contenção real justificar.
- Fila/sink/response têm limites, tarefas criadas pelo harness são aguardadas, StopAsync conclui ExecuteTask e leases são liberados. Ensaio de 1.000+20 observações não prova ausência de vazamento por dias nem mede RSS com modelo carregado.
- Geometrias de três/quatro vértices, apenas duas versões fictícias e máquina local sob carga. Não extrapolar os percentis para rotas complexas, homeserver, concorrência pública ou desempenho de ETA.
- Artefato real certificado continua inexistente; nenhuma AuditadaSemProtecao foi criada. Aprovação é técnica/local, sem autorização de produção/deploy.

## Reprodução PowerShell

Não iniciar a API. Os runners criam serviços exclusivos; nunca fornecer connection string operacional. Docker Desktop deve estar saudável. Arquivos de resultados devem estar em diretório novo.

```powershell
Set-Location D:/repositorio_github/NoPonto/noponto-backend
dotnet build NoPonto/NoPonto.csproj --no-restore --verbosity quiet
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter 'FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsPollingCadenciaTests|FullyQualifiedName~TelemetriaMlIdentidadeTests' --verbosity quiet
pwsh -NoProfile -File tools/eta_ml/homologate_shadow.ps1
# Alternativa com diretório NOVO explicitamente escolhido:
# pwsh -NoProfile -File tools/eta_ml/homologate_shadow.ps1 -ReportDirectory D:/temp/shadow-3e1-nova-execucao
git diff --check

Set-Location D:/repositorio_github/NoPonto/ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests -p test_eta_history_service.py -q
python -m unittest discover -s tests -p test_migration_3c.py -q
docker build -f Dockerfile.eta-history -t noponto-eta-history:3e1-local .
git diff --check

Set-Location D:/repositorio_github/NoPonto/noponto-backend
pwsh -NoProfile -File tools/eta_ml/smoke_shadow_ml.ps1 -Image noponto-eta-history:3e1-local
```

Para habilitação local futura com API, verificar separadamente todos os providers/configs/conexões como fixtures locais antes de usar flags do guia 3E. Esta homologação aprova os componentes e seu isolamento; não inicia a API automaticamente.
