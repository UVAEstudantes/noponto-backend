# Avanço operacional após matching GTFS-RT — complemento focal

Data: 2026-10-11. Complementa [auditoria anterior](09-passagens-pos-gtfsrt-20261011.md). Somente diagnóstico por leitura local; nenhuma execução em produção, banco, Redis, ambiente de desenvolvimento ou testes. Consultados AGENTS.md, histórico, auditorias anteriores e implementações existentes. Alterações preexistentes preservadas.

## Conclusão e limite da evidência

O volume fornecido **não demonstra que 7.833 observações avançaram o cursor nem que cruzaram paradas**. `viagem_processadas` inclui qualquer resultado operacional não nulo, inclusive rejeições. Checkpoint pode persistir movimento dentro do mesmo segmento sem qualquer passagem. O primeiro gate ainda não quantificado é: depois da seleção de identidade/projeção e da decisão operacional, quantas observações efetivamente elegíveis cruzaram uma ocorrência posterior ao cursor?

Prioridade: recuperar os contadores já existentes por ciclo, classificar resultados/estados e comparar posição, cursor e próxima ocorrência de poucos veículos. Só então escolher uma correção. Não há justificativa para relaxar matching ou proteções para elevar volume. Os 11 eventos Outbox e 11 cruzamentos contabilizados podem coincidir, mas essa igualdade não certifica 11 passagens nem explica sua origem.

Base local: HEAD `6f64538b47cb3987c5bf023d9878931b69228dac`. A imagem informada `sha256:dbe503a480387dc1ab6e54857cc7f052d3524a61991666fc14e022dac833af79` e início `2026-10-10T14:37:46.200180815Z` identificam o runtime; não comprovam seu commit ou correspondência exata com estas fontes. As regras abaixo são da implementação local, condicionadas à confirmação dessa correspondência.

## Evidências novas e denominadores

Segundo o usuário, produção tem `EnriquecerTodasLinhas=true`, primárias BUS/BRT GTFS-RT habilitadas, matching aceito na maioria das posições novas e ausência de falhas importantes de infraestrutura nos ciclos inspecionados. Isso reduz a prioridade de seleção por assinantes e indisponibilidade generalizada nesse recorte; não valida todos os resultados operacionais.

| Dado agregado de 20 ciclos disponíveis | Interpretação permitida |
|---|---|
| 8.196 GPS novos | Observações, não veículos nem viagens distintas |
| 7.833 enriquecidos | 95,57% dos novos; no código, presença de `PosicaoNaRota`, não certificação de cadeia de ocorrências |
| 7.833 viagens processadas | Resultados não nulos; igualdade com enriquecidos não prova 100% de sucesso operacional |
| 642 escritas duráveis | 632 checkpoints + 10 escritas semânticas |
| 632 checkpoints | 98,44% das escritas; não implicam imobilidade |
| 10 alterações semânticas | Commits classificados como semânticos, não dez viagens ou dez paradas |
| 11 eventos Outbox | Soma de eventos nas decisões com commit; tipos desconhecidos |
| 11 catch-up de passagens | Soma de ocorrências selecionadas antes do commit em um ramo; não total de passagens confirmadas |

São 20 ciclos **disponíveis** nos últimos dez minutos: não assumir cobertura contínua, mesmo horário de início/fim entre agregados externos ou população constante. Os 363 novos sem enriquecimento não explicam sozinhos o pequeno volume posterior. Também não calcular `7.833 - 642 = 7.191` atualizações quentes: resultados rejeitados, nulos e tentativas de recuperação têm contabilidade diferente.

## Funil após matching

