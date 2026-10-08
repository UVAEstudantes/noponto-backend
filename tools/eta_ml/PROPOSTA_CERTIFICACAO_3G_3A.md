# 3G.3A — proposta de certificação prospectiva ETA/ML

07/10/2026. **Somente investigação e desenho; implementação 3G.3B depende de aprovação.** Nenhuma migration, persistência, instrumentação funcional, coleta, configuração, política3G.2 ou classificação de dados foi alterada. Nenhum banco/Docker/produção/SSH/treino foi executado. Shadow permanece OFF, ETA público independente. Fontes atuais:775GPS/758candidatos técnicos/17sem label/zero certificados; outlier764 individualmente não esclarecido.

## 1. Escopo do atestado

`AuditadaSemProtecao` deve significar: execução operacional inteira, iniciada e encerrada sob um produtor/contrato autorizado, com cobertura demonstrada de **todas as decisões e falhas relevantes** e nenhuma proteção/ambiguidade registrada. Não significa posição física perfeita, chegada observada por sensor, cobertura GPS contínua no mundo real ou passagem interpolada exata. Ausência de evento negativo e último estado confiável são insuficientes.

Três resultados separados: ausência de proteção demonstrada → AuditadaSemProtecao; proteção/ambiguidade positivamente registrada → ProtegidaOuAmbigua; lacuna/contrato desconhecido/execução incompleta → NaoVerificada. Se houver proteção comprovada e também lacunas, retornar ProtegidaOuAmbigua **com completude=false**, sem esconder as lacunas. Nunca converter desconhecido em limpo por recuperação, término, restart ou migration.

## 2. Inventário observado e lacunas

As referências abaixo apontam para o código existente, não implementações propostas.

| Fonte | Garantia existente | Lacuna de certificação |
|---|---|---|
| [ViagemOperacional.cs](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.cs) Decidir/PersistenciaViagemOperacional | início/fim/passagens; semântica/checagem de persistência | decisões com lista de eventos vazia; nenhuma sequência de qualidade ou release por execução |
| [ViagemOperacionalRepository.cs](../../NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs) | PostgreSQL autoritativo; advisory lock por veículo; leitura/CAS de versão; estado+outbox na mesma transação | linha ViagensOperacionais substituída por veículo; sucesso quente pode não escrever PG; falha retorna status/log sem ledger de falhas |
| [ViagemOperacional.Integridade.cs](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.Integridade.cs) | âncora/proteção/recuperação circular persistíveis | entrada em Ambigua e wrap recuperado podem emitir []; registro reinicia/substitui e pode virar null; não guarda toda história |
| [ViagemOperacional.Mudanca.cs](../../NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs) | avaliação de candidato e troca; troca confirmada finaliza antiga/inicia nova | primeira evidência/manutenção/substituição usam []; cancelamento/rejeição podem retornar null para fluxo normal |
| [ViagemOperacionalRedisScript.cs](../../NoPonto/4-Data/Repositories/ViagemOperacionalRedisScript.cs) | CAS/projeção, versão durável, merge quente, TTL24h na projeção | TTL/perda/restart não provam continuidade do intervalo quente; versão durável não conta todas as decisões |
| [ViagemOutboxWorker.cs](../../NoPonto/2-Application/Services/BackgroundServices/ViagemOutboxWorker.cs) | claim SKIP LOCKED, batch<=100, lease2min, materialização+ACK PG atômicos no lote | enum/validator/histórico não aceitam eventos novos sem revisão; outbox processado retido7dias default e apagável, não prova permanente |
| [HistoricoEventoRepository.cs](../../NoPonto/4-Data/Repositories/HistoricoEventoRepository.cs) | journal+passagem transacionais; EventId/payload idempotentes, divergência rejeitada | registro de passagem não caracteriza método/incerteza física; passagem pode ter fallback de timestamp |
| [RetryOperacionalGps.cs](../../NoPonto/2-Application/Services/GPS/RetryOperacionalGps.cs) | predecessor/contexto verificados; retry exige persistência durável; Updated quente não é ACK | pendência Redis pode expirar/perder; encerra por superação/contexto perdido/limites sem evento de qualidade por viagem |
| [GpsPollingService.cs](../../NoPonto/2-Application/Services/GPS/GpsPollingService.cs) ConfirmarPosicaoAsync | mapa e operação separados; matching infra/retry tratados; coleta depois da decisão | mapa aceito não implica operação confirmada; viagem=null, fila/falha de telemetria ou sampling não constituem atestado |
| [TelemetriaMlWorker.cs](../../NoPonto/2-Application/Services/BackgroundServices/TelemetriaMlWorker.cs)/publisher | PG antes de XACK, retry idempotente; fila/stream limitados | não cobre decisões não amostradas; perda/trim/dlq/descarte não dá cobertura temporal por execução |
| HistoricoPassagens/TelemetriasVeiculoMl | associação/identidade/label/feature offline | fontes volumosas e incompletas para reconstruir proteção; nenhuma certificação retrospectiva |

