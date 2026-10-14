# GPS 4C.9 — decisão sobre cursor adiantado

## Decisão

O bloqueio imediato está comprovado nas quatro versões: posição abaixo da fração do cursor e próximas ocorrências além do intervalo percorrido. Não há prova suficiente de bug na preservação do cursor nem de início de nova execução. Não aplicado patch funcional; SQL, geometria, TTL, dados e flags permanecem intactos. Não houve acesso remoto, nova SELECT ou janela diagnóstica.

Evidência PostgreSQL fornecida pelo operador: todas LINEAR, GTFS_DATARIO/DATARIO_GTFS_V1, sequência sem regressões. BRT 12 ocorrências/cursor 11=.7676462484/posição .523808; BRT 6/cursor 5=.8185431710/posição .422581; BUS 63/cursor 62=.9648180165/posição .858762; BUS 93/cursor 91=.9858968648/posição .070241. Os três primeiros alvos são terminais; BUS alvo 92 não é terminal. Isso elimina a hipótese de sequência estrutural regressiva nessas versões e confirma cursor adiantado, mas não mostra como ele foi alcançado.

## Fluxo responsável e limites da prova

- `ViagemOperacional.cs:149–158`: atualização normal substitui fração e progresso absoluto pela posição atual, mantendo o cursor fornecido pela transição. Regressão pode diminuir ambos, mas não libera ocorrência já incorporada.
- `OcorrenciaParadaRepository.cs:32–48`: cruzamento LINEAR exige avanço, ordem superior ao cursor e fração em (anterior,atual]; cursor é monotônico por `greatest`. Avanços abaixo do cursor não restauram passagens antigas. Não é um bloqueio eterno: cruzar a próxima ocorrência ainda permite passagem; baixá-lo reabriria ocorrências da mesma viagem.
- `ViagemOperacional.cs:174–190`: fim exige terminal incorporado/sem próxima, depois duas confirmações de permanência terminal. Ter cursor penúltimo, regressão ou passagem do tempo não finaliza. Nos quatro casos há próxima: não há condição terminal comprovada que deveria ter sido aplicada.
- `ViagemOperacional.cs:193–260`: somente após Finalizada, duas observações de candidato com versão/linha/sentido consistentes, sentido inequívoco, janela de 180s e movimento/progresso >=10m criam nova viagem e baseline próprio, sem passagens retroativas. Regra já existe e tem testes; o estado Ativa impede entrar nela deliberadamente. Não remover essa pré-condição por fração baixa.
- `ViagemOperacional.Mudanca.cs:14–18,93–105,109–129`: mudança de linha/sentido pode confirmar nova execução com evidências consistentes e plausíveis, mas só com MudancaOperacionalHabilitada. Mesma operação retorna Continuidade mesmo se versão geométrica mudar; apenas padrão/topologia diferente na mesma linha/sentido tem política explicitamente indefinida. Flag false é política operacional conhecida, não um bug demonstrado e não será ativada nesta tarefa.
- `ViagemOperacionalRepository.cs:132–195`: divergência ativa sem projeção pinada válida retorna ItineraryChanged antes de decisão/commit; não baixa cursor, não cria execução e não confirma posição nova. Os 27.260 resultados agregados podem representar continuidade presa à execução anterior, mas não têm vínculo individual/temporal com os quatro cursores. Não comprovam causalidade nem que uma inversão confiável deixou de ser processada.

## O que os contratos conseguem distinguir

Fração, comprimento, timestamp, linha/sentido/padrão/versão, estado/candidato, GPS e plausibilidade ajudam a rejeitar observação antiga, salto e identidade divergente. Pequena regressão não é nova viagem. Retorno físico na mesma viagem não autoriza reemitir ocorrências. Dois avanços no início também podem ser retorno/reprojeção da mesma viagem: não certificam nova execução se a anterior não terminou.