| Etapa | Decisão/gate | Saída possível sem passagem |
|---|---|---|
| GPS/CAS | Timestamp novo e publicação da observação; recuperação pendente pode ocorrer antes | Snapshot repetido descartado; backlog mantém atualização operacional pendente |
| Serviço observado | Precisa versão e fração projetada | Resultado nulo por informação insuficiente ou pendência no polling |
| Contexto operacional | Identidade anterior, versão durável/quente, timestamp, integridade | `Conflict`, `RejectedOlderOrEqual`, `InvalidState` |
| Mudança operacional | Somente quando flag habilitada e estado/continuidade elegíveis; confirmação de identidade | Candidato mantém timestamp e congela avanço; identidade insuficiente pode produzir `ItineraryChanged` |
| Projeção pinada | Matching observacional pode ser de outra versão; tenta conservar execução anterior | `ItineraryChanged` ou falha; projeção não solicitada não significa falha |
| Estrutura/ocorrências | Versão operacional, sequência válida, cursor pertencente à versão | `InvalidSequence`, `OccurrenceNotFromItinerary`; cadeia vazia pode passar validação e não ter alvo |
| Baseline | Nova execução, finalizada, troca de versão ou caminhos de proteção/adoção | Incorpora ocorrências já anteriores à posição, sem gerar passagens retroativas |
| Continuidade/estado | Normal, candidato, finalizada, proteção circular, recuperação | Cursor congelado, espera de confirmação ou rebaseline legítimo |
| Transição normal | Compara frações e ordem anterior; circular trata wrap | Movimento sem cruzamento, regressão, cursor já adiantado ou ausência de próximo alvo |
| Decisão de eventos | Passagem por ocorrência elegível; início/fim por regras próprias | Adoção pode suprimir passagens; início/fim não são passagens |
| Persistência | Atualização quente, checkpoint ou semântica; CAS/lock/versionamento | Hot update/checkpoint sem eventos; conflito após seleção de cruzamentos |
| Commit/Outbox | Estado e eventos atomicamente persistidos | Contador não informa tipo nem comprova linha nova se EventId já existe |
| Materialização | Worker entrega journal/histórico; lógica ETA na transação | Falha/pendência pode afetar label mesmo com evento existente; etapa posterior ao gate não medido |

`Created` indica ausência de linha durável anterior. Uma execução nova sobre veículo já conhecido pode retornar `Updated`. `Updated` cobre atualização quente, checkpoint, passagem, manutenção de candidato/proteção e transições de estado; não é sinônimo de avanço. `ItineraryChanged` é divergência detectada, não confirmação de mudança aplicada. Os demais resultados são `RejectedOlderOrEqual`, `InvalidState`, `InfrastructureFailure`, `Conflict`, `InvalidSequence` e `OccurrenceNotFromItinerary`. Não há contagem agregada atual de cada resultado.

### Posição, cursor e baseline

A fração projetada pode crescer e o cursor permanecer igual até ultrapassar a próxima ocorrência. A igualdade semântica exclui timestamp, fração confirmada e progresso absoluto; por isso várias posições em movimento podem resultar em Redis quente e checkpoint periódico (janela local de 60 segundos). Mudanças de cursor, estado, candidato ou integridade e eventos exigem escrita semântica.

No ramo linear normal, a seleção exige fração atual >= anterior, ordem > cursor e posição da ocorrência estritamente > fração anterior e <= atual. A posição atual pode regredir mantendo um cursor alto: eventos antigos não são reemitidos, e o próximo avanço depende de alcançar ocorrências posteriores. Isso deve ser medido, não chamado de bug apenas pelo cursor parado.

Baseline incorpora o passado até a posição de referência sem emitir passagens. O ajuste anterior em `70d7bcb` usa a posição atual no nascimento/reabertura, evitando reproduzir caminho de uma execução antiga. Baselines frequentes ou incorretamente localizados podem reduzir cobertura, mas não há denominador que demonstre isso no recorte.

`OcorrenciaParadaRepository` valida ordem/posição monotônica, IDs e pertencimento do cursor. **Não rejeita cadeia vazia como tal**: a transição pode retornar `Updated` sem próxima/terminal/cruzamentos. Matching valida geometria/fração/comprimento, não existência de ocorrências. Cadeias vazias, esparsas ou frações concentradas e cursor incompatível devem ser verificados na versão operacional concreta, sem presumir problema de importação.

### Mudança, proteção, recuperação e fim

`GpsPolling__MudancaOperacionalHabilitada`: **valor efetivo desconhecido**. Não consta das evidências fornecidas. Default/configuração local não certifica runtime. Obter valor efetivo e correspondência da imagem antes de atribuir causa à flag.