ACKs não são equivalentes: ACK SPPO confirma lote/geração; commit Redis confirma mapa; retry operacional encerra pendência por motivo (inclui descarte, não só sucesso); ProcessadoEmUtc confirma materialização outbox; XACK confirma persistência de telemetria selecionada. Nenhum deles isoladamente prova ausência de proteção.

### Ciclo e perda de confiabilidade sem journal equivalente

| Caminho real | Comportamento | Evidência nova necessária |
|---|---|---|
| Criação Decidir(null) | ViagemIniciada; estado+outbox duráveis | Begin com perfil/owner/contrato e início real observado, nunca minGPS retrospectivo |
| Progresso normal sem passagem/mudança | Redis CommitHot entre checkpoints | cobertura unsampled do caminho de decisão, não um evento por GPS |
| PossivelFim/retorno Ativa/confirmações | estado/contador muda; journal pode conter só passagem ou nada | registrar fase; retorno não implica proteção automaticamente, conforme política aprovada |
| Finalizada | fim após duas confirmações no terminal linear | Close com vínculo exato ao fim, ausência de trabalho pendente e fechamento da cobertura |
| Candidato operacional iniciado/mantido/substituído/cancelado | pode não emitir evento; estado candidato pode desaparecer | latch permanente de candidato/ambiguidade, com causa e cancelamento; retorno não limpa atestado |
| Circular Ambigua | motivo ContagemCircularNaoComprovada, eventos=[] | ProtectEntered no mesmo commit; nunca depender do log Warning |
| Circular recuperada por wrap | pode eventos=[] e integridade resetada | ProtectRecovered não remove proteção histórica; execução antiga continua contaminada |
| Perda circular com nova execução | fim antigo MotivoFim=PerdaContinuidadeCircular + novo início | fechar antiga protegida; nova evidência independente, sem herdar limpo |
| Reancoragem/adoção/baseline/troca de versão | baseline pode suprimir passagens; caminhos incompatíveis mantêm estado/timestamp sem evento novo | classificar reancoragem/adopção/estrutura desconhecida; nunca reconstruir continuidade por estado final |
| Matching inválido/infra, Conflict/InvalidState/InvalidSequence | operação pode não avançar; mapa pode avançar | lacuna pendente vinculada ao owner/viagem possível, não sucesso |
| Retry expirado/superado/contexto perdido | encerra pendência sem certificar execução | Gap/DroppedObservation; retry concluído não apaga lacuna já assumida |
| PG/outbox rollback/resultado de commit incerto | nenhuma prova negativa necessariamente persistida | flag local irreversível de incerteza e regra de restart; não exigir escrever no PG que está indisponível |
| Restart/perda de Redis | perde progresso quente/pendências; PG último checkpoint continua | epoch novo invalida cobertura de execuções abertas antigas, ainda que operação prossiga normalmente |
| Concorrência/release diferente | locks/CAS atuais ajudam na operação, não identificam todos os escritores | fencing/registro de escritor; incompatibilidade→NaoVerificada |

Rejeição de duplicata antiga idêntica não deve, por si só, contaminar viagem: distinguir reentrega comprovadamente igual de observação sem destino/predecessor confiáveis. Evento emitido com fim lógico na mudança operacional não é passagem física.

## 3. Alternativas

