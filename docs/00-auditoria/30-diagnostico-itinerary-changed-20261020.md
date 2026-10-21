# Continuidade operacional e ItineraryChanged — 2026-10-20

Diagnóstico sobre `audit/gps-bus-brt-pipeline`, base `b816690484f514a7ce8bf55d7f7adb804f499dfb`. Nenhuma regra funcional alterada. Consultados AGENTS.md, HISTORICO.md, auditoria 24 e relatórios 27–29. Não há instruções locais adicionais nos diretórios envolvidos.

## Conclusão e alcance da prova

**Existe perda real de continuidade operacional na amostra.** GPS e matching novos coexistem com viagens antigas ainda `Ativa`, sem avanço de timestamp/cursor/versão durável. O retorno denominado `ItineraryChanged` representa uma **recusa de continuidade**, não uma troca confirmada. As proteções evitam atribuir passagens de outro percurso à viagem antiga; o fluxo conservador, contudo, pode continuar recusando as observações seguintes sem estabelecer a execução atual.

Os 24.948/31.641 resultados anteriores (78,85%) continuam sendo avaliações, não viagens ou trocas distintas. A nova amostra demonstra causas concretas e repetição, mas **não permite decompor retrospectivamente as 24.948 avaliações**, estimar a frota inteira ou provar o primeiro instante de perda de cada viagem.

O problema observado afeta a identidade/cursor operacional e o registro de passagens. Não implica congelamento automático da coordenada nem indisponibilidade universal de ETA público. A distinção foi comprovada em produção e por composição de código real em testes sintéticos.

## Observação limitada de produção

Sonda: [itinerary-readonly-probe.py](itinerary-readonly-probe.py). Três painéis independentes; cada execução usa pseudônimos com sal aleatório descartado. **Não correlacionar pseudônimos entre arquivos.** Nenhum payload GPS bruto, ordem física, ID de viagem, coordenada, geometria ou segredo foi persistido nas evidências.

1. **Continuidade, 17:42:21–17:43:36 UTC:** duas linhas REST, primeiras até 12 identidades ordenadas por linha; disponíveis oito BUS/898 e oito BRT/35. Três leituras dos mesmos 16 veículos, separadas por 35s; consulta durável por chave na primeira e última. [Evidência](itinerary-production-evidence-20261020.jsonl).
2. **Geometria, 17:44:31 UTC:** novo painel de 16 veículos; replay espacial em seis divergentes BUS e cinco BRT, no máximo seis/modal. [Evidência](itinerary-geometry-evidence-20261020.jsonl).
3. **Cursor/terminal, 17:45:39 UTC:** novo painel com nove BUS e oito BRT; cursor por PK e próximas/últimas ocorrências por índice `(PadraoVersaoId, Ordem)`, `LIMIT 1`. [Evidência](itinerary-structure-evidence-20261020.jsonl).

| Painel de continuidade, mesmas identidades | BUS/898 | BRT/35 |
|---|---:|---:|
| Veículos acompanhados | 8 | 8 |
| Versão do matching diferente da pinada na primeira leitura | 7 | 6 |
| Linha atual diferente da operacional | 5 | 2 |
| GPS cresceu entre primeira e última leitura | 8 | 8 |
| Estado Redis inteiro idêntico na última leitura ao anterior, apesar de GPS novo | 7 | 5 |
| Versão durável PostgreSQL permaneceu igual entre início e fim | 7 | 5 |
| ETA armazenado na primeira leitura | 8 | 0 |

Todos os 16 tinham matching, alvo observacional e estado operacional `Ativa`; nenhum tinha candidato. Os sete BUS bloqueados também permaneceram idênticos na leitura intermediária com GPS novo. BRT repetiu o mesmo GPS no meio da janela: esses oito casos **não** foram contados como novas leituras nesse intervalo; no final os oito receberam GPS crescente. Três BRT avançaram operacionalmente, incluindo um que inicialmente tinha versões diferentes. Portanto, divergência de versões não equivale necessariamente a rejeição.