Com mudança habilitada, `Ativa`/`PossivelFim` e identidade confiável podem formar candidato. Confirmação exige identidade consistente, sentido inequívoco, plausibilidade operacional, janela de até 180 segundos, deslocamento físico e projetado de pelo menos 10 metros e ausência de salto. Troca apenas de padrão mantendo linha/sentido não basta para abrir nova execução. Enquanto aguarda, o cursor pode ficar congelado. Confirmação pode encerrar a anterior e iniciar outra: dois eventos em um commit. A adoção após candidato pode fazer baseline e suprimir passagens no intervalo congelado.

Circular com continuidade ambígua conserva proteção existente inclusive com flag desabilitada. Não equivale ao wrap circular normal. Uma prova válida pode recuperar uma volta com rebaseline sem reconstruir passagens; recuperação por duas observações consistentes pode emitir fim anterior com `PerdaContinuidadeCircular` e início novo, sem labels retroativos. Não se inventam voltas perdidas.

Na linear, o terminal leva a `PossivelFim`, seguido de duas confirmações para encerrar. Não há limite máximo de intervalo nesse ramo; confirmação observa cursor terminal, não exige nova aproximação física. Assim um cursor terminal persistente pode sustentar encerramento tardio, inclusive com fração atual menor. Após `Finalizada`, novo início depende de candidato confirmado, plausibilidade/sentido e deslocamentos; não nasce de cada matching aceito.

Proteção e ramo de candidato de mudança ativo forçam escrita semântica quando persistidos. Apenas dez commits semânticos tornam improvável que **muitas atualizações bem-sucedidas** desses ramos sejam a explicação dominante, se a contabilidade e imagem forem as mesmas. Isso não exclui rejeições nesses ramos nem espera de candidato após `Finalizada`, que não tem a mesma classificação forçada.

## Contadores: o que medem e o que não medem

| Contador | Implementação e limitação |
|---|---|
| `viagem_chamadas` | Incremento após GPS/CAS aceito no polling, antes de decidir pendência/chamada efetiva; não quantidade de viagens distintas |
| `viagem_processadas` / `viagem_concluidas` | Ambos incrementados para todo resultado não nulo; `concluidas` não significa execução finalizada |
| `viagem_conflitos` / `viagem_infra` | Subconjuntos explícitos dos resultados; demais rejeições não têm distribuição agregada equivalente |
| `viagem_durable_writes` | Incrementado após commit bem-sucedido do repositório |
| `viagem_checkpoint_writes` | Commit classificado checkpoint, inclusive persistência durável exigida por retry sem alteração semântica |
| `viagem_semantic_writes` | Commit semântico; eventos, mudança semântica de estado ou classificação forçada. Pode conter zero, um ou vários eventos |
| `viagem_skipped_durable_writes` | CAS quente bem-sucedido sem SQL durável. Solicitar valor real; não inferir por subtração |
| `viagem_outbox_events` | Soma de `decision.Eventos.Count` após commit, sem separar `PassagemParada`, `ViagemIniciada`, `ViagemFinalizada`; não ack do worker nem contagem de INSERTs inéditos |
| `catchup_passagens_total` | Soma de `transition.Ultrapassadas.Count` no ramo normal com estado anterior, antes de validações/commit; inclui uma única ocorrência, sem exigir gap grande |
| `catchup_passagens_max_por_observacao` / gap | Máximo e gap de seleção; não certificam commit nem mostram quantas observações tinham cruzamento |
| `itinerary_changed_ocorrencias` | Resultado divergente da identidade operacional; usar antes de propor nova métrica |
| `projecao_operacional_*` | Solicitada/encontrada/inelegível/falha para contexto apto; encontrada não garante adoção. `NaoSolicitada` não incrementa; comandos não são contador universal de consultas físicas |

Catch-up é contado antes de possíveis conflitos/falhas e pode incluir ocorrências posteriormente suprimidas por adoção/regras. A lista `OcorrenciasUltrapassadas` do resultado também não substitui a lista de eventos efetivamente confirmados. Nenhum desses contadores mede avanço físico, crescimento da projeção por observação, ausência de alvo ou total de transições elegíveis.

