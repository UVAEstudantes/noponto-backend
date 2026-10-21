# Continuidade das evidências de mudança operacional — 2026-10-20

## Entrega e escopo

Diagnóstico 33 preservado separadamente em **`0d93ada`**, filho de `bcb4ea3`, na branch `audit/gps-bus-brt-pipeline`: somente teste de caracterização, relatório, evidência sanitizada e histórico. Os hashes anteriores e TRXs, inclusive os timeouts de Shadow, foram conferidos. Não havia WIP adicional na árvore inicial.

A correção funcional desta etapa invalida duravelmente a candidata diante de observação nova negativa e exige duas novas evidências positivas antes de confirmar linha/sentido. **`MudancaOperacionalHabilitada` permanece false por padrão**, sem alteração de configuração ou produção. Matching, ETA, ML, SignalR, frontend, EventIds, idempotência, materializador, SQL de ocorrências e motivo de encerramento não foram alterados. Sem deploy, push, merge ou PR.

Resultado final: **248/248 integrados reais + 108/108 unitários direcionados + 758/758 da seleção oficial offline**, 1.114 casos distintos, zero falhas/ignorados nas seleções finais. Falhas intermediárias não foram apagadas: RED comportamental, montagem de teste, estado circular legado e uma rodada integrada com expectativas antigas/retornos de infraestrutura estão registrados abaixo e na [evidência sanitizada](evidence-continuity-validation-20261020.json). As instabilidades de validação estão separadas no [relatório 35](35-instabilidades-validacao-evidencias-20261020.md).

## Causa-raiz e alteração

Havia três fronteiras que mantinham a evidência anterior:

1. O avaliador retornava candidata nula para algumas contradições, mas `AplicarAvaliacaoMudanca` não gerava uma decisão de invalidação. O guard do repositório devolvia `ItineraryChanged` sem persistir essa quebra. Outros motivos de matching inválido e recuo mantinham a candidata no próprio avaliador.
2. `ViagemObservadaService` retornava null quando faltava versão/fração de matching, antes de alcançar o repositório. GPS novo válido sem matching não participava da continuidade das evidências.
3. O retry reagendava indiscriminadamente todo resultado com `MatchingOperacionalPlausivel=false`, sem distinguir uma observação negativa processável de indisponibilidade de infraestrutura.

Foram alterados **cinco arquivos funcionais, 70 linhas adicionadas/19 removidas**:

- `ViagemOperacional.Mudanca.cs`: observação nova ambígua, implausível, incompatível, sem matching ou regressiva invalida candidata. Infraestrutura e timestamp antigo/igual conservam a evidência. A aplicação da avaliação produz uma decisão sem eventos que limpa apenas a candidata e avança o timestamp consumido.
- `ViagemObservadaService.cs`: recebe as opções GPS já registradas pelo DI. Com a flag true, encaminha ausência de matching; falha explícita de matching/projeção por infraestrutura não invalida. Com flag false ou opções omitidas em consumidores legados, mantém o guard anterior.
- `ViagemOperacionalRepository.cs`: valida identidade do veículo, coordenadas e timestamp antes de tratar ausência de matching. Somente viagem elegível com candidata entra na invalidação; não executa baseline da observação rejeitada. Usa a mesma decisão, transação, versão, advisory lock, Outbox e projeção Redis existentes. A mudança semântica de candidata é durável mesmo sem passagem/evento. A leitura sem matching sem candidata não cria viagem ou eventos.
- `RetryOperacionalGps.cs`: sob a flag, deixa a observação negativa alcançar esse mesmo fluxo. Falha de infraestrutura continua reagendada; ACK da recuperação só ocorre após confirmação durável, como antes.
- `ViagemOperacional.Integridade.cs`: mantém a exigência de prova circular após limpar a candidata. Âncora retida mais antiga que o timestamp consumido, ou registro circular legado sem âncora, continua passando pela prova/limites existentes. Não foi criada outra regra de wrap ou de movimento.

O timestamp gravado na invalidação é uma **barreira de observação consumida**: impede que retry/posição anterior recrie a candidata através da contradição. Não significa adoção da posição dessa leitura. Viagem, linha, sentido, versão, cursor, progresso, fase, confirmações de terminal e eventos permanecem os anteriores. `Updated` nesse caso significa commit da invalidação, não matching incorporado ou passagem aceita.

A correção reutiliza o mecanismo existente. O bloco do cursor LINEAR de `bcb4ea3`, a transação compartilhada e a construção do encerramento lógico foram comparados literalmente com a base e permaneceram intactos. Não houve nova tabela, coluna, migration ou contrato público de evento.

## Antes/depois e RED/GREEN