| Critério | A: journal/outbox versionado | B: resumo monotônico | C: resumo + journal mínimo |
|---|---|---|---|
| Completude | só transições não bastam; exige Begin/Close/checkpoints/epoch/fencing | flags OR permanentes ajudam, mas resumo sozinho não permite reconstruir intervalos ou provar omissão | checkpoints e sequências reconstruíveis + latches permanentes; independente confronta resumo |
| Atomicidade | inserir qualidade na transação operacional existente | atualizar resumo na mesma transação | ambas na mesma transação, sem dual write assíncrono para eventos críticos |
| Restart/Redis perdido | epoch fecha cobertura desconhecida; outbox recupera eventos existentes | histórico de gaps precisa sobreviver à substituição do veículo | resumo por ViagemId conserva contaminação; eventos explicam; recuperação não lava histórico |
| PG falha | sem evento persistido, exigir dirty local/restart conservador | idem; flag não pode ser gravada durante indisponibilidade | idem, nunca assumir que uma fila RAM resolverá crash |
| Versões | leitores fail-closed para tipos desconhecidos | flags/bitset novos precisam contrato explícito | contrato de evidência independente de EventoViagem schema2 e política3G.2 |
| Consulta | ler todos os eventos selecionados/sequências | PK por execução, barato; audibilidade limitada | PK resumo + índice seletivo de eventos por execução/seq; não scan de telemetria |
| WAL/escritas | eventos+materialização/ACK; checkpoints geram eventos | upsert repetido da linha/head; pode ser menor | head pequeno por commit já existente + eventos por mudança/checkpoint; custo adicional real a medir |
| Falso positivo | alto se tratar ausência negativa como prova | alto se confiar apenas estado limpo/current snapshot | menor se exigir todos os invariantes; continua dependente de instrumentação completa e writer fencing |
| 4GB | viável com limites; não usar histórico ilimitado | mais compacto, mas não escolher sacrificando independência | recomendado conservadoramente, com retenção/benchmark antes de rollout |

Auditoria externa controlada é uma quarta alternativa restrita: responsável/evidências cobrindo viagem inteira em sessão prospectiva, fora da certificação automática. Referência preenchida/manual e resultados de matching não equivalem a prova. Nenhuma dessas opções prova qualidade física de labels sem informações adicionais.

## 4. Recomendação C — mínima que permite verificação independente

Uma linha **por execução**, não por veículo, de resumo durável e monotônico, e journal pequeno de Begin/QualityTransition/Checkpoint/Close reaproveitando o transporte PG outbox. Sem novo broker/serviço/stack de observabilidade. Não guardar todo GPS. Não estender indiscriminadamente EventoViagem schema2: envelope/tipo de qualidade separado com dispatch explícito e validator próprio. Passagens continuam no caminho atual.

Pré-condição: inventário exaustivo dos caminhos acima e todos os retornos de enriquecimento/commit/retry. Instrumentar apenas repository não cobre falhas anteriores à decisão. Inicialmente certificar somente execução iniciada/fechada no **mesmo epoch de produtor autorizado**, sem takeover, perda de Redis, incerteza, gaps ou trabalho pendente. Restart/troca de release descarta certificação da viagem aberta, sem forçar nova viagem operacional nem modificar ETA.

### Continuidade entre transições

Não basta sequência1..N: ela detecta perda depois da produção, não transição omitida pelo produtor. A prova também exige binary/contract allowlisted, caminhos negativos completos, owner exclusivo, testemunho cumulativo de decisões/checkpoints e encerramento sem pendências. O verificador independente não reaproveita a função que produz o resumo: recalcula invariantes/eventos/tempos/gaps, confronta versões e rejeita contradições.

No caminho quente, contador de decisões/último GPS e flags irreversíveis podem acumular em memória **apenas no mesmo owner**, sem escrita PG por GPS. Flush nos commits duráveis que já existem quando houver transição de qualidade ou checkpoint de evidência devido, e no Close; checkpoint de evidência deve conter intervalo coberto, count cumulativo, último predecessor e flags. Mudança apenas da âncora circular não exige upsert/evento de qualidade por GPS. O relógio do checkpoint de evidência precisa ser próprio: no circular, commits semânticos repetidos atualizam o checkpoint operacional e poderiam adiar indefinidamente um checkpoint de qualidade se ambos usassem o mesmo relógio. Falha/ambiguidade observada já exige commit semântico ou bloqueia elegibilidade local antes de qualquer retorno/ACK. Falha sem PG mantém dirty local; próxima transação bem-sucedida grava Gap; crash antes disso é coberto pela invalidação automática do epoch antigo, nunca por recuperação do Redis.

