# Recuperação de linha/sentido: requisitos de habilitação — 2026-10-20

## Recomendação

**Manter `MudancaOperacionalHabilitada=false`.** Antes de habilitar, recomendo corrigir a continuidade das evidências candidatas e explicitar o motivo do encerramento por mudança operacional. Não há evidência de chegada fictícia nos consumidores ativos de histórico, estatística pública ou eventos de parada examinados. Há, porém, confirmação usando evidência anterior a uma rejeição e ambiguidade no contrato de fim para auditorias futuras.

Esta entrega contém somente caracterizações, evidência sanitizada e documentação. Base/HEAD `bcb4ea3`, branch `audit/gps-bus-brt-pipeline`. Nenhuma regra funcional foi modificada; matching, cursor LINEAR, ETA, frontend, ML, configurações, migrations e runner permanecem intactos. Flag true apenas em opções locais dos testes descartáveis. Sem acesso à produção, deploy, push, merge ou PR. A solução abaixo é proposta para aprovação, não implementação concluída.

## Evidências não consecutivas

`OperationalPolicy_NonConsecutiveEvidence_CompareCandidateReset` exercita o repositório real com Redis/PostGIS e 19 sequências. BUS representa mudança de linha; BRT inclui mudança de sentido da mesma linha. A observação A é um snapshot operacional durável antigo; B usa pontos reais da geometria e testemunho de matching na fronteira do repositório. A ambiguidade é produzida publicando outra geometria coincidente em outro sentido. Esses 19 casos não são replays completos da aquisição HTTP ou prova da identidade verdadeira do veículo.

| Sequência/condição | Implementação atual na última leitura B | Comparação com reinício da candidata |
|---|---|---|
| A → B → sentido ambíguo → B | Confirma usando B anterior à ambiguidade; estado/candidata anteriores sobreviveram à rejeição. | Último B vira primeira evidência; outro B coerente confirma. |
| A → B → salto implausível → B | Também confirma. O salto intermediário não é adotado, mas não invalida a primeira evidência. | Exige outro B depois do reinício. |
| A → B → matching operacional insuficiente → B | Também confirma; `MatchingOperacionalPlausivel=false` não quebra a sequência persistida. | Exige outro B. Esse sinal também pode significar aquecimento/falta de predecessor; a política conservadora aumenta a espera. |
| A → B → outro padrão no sentido original A → B | A observação de identidade incompatível é rejeitada; a candidata B permanece e confirma depois. | Reinicia após a contradição de identidade. |
| A → B → pequeno recuo na candidata → B | Mantém a primeira candidata; o último B pode satisfazer progresso acumulado em relação a ela. | Comparação conservadora reinicia; não prova que todo pequeno recuo seja erro, pois jitter é possível. |
| A → B → posição estacionária → B | Mantém a âncora até existir deslocamento/progresso suficiente. | Pode ser mantida: identidade continua coerente; não confirma enquanto falta movimento mínimo. |
| A → B → timestamp igual/anterior → B | Pacote antigo é recusado sem invalidar candidata; último B confirma. | Preservar esse comportamento. Um pacote antigo não é evidência nova contraditória. |
| A → B → silêncio → B, intervalo 120s ou 180s | Confirma; janela atual inclui exatamente 180s. | Preservar. Sem mensagem nova, não houve evidência contraditória observada. |
| A → B → silêncio → B, intervalo 181s | Último B substitui a candidata vencida, sem confirmar. | Preservar limite existente; não foi proposta outra duração. |
| A → B → A válido → B | A cancela a candidata; último B inicia outra, sem confirmar. | Controle de contradição já tratado corretamente, com cursor preservado. |
| A → B → C válido → B | C substitui B; último B substitui C, sem confirmar. | Controle de identidade diferente já tratado corretamente. |

Dois casos adicionais, `OperationalPolicy_UnmatchedGps_ServiceSkipsCandidateInvalidation`, comprovam A → B → GPS novo sem versão/fração de matching → B, em BUS/BRT. `ViagemObservadaService.AtualizarInternoAsync` retorna null antes do repositório. A candidata e o snapshot permanecem idênticos; o último B confirma. Não se pode resolver esse caminho alterando somente o retorno de `AvaliarMudancaOperacional`.