No painel inicial, o atraso do último GPS operacional vai de minutos a **275,19 horas (11,47 dias)** entre os bloqueados. Isso mede o último timestamp guardado, não demonstra observação contínua por 11 dias. A repetição diretamente acompanhada durou cerca de 70s. Os snapshots duráveis confirmam que não se trata apenas de um cache Redis esquecido.

Casos reproduzíveis do primeiro painel:

| Pseudônimo | Situação comprovada |
|---|---|
| BUS `25b64d1dc5fa` | GPS da linha 898; operação ainda na 840, última atualização 17/10 15:21:28 UTC, cursor 59/fração 0,978561. Três GPS crescentes; operação e versão durável 343 preservadas. ETA presente inicialmente. |
| BUS `933d670dab6b` | Mesma linha 898, outro sentido/padrão; operação em 20/10 17:36:03 UTC, fração 0,713156/cursor 91; GPS segue avançando no outro matching sem atualizar a operação. |
| BRT `92d390057508` | GPS da 35; operação ainda na 53 desde 09/10 06:30:58 UTC, cursor 6/fração 0,517174, versão durável 356. GPS final novo, operação idêntica. |
| BRT `2008ad2b5b48` e `781b20e04020` | Mesma linha **e mesmo SentidoId**, padrões diferentes; operações fixadas em frações 0,977538 e 0,969293, ambas cursor 15. GPS novo não atualiza as operações; atraso final de aproximadamente 1,37h e 1,82h. |
| BRT `3aa99bbc9192` | Mesma linha 35, outro sentido; operação em 17:19:45 UTC, fração 0,977955/cursor 24. Novo GPS no percurso de retorno mantém a operação antiga. |

### Causas geométricas discriminadas

O replay usa coordenada armazenada e a **geometria da própria versão pinada**, com o orçamento existente `90 × 2 / 3,6 × deltaSegundos + 50m`, sublinha limitada ao intervalo de frações e limite espacial 250m. Não compara diretamente frações de versões diferentes. Consulta também a cadeia versão→padrão→sentido→linha por PK. Não executa matching global da frota nem altera qualquer estado.

| Pseudônimo do painel geométrico | Resultado |
|---|---|
| BUS `14c6856f88ed`, 898 | Sentidos Campo Grande/Sepetiba. Projeção na geometria pinada 0,599517 < 0,713156, distância 50,83m. **Regressão**, apesar da proximidade. |
| BUS `6cf4e77fc50d`, 898 | Sentido oposto. Projeção pinada 0,745690 < 0,958153, distância 3,79m. **Regressão**, não ausência de GPS. |
| BRT `aa1181a9da58`, 35 | Mesmo sentido Terminal Paulo da Portela, padrões `...:1:evqb:...` e `...:1:n6ku:...`. Projeção pinada 0,185794 < 0,977538; distância 5,02m. |
| BRT `2ce14fcab7e5`, 35 | Os mesmos dois padrões/sentido. Projeção pinada 0,708025 < 0,969293; distância 2,32m. |
| BRT `21ebfd76202c`, 35 | Sentidos Terminal Paulo da Portela/Terminal Alvorada. Projeção pinada 0,912545 < 0,977955; distância 10,05m. |
| BUS `948ae2f6336a`, 840→898 | Projeção espacial até seria próxima (1,04m) e não regressiva, mas **o código de linha da cadeia pinada difere do GPS**. Essa condição sozinha torna o ramo SQL operacional inelegível. |

Nos 11 replays, há seis divergências de linha e cinco da mesma linha com projeção regressiva. Todos os padrões pinados consultados continuam com `VersaoAtualId` apontando para aquela versão; versões pinada e observacional têm `Numero=1`. **Esta amostra não sustenta a hipótese de que o excesso seja dominado por uma atualização de versão do mesmo padrão.** Os dois BRT de mesmo sentido têm padrões distintos, ambos atuais; não são simplesmente A antiga/B nova do mesmo padrão.

Dez das 11 geometrias pinadas consultadas são não simples (`ST_IsSimple=false`), todas `LINEAR` e não fechadas. Isso pode refletir retornos, trechos compartilhados e cruzamentos; não comprova geometria incorreta. Nenhuma foi tratada como circular ou corrigida por esse motivo.