Gap entre checkpoints precisa limite de domínio explícito, baseado em ciclos/GPS/infra e não fixado aqui. **Heartbeat não prova GPS recebido nem ausência de proteção entre dois checkpoints**. Um ciclo sem observação/dados insuficientes fica desconhecido segundo regra aprovada; finalização não interpola essa lacuna. Se não for possível garantir latches em todos os caminhos ou fencing do quente, não aprovar certificação automática; avaliar witness por decisão ou limitar a sessões controladas. Não prometer prova absoluta de tudo que ocorreu fisicamente sem sensores.

### Concorrência/fencing — requisito, não detalhe opcional

Lock PG existente só cobre transação, não intervalo entre checkpoints. Propor writer_epoch/owner_token por veículo/execução com compare-and-swap no head e validação dentro da mesma transação PG, além de contexto/token/contador no CommitHot Redis. Writer antigo/sem token não pode encerrar atestado. Lease/takeover/release conflitante contamina permanentemente a execução; novo owner não assume seu intervalo limpo.

No rollout inicial deve ser demonstrado escritor único, sem binário legado concorrente que ignore o novo token Lua. Tokens cooperativos sozinhos não cercam processo legado. Se não houver barreira de acesso/compatibilidade que impeça escritor não instrumentado (a definir: controle de instâncias e guard de sessão/role/trigger PG), a certificação fica OFF. Nenhuma alteração de credenciais/rede/trigger é autorizada por este documento. Abrir novo epoch em dois processos precisa serialização durável; existência de dois epochs concorrentes não é sinal de funcionamento correto.

## 5. Contratos propostos — não migrations prontas

`eta-trip-evidence-v1` e `eta-trip-certifier-v1` são nomes propostos, ainda não allowlisted. Distância segue eta-route-versioned-3g2-v2. Campos/nomes finais dependem aprovação.

**EvidenciaExecucaoEtaMl (head sugerido):** ViagemId PK; EvidenceContract; ProducerEpoch/OwnerToken; CollectionProfileId; BackendCommit/ImageDigest; ConfigHash; LinhaId/SentidoId/PadraoId/VersaoId/Topologia; InicioGpsUtc/FimGpsUtc; InicioRegistradoUtc/FimRegistradoUtc; LastQualitySeq/LastOperationalVersion; DecisionCount; LastObservedGpsUtc; ProtectionEver/GapEver/UnknownReasonMask por OR; FirstProtectionUtc/FirstGapUtc; Status Open/ClosePending/Closed; ClosureEventId/ExpectedQualitySeq; LastPayloadDigest. Nunca atualizar false depois de true. Não representar defeitos por valor nulo interpretado como false. Uma nova execução tem novo PK, sem reset do registro antigo.

**Envelope de qualidade:** schema, EventId=`qualidade:{ViagemId}:{seq}`; seq contígua de qualidade (não a Versao global por veículo); tipo Begin/Transition/Checkpoint/Close; prev_digest/payload_digest canônico; GPS/effective timestamp quando conhecido + recorded_at UTC; operacional prev/next version; owner/release/profile; before/after quality state; motivo enum versionado; cumulativos e intervalos de cobertura; relações com início/fim/passagens existentes e tasks/observações quando necessárias. Timestamp desconhecido usa null+reason, nunca inventado. recorded_at não substitui timestamp GPS. EventId duplicado/payload igual é idempotente; payload diferente é conflito permanente.

Hash chain verifica encadeamento/integridade, não autentica produtor nem substitui raiz externa de confiança. RecordedAt pode ser posterior a TimestampGps por outbox/retry; permitir apenas causalidade definida, comparar microssegundos de PG sem inventar tolerância de segundos. OperationalVersion por veículo não é sequência de qualidade por viagem e pode incluir outros estados; mapear, não exigir que ambos contadores sejam idênticos.

