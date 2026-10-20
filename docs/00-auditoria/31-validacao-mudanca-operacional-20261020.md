# Mudança operacional: recuperação existente e bloqueio de retorno — 2026-10-20

## Resultado e decisão recomendada

**O mecanismo existente recupera mudanças comprovadas de linha/sentido, inclusive de viagens duráveis antigas. Ainda não está pronto para habilitação geral: foi reproduzido um bloqueio no retorno à operação original atrás do cursor já incorporado.** A proteção de idempotência impede duplicação, mas a operação passa a recusar novos avanços com `InvalidState`.

Nenhuma regra funcional, matching, reancoragem, flag padrão, configuração produtiva, frontend ou ML foi alterada. Não houve acesso novo à produção, deploy, push, merge ou PR. A flag foi true exclusivamente nos objetos de opções dos testes descartáveis.

O diagnóstico 30 e seus oito arquivos foram revisados e preservados em **`fc93336`**, filho de `b816690`, na branch `audit/gps-bus-brt-pipeline`. Conferidos três hashes de evidências, onze fontes protegidas e os TRXs anteriores de 345 casos; nenhuma credencial, payload GPS bruto ou ordem física de produção nos artefatos. A sonda produtiva foi somente versionada, não executada novamente.

## Ambiente e caminho exercitado

Reutilizado, sem alteração, `tools/gps/test_pipeline_disposable.ps1`: imagens locais `postgis/postgis:16-3.4` e `redis:7.4-alpine`, `--pull never`, rede exclusiva, portas aleatórias em 127.0.0.1, tmpfs, limites de memória/CPU/PIDs e identificação de propriedade para limpeza. Banco `noponto_gps_fixture` e schemas exclusivos migrados pela fixture existente; nenhum banco preexistente foi conectado pelos testes.

Os novos casos usam a classe parcial `GpsPipelineDisposableIntegrationTests.OperationalRecovery.cs` e o guard `GpsIntegrationResources`, validando tokens de isolamento PostgreSQL/Redis **antes** de inicializar a fixture. Os quatro containers preexistentes foram comparados antes/depois de cada rodada; os relatórios de teardown comprovam preservação e ausência de recursos próprios restantes.

Nos 20 casos de recuperação antiga, o fluxo é HTTP local/GTFS-RT → fonte BUS/BRT e crosswalk → matching PostGIS individual ou set-based → predecessor/CAS Redis → repositório operacional PostgreSQL/Redis → Outbox/materializador → REST e WebSocket SignalR reais. A validação de padrão BRT usa pontos da geometria PostGIS deslocada e o enriquecedor/repositório reais, individual/lote; não simula uma fonte produtiva. Os oito casos de evidência insuficiente usam testemunhos de matching na fronteira do repositório, com posição geométrica e consulta real de sentido inequívoco. Essas diferenças de alcance não são contadas como equivalentes a transporte completo.

Estados antigos foram **semeados somente no banco descartável**, com último GPS operacional de 11 dias e fase `Ativa` ou `PossivelFim`. O terminal da fixture é colocado no fim de uma **nova versão publicada**, sem editar a versão imutável existente. O estado `PossivelFim` tem terminal já incorporado e uma confirmação; o `Ativa` conserva terminal pendente. A admissão de GPS antigo não foi relaxada: as leituras novas usadas no pipeline respeitam o validador existente.

“Reinício” significa reconstruir serviços, clientes HTTP e host de teste, preservando o estado PostgreSQL e removendo a projeção Redis operacional; não significa reiniciar o sistema operacional ou os containers. Há casos com e sem retenção do predecessor físico; o watermark numérico permanece sem ser usado como prova espacial.

## Casos em que a recuperação funciona

| Cenário | Evidência e comportamento observado |
|---|---|
| Mudança de linha | Primeira evidência elegível conserva viagem/cursor antigos e persiste candidata. Outra evidência coerente, com movimento/progresso, confirma a execução nova. |
| Mudança de sentido na mesma linha | Mesma regra; identidade nova inclui sentido/padrão/versão corretos. Nenhum ramo especial por BUS/BRT foi criado. |
| Viagem antiga `Ativa` ou `PossivelFim` | A idade de 11 dias não bloqueia a transição baseada em GPS novo. A confirmação não exige inventar a chegada que faltava na execução anterior. |
| Encerramento lógico | `ViagemFinalizada` pertence à viagem/versão antigas, no instante da confirmação; ocorrência, parada e timestamp de passagem são nulos. `ViagemIniciada` usa outra identidade e o mesmo instante de fronteira. |
| Baseline novo | Volta zero, cursor inicial na ocorrência 1 incorporada pela posição de confirmação; alvo operacional é a ocorrência 2 da versão nova. Zero passagens retroativas no Outbox, journal e `HistoricoPassagens`. |
| Cruzamento futuro | Após a confirmação, cruzar a ocorrência 2 gera exatamente uma passagem da viagem/versão/sentido/linha novos. Reentrega ao worker e duas persistências diretas idênticas preservam uma única linha de histórico. |
| Reinício com predecessor retido | Candidata recuperada do PostgreSQL. A primeira leitura após o reinício pode fornecer a segunda evidência e confirmar a mudança. Não se exige que as duas leituras ocorram na mesma instância. |
| Reinício sem ativo/recente | A primeira leitura após o reinício aquece a prova física, sem confirmar. A seguinte confirma usando a candidata durável ainda dentro da janela. |
| ETA e realtime | REST e SignalR mantêm paridade, timestamps e identidade do matching. Na primeira leitura após reconstrução, o ETA pode permanecer ausente por falta da referência temporal local; na seguinte volta a ser calculado. Recuperação operacional e elegibilidade do ETA são separadas. |

