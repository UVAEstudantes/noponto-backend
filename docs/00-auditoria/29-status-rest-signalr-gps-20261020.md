# Paridade de status GPS entre REST e SignalR — 2026-10-20

Base: `204caef`, branch `audit/gps-bus-brt-pipeline`. Correção local validada, sem deploy, push ou merge. Frontend, ML e produção não foram modificados.

## Causa comprovada

`GpsSignalRPublisher` utilizava os DTOs aceitos no ciclo como posições atuais. Para os demais veículos do índice da linha, lia apenas `:recente` e impunha `SemSinal`. Uma duplicata GPS corretamente descartada deixa de pertencer a `aceitos`, mas isso não significa que seu payload `:ativo` expirou. O REST consultava `:ativo` primeiro e preservava seu status. Os dois canais interpretavam o mesmo estado Redis de formas diferentes.

Antes de alterar runtime, `SignalR_StatusLifecycle_MatchesRestWithoutRenewingDuplicate`, com BUS e BRT, passou pelo primeiro snapshot e falhou na comparação após o ciclo duplicado: **esperado Ativo (REST), recebido SemSinal (WebSocket)**. Resultado vermelho: 0/2 aprovados, duas falhas na asserção de status, sem timeout de transporte. Testes existentes que caracterizavam essa divergência foram atualizados para exigir paridade.

## Alteração mínima

Somente `GpsSignalRPublisher.cs` mudou em produção. O publicador resolve os veículos do índice e dos aceitos a partir dos payloads persistidos: lê `:ativo`, busca `:recente` somente quando o ativo está ausente/em branco e reutiliza `VeiculosLinhaRuntimeReader.Decode`, a política oficial de leitura por linha. O status persistido no ativo é preservado; o recente é publicado como `SemSinal`, com timestamps originais. `aceitos` identifica candidatos, sem substituir a leitura do estado vigente.

```mermaid
flowchart LR
  I[Índice da linha e IDs aceitos] --> A{Payload ativo?}
  A -->|Sim| D[Decode oficial: preservar status]
  A -->|Ausente| R[Recente: SemSinal]
  R --> F[Filtrar pela linha atual do payload]
  D --> F
  F --> P[PosicaoAtualizada ou LinhaEsvaziada]
```

O filtro de pertencimento à linha permanece, inclusive com índice antigo apontando para veículo transferido. `PosicaoAtualizada(posicoes[])`, projeção/aliases legados, grupos e `LinhaEsvaziada({ codigoLinha })` não mudaram. A supressão de vazios e a geração de inscrição/reconexão permanecem intactas. Sem assinantes, continuam zero leituras/broadcasts.

Leituras ativas são compartilhadas apenas dentro da publicação, por identidade do veículo. O memoizador de recentes já existente continua reutilizado. Não há nova chave, relógio, configuração, retenção ou escrita. Leituras novas entram nos contadores existentes. Uma falha de leitura propaga a falha do ciclo, sem inventar ausência ou publicar recente como substituto de uma leitura ativa que falhou. Ativo não vazio corrompido segue a política oficial existente de omissão, sem fallback reparador.

## Antes e depois observados

| Estado da fixture | REST | SignalR antes | SignalR depois |
|---|---|---|---|
| Primeiro GPS aceito, ativo presente | Ativo | Ativo | Ativo |
| GPS repetido, ativo presente | Ativo | **SemSinal** | **Ativo** |
| Ativo expirado, recente válido, GPS repetido | SemSinal | SemSinal | SemSinal |
| Reconexão enquanto só há recente | SemSinal | SemSinal | SemSinal |
| Retorno com GPS estritamente novo | Ativo | Ativo | Ativo |
| Transferência, índice antigo recolocado | Sem veículo na linha antiga válida | LinhaEsvaziada | LinhaEsvaziada; veículo apenas na linha atual |
| Sem ativo nem recente | Sem posição | LinhaEsvaziada | LinhaEsvaziada, suprimida até nova transição/inscrição |

As linhas anteriores ao fix combinam a reprodução vermelha com a caracterização já versionada em `204caef`; não foi rodada uma segunda homologação completa do runtime antigo. Corrupção de payload não é certificada como paridade do endpoint individual, cujo tratamento histórico difere do leitor por linha; não foi alterado nesta etapa.