**Registro de epoch/coleta:** uma pequena fonte durável append-only com epoch, release/contratos/config/política, registration/activation UTC do banco, compatibilidade/escritor autorizado, stop/takeover/revocation. Não tomar commit/migration/date/time local como prova de ativação. Se ativação/revogação não puder ser comprovada, toda execução afetada é NaoVerificada.

**Qualidade do label (eixo independente):** método interpolated_segment/gps_confirmation_fallback/unknown, timestamps e posições predecessora/confirmadora, versão/volta/alvo, motivos, intervalo entre observações. TimestampPassagem hoje retorna GPS quando versão/frações/tempo<=0/>180s/progresso/interseção não permitem interpolação. Igualdade TimestampPassagem==TimestampGps sozinha não identifica método (interpolação no extremo também pode dar igualdade). Uma futura extensão deve registrar método/predecessor **no ato da produção**, sem recalcular label antigo ou adicioná-lo como feature. Unknown não vira accurate; passagem interpolada não vira chegada física certificada. Esta extensão e a política de aceitar/excluir métodos são decisões próprias, não mudança automática dos gates3G.2.

## 6. Pseudofluxo transacional

```text
entrada de decisão/retry sob owner serial por veículo:
  validar epoch, identidade, predecessor, release e task admission
  classificar cada resultado (inclusive falhas, rejeições e viagem desconhecida)
  latchear protection/gap localmente antes de retornar sucesso/erro
  se falha não puder ser associada com certeza: contaminar cobertura do owner;
    atribuição futura só pode negar certificação, nunca provar ausência

se decisão exclusivamente quente e nenhuma mudança crítica:
  CAS Redis com owner/token/contador + flags cumulativas
  token perdido/Redis indisponível => gap; não certificar continuidade pela projeção
  não INSERT PG por GPS

se início, transição crítica, checkpoint já devido ou fim:
  BEGIN PG; advisory lock veículo; ler estado + head sob lock
  validar CAS operacional + owner + versão de contrato + monotonicidade
  gerar seq de qualidade/digests e flag OR (sem limpar)
  gravar estado operacional como hoje
  somente se transição de qualidade/checkpoint devido/Close: atualizar head por ViagemId
    e incluir envelope de qualidade; não escrever head em cada atualização de âncora
  inserir eventos operacionais e, quando devido, envelope de qualidade no MESMO outbox/tx
  COMMIT; então projetar Redis
  rollback/commit incerto => latch dirty; ACK não limpa

worker existente (dispatch proposto):
  materializar journal operacional/passagem ou envelope qualidade
  preservar idempotência e divergência de payload
  materialização + ProcessadoEmUtc atômicos no batch
  fallback de item: manter reentrega idempotente; não confundir ACK isolado com prova

ClosePending:
  prova de fim operacional real, mesmo owner e nenhuma task admitida pendente
  emitir Close com cumulativos/seq final; impedir novas admissões nessa cobertura
Closed certificável offline:
  head/Close/início/fim/journal materializados coerentes, seq completa e perfil ativo
  qualquer dirty/gap/protection permanece; consumidor atrasado não força aprovação
```

Uma mudança que fecha antiga e abre nova deve atualizar ambos heads/eventos na mesma transação já usada pela decisão, conservando motivos. Fechamento lógico por mudança/perda de continuidade não inventa chegada. Timeouts de PG não são corrigidos com write em best-effort RAM. Viagens abertas de epoch anterior são desconhecidas mesmo que reinício tenha zero perda observável; essa exclusão é intencional no MVP conservador.

## 7. Verificador offline e integração

Implementação futura no ML separada do reducer produtor C#. Inputs: bundle seletivo selado, eventos/head/epoch/estruturas e perfil autorizado; versões allowlisted. Ordenação por ViagemId/seq, JSON canônico, hashes, sem dependência da ordem de materialização. Verificar:

1. Begin e Close reais, mesmo epoch/profile ativo; viagem iniciada depois do maior cutoff/activation efetivamente comprovado; intervalo completo, sem atravessar release/takeover/revogação.
2. Contiguidade1..ExpectedQualitySeq, digests, idempotência, envelopes e vínculo operacional; tipos desconhecidos/duplicata conflitante → NaoVerificada.
3. Cobertura cumulativa/temporal, checkpoints/predecessores, tarefas resolvidas, nenhum gap/dirty ou contador inconsistente. Não contar dias de relógio sem evidência.
4. Recalcular flags negativas pelos eventos e confrontar resumo. Recuperação não limpa latch; conflito head/journal → NaoVerificada e alerta.
5. Produzir classificação+reason_codes+complete flag+hashes do input/verificador/profile/políticas, sem features novas, e eixo de método/qualidade do label separado.

Fluxo conceitual: snapshot seletivo inclui heads/quality journal/epochs dos ViagemIds, sem scan de telemetria → verificador independente → somente viagens aprovadas geram audit real do exporter + evidência externa hashada → exporter local READ ONLY/3G.2 → qualify_real confere pacote/relatório/versionamento e gates existentes. As fontes novas alteram contrato de snapshot: propor v3, mantendo v1/v2 históricos identificáveis e incapazes de certificação automática nova. Guard de funções/triggers/FKs deve fechar dependências novas, sem relaxar constraints. Reutilizar EventosViagem pode reduzir tabela extra; decisão depende índices e compatibilidade do worker/validator, não supor que snapshot11tabelas bastará.

qualify_real atual aceita source=durable-protection-ledger mas só valida atestado, flags, arquivos/hashes/intervalos. **Não valida uma prova formal do ledger.** Implementação futura precisa validar relatório determinístico ou recomputar verificação, sem confiar apenas true no pacote. Não ensinar pipeline a inventar evidência para satisfy gate. Não há dataset pronto pelo mero surgimento da nova tabela.

## 8. Custo derivado do código — sem números inventados

Atualmente uma decisão durável abre tx, faz advisory lock/read, grava estado e inclui N eventos em batch outbox; materialização grava journal+passagem quando aplicável e ACK. Progresso linear pode ser só Redis até checkpoint default60s (`GpsPoolingOptions.cs`). Circular: AtualizarAncoraCircular altera o record Integridade, e MudouSemanticamente compara Integridade; com matching confiável pode **já gravar por GPS**. Não prometer redução de escrita circular sem benchmark/mudança autorizada.

Se W=commits operacionais duráveis, Q=commits que realmente exigem atualizar prova, T=transições relevantes, C=checkpoints de cobertura e V=viagens: delta proposto ≈Q upserts pequenos de head (coalescer múltiplas passagens numa decisão) + O(T+C+2V) envelopes outbox/materializados e ACKs. Q não é automaticamente W: commits de atualização de âncora/progresso sem transição de qualidade e sem checkpoint devido não acrescentam escrita da prova. Validação de owner pode acrescentar leitura no commit; seu custo também deve ser medido. Epoch registro é por startup/activation/takeover, não por GPS. Para regime linear sem eventos, C≈veículos ativos×duração/intervalo de checkpoint, **fórmula de planejamento**, não volume observado. Expiração/gap pode exigir um commit crítico adicional por episódio, não por todo GPS rejeitado. Checkpoints com evento aumentam materialização mesmo quando transação PG já existia.

Head de tamanho fixo e hashes/enum/referências limitados, sem listas GPS/erros stacktrace acumuladas. PK(ViagemId), unique quality(ViagemId,seq), índice parcial Open e índices por perfil/fim somente se consulta/benchmark justificar; outbox único existente reaproveitado. Append-only quality events geram WAL/índices; upsert head gera novas versões/dead tuples. Não estimados bytes/s, RSS, TPS, p95, WAL/rota/veículo: não medidos nesta fase documental.

Retenção deve ser definida por janela de treino + janela de auditoria, archive/snapshot antes de remover evidência; ausência por retenção gera NaoVerificada. Outbox7dias atual é transporte, não armazenamento do ledger. Não introduzir retenção infinita nem deletion job nessa etapa. Benchmark3G.3B deve medir delta PG/WAL/RSS/p95/pool/vacuum nos regimes linear/circular/infra/retry, preservar orçamento e ETA público. Sem escrita extra por GPS linear na proposta-base; se witness por decisão for indispensável, voltar para aprovação de custo/escopo antes de implementar.