As candidatas são recuperadas do PostgreSQL após remoção da projeção Redis/reconstrução do repositório na matriz de 19 casos. Portanto a retenção atravessa reinício; não é apenas memória local. Nenhuma das sequências emite passagens retroativas.

### O que isso prova sobre troca incorreta

Prova que **uma rejeição nova pode ser atravessada pela confirmação**, inclusive quando o avaliador puro retorna candidata nula. Não prova que o veículo de produção estivesse realmente em A ou B. Nos testes não há observação independente do serviço executado; não foi medida taxa de falso positivo da frota.

O risco concreto é condicional: se os dois matching B forem enganosos num corredor compartilhado e houver uma observação intermediária que indique ambiguidade ou incoerência, o estado atual ainda encerra A e cria B. Exigir evidências consecutivas após essa barreira impede essa confirmação imediata específica. Não elimina erros persistentes de matching: dois B enganosos consecutivos também podem satisfazer a regra. A proposta não altera matching nem promete reconhecer a verdade operacional apenas pelo GPS.

### Causa e política proposta

- Em `ViagemOperacional.Mudanca.cs`, ambiguidade, salto e diferença apenas de padrão podem retornar `EvidenciaRejeitadaOuInsuficiente` com candidata nula. Em `ViagemOperacionalRepository.cs`, o guard de candidata pendente devolve `ItineraryChanged` sem persistir essa invalidação; outros motivos já conservam a candidata no avaliador. No serviço, ausência de matching nem chega a esse guard.
- **Proposta para aprovar:** duas evidências positivas da mesma identidade, crescentes no tempo, com a prova espacial/temporal e o movimento mínimo existentes, sem observação operacional nova inválida/ambígua/contraditória entre elas. Persistir a invalidação da candidata sob CAS/fencing, sem adotar posição, cursor, versão ou passagens da observação rejeitada. Uma nova evidência B volta a iniciar a candidata no mecanismo existente.
- Tratar GPS novo sem matching como barreira conservadora de confirmação, distinguindo-o de ausência de mensagem e falha de infraestrutura. O fluxo existente precisa transportar esse resultado até a decisão operacional de invalidação; não deve criar viagem nem contornar a validação geométrica quando falta matching. Nenhum segundo mecanismo de transição é necessário.
- Ausência de GPS não é uma rejeição: manter a candidata até a janela atual de 180s. Timestamp igual/anterior não limpa candidata. Falha transitória de banco/rede e retry não contam como evidência positiva nem justificam, isoladamente, afirmar uma contradição do veículo. Posições estacionárias coerentes podem conservar a âncora sem confirmar; recuo durante a candidatura deve ser tratado conservadoramente até existir tolerância explicitamente justificada.
- Não renovar indefinidamente a janela com leituras estacionárias e não reaproveitar o ponto anterior à barreira. Confirmar B depois de reiniciar exige mais uma evidência elegível, inclusive após aquecimento/reinício sem predecessor; isso troca rapidez de recuperação por menor risco de transição prematura.

A comparação nos testes usa **o próprio avaliador existente**, com cópia da candidata limpa; não implementa uma política alternativa persistente. Confirma primeira evidência no último B e confirmação no B seguinte. A invalidação durável proposta ainda precisa de implementação aprovada e regressões próprias de concorrência, rollback, retry e reinício. Os testes atuais caracterizam o comportamento vigente; não são GREEN de uma correção dessa política.

## Encerramento lógico: produtores e consumidores

Há três produtores: terminal LINEAR confirmado (`ViagemOperacional.cs`), mudança de linha/sentido (`ViagemOperacional.Mudanca.cs`) e perda de continuidade circular (`ViagemOperacional.Integridade.cs`). Mudança operacional emite `ViagemFinalizada` da execução antiga no instante de confirmação de B, sem ocorrência, parada ou timestamp de passagem; `MotivoFim` fica nulo. A recuperação circular já informa `PerdaContinuidadeCircular`. Mesmo no fim normal, o timestamp de finalização vem depois das confirmações; não representa automaticamente o instante da passagem terminal.

O inventário abaixo cobre fontes rastreadas desta branch e leituras complementares explicitadas. Não certifica consumidores externos, jobs privados ou versões implantadas fora do workspace.