Há uma diferença adicional de escopo: recuperação no início do ciclo ocorre antes de criar/instalar o contexto de performance. Já `RecuperarVeiculoAsync` antes do CAS individual ocorre dentro de `GpsCommitPerformanceContext`, e suas escritas/catch-up podem entrar nos contadores do ciclo. `viagem_processadas` contabiliza o resultado externo do processamento corrente, não cada retry interno. Não parear automaticamente tentativas, escritas e observações como se fossem a mesma população.

### Reconciliação dos 11 eventos

Pela implementação, `durable = checkpoint + semantic`, observado como `642 = 632 + 10`. Eventos tornam a escrita semântica nos caminhos normais; checkpoints não explicam onze eventos. Mas dez commits semânticos podem conter onze eventos: por exemplo, nove com um e um com dois; ou oito com um, um sem eventos e um com três. Fim+início já oferece um mecanismo de dois eventos.

Também é possível que os onze sejam passagens distribuídas em dez commits, sem início/fim nessa janela. **Não é possível escolher essa explicação com os totais**. Catch-up inclui seleção que pode não virar evento; eventos incluem início/fim; escopos de retry e deduplicação diferem. A igualdade `outbox_events = catchup_total = 11` não impõe igualdade de conjuntos. Reconciliar tipo/EventId/horários e commits, distinguindo criação do evento, timestamp lógico e processamento pelo worker. Consultas de journal e backlog da auditoria anterior são auxiliares, não medição exata das listas de decisões do ciclo.

## Matching observacional versus operacional pinado

O matcher pode aceitar a melhor versão observacional enquanto a viagem mantém versão anterior. Para `Ativa`/`PossivelFim` confiável, a projeção operacional tenta continuar a versão pinada: precisa identidade elegível, geometria/comprimento, distância dentro do orçamento e avanço compatível. Matching aceito em outra versão não implica que essa projeção foi encontrada ou adotada. Falha de projeção retorna infraestrutura; inelegibilidade/divergência pode retornar `ItineraryChanged` sem passagem. Com troca real de linha e flag desabilitada, preservar identidade antiga não confirma automaticamente execução nova.

Quando versões coincidem, não tratar toda projeção inelegível como rejeição operacional: o gate de consumo da projeção é condicionado à divergência de versões. Da mesma forma, denominador correto de sucesso de projeção é o conjunto solicitado/elegível, não todos os GPS enriquecidos.

DTO de telemetria mantém o matching observacional. Associação ML exige compatibilidade exata de identidade/versão/sentido/timestamp e estado confiável. Uma passagem da versão pinada pode coexistir com amostra observacional sem `ViagemId` associado. Essa diferença interessa à cobertura do dataset, mas não prova ausência de evento operacional. Comparar IDs de versão e alvo separadamente.

## Cadência GTFS-RT e histórico físico

BUS atualiza aproximadamente a cada 30–40 segundos e BRT a cada 60; polling a cada 15 segundos não multiplica observações novas. Descartar timestamps repetidos é esperado. Um salto normal monotônico pode selecionar todas as ocorrências intermediárias: intervalo maior não reduz necessariamente passagens. Janela de interpolação de 180 segundos muda qualidade/tempo inferido, não significa que toda seleção acima dela seja descartada.

Há um ponto concreto a verificar: predecessor físico vem do DTO **ativo** Redis. TTL ativo local é 40 segundos; valor efetivo produtivo é desconhecido. Repetições não renovam automaticamente esse histórico ativo; watermark preserva deduplicação, não coordenadas predecessoras. Intervalos BRT de 60 segundos, BUS perto do limite e snapshots perdidos podem deixar observação sem predecessor ativo.

Matching aceito pode ocorrer com bearing da fonte/memória, mas `MatchingOperacionalPlausivel` requer histórico físico válido, timestamps crescentes e ausência de salto. Perder esse histórico afeta confirmação de mudança, novo início pós-finalizada e recuperação; não bloqueia universalmente atualização normal da mesma versão. Não recomendar alteração de TTL sem medir esse caminho e seu valor efetivo.