O replay explica inelegibilidade, mas não captura o retorno interno original de cada chamada: não houve ativação dos diagnósticos correlacionados. O vencedor global pode diferir do DTO final por histerese. A inferência é forte nos casos em que **qualquer** projeção relevante já viola identidade/progressão; não foi inventado um log de `ItineraryChanged` por veículo. A equivalência com os ramos do código foi reproduzida localmente.

## Todas as origens do status

Implementação registrada no DI: `ViagemOperacionalRepository`, em `Program.cs`. `ItineraryChanged` é um **resultado da avaliação**, não um valor de `EstadoViagem` persistido; os estados continuam `Ativa`, `PossivelFim`, `Finalizada`.

| Origem | Condições exatas que levam ao status |
|---|---|
| Repositório operacional, candidato insuficiente | Identidade não protegida por ambiguidade circular; flag `MudancaOperacionalHabilitada=true`; fase `Ativa`/`PossivelFim`; candidato já existente; avaliação `EvidenciaRejeitadaOuInsuficiente` sem decisão de mudança. Preserva observação/candidato antes da escrita. Pode ocorrer sem diferença de versão atual. |
| Repositório operacional, projeção recusada | Fase `Ativa`/`PossivelFim`, versão GPS diferente da pinada, identidade confiável, nenhuma decisão de mudança e projeção solicitada. Recusa se status não é `Encontrada`, payload ausente, versão projetada diferente da pinada, fração não finita/fora de [0,1]/menor que a confirmada ou comprimento não finito/≤0. `FalhaInfraestrutura` tem retorno próprio, não `ItineraryChanged`. |
| Repositório operacional, projeção não solicitada | Identidade confiável, nenhuma decisão de mudança, fase **Ativa**, versão ainda divergente após o ramo anterior. |
| Repositório Redis legado | Após validação e timestamp crescente, `PadraoVersaoId` diferente do snapshot retorna o status no C#. O Lua repete a proteção na escrita/CAS, cobrindo corrida. Essa implementação não está registrada como autoridade atual. |

Em `PossivelFim` com projeção **não solicitada**, o terceiro ramo acima não se aplica. A regra pura pode preservar identidade/contador e atualizar somente o timestamp, retornando `Updated`; ausência de `ItineraryChanged` também não prova continuidade recuperada. Identidade circular já protegida segue a máquina própria e não esses ramos de divergência.

Dados inválidos, GPS antigo/igual, ausência de matching, conflito de CAS e falha de infraestrutura têm outros resultados. O número `4` do script `ProjectDurable` significa **merge quente**, não `ItineraryChanged`; não é conversão para o enum operacional.

