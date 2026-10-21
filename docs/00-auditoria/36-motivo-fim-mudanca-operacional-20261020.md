# Motivo explícito do encerramento operacional — 2026-10-20

## Resultado e escopo

Base `a2f0abc`, branch `audit/gps-bus-brt-pipeline`, árvore inicial limpa. O ramo existente de mudança de linha/sentido confirmada passa a emitir `ViagemFinalizada` com **`MotivoFim="MudancaOperacional"`**. O fim continua sem ocorrência de parada, parada, timestamp de passagem ou GPS de passagem; o início continua pertencendo à nova execução.

Somente **dois arquivos funcionais, quatro linhas adicionadas/duas removidas**:

- `NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs`: acrescenta o motivo na construção do evento de fim do ramo `MudancaConfirmada`.
- `NoPonto/4-Data/Repositories/HistoricoEventoRepository.cs`: amplia explicitamente a lista de motivos aceitos por `EventoViagemValidator.Validar`. O parser de stream já lê `motivo_fim` e usa esse validador; sua implementação não precisa mudar.

Todo o restante desses dois arquivos foi comparado literalmente com a base, descontadas apenas essas duas substituições. O record JSON, EventIds, transação, CAS/fencing, Outbox, worker, materializador, journal, matching, continuidade das evidências, cursor LINEAR, terminal, circular, ETA, ML, SignalR e frontend permanecem inalterados. **Flag global false**, sem acesso a produção, deploy, push, merge ou PR. Migrations somente pelo runner existente no banco descartável autorizado, nenhuma migration nova.

## Compatibilidade anterior/posterior

| Contrato | Comportamento após a alteração |
|---|---|
| `SchemaVersion=2`, campos e EventIds | Inalterados. Fim `fim:<ViagemId antigo>`; início `inicio:<ViagemId novo>`. |
| Evento antigo sem `motivo_fim` ou com null | Desserializa como null. Não há inferência, backfill, migration ou reescrita. O serializador continua omitindo o motivo nulo; o JSON legado canônico tem roundtrip exato. |
| `PerdaContinuidadeCircular` | Continua aceito e produzido somente pelo mecanismo circular existente. |
| `MudancaOperacional` | Novo valor explícito no campo opcional existente, produzido somente quando o avaliador confirma mudança de linha/sentido. Não marca criação/manutenção/cancelamento/invalidação de candidata, troca de versão ou padrão BRT bloqueado. |
| Motivo desconhecido, vazio, grafia divergente ou em outro tipo de evento | Continua rejeitado. Validações de identidade, schema e estrutura não foram relaxadas. |
| Reentrega com mesmo EventId/payload | Continua idempotente. Outbox/journal mantêm comparação integral de payload, incluindo motivo. |
| Mesmo EventId, motivo null versus novo valor | Continua conflito de payload. Não é permitido atualizar um fim histórico para “completar” o motivo, nem retirar o motivo de um evento novo. |
| Binário/validador anterior | Rejeita `MudancaOperacional`. Compatibilidade de leitura de dados antigos é preservada pelo leitor novo; leitores antigos não ganham aceitação do novo valor. Uma futura habilitação exige consumidores atualizados antes do produtor habilitado. Rollback para leitor antigo com eventos novos pendentes exige tratamento operacional próprio, sem editar payloads. |

É uma extensão da lista de valores de um campo opcional já existente, solicitada expressamente; não exige mudar formato, schema ou identidade. O teste de conflito legado injeta uma colisão deliberada no laboratório para verificar a proteção, não simula uma migration nem presume que um estado operacional ativo com fim já gravado seja uma trajetória normal.

## RED/GREEN e resultados reais

| Rodada | Resultado |
|---|---|
| Unitário inicial sobre runtime original | 14 aprovados/4 falhas: novo motivo rejeitado e três casos de confirmação emitindo null. |
| Primeira montagem integrada | 0/4: teste comparou REST atual com mensagem SignalR anterior ainda na fila. Corrigido consumo de cada ciclo somente no teste; não é RED funcional. |
| Integrado RED válido, runtime original | **0/4**: BUS/BRT individual/set-based chegam à asserção que exige o motivo, encontrado null. |
| Unitário RED repetido com controles reforçados | **14 aprovados/4 falhas**. Fixtures dos outros tipos de evento são válidas com motivo null, evitando falha incidental por EventId inválido. |
| Unitário GREEN após alteração | **18/18**, mesmos quatro arquivos de regressão por SHA-256. |
| Integrado GREEN direcionado | **26/26**: quatro regressões de pipeline idênticas ao RED válido e 22 novos casos de persistência/controles. |
| Seleção integrada ampliada final | **293/293**, oito classes: pipeline, repositório operacional, mudança ponta a ponta, circular PostgreSQL, wrap, retry Redis, estado durável e batch Outbox. Nenhuma falha de concorrência nesta rodada. |
| Unitários direcionados finais | **130/130**, sete classes, incluindo 15 novos casos de contrato JSON/parser. |
| Offline oficial inicial | **757/758**: timeout existente Shadow, BUS set-based, `export_failure`. Não foi ocultado nem aceito como GREEN. |
| Shadow focado, depois da seleção integrada | **48/48**, sem alteração de código, teste ou timeout. |
| Offline oficial repetido | **758/758**, 28 classes verificadas pelo script oficial. Não comprova resolução da instabilidade. |