Nos dois modais, a regressão mede payloads de ativo/recente/watermark/índice e TTLs antes/depois do ciclo duplicado: conteúdos idênticos, TTLs não aumentam, zero nova tentativa CAS ou observação de viagem. Depois da expiração ativa, a duplicata não recria ativo nem renova recente. O próximo GPS novo recupera predecessor e ETA elegível. A publicação não recalcula ETA nem transforma a duplicata em movimento físico.

## Validação executada

| Etapa | Resultado |
|---|---|
| Reprodução integrada antes do runtime | 2 falhas esperadas, BUS/BRT, REST Ativo × SignalR SemSinal |
| Testes offline do publicador | 20 aprovados: 9 existentes e 11 novos |
| Homologação completa Redis/PostGIS/WebSocket reais | **16/16 aprovados**, zero falhas/ignorados |
| Build Release completo `--no-restore --no-incremental` | Zero erros; 5 avisos anteriores (CS8981 ×2, CS7022, xUnit2031 ×2) |
| Seleção offline oficial `.ci/offline_tests.py` | **758/758 aprovados**, zero falhas/ignorados; verificador TRX aprovado, 28 classes |

O teste novo compara tanto `/veiculos/{id}` como `/veiculos/linha/{codigo}` com o WebSocket: identidade, linha, status, coordenadas, GPS/servidor/predecessor, sentido, padrão, versão e ETA. Comparações foram acrescentadas também à matriz de intervalos e às transições de rota, terminal e falha HTTP. A fixture offline de retorno de GPS agora persiste a nova posição antes da publicação, como faz o CAS real.

| Requisito | Teste exato e evidência |
|---|---|
| BUS/BRT, duplicata ativa, recente histórico, retorno e reconexão | `SignalR_StatusLifecycle_MatchesRestWithoutRenewingDuplicate`: dois modais, três canais, payloads/TTLs intactos, sem commit da duplicata, predecessor/ETA no retorno |
| Prioridade oficial, status persistido e candidato antigo | `ActivePayloadWinsOverRecentAndCycleCandidate`: BUS/BRT × ativo persistido Ativo/SemSinal, recente divergente, sem ler recente nem escrever |
| Ausência/branco e corrupção do ativo | `MissingActive_UsesOfficialHistoricalPolicyWithoutWriting` e `InvalidActive_PreservesOfficialSkipPolicy`: resultados iguais ao leitor oficial por linha |
| Falha Redis e candidato sem payload persistido | `ActiveReadFailure_DoesNotPublishFalseEmptyOrHistoricalAndCanRetry` e `AcceptedCandidateWithoutPersistedPayload_DoesNotBecomeActive` |
| Intervalos, predecessor e ETA com Redis/PostGIS reais | `BusBrtMatrix_RealHttpSqlCasRestAndSignalR`: BUS/BRT individual/lote em 30/45/60/240s, 16 pares; 12 curtos com predecessor/ETA, 4 longos bloqueados |
| Cadência e TTLs reais preservados | `CadenceAndPredecessorTogether_RespectHttpCacheWithoutSecondGate` e `RealExpiryAndAcquisition_OverSixtySeconds_KeepRecentHistorical` |
| Transferência, sentido, versão e filtros de segurança | `RouteDirectionVersionAndTerminal_KeepSafetyGates`: BUS/BRT, comparação REST/WebSocket e índice antigo sem vazamento |
| Terminal, GPS antigo, fora de ordem e HTTP503 | `TerminalOldGpsAndHttpFailure_DoNotInventEta`: terminal Finalizada sem ETA/alvo, estado final REST/WebSocket igual |
| CAS concorrente, duplicata e fencing | `RedisCas_ConcurrentDuplicateOutOfOrderAndFencingAreAtomic`: 1 aceite/1 rejeição concorrentes, rejeições posteriores sem renovar payload/TTL |
| Remoções, duas linhas/consumidores, cancelamento/reconexão | `SignalR_ExpiryWithOtherLiveLine_IsolatedAndResynchronized` e `SignalR_TwoConsumersReconnectAndExpiry_DeliverIdentifiedRemoval` |
| Ausência completa de GPS, filtro de idade e inscrição vazia | `SignalR_NoGpsEarlyReturns_ExpireWithoutUnrelatedBeacon` e `SignalR_ColdEmptySubscriptionAndHttpFailure_OneRefreshPerSubscription` |