## 9. Compatibilidade, rollout e rollback propostos

1. Aprovar contrato/semântica/domínio/custo e desenho de fencing; flag de certificação separada OFF, não usar flag Shadow/sampling. Migration futura aditiva, sem backfill de status limpo.
2. Fixtures isoladas + fault injection e verifier independente, todos writers compatíveis ou execução não certificável. Readers antigos de EventoViagem não recebem tipos novos sem dispatch explícito; coexistência só sob prova de compatibilidade.
3. Implantação futura autorizada: registrar epoch/profile realmente ativo com horário do banco, commit/image/config/políticas e prova de escritor único. Catálogo oficial de perfis novo (backend/exporter/ML), preservando perfis históricos; não reutilizar cutoff14:53 ou21:00 para a nova prova.
4. Iniciar certificação apenas para viagens com Begin novo após activation; já abertas continuam NaoVerificada. Virada de release/epoch invalida abertas para certificar, não muda seu ViagemId público.
5. Dry-run de verifier e snapshot/qualificação; sem promoção de modelo, treino ou Shadow. Gates de volume/causalidade continuam.
6. Rollback: desabilitar produção de prova e revogar epoch; execuções abertas/intervalos afetados desconhecidos. Preservar tabelas/provas já fechadas; não executar Down destrutivo nem resetar flags. Reader incompatível nunca aprova; nenhuma recertificação de dados anteriores.

## 10. Matriz de testes para 3G.3B — não executada

| Cenário | Injeção/assert principal | Resultado esperado |
|---|---|---|
| Normal linear/circular confiável | Begin→decisões→checkpoints→fim/Close; validar cálculo geográfico separado | AuditadaSemProtecao só com toda cobertura/materialização; nenhuma certificação física implícita |
| PossivelFim retorna Ativa | evento de fase sem proteção/lacuna inventada; fim único | limpo possível conforme regra de domínio |
| Circular protegida/recuperada | [] na decisão original, novos eventos+head atômicos | ProtegidaOuAmbigua permanente |
| Candidato/cancelamento | inicia/substitui/cancela; não apagar latch | ProtegidaOuAmbigua se política conservadora aprovada |
| Reancoragem/adoção/troca geométrica | baseline sem passagem ou identidade não provada | NaoVerificada ou protegida conforme motivo explícito |
| Redis perdido/TTL/restart | perder progresso/pendência em qualquer posição | NaoVerificada na antiga, nunca herdar limpo |
| PG falha/rollback antes e após head/outbox | fault hooks e commit uncertain | nada parcial publicado; dirty na retomada; crash invalida epoch aberto |
| Outbox reentrega/lease perdido/fallback | crash antes/depois materialização/ACK | idempotência; pending não autoriza fechamento; seq independentemente da ordem de consumo |
| Execução incompleta | início ou fim/Close ausentes | NaoVerificada |
| Sequência/hashes | remover evento, adulterar contador, duplicar seq/payload conflitante | NaoVerificada; duplicata exatamente igual não cria proteção fictícia |
| Timestamps | regressão GPS, timezone ausente, recorded anterior impossível, predecessor incompatível | NaoVerificada; sem inferir continuidade por timestamp final |
| Release concorrente/atualização | dois writers/token antigo/takeover sem restart | NaoVerificada; CAS não transfere limpo |
| Retry expirado/superado/recalculado | ACK de descarte vs commit confirmado | motivo registrado; desconhecido não se limpa |
| Label interpolado/fallback/desconhecido | mesmo timestamp no extremo vs fallback | método explícito, sem certificado físico por igualdade |
| Sampling0/10/100 e fila telemetria cheia | mesmas decisões de proteção com telemetrias distintas | prova operacional independente do sampling; cobertura dataset/labels separada |
| Versão antiga/evento desconhecido/retention | remover fonte ou mudar schema/política | NaoVerificada |
| Carga/memória/indices/retention | múltiplos veículos/decisões, bounded reasons/erros | custos medidos, ETA/polling públicos preservados |