| Consumidor/superfície | Interpretação efetiva | Resultado/risco |
|---|---|---|
| `ViagemOperacionalRepository`: Outbox e projeção/espelho Redis | Persiste fronteira; `Fields` serializa os campos presentes. Stream é espelho limitado, PostgreSQL autoritativo. | Não cria passagem por finalizar. A linha operacional por veículo passa a representar B; o journal conserva o fim de A. |
| `ViagemOutboxWorker`, registrado em `Program.cs` | Valida envelope e materializa em transação, com ACK/lease/idempotência existentes. | Não interpreta fim como chegada. |
| `HistoricoEventoRepository` / `EventosViagem` | Todo evento válido entra no journal; só `PassagemParada` passa pelo join estrutural e insere `HistoricoPassagens`. | Fim lógico adiciona fronteira, não adiciona histórico de parada. |
| `EventoViagemValidator` / parser | Aceita fim sem ocorrência; só passagem exige parada/timestamp. Único motivo de fim não nulo atualmente aceito é `PerdaContinuidadeCircular`. | Novo motivo operacional exige ampliar explicitamente esse contrato; não basta mudar configuração/payload. |
| `HistoricoPassagemWorker` e Lua `Commit` legados | Worker delega ao mesmo materializador; Lua valida identidade/fase/eventos. | Não registrado como worker funcional atual em `Program.cs`; preservado para compatibilidade/testes. Sem inferência de chegada encontrada. `ProjectDurable`/`CommitHot` continuam no fluxo atual, com outra responsabilidade. |
| `GpsCicloPerformance.Operacional` | Contadores separados de início, fim e passagem. | `CommitFim` mede encerramentos, não atendimentos de terminal. Não agrega motivos de fim. |
| `EventosParadaService` / `EventosParadaController` | Projeta `ARRIVAL`/`DEPARTURE` estimados usando GPS ativo e ocorrência futura; não lê o journal de viagem. | `ARRIVAL` é previsão, marcada `IsEstimated=true`; não é conversão de `ViagemFinalizada` em chegada observada. |
| `VeiculosController.GetHistoricoStats` | Agrupa `HistoricoPassagens`. | Nenhum aumento de passagens por fim lógico; endpoint público exercitado no teste. |
| `AdminDashboardController`, `AdminMlController`, `AdminLinhasController` | Estatísticas também consultam `HistoricoPassagens`. | Código legado excluído pelo `.csproj`; inspecionado, não chamado. Sem contador atual que converta fim lógico em passagem. |
| `HistoricoEventoRepository` → `EtaV2Repository.ClosePassageAsync` | Só passagem fecha previsão como realizada; manutenção pode expirar pendentes. | Fim lógico não realiza previsão/chegada. Inspeção somente; ETA não modificado nem reativado. |
| `ETA_ML_AUDITORIA_REAL_3B_2.sql` | Conta fronteiras e duração início/fim; seleção de inventário exclui perda circular e exige pares de passagem. | Pode contar uma execução interrompida com motivo nulo como fechada/candidata se cumprir os demais filtros. Não prova terminal atendido nem certifica qualidade; não executado. |
| `tools/EtaMl.Export/Program.cs`, `EtaDataset.cs`, `tools/eta_ml/pipeline.py` | Usa início/fim para delimitar execução e split; label exige passagem real compatível e qualidade auditada. Exportador está preso ao manifesto da coleta antiga. | Não fabrica label de terminal a partir do fim; “execução completa” precisa significar intervalo fechado/auditado, não percurso integral. Cuidado antes de incluir dados futuros; nenhum export/treino executado. |
| `ETA_ML_CANDIDATOS_3A.sql`, `docs/00-auditoria/09-passagens-pos-gtfsrt-20261011.sql` | Descoberta por passagem ou inspeção explícita de fronteira/motivo e lacuna até última passagem. | Não infere chegada; scripts não executados nesta etapa. |
| Frontend/admin e ML vizinhos, leitura complementar | Busca nos fontes TS/TSX do frontend não encontrou consumidor direto do journal/fim; admin usa estatística de passagens. Verificador prospectivo do ML liga fronteiras e declara `physical_arrival_certified=False`. | Repositórios vizinhos não foram alterados nem auditados integralmente; nenhum consumidor externo foi presumido seguro. |