Seleções finais: **1.181 casos distintos aprovados**, zero ignorados, sem omitir a falha offline intermediária. As quatro rodadas descartáveis preservaram os quatro containers preexistentes e removeram todos os recursos próprios. Redis 7.4/PostGIS 16/3.4 reais, imagens locais sem pull, tmpfs, rede exclusiva, portas aleatórias loopback e guards do runner inalterado.

Comandos exatos, contadores por classe/método, hashes dos TRXs, isolamento/teardown e 26 observações sanitizadas estão no [manifesto](operational-end-reason-validation-20261020.json). TRXs brutos ignorados em `NoPonto/lab-output/operational-end-20261020-*`. Release compilado com os cinco avisos anteriores: CS8981 ×2, CS7022 e xUnit2031 ×2; nenhum aviso novo.

## Requisitos e evidências

| Requirement | Evidence |
|---|---|
| Motivo apenas na confirmação de linha/sentido; identidade antiga/nova e IDs estáveis | `BRT42Para43_OuSentido_ConfirmaComBaselineSemPassagens`, `OperationalEnd_DurableConfirmation_ReplayRollbackAndConcurrency` e os 20 `OperationalRecovery_OldDurableTrip_ColdStartAndRestart`. |
| Serialização, desserialização, parser/validação e motivos null/circular/novo | `EndReason_JsonAndStreamRoundTrip_PreservesIdentityAndOptionalField`, `EndReason_UnknownValues_RemainRejectedByValidatorAndParser`, `EndReason_OnOtherEventTypes_RemainsRejected` e `EndReason_DoesNotRelaxIdentityOrSchemaValidation`. |
| Sem inferência/reescrita de eventos antigos | `EndReason_LegacyJsonWithoutReason_DoesNotInferOrRewriteReason` e `OperationalEnd_LegacyNull_ConflictingReasonPreservesJournalAndOutbox`. |
| Outbox/journal/materializador idempotentes, rollback, concorrência e reinício | `OperationalEnd_DurableConfirmation_ReplayRollbackAndConcurrency` (6 BUS/BRT), `OperationalEnd_MaterializerRollback_RestartAndReplayAllReasons` (6) e a colisão legada (2). Compara payloads do Outbox, estado durável, journal e quantidade de passagens; reentrega ao consumidor reconstruído usa o payload persistido. Suíte `ViagemOutboxBatchTests` anterior integral também passou. |
| Nenhuma passagem/chegada terminal fabricada | `OperationalPolicy_LogicalEnd_HistoryStatsAndStopEventsKeepSeparate` e `OperationalEnd_DurableConfirmation_ReplayRollbackAndConcurrency`: duas passagens reais anteriores, cinco eventos totais após fim/início, fim sem parada e nova execução ativa com zero confirmações terminais. |
| BUS/BRT, matching individual/set-based e consumidores públicos | Os quatro casos `OperationalPolicy_LogicalEnd_HistoryStatsAndStopEventsKeepSeparate` usam HTTP GTFS-RT, matching PostGIS, Redis, REST por veículo/linha, SignalR WebSocket e estatística pública HTTP. Projetor de eventos de parada chamado diretamente: somente previsão na nova versão, sem passagem ou chegada registrada a partir do fim lógico. Endpoint de eventos de parada não hospedado. |
| Terminal normal e perda de continuidade circular | `OperationalEnd_NormalTerminal_KeepsNullReasonAndRealPassages` e `OperationalEnd_CircularLoss_KeepsExistingReason`: quatro casos cada, BUS/BRT e flag false/true. Terminal conserva as três passagens reais e motivo null; circular conserva `PerdaContinuidadeCircular`, sem passagem fabricada, inclusive após perda Redis/reconstrução. |
| Proteções GPS anteriores e flag padrão | Matriz anterior de evidências/cursor/circular reexecutada na seleção ampliada; `AusenciaOuFlagDesligada_NaoAvaliaMudanca` mantém default false. Repositório operacional, integridade, opções, configuração, matching e fontes ETA/ML inalterados por hash. |

Revisão das dez novas funções de teste: nenhuma sem asserção ou limitada a presença/truthiness. Asserções cobrem valores independentes, identidade completa, estado, coleções, efeitos negativos, rollback e conflitos. REDs comprovam detecção das duas alterações funcionais; não foi executado mutation engine nem relaxada proteção para aprovar testes.

## Riscos e próximos limites

Nenhum custo novo de consulta, escrita ou formato: o evento de fim já era persistido; recebe apenas o motivo. É necessário coordenar leitores/produtor numa futura habilitação, porque o leitor antigo rejeita o novo valor. Eventos antigos nulos permanecem indistinguíveis quanto ao motivo e não devem receber inferência retrospectiva.

Shadow continua intermitente: o [relatório 37](37-instabilidade-shadow-encerramento-20261020.md) separa a ocorrência desta etapa dos resultados da correção. O relatório 35 permanece histórico e inalterado; sua falha de concorrência não reapareceu nesta seleção, sem declarar causa resolvida.

Não certificados carga de frota, réplicas/backplane, consumidores externos, frontend, processos mortos em pontos arbitrários ou reinício físico dos containers. Reinício testado significa reconstrução de serviços/repositórios/worker com PostgreSQL preservado e perda controlada da projeção Redis. Regras de padrões BRT diferentes no mesmo sentido continuam bloqueadas como antes. A entrega permanece com flag OFF e não autoriza habilitação produtiva.