Propriedades testáveis: OR nunca volta false; remover/trocar qualquer prova necessária nunca melhora classificação; reorder materializado não altera resultado; mesmo input/versão dá bytes/hash iguais; recuperação não lava passado; todos os caminhos desconhecidos impedem aprovação. Revisar também produtor deliberadamente incompleto: verificador detecta contradições/gaps, mas não autentica omissão perfeita de um binário não confiável; fencing/allowlist são pré-condições explícitas.

## 11. Arquivos/alterações futuras exatas por responsabilidade

Backend proposto: novo `NoPonto/2-Application/Services/GPS/EvidenciaExecucaoEtaMl.cs` (reducer/contratos puros); `ViagemOperacional.cs/.Mudanca.cs/.Integridade.cs` (mapa de transições sem mudar decisões); `ViagemOperacionalRepository.cs` (head+envelope na tx existente, owner/CAS); `ViagemOperacionalRedisScript.cs/Codec.cs` (token/witness compatíveis); `GpsPollingService.cs/RetryOperacionalGps.cs` (latches de falhas antes da operação e pendências); `ViagemOutboxWorker.cs/HistoricoEventoRepository.cs` (dispatch/materialização qualidade separados); context/entities e **migration aditiva a definir após contrato aprovado**; testes puros/integrados exclusivos correspondentes. Método de label futuramente exige metadados no EventoViagem e materializador sem modificar label/features; aprovação separada.

Ferramentas backend: `snapshot.py/snapshot_schema.py` e reviewed dependencies/guards (v3 seletivo); `tools/EtaMl.Export/Program.cs` (referência ao resultado offline verdadeiro, sem remover AuditadaSemProtecao); catálogo `tools/collection_profiles.json` após activationreal; testes/docs/histórico. SQL3A/EtaDataset política3G.2 permanecem; qualquer nova regra de qualidade física exige etapa própria.

ML proposto: novo `tools/eta_ml/verify_trip_evidence.py` e testes independentes/fixtures compartilhadas de contrato; `qualify_real.py` valida resultados/evidência reproduzível; catálogo de coleta compatível e novos exemplos de evidência; receipt/versionamento quando necessário, sem reduzir volume. Pipeline/features/modelo/serviço HTTP/Shadow não precisam redesign nem treino nesta etapa. Atualizar revisionmanifests apenas se arquivos originalmente transferidos forem efetivamente revisados, preservando originais.

## 12. Decisões de domínio e critérios de aprovação

Antes de implementar, aprovar explicitamente:

1. Escopo operacional do atestado, distinto de qualidade física; execução com interpolação pode receber atestado operacional, sem permitir claims físicos? Quais métodos de label serão aceitos numa política futura?
2. Candidato cancelado, reancoragem, adoção e mudança de versão contam proteção ou desconhecido? Recomendação inicial conservadora: qualquer candidato operacional compromete a viagem antiga; recuperação não limpa; nova execução começa prova própria.
3. Limite/semântica de gap de observação/ciclo/checkpoint e tratamento de indisponibilidade da fonte. Nenhum valor proposto como aprovado nesta fase.
4. Exclusão de toda viagem atravessando restart/takeover/release/Redis perdido; política inicial recomendada mesmo com recuperação operacional bem-sucedida.
5. Contrato de owner/fencing e barreira a writers antigos; sem isso não habilitar certificação. Aprovar impacto no Lua/codec e eventual guard PG, migration/snapshotv3, retenção/quotas/custos antes de execução.
6. Quem ativa/revoga perfil, valida atestado externo/raiz de confiança e aprova uso offline? Datas/tokens/senhas não fazem parte deste documento.

Aprovação para 3G.3B exige: desenho fechado de todos caminhos/fencing/latches, defaults OFF, testes de falha passando sem falso positivo desconhecido→limpo, medição de custo/capacidade compatível com4GB e plano de rollout/rollback prospectivo auditável. Aprovação da proposta não autoriza executar migrations/deploy/produção nem treinar dados antigos. A aprovação funcional final dependerá das medições, não da estimativa textual.

**Entrega desta rodada:** este documento e acréscimo em HISTORICO, somente. Verificação documental/diff/whitespace; testes funcionais, build, Docker/PG não executados por não haver implementação. Parar e aguardar aprovação da proposta antes de implementar3G.3B.