Snapshots intermediários perdidos podem: (a) ser recuperados por cruzamentos no ramo normal; (b) ocultar aproximação/confirmação terminal; (c) interromper confirmação de candidato após mais de 180 segundos; (d) tornar continuidade circular ambígua, sem recuperar número arbitrário de voltas; (e) perder cruzamentos em rebaseline/adoção. Separar ausência no feed, perda na coleta e repetição legítima; não inferir todas a partir de lacuna na telemetria amostrada.

## Hipóteses priorizadas

| Prioridade | Hipótese e evidência | Como discriminar |
|---|---|---|
| 1 | Movimento no mesmo segmento, posições repetidas espacialmente ou cursor já adiantado: predominância de checkpoint é compatível, mas não comprova | Pares de posição/projeção da mesma execução e versão, cursor e próximo alvo; distribuição de resultados/estados |
| 1 | Observações sem alvo/cadeia útil ou retidas após fim: matching não valida cadeia; `Updated` não certifica elegibilidade | Sequência da versão pinada, próxima/terminal, estado/candidato; comparar linear/circular reais |
| 2 | Perda de predecessor com cadência versus TTL impede confirmações apesar de matching aceito | TTL efetivo, presença/idade de histórico, plausibilidade e estado; separar BUS/BRT |
| 2 | Divergência observacional/pinada ou mudança não habilitada | Flag efetiva, `itinerary_changed_ocorrencias`, projeção por resultado e IDs de versão |
| 3 | Baseline/adoção/recuperação absorvem cruzamentos sem labels | Motivo de baseline e supressão, por observação elegível; dez semânticas limitam explicação de proteção bem-sucedida massiva |
| 3 | Seleção de cruzamentos sem commit ou eventos sem materialização | Conflitos/retry, tipo/EventId, backlog/journal/histórico; infraestrutura geral não é a explicação prioritária nas evidências novas |

Linhas 796, 798, 868 e 869 são casos de inspeção, não população representativa. As evidências novas não informam seus estados, versões, topologias ou trajetórias; não atribuir circularidade ou causa a um código. Repetir inspeção por sentido/versão e comparar uma linha de controle com passagens. Encerramentos tardios anteriores são compatíveis com terminal confirmado tarde, espera pós-finalização e recuperação, mas não demonstram que esses casos explicam os 7.833 resultados.

## Coleta manual seletiva

Nenhum comando abaixo foi executado. Operador deve fornecer janela UTC exata dos 20 ciclos, identidade da aplicação e nomes reais. Não coletar ambiente completo/segredos. Capturar valores efetivos de `GpsPolling__MudancaOperacionalHabilitada` e TTL ativo pelo mecanismo de configuração usado no deploy; presença/ausência de variável isolada não prova valor efetivo se há outra fonte de configuração. Confirmar commit associado à imagem pelos metadados de build disponíveis.

Para logs em Docker, sem iniciar/reiniciar nem alterar configuração, substituir placeholders e executar manualmente; limitar janela inicialmente a dois minutos que contenham snapshots novos BUS e BRT:

```powershell
$auditContainer = 'PREENCHER_CONTAINER'
$auditInicio = 'PREENCHER_ISO_UTC'
$auditFim = 'PREENCHER_ISO_UTC'
docker logs --since $auditInicio --until $auditFim --tail 20000 $auditContainer 2>&1 |
  Select-String -Pattern 'viagem_processadas|viagem_skipped_durable_writes|itinerary_changed_ocorrencias|projecao_operacional_|catchup_passagens_|continuidade|viagem criada|InvalidSequence|OccurrenceNotFromItinerary|ItineraryChanged|Conflict|InfrastructureFailure'
```

Conferir truncamento e linhas reais antes de agregar. Não contar mensagens de duração como viagens. `ItineraryChanged` não tem necessariamente warning individual; ausência da palavra não prova zero. Solicitar linha completa de performance por ciclo, preservando correlação/horário, incluindo chamadas/processadas/conflitos/infra, skipped/durable/checkpoint/semantic, projeção, catch-up e divergência.

Para banco, usar os blocos seletivos [SQL anterior](09-passagens-pos-gtfsrt-20261011.sql): Q1 índices, Q3 telemetria por código/janela, Q4 execução individual e Q5 backlog limitado. Preparar janela real em cópia do roteiro; não reutilizar horários exemplificativos como se fossem os vinte ciclos. Inspecionar plano com `EXPLAIN` simples, sem `ANALYZE`, antes de executar. Ausência de índice apropriado ou timeout encerra a consulta; não elevar limite automaticamente. Telemetria é amostrada e pode representar identidade observacional: não equivale à sequência de decisões do repositório.