O ensaio de TTL sem aceleração durou **60,067s**. Aos **41,023s**, o ativo já não existia, o REST retornou recente `SemSinal`, o GPS estava intacto e restavam **138,955s** de recente. O sucessor BRT com delta GPS de **45s** recuperou predecessor e ETA **28,6257s**, recebido no WebSocket. GETs BUS: 0/30/60s; BRT: 0/45s. Na simulação de 90s: BUS 0/30/60/90, BRT 0/45/90. Não se afirma precisão produtiva de ETA a partir dessas posições sintéticas.

A matriz e a regressão nova comprimem o tempo apenas de chaves conhecidas no Redis descartável, sem modificar a configuração de TTL da aplicação. O ensaio de 60s é separado e não altera TTLs. O runner existente permaneceu intacto: imagens locais sem pull, rede/labels exclusivos, portas aleatórias em loopback, armazenamento tmpfs sem volumes/binds persistentes, limites de recursos e guardas antes de SQL/migrations da fixture. Nas rodadas vermelha e verde: quatro containers preexistentes inalterados, zero containers próprios restantes e rede exclusiva removida.

Comandos locais utilizados (PowerShell 7 para o runner):

```powershell
# Reprodução antes do runtime; diretório novo e absoluto
pwsh -NoProfile -File tools/gps/test_pipeline_disposable.ps1 -Run -ResultsDirectory D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/gps-status-20261020-red -Filter 'FullyQualifiedName~NoPonto.Tests.GpsPipelineDisposableIntegrationTests.SignalR_StatusLifecycle_MatchesRestWithoutRenewingDuplicate'
dotnet test NoPonto/NoPonto.csproj -c Release --no-restore --filter FullyQualifiedName~NoPonto.Tests.GpsSignalRPublisherTests --logger 'trx;LogFileName=publisher.trx' --results-directory NoPonto/lab-output/gps-status-20261020-publisher --verbosity quiet
pwsh -NoProfile -File tools/gps/test_pipeline_disposable.ps1 -Run -ResultsDirectory D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/gps-status-20261020-integrated
dotnet build NoPonto.sln -c Release --no-restore --no-incremental --verbosity quiet
$offlineFilter = py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $offlineFilter --logger 'trx;LogFileName=offline.trx' --results-directory NoPonto/lab-output/gps-status-20261020-offline --verbosity quiet
py -3.13 -B .ci/offline_tests.py verify NoPonto/lab-output/gps-status-20261020-offline/offline.trx
```

Os diretórios já usados pelo runner precisam ser substituídos por novos caminhos em uma repetição. Brutos em `lab-output` continuam ignorados. O agregado [gps-signalr-status-validation-20261020.json](gps-signalr-status-validation-20261020.json) registra contadores TRX, hashes SHA256, observações sintéticas sanitizadas e comparação de fontes protegidas com `204caef`. Nenhuma credencial é versionada.

## Limites e próxima etapa

REST e WebSocket continuam sendo leituras em instantes distintos. Expiração ou commit concorrente entre elas pode produzir diferenças transitórias legítimas; esta correção remove a diferença de política, sem criar um snapshot atômico entre canais. Não há ACK/replay de aplicação; a reinscrição sincroniza no próximo ciclo como antes.

O custo passa a incluir um GET ativo por identidade candidata, compartilhado por publicação: para duplicata ativa, substitui a leitura recente; para ativo ausente, há um miss adicional antes do fallback; para aceito novo, há leitura do estado persistido. São operações paralelas por linha, sem leitura recente desnecessária quando há ativo. Não houve ensaio de carga de pico nem validação de réplicas/backplane, proxy, provider/frota ou APK reais. Recomenda-se medir latência/leituras Redis e paridade dos consumidores em homologação com carga representativa antes de decidir deploy.

CAS, deduplicação, TTL40/180, timestamps, matching, predecessor, ETA, terminal, hubs/contratos, REST e infraestrutura permaneceram inalterados; as fontes protegidas estão conferidas no agregado. Frontend não foi acessado nem alterado nesta etapa. Nenhuma conexão produtiva, migration produtiva, deploy, push, merge ou retomada ML foi executada.