Nos casos sem predecessor no início, duas evidências elegíveis não significam necessariamente duas mensagens brutas: é preciso obter a prova física atual. Na matriz com reinício, a candidata é registrada em t=30; com predecessor retido confirma em t=60, sem predecessor confirma em t=90. Em t=120 ocorre a primeira passagem futura, sem recuperar passagens da lacuna de 11 dias.

O fim lógico mantém `MotivoFim` ausente no evento atual. A ausência de parada distingue esse evento de uma chegada, mas consumidores que interpretem qualquer `ViagemFinalizada` como atendimento ao terminal precisam ser revisados antes de habilitar a flag. Nenhum consumidor foi alterado.

## Evidência insuficiente, ambiguidade e padrões BRT

`OperationalRecovery_InsufficientEvidence_DoesNotConfirm` cobre oito condições com Redis/PostGIS reais:

| Condição intermediária | Resultado existente |
|---|---|
| Sem deslocamento | `Updated`, candidata original mantida; não cria viagem, fim ou passagem. |
| Fração regressiva pequena | `Updated`, candidata mantida; não satisfaz o progresso mínimo para confirmar. Não significa adoção de uma nova execução regressiva. |
| Matching declarado insuficiente | `ItineraryChanged`, snapshot/candidata anteriores preservados integralmente. |
| Sentido ambíguo | Uma geometria concorrente em outro SentidoId da mesma linha torna a consulta real ambígua; retorna `ItineraryChanged` e conserva a candidata durável. |
| Salto físico/projetado implausível | `ItineraryChanged`, sem escrita operacional ou emissão de eventos. |
| Intervalo de 181s | Substitui a candidata vencida; essa leitura não confirma. Outra evidência da candidata nova pode confirmar. |
| Timestamp igual ou anterior | `RejectedOlderOrEqual`, sem alteração do estado/candidata. |

**As evidências positivas não precisam ser consecutivas no comportamento atual.** Nos casos de rejeição, o repositório preserva a candidata anterior, embora algumas avaliações puras retornem candidato nulo. Uma leitura positiva posterior dentro da janela pode confirmar usando a evidência anterior à rejeição/ambiguidade. Isso foi comprovado; não foi classificado automaticamente como defeito, pois a política explícita do repositório conserva a evidência. A aceitação dessa semântica deve fazer parte da decisão de habilitação.

Para BRT com **padrão diferente no mesmo sentido**, o matching atual é válido nos dois caminhos, mas a operação continua na versão antiga, sem candidata ou eventos novos. A flag **não** define equivalência entre os padrões e **não** resolve esse grupo do diagnóstico 30. O teste usa duas versões/padrões distintos, mesma linha/SentidoId, geometria deslocada cerca de 111m e projeção regressiva na versão pinada. O motivo é a política já existente: “Diferença apenas de padrão/topologia: política de nova execução não definida”.

## Bloqueio comprovado: A → candidata B → retorno atrás do cursor em A

Reprodução em HTTP, matching, Redis, PostgreSQL e Outbox reais, nos caminhos individual e set-based. Método: **`OperationalRecovery_ReturnBehindCursor_ExposesOutboxConflict`**.

| Passo | Estado/efeito real |
|---|---|
| A, frações 0,10 → 0,45 | Mesma viagem A, cursor 2. Passagens das ocorrências 1 (0,20) e 2 (0,40) já materializadas. Terminal em 1 permanece pendente. |
| Outra linha B, 0,25 → 0,26 | Uma candidata B elegível é persistida; a mudança não chegou a ser confirmada. |
| Retorno a A, 0,10 → 0,11 | A candidata é cancelada. O caminho de adoção/baseline preserva o ID da viagem A, mas reduz o cursor para zero. Nenhuma passagem é emitida nesse baseline. |
| A volta a cruzar 0,20 | Tenta emitir `passagem:<mesma viagem>:<mesma ocorrência>:0` com timestamp/payload novos. O Outbox já contém esse EventId com o payload antigo. |
| Duas novas leituras, 0,25 e 0,26 | Repetem `InvalidState`; a transação operacional é revertida e mantém o baseline antigo, abaixo do cursor histórico. GPS, REST e ETA continuam atualizando. |