`OperationalPolicy_LogicalEnd_HistoryStatsAndStopEventsKeepSeparate` cobre BUS individual e BRT set-based com aquisição HTTP/GTFS-RT local, matching e persistência reais. A registra duas passagens e conserva terminal pendente; a confirmação de B encerra A sem chegada. Journal tem cinco eventos (início A, duas passagens A, fim A, início B); payloads anteriores intactos, histórico e estatística pública continuam em duas passagens. A projeção real de eventos de parada usando o novo GPS gera somente previsão na versão B. O projetor foi chamado diretamente; o endpoint HTTP de eventos de parada não foi hospedado nesse teste. O teste também confirma que `MotivoFim="MudancaOperacional"` seria rejeitado pelo validador atual.

**Proposta para aprovar:** preencher `MotivoFim="MudancaOperacional"` nos novos encerramentos desse ramo, ampliando o validador e verificando compatibilidade do parser, serialização e worker. Preservar EventId, idempotência, ausência de parada/timestamp de passagem e payloads já gravados. Fim com motivo ausente em histórico antigo permanece não classificado; não reescrever eventos nem presumir terminal a partir de null. Não é necessário mudar estatísticas públicas de passagens ou eventos de parada para isso. Separar em auditorias futuras “execução encerrada”, “percurso integral” e “passagem terminal comprovada”.

## Padrões BRT no mesmo sentido após ativar a flag

A flag recupera linha ou sentido diferentes. **Não define uma nova execução apenas por mudar padrão/topologia no mesmo `LinhaId`/`SentidoId`.** O bloqueio abaixo diz respeito a execução antiga ainda `Ativa`/`PossivelFim`; `Finalizada` tem seu fluxo próprio de novo início e não deve ser generalizada como o mesmo cenário.

| Caso | Resultado esperado com flag true | Evidência |
|---|---|---|
| Padrões distintos, mesmo sentido, geometria diferente, projeção regressiva na versão pinada | Matching pode identificar padrão novo, mas operação antiga fica recusada/congelada; sem candidata de mudança, nova viagem ou passagens novas. | Dois testes existentes `OperationalRecovery_BrtDifferentPatternSameDirection_RemainsSeparate`, individual/set-based, reexecutados. |
| Expresso/parador, variante curta/longa, ramal ou serviço alternativo representados por padrões distintos no mesmo sentido | Flag não autoriza presumir equivalência nem nova execução. Pode continuar na versão antiga se projeção elegível avançar, ou recusar se inelegível/regressiva. | Consequência do ramo de identidade/padrão; não foram coletadas trajetórias reais dessas categorias. |
| Geometrias sobrepostas/equivalentes, mas identidades de padrão diferentes | Sem migração automática de execução pela flag. Equivalência visual não basta para copiar cursor ou eventos. | Regra de identidade; sem teste novo de todas as geometrias equivalentes. |
| Mesmo padrão com nova versão geométrica | Não representa, por si, mudança de execução. Versão operacional continua pinada; projeção antiga pode avançar ou bloquear. | Regra de versão, caso distinto de padrão diferente. |
| Diferença de topologia/circular no mesmo sentido | Não ganha política nova; proteções circulares continuam aplicáveis. | Inspeção do ramo e controles circulares existentes, não certificação de cada padrão BRT real. |

Esse grupo pode ficar para depois do TCC **se o limite for aceito e monitorado**. A habilitação não poderá ser anunciada como recuperação universal BRT. Se a continuidade desses padrões for requisito do piloto, eles passam a ser bloqueio de habilitação desse escopo. Não reduzir recusas relaxando matching, cursor, idempotência ou equivalência estrutural.

## Priorização objetiva