Referências: `ViagemOperacionalRepository.cs:107–199`, `ViagemObservadaRepository.cs` (C# e Lua), `ViagemObservadaService.cs`, `GpsCorrelatedDiagnostics.ProjectionRejection` e `ViagemOperacionalRedisScript.ProjectDurable`.

## Por que matching e viagem divergem

- O matching observacional considera as versões atuais dos padrões da linha, distância/bearing, histórico em memória, histerese e validação temporal. O histórico em memória pode perder referência após `MaxCiclosSemRota=3` e aceitar uma nova inicialização.
- A viagem é recuperada por **veículo**, não por `trip_id` externo ou por cada ciclo GPS. Conserva sua versão/padrão/sentido/cursor até uma transição operacional autorizada. Reiniciar o processo, expirar o ativo de 40s ou o recente de 180s não encerra a viagem durável.
- O hash operacional tem TTL de 24h, mas sua expiração apenas força leitura do PostgreSQL. **Não há prazo de validade operacional que crie outra execução automaticamente.**
- Há um caminho próprio de projeção na versão fixada; ela continua consultável mesmo quando deixa de ser a versão atual. A cadeia de identidade deve corresponder à pinada e ao código da linha atual. O ramo SQL requer candidato global distinto da pinada; se o global é a própria pinada, o enriquecedor tenta reaproveitá-lo.
- A projeção usa orçamento relativo ao **último timestamp operacional aceito**. Rejeições não atualizam esse timestamp. Após horas/dias, a janela espacial pode cobrir a geometria inteira, mas linha diferente, regressão ou distância excessiva continuam inelegíveis. Esperar mais não resolve essas condições.
- A histerese do matching pode publicar B enquanto o vencedor global é A, ou manter o anterior com coordenada reprojetada. Isso é separado da identidade operacional. `trip_id`/`direction_id` externos não encerram nem substituem diretamente a viagem interna.

Testes existentes `ProjecaoOperacional_GlobalB_EAInterno_SaemDaMesmaChamadaSemContaminarDto` e `GlobalA_ComHisteresePublicandoB_ReutilizaAParaOperacaoSemRamoRedundante` comprovam essa separação. O teste conectado existente `PointerAParaB_GlobalUsaB_EVersaoPinadaAContinuaProjetavelComCadeiaValidada` documenta o caso de atualização de versão; foi lido, **não executado** nesta etapa.

## Terminais, retornos e circulares

Para `LINEAR`, cruzar/incorporar a última ocorrência leva a `PossivelFim`; duas confirmações posteriores sem alvo/cruzamento finalizam. Depois de `Finalizada`, nova execução exige sentido inequívoco, duas evidências consistentes em até 180s e deslocamento/progresso de pelo menos 10m. A proteção contra alternância instantânea é esperada.

**A lacuna aparece quando a viagem antiga não chega a Finalizada:** mudança de linha/sentido antes de comprovar o fim pode perder a projeção antiga, retornar antes da regra de terminal e continuar recusando leituras válidas. O código já dispõe de um caminho opcional para confirmar mudança de linha/sentido com duas evidências e baseline sem passagens retroativas; o default da flag é false. Não há valor habilitando essa flag no ambiente/appsettings inspecionados. O comportamento conservador preserva identidade, mas não representa satisfatoriamente uma operação que já mudou de serviço.

No painel estrutural, as 17 viagens permanecem `Ativa`, todas com uma ocorrência terminal ainda à frente do cursor. Os terminais ficam entre 0,999957 e 1. Isso torna concreto o requisito ainda pendente de incorporação do fim. **Não prova que o veículo deveria ter cruzado aquela fração nem que faltou uma das duas confirmações**: nenhuma trajetória desde a chegada/primeira divergência foi coletada. É necessária evidência da fronteira para decidir se houve falha de matching, parada terminal mal posicionada ou término real sem observação suficiente.

Também foi observada a condição antiga 4C.8/4C.9: mesma versão, posição confirmada baixa e cursor próximo do fim. Exemplo BUS `74fa2a9d528d` no terceiro painel: posição 0,100668, cursor 91 em 0,974770, próximo alvo 92 em 0,999999931. BRT `f1f14930a43c`: posição 0,230401, cursor 24 em 0,943491. São **outro mecanismo de falta de passagens**, possível com `Updated`; não devem ser somados a `ItineraryChanged`. Resetar o cursor na mesma viagem poderia duplicar eventos.

Para `CIRCULAR`, a volta pode manter viagem e cursor com prova dirigida; ambiguidade preserva a integridade e bloqueia passagens até recuperação comprovada. A projeção entre versões divergentes permanece não regressiva e não interpreta uma queda da fração como wrap. Os testes de integridade cobrem conservação/recuperação e nova execução quando autorizada. **Nenhum circular foi observado nos três painéis**; não há atribuição produtiva do excesso a circulares.

## Impacto para o usuário e diferenças BUS/BRT

| Superfície | Efeito comprovado ou limite |
|---|---|
| Viagem/cursor operacional | Nas rejeições anteriores à escrita, ficam antigos. Não há criação/finalização nem avanço de ocorrências; o GPS novo seguinte pode repetir a mesma recusa. |
| Passagens | Nenhuma passagem é selecionada/persistida por essa avaliação. A falta de novos eventos decorre antes do Outbox/worker. Não foi auditada a materialização histórica nem contado o total de chegadas reais perdidas. |
| Próxima parada operacional | O retorno de rejeição não contém `ProximaOcorrenciaOperacional`. Consumidores que exigem a identidade/ocorrência operacional não têm alvo confirmado nessa avaliação. |
| Próxima parada do GPS/mapa | O enriquecedor já calculou alvo e distâncias do matching atual. A rejeição não reescreve o DTO aceito. Pode diferir da ocorrência que o cursor antigo alcançaria. |
| ETA físico público/eventos de parada | Calculado antes da decisão de viagem, usando matching causal e identidade do destino. Pode permanecer válido apesar da rejeição operacional. Os oito BUS tinham ETA inicialmente, incluindo sete com operação divergente. O endpoint público pode oferecê-lo onde os gates de identidade/frescor/distância permitem. |
| Coordenada/REST/SignalR | CAS de posição antecede a avaliação operacional; a rejeição não desfaz o aceite do GPS nem sua publicação. Não implica marcador congelado. |
| Dados derivados que exigem viagem confirmada | O gate de identidade recusa o vínculo operacional incompatível. Não se deve suprir a identidade antiga a uma nova observação para preencher dados faltantes. Nenhuma implementação ML foi alterada/ativada. |

BUS e BRT usam a mesma autoridade, proteções e flag; não há ramo que autorize mudança só por modal. Nesta amostra, BUS expõe principalmente reutilização em outras linhas e sentidos; BRT também expõe padrões diferentes dentro do mesmo sentido. O tipo do serviço registrado nos padrões consultados é `GTFS`, sem evidência de uma regra especial de continuidade por BUS/BRT.

O BRT continuava sem ETA mesmo entre os controles que avançavam operacionalmente. A imagem observada é **a mesma da auditoria de 19/10**, `sha256:db64e5200aad3faf48f55a095ca769170b650df885938d673693bc2d98b0bfb9`, iniciada em 18/10. Não houve deploy desta branch nem comprovação de equivalência binária com `b816690`. A perda anterior de predecessor/cadência é uma causa suficiente já reproduzida para o ETA BRT; não atribuir todo ETA ausente a `ItineraryChanged` nem tratar o runtime observado como regressão das correções locais posteriores.

## Reproduções e validação

**345 casos distintos aprovados, zero falhas/ignorados nas execuções finais por seleção.** SDK 9.0.306, xUnit/VSTest; compilação do projeto com cinco avisos anteriores. Comandos e hashes em [itinerary-validation-20261020.json](itinerary-validation-20261020.json). TRXs locais em `NoPonto/lab-output/itinerary-audit-20261020` foram parseados como XML, conferindo resultados individuais e totais.

| Requisito | Evidência reproduzível |
|---|---|
| GPS crescente repete a recusa, BUS/BRT | `GpsPassagens4C6Tests.ActiveVersionDivergence_RepeatsWithoutReanchoringOrDecision`: três leituras/caso; projeção não solicitada, inelegível, versão errada e regressiva; estado antigo, zero decisões/commit. |
| Motivo exato sem efeito colateral do diagnóstico | `GpsCorrelatedDiagnosticsTests.RealRejectionHookEmitsExactReasonAndPinnedIdentity` e `ThrowingSinkDoesNotChangeRepositoryResultOrExistingCounters`. |
| Versão apenas, padrão no mesmo sentido e sentido diferente; Ativa/PossivelFim; ETA público simultâneo | **Novo** `GpsItineraryContinuityDiagnosisTests.FreshMatchingAndPhysicalEta_CoexistWithOldPinnedTripRejection`: 12 casos × três GPS recentes; operação sintética de dois dias preservada; ETA de 100s e `ProjectRoadEvents` reais; sem Redis/PostgreSQL. Não é replay SQL de uma trajetória real. |
| Terminal e retorno conservadores | `ViagemOperacionalRegraTests.Terminal_T0T1T2_FinalizaSemV2_ReconstrucaoCodecPreservaContador`, `PrimeiroR2_PreservaTerminal_SemFinalizacaoPrematura`, `Finalizada_MesmaLinhaMesmoSentidoComProgresso_CriaNovaViagemSemRetroativos`. |
| Mudança de versão não define outra execução; padrão/topologia exigem política | `MudancaOperacionalRegraTests.SomenteVersaoGeometrica_NaoCriaOutraExecucao`, `SomentePadraoOuTopologia_FicaInsuficiente`, `DuasEvidencias_ConfirmamLinhaOuSentido`. |
| Circular exige prova e não presume nova volta | `IntegridadeCircularRegraTests.SemProva_PreservaVoltaCursorEAncora_BloqueiaPassagens`, `VersaoGeometricaDiferente_NaoProvaVolta`, `ReancoragemSustentada_NovaExecucaoSemPassagens_IndependeDaFlag`. |
| Cursor adiantado é outro fenômeno | `GpsCursor4C9Tests.CursorAhead_NoResetOrDuplicateUntilUnincorporatedTargetCrosses` e `GpsCursor4C8Tests.LinearRegression_KeepsCursorAndTripThenAcceptsProgressBelowCursorFraction`. |

Execuções: 38 existentes de recusa/diagnóstico + 295 existentes de matching/viagem/mudança/circular/cursor/ETA + 12 novos de composição. A primeira montagem dos oito casos novos falhou em `InvalidState` porque a fixture compartilhada tinha coordenada (0,0); corrigida **somente a fixture**. Depois passaram oito; a matriz final acrescentou quatro casos de padrão distinto no mesmo sentido e passou 12. A falha inicial não é apresentada como RED de um defeito funcional corrigido.

Não executados: integração conectada de testes, Docker local, migrations, inicialização da API, testes frontend/dispositivo, ML, carga, histórico amplo, contagem global de eventos, deploy, push, merge ou PR. O build completo `dotnet build NoPonto.sln --no-restore --no-incremental --verbosity quiet` foi rejeitado pela revisão automática de aprovação antes de executar (`approval required by policy`, com aprovação `Never`); nenhuma causa técnica adicional foi informada. O projeto foi compilado pelas execuções de testes, sem alterar dependências.

Leituras remotas totais: seis GETs loopback de duas linhas; cinco `EVAL_RO` sobre até 51 chaves nomeadas, apenas HGET/HGETALL; três leituras limitadas de índices; 48 buscas de estado por PK; 11 replays de duas versões; 17 buscas estruturais por índices, total **79 transações READ ONLY** curtas. `statement_timeout=3s`, `lock_timeout=500ms`, idle=5s, socket/psql=10s. Nenhum scan de telemetria/eventos, `COUNT(*)` global, `EXPLAIN ANALYZE`, SCAN/KEYS Redis, escrita SQL ou mudança de configuração. Falha SSH inicial com `IdentitiesOnly=yes`; conexão autorizada pelo agente funcionou sem essa opção; credenciais/permissões/servidor não alterados.

## Proposta antes de qualquer correção

1. **Não alterar o matching, não aceitar projeção regressiva e não resetar cursor para reduzir contagem.** Esses gates explicam rejeições válidas nas geometrias consultadas.
2. A correção funcional a avaliar é a **fronteira entre execuções após troca comprovada de linha/sentido sem finalização observada da antiga**. Reutilizar o avaliador existente sob flag, em vez de criar mecanismo duplicado: duas evidências atuais coerentes, movimento/progresso plausíveis, término lógico sem inventar chegada e nova execução com baseline sem retroativos. Antes de implementar/habilitar, revisar a política de encerramento e validar persistência/CAS/retorno/terminal com integração isolada. **Não habilitado nesta etapa; nenhuma operação produtiva proposta está autorizada automaticamente.**
3. Os casos BRT de **padrão diferente no mesmo sentido** exigem decisão separada: a implementação existente declara que a política de nova execução não está definida para esse caso. Ligar a flag de mudança não resolve isso por si só. É necessário verificar o papel dos dois padrões e a fronteira real de execução; não unificar padrões nem transportar frações por proximidade espacial.
4. Uma melhoria segura e independente é distinguir **recusa de continuidade** de **mudança confirmada** na observabilidade, mantendo o enum/contratos compatíveis. O diagnóstico correlacionado já contém identidade pinada/observacional e motivos; propor seu uso limitado, ou contadores agregados desses motivos sem novas escritas operacionais. Qualquer ativação produtiva exigirá autorização separada. Nesta rodada, a sonda somente leitura e os testes fornecem a evidência sem ativar logs.

Não há correção funcional única comprovadamente segura para todos os grupos observados. A evidência já é suficiente para tratar a continuidade operacional como problema real e revisar a fronteira de execução; não é suficiente para relaxar proteções ou declarar todas as rejeições incorretas.