As duas consultas complementares abaixo são por chave/versão específica, sem varredura global. Executar separadamente, somente após conferir índices PK de veículo e `(PadraoVersaoId, Ordem)`. Cap de 1.001 ocorrências indica truncamento se alcançado. Não repetir continuamente nem varrer todos os veículos. Estado é **array JSON de 27 posições**, não objeto com propriedades; números são índices zero-based do codec local.

```sql
-- PREENCHER um veículo previamente escolhido nos logs/telemetria.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
SELECT "OrdemVeiculo", "Versao", "AtualizadoEmUtc",
       "Estado"->>0 AS viagem_id, "Estado"->>2 AS versao_pinada,
       "Estado"->>4 AS ultima_atualizacao_ticks,
       "Estado"->>5 AS fracao, "Estado"->>6 AS ultima_ocorrencia,
       "Estado"->>7 AS ultima_ordem, "Estado"->>8 AS codigo,
       "Estado"->>11 AS estado, "Estado"->>12 AS confirmacoes_terminal,
       "Estado"->>14 AS candidato_versao, "Estado"->>16 AS candidato_ticks,
       "Estado"->>22 AS cursor_id, "Estado"->>23 AS cursor_ordem,
       "Estado"->>24 AS volta, "Estado"->>25 AS progresso_absoluto,
       "Estado"->>26 AS topologia, "IntegridadeCircular"
FROM "ViagensOperacionais" WHERE "OrdemVeiculo" = 'PREENCHER_VEICULO';
ROLLBACK;
```

```sql
-- Repetir separadamente para versão pinada e observacional, não inferir que são iguais.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
SELECT "Id", "Ordem", "PosicaoTracado", "DistanciaAcumuladaMetros"
FROM "OcorrenciasParadasPadroes"
WHERE "PadraoVersaoId" = 'PREENCHER_UUID_VERSAO'::uuid
ORDER BY "Ordem" LIMIT 1001;
ROLLBACK;
```

O PostgreSQL mostra checkpoint durável, não todo avanço quente. Uma segunda leitura manual e amostras de histórico ajudam, mas não recuperam cada decisão intermediária. Comparar cursor à próxima ordem e fração, terminal e topologia; em circular considerar volta. Integridade tem enum serializado: preservar valor bruto e motivo, não interpretar número sem codec correspondente ao runtime. Não exportar coordenadas/identificadores pessoais para o relatório público.

Para reconciliar os onze, preferir eventos por execução/EventId selecionados e journal da janela via roteiro anterior; registrar tipo, timestamp lógico e criação/processamento quando disponíveis. Não fazer scan global de `OutboxViagens` por criação sem índice adequado; retenção/processamento podem tornar uma fotografia incompleta. O contador de eventos da decisão não pode ser reconstruído exatamente só com linhas deduplicadas da tabela.

## Instrumentação mínima proposta, sem implementação

Primeiro utilizar os contadores existentes. Se persistir a lacuna, acrescentar agregados por ciclo, sem novas consultas SQL, coordenadas ou IDs como labels de métricas:

1. Resultado operacional exclusivo: cada status, nulo por ausência de matching, pendência; separar origem observação corrente/retry.
2. Ramo exclusivo da decisão: normal, baseline com motivo, candidato de mudança, espera pós-finalizada, proteção, recuperação/adoção. Contar entrada e resultado, distinguindo retenção de rejeição.
3. Denominador das transições: avaliadas normais sem baseline; com/sem alvo; com zero/com algum cruzamento; total selecionado. Separar seleção, supressão com motivo e commit.
4. Eventos **após commit por tipo**, diferenciando eventos da decisão de novas linhas Outbox se for necessário reconciliar deduplicação. Passagens selecionadas antes do commit não devem ser chamadas confirmadas.
5. Histórico físico disponível, idade e plausibilidade; avanço/regressão/igualdade projetados somente dentro da mesma identidade/versão/volta, separados de movimento físico. Próximo alvo ausente só no denominador operacional elegível; finalizada não entra como falha de alvo ativa.