O estado ViagemObservadaState não retém identidade externa de execução, causa da regressão, geometria do trajeto percorrido ou uma âncora LINEAR comprovando fechamento. Latitude/longitude do candidato servem às regras já existentes, não constituem prova de partida de uma execução nova. TripId existe nas observações das fontes e em GpsStructuralHints, mas não é identidade confiável de execução no estado operacional: não tratar troca de hint como autorização. Um trip_id pode repetir entre dias e requer associação estrutural/data de serviço/ocorrência de execução verificadas. Inversão de sentido no matching pode ser correta ou erro geométrico; bearing/plausibilidade reduzem ambiguidades, não certificam uma partida.

Os mesmos contratos resumidos admitem duas histórias: execução original regressiva que não pode duplicar passagens e execução nova cuja partida não foi identificada. Exigir decisões diferentes a partir das mesmas entradas é impossível sem informação adicional ou nova política de negócio. Congelar a fração em seu máximo também não resolve essa ambiguidade e modificaria validação/projeção/interpolação futuras.

## Reprodução e expectativa

`GpsCursor4C9Tests.cs`: seis casos, quatro frações reais com IDs/tempos/geometria sintéticos, pequena regressão e retorno declarado pela fixture na mesma viagem. A decisão pura real conserva viagem/cursor, permite avanços abaixo do cursor sem duplicar e emite a próxima ocorrência apenas quando transição posterior a seleciona. Transições são fixtures, não execução SQL nem prova de trajetória física produtiva. Os casos não certificam retorno real; explicitam a expectativa quando a identidade da viagem é mantida.

Reutilizados testes existentes de Finalizada + mesma linha/sentido com movimento criando novo ID/baseline sem retroativos, e mudanças operacionais protegidas. Não existe teste RED de bug funcional: fazer falhar um teste que exige reset baseado apenas em fração seria inventar a regra cuja segurança ainda não foi demonstrada.

## Menor evidência e proposta limitada

Não é necessária nova auditoria ampla nem repetir a SELECT. Para um dos quatro casos, a informação decisiva é **prova de fronteira de execução**: chegada/fim anterior e partida nova auditadas, ou identidade externa de execução com data de serviço e mapeamento confiável à mesma linha/sentido/padrão, correlacionada às observações antes/depois. Para localizar defeito de projeção, alternativa é o par de observações já existente no instante da primeira regressão: mesma viagem/versão, frações, cursor, timestamps, posição física/bearing e matching, examinados em artefato privado; os resumos fornecidos não contêm esse par. Isso distingue percurso físico regressivo de projeção em outro trecho, mas sozinho não certifica partida nova.

Se essa prova mostrar fim/partida e o estado continuou Ativa, propor reaproveitar a transição de encerramento/início com novo ViagemId e baseline da posição atual, mantendo cursor antigo selado e deduplicação por viagem/ocorrência/volta. Nunca gerar retroativos ou baixar cursor na mesma viagem. A integração dessa prova pode exigir contrato interno/estado adicional; deve ser desenhada e aprovada antes de editar persistência. Se apenas matching incorreto for demonstrado, corrigir sua origem com teste RED daquele par, sem tocar no cursor. Até então, não há patch mínimo seguro demonstrado para liberar as ocorrências antigas.

## Validação executada

Filtro final explícito das seis classes offline: GpsCursor4C9Tests, GpsCursor4C8Tests, GpsPassagens4C6Tests, ViagemOperacionalRegraTests, MudancaOperacionalRegraTests e MudancaOperacionalIntegracaoLogicaTests. `dotnet test --no-build --no-restore`: **111 aprovados, zero falhos/ignorados**, incluindo os seis casos novos. TRX: `D:\repositorio_github\NoPonto\gps-4c9-validation-20261014\gps4c9-offline.trx`.

A primeira execução usou filtro amplo MudancaOperacional: 112 aprovados e 13 falhos durante inicialização por ausência de POSTGIS_TEST_CONNECTION para banco isolado. Isso foi erro de seleção de testes, não teste RED de regra funcional; nenhum banco foi acessado. O TRX inicial gps4c9.trx foi preservado. Cinco warnings preexistentes na compilação. Não executadas operações remotas, Docker, migrations ou testes de banco com conexão configurada.

Arquivos desta etapa: novo GpsCursor4C9Tests.cs, este relatório e acréscimo no HISTORICO.md. Todos os demais trabalhos preservados.