Um observador de log de teste capturou **`EventoViagemPayloadConflictException`** para o EventId da passagem antiga. A exceção deriva de `FormatException`; por isso o resultado público operacional é `InvalidState`, **não** `InfrastructureFailure`. O journal e as duas linhas de histórico antigas permanecem íntegros: não houve duplicação materializada ou atribuição a B. Entretanto, a continuidade operacional fica bloqueada por uma tentativa repetida de duplicação.

Cadeia causal no código existente:

1. `AvaliarMudancaOperacional`: retorno à operação original cancela a candidata.
2. `ViagemOperacionalRepository`: a presença de candidata anterior com flag ON escolhe baseline e `adocaoLegado`.
3. A transição do baseline usa a posição atual e substitui cursor/ordem pelos anteriores a ela, mesmo sendo **a mesma execução e versão**.
4. O próximo avanço tenta novamente uma ocorrência já incorporada.
5. `InserirOutboxAsync` exige payload idêntico para EventId repetido; a violação causa rollback. A proteção está correta e deve ser preservada.

Essa falha é diferente de uma troca confirmada para B e diferente do grupo de padrões BRT. Os testes novos de caracterização **passam por reproduzir e verificar o bloqueio**; não significam aprovação do requisito de continuidade nesse retorno. O retorno A→B candidata→A que volta **à frente** do cursor continua aprovado pelos testes existentes.

## Segurança e regressões

Reexecutados os testes existentes de confirmação concorrente (um commit), candidato durável/perda de Redis, rollback depois do Outbox, timestamp duplicado/fora de ordem, CAS de posição e fencing, terminais, nova execução após `Finalizada`, versão pinada e integridade circular. A flag não participa do CAS Redis; as proteções reais de fencing/monotonicidade continuaram sendo exigidas.

Circulares foram exercitados pelos testes existentes com geometria PostGIS fechada, prova dirigida, divergência, wrap, recuperação concorrente e proteção persistida. O cenário novo de retorno conflitante é `LINEAR`; **não se propõe aplicar uma correção de cursor linear à contagem de voltas circular**.

A opção true faz parte do objeto de opções em memória da fixture. `GpsPollingOptions.MudancaOperacionalHabilitada` continua false por padrão; Program, appsettings, arquivos de ambiente, runner, infraestrutura, contratos e código produtivo permanecem iguais a `fc93336`/`b816690`. As correções anteriores de aquisição BRT, predecessor, remoções e paridade REST/SignalR não foram editadas e são novamente verificadas pela suíte integrada.

## Testes, comandos e limites

As execuções e hashes estão em [operational-recovery-validation-20261020.json](operational-recovery-validation-20261020.json), com isolamento, teardown, contagem de casos e observações sanitizadas. Resultados brutos locais ficam sob `NoPonto/lab-output/mudanca-operacional-20261020-*`, ignorados pelo Git.

**Seleções finais: 124 casos distintos aprovados, zero falhas/ignorados** — 76 existentes na primeira rodada e 48 no pipeline final (16 anteriores e 32 novos). Os 32 novos são 20 de recuperação antiga/reinício, dois de padrão BRT separado, oito de evidência insuficiente e dois de caracterização do retorno conflitante. Esses últimos dois verificam um requisito ainda não atendido; o total verde não autoriza a flag. A rodada final do pipeline durou 1min21s nos testes, incluindo a janela real de expiração de 60s das regressões anteriores.