Esses agregados fecham o funil sem instrumentar todos os pontos ou expor trajetórias. Projeção já tem resultados agregados: acrescentar apenas adoção/rejeição quando a versão diverge, se os contadores atuais não discriminarem. Instrumentação é proposta para etapa autorizada futura, não alteração realizada nesta auditoria.

## Ordem recomendada

1. Confirmar correspondência imagem/fontes, flag de mudança e TTL efetivos; recuperar linhas completas de performance dos mesmos ciclos. Tipar os onze eventos antes de chamá-los passagens.
2. Selecionar poucos veículos das quatro linhas e controle, incluindo BUS/BRT, com matching aceito. Identificar estado, cursor, próxima ocorrência, versão pinada/observacional e histórico físico. O primeiro conjunto que falhar em cruzamento elegível define a investigação seguinte.
3. Se alvo/estrutura incorretos: revisar a versão e sequência específicas. Se espera/candidato: verificar predecessor/plausibilidade e regra/flag. Se divergência: revisar projeção pinada. Se cruzamento elegível confirmado sem evento: revisar supressão e commit; com evento sem label: revisar worker/materialização.
4. Corrigir apenas causa demonstrada em etapa posterior, com testes relevantes e avaliação de compatibilidade. Não modificar flags/proteções nesta etapa, preencher passagens retroativas nem promover todo `Updated` a label.

O primeiro treino experimental continua dependendo de labels materializados, fronteiras/identidade confiáveis e procedência do exportador pós-GTFS. Volume alto de matching e checkpoint é útil como observação, mas não certifica qualidade/completude de execução. Amostras exploratórias seletivas podem orientar o diagnóstico sem alterar operação pública.

## Evidência no código local

| Fonte | Ponto auditado |
|---|---|
| [GpsPollingService.cs](../../NoPonto/2-Application/Services/GPS/GpsPollingService.cs) | Recuperação inicial, seleção/CAS, escopo de performance, contadores do ciclo, predecessor ativo |
| [GpsCicloPerformance.cs](../../NoPonto/2-Application/Services/GPS/GpsCicloPerformance.cs) | Métodos `RegistrarViagem`, `RegistrarCatchupPassagens`, `RegistrarViagemDurableWrite`, `RegistrarProjecaoOperacional` |
| [ViagemObservadaService.cs](../../NoPonto/2-Application/Services/GPS/ViagemObservadaService.cs) | Nulos, resultados, warnings e divergência |
| [ViagemOperacionalRepository.cs](../../NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs) | Projeção pinada, baseline, catch-up antes do commit, hot CAS, commit e Outbox |
| [OcorrenciaParadaRepository.cs](../../NoPonto/4-Data/Repositories/OcorrenciaParadaRepository.cs) | SQL de validação, seleção linear/circular, cursor/próxima/terminal |
| [ViagemOperacional.cs](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.cs) | `PersistenciaViagemOperacional`, `ViagemOperacionalRegra`, início/passagens/fim/pós-finalizada |
| [ViagemOperacional.Mudanca.cs](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs) e [Integridade](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.Integridade.cs) | Candidato, adoção, proteção e recuperação circular |
| [GpsEnriquecimentoService.cs](../../NoPonto/2-Application/Services/GPS/GpsEnriquecimentoService.cs) e [Batch](../../NoPonto/2-Application/Services/GPS/GpsEnriquecimentoService.Batch.cs) | Matching plausível, rota válida e projeção operacional |
| [ViagemOperacionalCodec.cs](../../NoPonto/4-Data/Repositories/ViagemOperacionalCodec.cs) | Ordem dos campos usados na consulta durável |

## Verificação desta entrega

Revisão estática das fontes, contabilidade, SQL e links locais. Nenhum build, teste unitário/integração, consulta/EXPLAIN real, leitura de produção/Redis, serviço, migration, mudança de flag/contrato/infra, commit ou deploy executado. Runtime da flag/TTL, tipos dos onze eventos e distribuição das decisões permanecem informações a obter manualmente. Documentação e acréscimo no histórico são as únicas alterações desta etapa.