| Rodada | Resultado comprovado |
|---|---|
| Caracterizações originais, antes de alterar runtime | 23/23 passaram por reproduzir o comportamento anterior; incluíam confirmação atravessando contradição e o guard sem matching. |
| Regressões desejadas, runtime original | **RED: 11 aprovados/10 falhas**, 21 casos. Falhas nos oito cenários contraditórios e nos dois sem matching; os controles de silêncio/duplicata/estacionário/identidade passaram. |
| Mesma fonte de regressão, após correção | **GREEN: 21/21**. SHA-256 do arquivo de regressão igual no RED e GREEN. A matriz passou também na seleção final. |
| Montagem da matriz adicional | Três CS0121 por `new(...)` ambíguo entre overloads do serviço; corrigidos somente com tipo explícito nos testes. Não é RED de comportamento. |
| Circular sem âncora, antes da salvaguarda completa | **4 aprovados/4 falhas**: limpar candidata em snapshot legado sem âncora deixava a volta circular sem a proteção anterior. |
| Matriz ampliada depois da salvaguarda | **61/61**: inclui oito casos circulares, estados com/sem âncora, wrap comprovável e retorno sem prova suficiente. |
| Ingresso e retry reais | **12/12**: HTTP/matching BUS/BRT individual/lote e retry com Redis/PostGIS reais, incluindo infraestrutura/rollback. |
| Primeira seleção integrada ampla | **243 aprovados/5 falhas**: quatro expectativas antigas de reinício sem predecessor e um teste existente com seis retornos `InfrastructureFailure`. Sem alteração da asserção de concorrência. |
| Reinício/race após atualizar expectativa autorizada | **21/21**: os 20 casos antigos recuperam corretamente; corrida de mesmo timestamp permanece inalterada e passou. |
| Seleção integrada final repetida | **248/248**: todos os sete grupos selecionados, inclusive corrida, reinício, circulares, passagem e expiração real. |
| Unitários direcionados | **108/108**: regra, serviço, retry e circular. |
| Allowlist oficial offline | **758/758**, todas as 28 classes verificadas pelo script oficial, incluindo Shadow sem alterar seus testes. Não comprova eliminação dos timeouts históricos. |

No exemplo A → B → rejeição → B, a última leitura B agora cria outra candidata. Só outro B posterior, coerente e com movimento/progresso suficientes, encerra A e cria B. Sem passagem na lacuna ou no cancelamento. Na ausência de predecessor após reinício, a leitura de aquecimento é negativa; os quatro cenários afetados agora confirmam na terceira leitura após reiniciar: aquecimento, primeira evidência válida e segunda evidência válida. A primeira passagem inédita é verificada depois dessa fronteira, sem relaxar ETA ou paridade REST/SignalR.

## Requisitos e evidências executadas

| Requirement | Evidence |
|---|---|
| Ambiguidade, salto, matching insuficiente/incompatível e recuo invalidam; duas novas positivas | `OperationalPolicy_Contradiction_DurablyRequiresTwoNewPositives` (19 casos) e `EvidenceContinuity_IncompatibleMatching_DoesNotAdoptIdentityOrPosition` (7 casos). Inclui retorno ambíguo ao próprio A sem adotar sua posição. |
| GPS válido sem matching alcança persistência | `OperationalPolicy_UnmatchedGps_DurablyInvalidatesCandidate` (BUS/BRT), incluindo perda da projeção e duplicata. `EvidenceBarrier_ServiceRoutesOnlyEnabledNonInfrastructureObservations` cobre os ramos do serviço diretamente. |
| GPS inválido não limpa candidata | `EvidenceContinuity_InvalidRawGps_DoesNotEraseCandidate`: coordenada inválida, epoch e futuro inválido. GPS antigo/igual mas admissível também é controlado na matriz de 19 casos. |
| Ingresso com matching individual/set-based | `EvidenceIngress_RealMatchingGap_TwoEligiblePositivesAfterBarrier` (4 casos): posição HTTP válida fora da rota quebra a candidata; o reaquecimento não conta como positivo; confirma após dois positivos elegíveis, não simplesmente duas mensagens brutas. |
| Durabilidade, concorrência, rollback, retry antigo, próximo alvo e passagem futura | `EvidenceContinuity_ConcurrentOrRollback_DurableBarrierAndFuturePassage` (8 casos): uma única invalidação concorrente; falha após Outbox reverte estado; reload recupera barreira; snapshot antigo não apaga candidata nova; uma passagem inédita da versão nova. |
| Fencing da projeção Redis | `EvidenceContinuity_LateProjection_CannotResurrectCandidate` (BUS/BRT): projeção atrasada da invalidação não sobrescreve candidata mais recente; retry antigo é recusado. |
| Falha transitória conserva evidência | `EvidenceContinuity_InfrastructureFailure_PreservesEvidence` (6 casos): diagnóstico global/direcionado e falha de projeção, com/sem matching. O B posterior confirma usando a candidata conservada. |
| Retry real, ACK somente durável | `EvidenceIngress_RetryRealRedis_BarrierCommitsBeforeAcknowledgement` (8 casos): negativo, sem matching, infraestrutura e rollback; fila Redis só conclui após commit da barreira. |
| Flag false, silêncio 120/180s, limite 181s, duplicata/antigo, estacionário | `EvidenceContinuity_FlagOff_UnmatchedKeepsPersistedCandidate`, controles de `OperationalPolicy_Contradiction_DurablyRequiresTwoNewPositives`, testes existentes do serviço e regra. Janela e movimento mínimo permanecem os mesmos. |
| Retorno A/B/A e nova identidade/cursor | `EvidenceContinuity_ABA_NewExecutionHasOwnCursor`: volta confirmada a A cria outra execução, sem herdar cursor 2 da primeira A; passagem 1 inédita pertence à nova identidade. |
| Terminal | `EvidenceContinuity_Terminal_InvalidationDoesNotConfirmArrival`: não incrementa confirmação terminal na rejeição; finalização posterior segue a regra existente. |
| Circular e snapshots legados | `EvidenceContinuity_CircularAfterInvalidation_RetainsProofRequirement` (8 casos): não fabrica âncora, volta ou passagem; sem prova suficiente entra na proteção existente. Suítes circulares/wrap anteriores também reexecutadas. |
| Cursor LINEAR e reinício de serviços | Os 12 casos `OperationalRecovery_ReturnBehindCursor_PreservesIncorporatedCursor` e 20 `OperationalRecovery_OldDurableTrip_ColdStartAndRestart` passam; repositório/host reconstruídos e projeção removida conforme cada cenário. |
| Encerramento/idempotência anteriores | `OperationalPolicy_LogicalEnd_HistoryStatsAndStopEventsKeepSeparate` e controles de journal/worker permanecem aprovados, inclusive `MotivoFim` operacional ainda nulo. BRT de padrão diferente no mesmo sentido continua separado. |