| Requisito | Testes concretos |
|---|---|
| Duas evidências de linha/sentido, fim lógico e identidade nova | `OperationalRecovery_OldDurableTrip_ColdStartAndRestart`, `ABB_MatchingReal_ConfirmaEMaterializaSemPassagens`, `CodigosDistintosSobrepostos_ABB_ConfirmaNovaExecucaoSemPassagem`. |
| Cursor, próxima parada, sem retroativos/duplicações | `OperationalRecovery_OldDurableTrip_ColdStartAndRestart`, `MudancaHabilitada_CancelamentoBaseline_CursorProximaEPassagemFutura`, `ABA_BaselineCoerente_SomentePassagemFutura`. |
| CAS/fencing/concorrência | `RedisCas_ConcurrentDuplicateOutOfOrderAndFencingAreAtomic`, `MudancaHabilitada_ConfirmacaoConcorrente_ApenasUmCommit`, `CorridaMesmoTimestamp_CasOutboxSomenteUmaVez`. |
| Terminais/circulares | `RedisTerminal_ReinicioT0T1_Finalizada_TresPassagensMaisInicioEFim`, `CircularFechada_EnriquecimentoReal_DivergenciaWrapBaselineEPassagem`, `RecuperacaoConcorrente_ComFlagDesligada_ApenasUmaExecucaoNova`. |
| Insuficiência/regressão/ambiguidade | `OperationalRecovery_InsufficientEvidence_DoesNotConfirm`. |
| Reinício e durável antigo | `OperationalRecovery_OldDurableTrip_ColdStartAndRestart`, `MudancaHabilitada_CandidatoDuravelPerdaRedis_ConfirmacaoSemPassagens`, `Retry_NovaInstanciaComRedisPreservado_RecuperaSemReplayDaFonte`. |
| BRT, padrão distinto no mesmo sentido | `OperationalRecovery_BrtDifferentPatternSameDirection_RemainsSeparate`. |
| Retorno atrás do cursor: requisito ainda não atendido | `OperationalRecovery_ReturnBehindCursor_ExposesOutboxConflict`, individual/lote. |

Montagem inicial dos 18 casos esbarrou no trigger de imutabilidade por tentar ajustar uma ocorrência já publicada; corrigida somente a fixture para criar nova versão. Na rodada seguinte, dez casos passaram e 16 contrariaram a expectativa inicial de aquecimento operacional após reinício: com predecessor válido, a confirmação já ocorria na primeira leitura. Os testes passaram a discriminar retenção/perda dessa prova, sem alterar runtime. A primeira caracterização de retorno esperava `InfrastructureFailure`; a leitura do retorno/captura da exceção confirmou `InvalidState`, corrigindo somente a expectativa diagnóstica.

Não há alegação de RED/GREEN de correção funcional: **nenhuma foi implementada**. As falhas de montagem e as hipóteses refutadas são preservadas nos agregados, separadas dos resultados finais. As compilações ocorreram por `dotnet test -c Release --no-restore`, com cinco avisos preexistentes. Não foi repetido build completo separado nem a suíte offline integral, pois nenhum código funcional foi alterado.

Não executados: produção, fonte externa real, frontend/dispositivo, carga da frota, múltiplas réplicas/backplane, crash de processo em ponto arbitrário, reinício dos containers, migrations em banco real, deploy ou ML. O reinício e as reentregas exercitados são os explicitados acima; a precisão do ETA contra chegadas reais não foi medida.

## Proposta concreta antes de implementar

1. **Manter a flag OFF no padrão e em produção.** O sucesso nas trocas de linha/sentido não elimina o bloqueio de retorno comprovado.
2. Corrigir **somente o cancelamento da candidata no retorno à mesma execução/versão LINEAR**, reutilizando a máquina existente. O cursor já incorporado deve ser um limite inferior; um baseline da posição atual não pode reduzi-lo. Se o baseline atual estiver mais adiante, pode avançar conservadoramente sem retroativos; se estiver atrás, conservar a ocorrência incorporada e resolver o próximo alvo ainda não incorporado de forma coerente. Atualizar timestamp/posição com GPS válido não exige reabrir passagens antigas.
3. Conservar o baseline novo a partir da posição atual quando uma mudança **for confirmada**, pois é outra execução. Não reutilizar cursor de A em B, não mudar EventId, não ignorar conflito de payload, não apagar histórico/Outbox e não relaxar prova geométrica ou circular para evitar a recusa.
4. Antes dessa correção, converter a reprodução de retorno em regressão desejada: cursor não regride, cruzar novamente ocorrências antigas não produz conflito nem duplicata, e a primeira ocorrência ainda inédita é registrada uma vez. Executar novamente a matriz de cancelamento, terminal, CAS/rollback e circulares. Nenhum segundo mecanismo de transição é necessário.
5. Decidir explicitamente se evidências positivas separadas por ambiguidade/rejeição podem confirmar dentro de 180s. Os testes descrevem a política atual; exigir consecutividade seria outra mudança de regra, fora de uma correção mecânica de cursor.
6. Manter **padrão diferente no mesmo sentido** como grupo independente: determinar se os padrões representam serviço alternativo, retorno de execução, geometria equivalente ou identidade estrutural incorreta. A flag existente não resolve isso; não ampliar a política sem essa evidência.

Depois de aprovada e validada a correção restrita de retorno, a recomendação é reutilizar o mecanismo atual para linha/sentido em uma etapa controlada separada. A ativação produtiva continua dependendo de autorização específica e de observação de latência, gravações, rejeições e passagens. Nenhuma ativação ou modificação funcional está incluída nesta entrega.