| Momento | Ação recomendada |
|---|---|
| Antes de habilitar | Aprovar e implementar invalidação durável diante de observação nova negativa, cobrindo guard do serviço e repositório; testar confirmação após duas evidências novas, reinício, CAS/fencing, concorrência, rollback/retry e flag desligada, sem alterar matching/cursor. |
| Antes de habilitar | Marcar motivo de fim operacional e fechar contrato/testes dos consumidores. Motivo ausente não comprova chegada; chegada observada requer passagem terminal própria. Não há bug comprovado de contagem de chegada nos consumidores ativos. |
| Antes de habilitar | Validar em homologação a espera adicional com GPS intermitente e o custo de persistir invalidações, incluindo cenários de volta a A, ausência de matching e indisponibilidade. Só depois considerar ativação autorizada com observação e rollback da flag. |
| Monitorar na habilitação | Candidatas criadas, invalidadas por motivo, substituídas/vencidas, confirmadas e canceladas; tempo/quantidade de evidências até recuperação e alternâncias rápidas A/B/A. Falta de predecessor/aquecimento deve ser distinguida de ambiguidade/implausibilidade. |
| Monitorar na habilitação | GPS novo versus idade/avanço da operação, `ItineraryChanged` por causa/modal, `InvalidState`, conflitos de payload, alvo operacional e primeira passagem inédita, atrasos/retries/DLQ do Outbox e latência do commit. Os contadores existentes ajudam, mas o motivo genérico de rejeição não substitui essa classificação. |
| Depois do TCC | Política de padrões BRT no mesmo sentido, calibração de jitter/janela com trajetórias auditadas, métricas completas de percurso, certificação prospectiva/curadoria ML e relatórios históricos por motivo. ML permanece suspenso; não é requisito reativá-lo para recuperar linha/sentido. |

Nenhum valor novo de janela, velocidade, distância ou tolerância foi escolhido. O custo da política proposta é aumentar falsos negativos/tempo de espera quando há muitos dados ruins; a vantagem é não atravessar uma contradição conhecida. Essa troca precisa de validação local/homologação antes da habilitação, não de alterações silenciosas nesta auditoria.

## Validação e limites de reprodução

**Seleção integrada final: 125 aprovados, zero falhas/ignorados**, incluindo 23 novos casos, 12 regressões do cursor LINEAR, oito controles de evidência insuficiente, dois controles de padrões BRT e 80 casos existentes de repositório/mudança/circular. Não é uma reexecução integral do pipeline anterior: os casos restantes de aquisição/expiração e a matriz antiga de 20 recuperações não foram selecionados novamente nesta etapa sem alteração funcional. Os TRXs foram parseados para conferir nomes e contagens.

**Offline não certificado como estável nesta etapa.** A primeira rodada oficial passou 758/758, com validação das 28 classes. Após os dois casos adicionais do guard sem matching, duas rodadas completas terminaram com 757 aprovados/uma falha cada. Ambas falharam por `TaskCanceledException` no helper `Until` de `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`, condição `export_failure`, primeiro BUS individual e depois BRT set-based. Entre elas, os 48 casos desse método passaram isoladamente. Nenhuma asserção, timeout, configuração de paralelismo ou fonte ETA/ML foi alterada; a causa do timeout permanece sem diagnóstico. Não se atribui causalidade à recuperação operacional nem se oculta a última falha com o primeiro resultado verde. Antes de uma futura entrega funcional/habilitação, é preciso esclarecer essa instabilidade e obter validação repetível.

Quatro rodadas descartáveis concluídas, quatro containers preexistentes preservados e zero recursos próprios restantes em todos os teardowns. Conferidos 25 hashes de fontes protegidas da etapa anterior, incluindo o fix do cursor e seus testes; inventário adicional de 24 fontes de produtores/consumidores também sem diff contra a base. Build Release com os cinco avisos anteriores (CS8981 ×2, CS7022, xUnit2031 ×2), sem novos avisos. Todos os 23 casos novos passaram nas seleções que os executaram; nenhuma falha de montagem/compilação desses casos.

Os comandos, TRXs, contagens, observações sanitizadas, hashes e isolamento estão em [operational-enablement-validation-20261020.json](operational-enablement-validation-20261020.json). Usado somente o runner descartável existente, imagens Redis 7.4 e PostGIS 16/3.4 locais, sem pull, tmpfs e portas aleatórias loopback; migrations exclusivamente nos schemas descartáveis autorizados. Arquivos brutos de resultados ficam ignorados em `NoPonto/lab-output/recovery-policy-20261020-*`.

Não houve teste RED/GREEN de correção funcional: as novas caracterizações passam por demonstrar o comportamento atual e comparar uma entrada sem candidata no avaliador puro. Não executados fonte externa/frota, produção, frontend/APK, treinamento/exportação ML, carga/replicação/backplane ou crash arbitrário. Nenhum consumidor externo desconhecido foi auditado. Não foi criado commit nesta etapa de proposta.