As duas novas parciais de integração exercitam 52 casos adicionais: 40 em `EvidenceContinuity.cs` e 12 em `EvidenceIngress.cs`. Não são todas equivalentes a transporte completo: casos de concorrência/infraestrutura/circular usam testemunho explícito de matching no repositório com geometria/Redis/PostGIS reais. Os quatro casos de ingresso percorrem fonte HTTP, GTFS-RT, crosswalk, matching, predecessor, CAS e operação reais; o retry usa fila Redis real e resultado de reenriquecimento controlado. Não há fonte externa produtiva.

## Isolamento, revisão e limites

Dez rodadas descartáveis pelo runner existente, Redis 7.4 e PostGIS 16/3.4 locais, sem pull, tmpfs, portas aleatórias loopback e guards. Todos os teardowns preservaram quatro containers preexistentes, removeram a rede própria e deixaram zero containers próprios. Migrations apenas nos schemas descartáveis autorizados. TRXs e dados brutos ficam ignorados em `NoPonto/lab-output/evidence-continuity-20261020-*`; o JSON versionado contém contagens, hashes, nomes e observações sanitizadas.

Revisados os resultados observáveis por requisito e as asserções: estado anterior preservado, cursor/identidade explícitos, igualdade dos payloads históricos, unicidade de eventos, ausência de passagens na invalidação e avanço apenas da passagem inédita. Os REDs demonstram detecção da retenção indevida/guard e da borda circular legada. Sem mutation engine ou relaxamento de asserções/timeout. Build Release final com os cinco avisos anteriores (CS8981 ×2, CS7022, xUnit2031 ×2); um aviso CS8602 surgido na primeira implementação foi eliminado com a informação de nulabilidade já garantida pelo guard.

Riscos restantes: invalidação é gravação semântica e aumenta commits quando há candidata e GPS ruim; recuperação pode demorar mais com perda de predecessor. A validação não mede carga de frota, réplicas/backplane ou todos os crashes possíveis. Ingresso GPS e fila de retry continuam com as garantias anteriores de melhor esforço; não foi criado ledger durável de todo GPS. Contar timestamp atualizado como avanço espacial seria incorreto: a barreira conserva posição/cursor. Padrões BRT diferentes no mesmo sentido continuam sem política nova. Motivo do encerramento lógico segue pendente para commit separado e não foi habilitado nada em produção.

A rodada integrada ampla teve um retorno intermitente de infraestrutura em teste legado de concorrência, sem causa fechada; os resultados e limites estão no relatório 35. Os timeouts offline Shadow do diagnóstico 33 seguem como evidência histórica, embora a seleção oficial desta etapa tenha passado. Recomenda-se revisão desta correção com a flag OFF; esta entrega não autoriza habilitação produtiva.
