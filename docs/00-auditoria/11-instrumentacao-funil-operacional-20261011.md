# Etapa 4A — Instrumentação do funil operacional pós-matching

Data: 2026-10-11. Continuação das auditorias [09](09-passagens-pos-gtfsrt-20261011.md) e [10](10-avanco-operacional-pos-matching-20261011.md). Implementação de observabilidade e testes locais; nenhuma correção operacional, mudança de configuração, contrato público/persistido, endpoint, infraestrutura, migration ou deploy.

## Evidência atual e objetivo

Informações fornecidas pelo operador, sem acesso a produção: enriquecimento universal, polling 15 segundos/BRT 20 segundos, TTL ativo 40 segundos/recente e linha 180 segundos e aquisição GTFS-RT configurada em 30 segundos. TTL ativo passa de desconhecido na auditoria anterior a **40 segundos observado**; efeito causal ainda não demonstrado. `MudancaOperacionalHabilitada` continua com valor efetivo desconhecido.

Os 20 ciclos disponíveis somam 8.196 GPS novos, 7.833 enriquecidos/resultados processados, 642 escritas duráveis (632 checkpoints/10 semânticas), 11 eventos Outbox e 11 ocorrências no catch-up. Journal de outro recorte informado apresentou 23 passagens em dez minutos, em A63502/167 e D33169/798, inclusive múltiplos cruzamentos pelo mesmo GPS. Sem janela/correlação idêntica, não reconciliar 23 com 11 automaticamente.

A2-103/868 em fração 0,4482046, cursor 19 e próxima ocorrência 20 em 0,4746952 é um caso coerente sem passagem esperada naquele momento. A2-107/A2-110 em `PossivelFim`, terminal 39, uma confirmação e observações antigas não provam defeito ou fim físico por expiração. As métricas devem discriminar essas situações, sem alterar regras para gerar mais eventos.

## Inventário e implantação local

Reutilizado `GpsCicloPerformance`, já criado por ciclo e concorrente. A extensão [GpsCicloPerformance.Operacional.cs](../../NoPonto/2-Application/Services/GPS/GpsCicloPerformance.Operacional.cs) pertence à **mesma classe parcial**, com arrays de tamanho fixo para índices definidos por enum. Não guarda observações, veículos ou trajetórias. Não há registrador/middleware de métricas paralelo, nova dependência ou persistência.

| Componente | Observabilidade anterior preservada | Extensão |
|---|---|---|
| Polling | Dedupe, matching, CAS, chamadas/processadas, performance | Predecessor após CAS aceito; categoria pendência; sufixo no mesmo log |
| Serviço observado | Resultado, warnings, divergência | Distribuição exclusiva de resultado, inclusive nulo sem versão/fração |
| Regra operacional | Decisão de estado e lista de eventos | Classificação observacional da decisão devolvida, transição normal e eventos selecionados |
| Mudança/integridade | Candidato, proteção, recuperação, eventos existentes | Uma decisão externa; chamadas internas de início não duplicam contagem |
| Repositório operacional | Hot CAS, checkpoint, commit, Outbox/catch-up | Projeção pinada adotada e eventos tipados imediatamente após `CommitAsync` |
| Retry | Claim/backoff/lease/revalidação | Origem retry, claims, claims sem chamada ao serviço e predecessor reenriquecido |
| Matching/enriquecimento | Diagnóstico global/direcionado, projeção solicitada/encontrada/inelegível/falha | Reutilização dos dados já produzidos, sem novas consultas |
| Ocorrências | SQL de sequência/cursor/cruzamentos/próxima/terminal | Usa a transição já existente; SQL preservado |
| Outbox/journal | Worker já mede claimed/processed/inserts/ACK/falhas | Preservado; commit tipado não significa materialização |

`ViagemOperacionalRegra.Decidir` e `AplicarAvaliacaoMudanca` envolvem seus corpos anteriores com observação do resultado. Retorno, estado e eventos são os mesmos. Chamadas recursivas de início usam o corpo interno, para registrar somente a decisão externa de recuperação/mudança. Nenhum novo caminho de commit ou decisão foi criado.

O contexto assíncrono existente foi estendido com escopo diagnóstico de origem. O retry inicial é observado pelo agregador criado antes dele, mas permanece fora do `Current` legado. Stopwatch e ordem das operações anteriores foram preservados. Retry antes do CAS individual continua dentro do escopo legado quando chamado pelo lote. No fluxo paralelo, cada tarefa herda seu contexto; alteração temporária da origem retry é restaurada antes da observação nova.

## Nomes, denominadores e limitações

Todos os campos são emitidos, inclusive zeros, no sufixo textual `funil_operacional` da linha `Performance GPS`. As propriedades anteriores dessa linha mantêm nomes e significado. Formato fixo:

- `op_nova_resultado_<Categoria>` / `op_retry_resultado_<Categoria>`.
- `op_nova_<Contador>` / `op_retry_<Contador>`.
- `op_nova_bus_predecessor_<campo>` e equivalentes `brt`/`outro`, também com origem `retry`.

O uso de maiúsculas nos nomes abaixo é literal. Não há labels por veículo, linha, versão, UUID ou coordenadas. BUS aceita `ModalFonte=ONIBUS` (incluindo GTFS-RT) ou `BUS`; BRT aceita `BRT`; demais/ausentes ficam em `outro`.

### Resultados exclusivos

| Categoria após `resultado_` | Denominador/interpretação |
|---|---|
| `Created` | Resultado do serviço: linha durável anterior ausente, não necessariamente toda execução nova |
| `Updated` | Resultado atualizado: pode ser quente, checkpoint, evento ou estado mantido; não comprova cursor |
| `ItineraryChanged` | Identidade divergente/rejeitada, não mudança operacional aplicada |
| `RejectedOlderOrEqual` | Timestamp operacional não promovido |
| `InvalidState` | Entrada/estado inválido |
| `InfrastructureFailure` | Resultado de falha; pode ocorrer até após commit durável em falha posterior, sem desfazer commit |
| `Conflict` | Contexto/CAS/versão conflitante |
| `InvalidSequence` | Estrutura/sequência inválida ou ausente, conforme resultado do repositório |
| `OccurrenceNotFromItinerary` | Cursor não pertencente à cadeia |
| `NuloSemMatching` | Serviço retornou nulo por versão ou fração ausente; não identifica sozinho a causa do matching |
| `NaoProcessadoPendencia` | Nova observação com CAS aceito, operação retida por backlog; serviço não chamado |
| `Outro` | Reserva para status desconhecido; não usado pelos enums atuais |

Nas observações novas, soma das categorias = GPS aceitos pelo CAS que chegaram ao desfecho operacional; em ciclo completo, corresponde a `viagem_chamadas`. Nulos são excluídos de `viagem_processadas` legado. Em interrupção/exceção externa antes do desfecho, a igualdade pode não fechar. GPS que falhou CAS está fora desse denominador, ainda visível nas métricas anteriores de commit.

Retry: distribuição mede cada chamada efetiva ao serviço, não cada veículo com pendência ou tentativa de adquirir lease. `RetryClaim` inclui claims que não chegam ao serviço; `RetryNaoProcessado` completa esse denominador. Repetir a mesma observação em tentativas distintas é uma nova tentativa, não novo GPS. Claims inexistentes/budget esgotado não são chamados resultados rejeitados.

### Decisões e condições complementares

| Contador | Denominador e limitação |
|---|---|
| `Decisoes` | Decisões externas devolvidas pela regra ou aplicação de mudança; antes de validar/persistir |
| `Normal` | Continuidade da mesma identidade confiável sem adoção/candidato/proteção; não garante commit |
| `BaselineNova` | Regra nasceu sem estado anterior; não inclui início interno de recuperação/mudança |
| `BaselineAposFinalizada` | Estado anterior finalizado e nova execução confirmada |
| `BaselineMudanca` | Mudança confirmada que devolveu fim/início sobre baseline |
| `AdocaoCandidato` | Adoção solicitada ou candidato anterior no caminho de cancelamento; inclui cancelamento sem emissão |
| `CandidatoEspera` | Aplicação de primeira evidência/manutenção/substituição de candidato de mudança |
| `ProtecaoCircular` | Estado anterior ambíguo permanece protegido ou decisão tornou continuidade ambígua |
| `Recuperacao` | Proteção tornou-se confiável ou candidato circular recuperou volta; não implica recuperar passagens antigas |
| `EsperaFinalizada` | Finalizada permanece na mesma execução, com/sem candidato; ausência de alvo não contada como falha |
| `IdentidadeRetida` | Regra devolveu decisão com identidade estrutural divergente da anterior; rejeições anteriores à regra estão nos resultados |
| `EstadoAnteriorAtiva`, `EstadoAnteriorPossivelFim`, `EstadoAnteriorFinalizada` | Condições complementares por estado anterior nas decisões; não são distribuição do estado final |
| `ProjecaoPinadaAdotada` | Repositório consumiu projeção da versão anterior; anterior ao commit. Reutilizar também `projecao_operacional_*` |
| `RetryClaim` | Pendência efetivamente obtida por lease, inclusive descarte/ACK durável reconhecido |
| `RetryNaoProcessado` | Claim que terminou sem chamada ao serviço: guardas, ausência de plausibilidade, falha anterior etc.; não é duplicado como resultado nulo |

Os dez ramos `Normal` até `IdentidadeRetida` são exclusivos por decisão e somam `Decisoes`. Classificação é uma leitura dos estados/identidade/candidato antes/depois; não muda a regra. Rejeição antes da decisão não recebe ramo inventado. Contadores de estado/projeção/retry coexistem e não devem ser somados como ramos.

### Movimento e cruzamentos

| Contador | Denominador e limitação |
|---|---|
| `NormalTransicoes` | Transições do ramo normal, incluindo wrap circular; não inclui baselines/proteção/candidato/finalizada anterior |
| `NormalAvaliada` | Subconjunto normal com comparação de fração/cursor compatível: mesmo padrão/versão/topologia/volta, sem wrap, comprimento positivo e cursor consistente |
| `ComparacaoIncompativel` | Normal excluída da comparação geométrica/cursor; wrap legítimo também entra aqui e não é chamado de erro |
| `ProjecaoMaior`, `ProjecaoIgual`, `ProjecaoMenor` | Particionam `NormalAvaliada`; comparação exata da fração armazenada. Crescimento não comprova movimento físico |
| `CursorAvancou`, `CursorPermaneceu`, `CursorIncompativel` | Particionam `NormalAvaliada`; avanço de ordem, identidade/ordem estável ou resultado restante. Não compara voltas diferentes |
| `ComAlvo`, `SemAlvo` | Particionam `NormalTransicoes` pela próxima ocorrência da transição; sem alvo pode ser terminal legítimo ou cadeia vazia |
| `SemAlvoAtiva`, `SemAlvoPossivelFim` | Particionam `SemAlvo` por estado anterior; terminal em `PossivelFim` não é falha automática |
| `ZeroCruzamentos`, `ComCruzamentos` | Particionam `NormalTransicoes`: zero ou pelo menos uma ocorrência na transição |
| `OcorrenciasSelecionadas` | Soma das ocorrências nas transições normais; pode exceder observações e inclui wrap. Não significa persistência |
| `OcorrenciasSuprimidasMensuraveis` | Ocorrências realmente presentes na transição com adoção solicitada; não estima passado incorporado por baseline |

Identidade/versão estrutural pinada refere-se à geometria imutável. Não comparar a fração observacional de outra versão à anterior pinada. Cursor inconsistente ou geometria diferente exclui comparação, sem introduzir rejeição. Há duas populações deliberadas: seleção de ocorrências normais inclui wrap; comparação de frações exige a mesma volta. `NormalAvaliada + ComparacaoIncompativel = NormalTransicoes`.

SQL de baseline retorna cursor e lista de cruzamentos vazia. Não medir “suprimidas” subtraindo ordens: ordem não é quantidade de ocorrências, e baseline não fornece os cruzamentos contrafactuais. Esse passado permanece **não mensurável** sem consulta adicional/refatoração, excluídas nesta etapa. Não há prova adicional de movimento físico calculada; há somente existência do predecessor e flag de plausibilidade já produzida.

### Eventos por estágio

| Contador | Significado |
|---|---|
| `SelecionadoPassagem`, `SelecionadoInicio`, `SelecionadoFim`, `SelecionadoOutro` | Eventos na decisão externa devolvida, antes de validação/commit; podem falhar posteriormente |
| `CommitPassagem`, `CommitInicio`, `CommitFim`, `CommitOutro` | Mesmos tipos na lista incluída em transação com `CommitAsync` bem-sucedido |
| `CommitEventosForaEscopoLegado` | Quantidade de eventos confirmados quando esse agregador não era `Current` legado; inclui retry inicial |

Tipos atuais: `PassagemParada`, `ViagemIniciada`, `ViagemFinalizada`. `Outro` reserva tipos futuros. Não mede INSERT novo físico: `EventId` pode deduplicar no Outbox. Não mede journal, histórico ou ACK. Resultado `InfrastructureFailure` posterior ao commit não zera evento confirmado. Falha/resultado ambíguo no próprio `CommitAsync` não permite afirmar confirmação observada.

`viagem_outbox_events` e `catchup_passagens_total` anteriores foram preservados: primeiro soma eventos de decisões commitadas em seu escopo; segundo soma cruzamentos selecionados antes de commit num ramo específico, inclusive seleção unitária. Para ciclo completo/repositório instrumentado, soma dos quatro `Commit*` nas duas origens menos `CommitEventosForaEscopoLegado` reconcilia com o contador Outbox anterior. Caso log parcial/abortado ou outro produtor, não impor igualdade.

### Predecessor por modal e origem

| Campo após `predecessor_` | Denominador e limitação |
|---|---|
| `avaliados` | Novas: GPS aceito no CAS. Retry: reenriquecimento devolvido, antes das guardas seguintes; não todos os claims |
| `valido` | Histórico DTO presente, coordenadas atual/anterior válidas e timestamp anterior menor; não prova deslocamento nem ausência de salto |
| `ausente_invalido` | Complemento de `valido`; ausência e histórico inválido agrupados para limitar categorias |
| `idade_ate40s` | Delta GPS anterior→atual positivo até 40 segundos, somente predecessor válido |
| `idade_40a60s` | Delta >40 e <=60 segundos |
| `idade_60a180s` | Delta >60 e <=180 segundos |
| `idade_gt180s` | Delta >180 segundos |
| `plausivel`, `nao_plausivel` | Flag `MatchingOperacionalPlausivel` disponível/indisponível no DTO; não recalcula matching ou salto |

`valido + ausente_invalido = avaliados`; soma das quatro faixas = `valido`; plausível + não plausível = avaliados. Idade é delta entre timestamps GPS, não TTL restante Redis ou tempo de recepção. Modal/origem têm categorias fixas. Nenhuma leitura Redis extra foi adicionada.

## Exemplo sintético da linha de log

**SINTÉTICO, não coletado em produção e não benchmark.** Exemplo de seis GPS com CAS aceito: um nascimento, quatro atualizações normais e uma divergência; dois GPS cruzam três ocorrências ao todo. Três commits semânticos (início e dois conjuntos de passagens), um checkpoint e uma atualização quente. Nenhum retry. Trecho da linha com campos relevantes; a implementação emite também todos os outros campos legados e os campos novos zerados.

```text
Performance GPS: inicio=2026-10-11T15:00:00.0000000+00:00 fim=2026-10-11T15:00:01.0000000+00:00 total_ms=1000 viagem_chamadas=6 viagem_processadas=6 viagem_concluidas=6 viagem_durable_writes=4 viagem_checkpoint_writes=1 viagem_semantic_writes=3 viagem_skipped_durable_writes=1 viagem_outbox_events=4 catchup_passagens_total=3 itinerary_changed_ocorrencias=1 funil_operacional=op_nova_resultado_Created=1 op_nova_resultado_Updated=4 op_nova_resultado_ItineraryChanged=1 op_nova_Decisoes=5 op_nova_Normal=4 op_nova_BaselineNova=1 op_nova_NormalTransicoes=4 op_nova_NormalAvaliada=4 op_nova_ProjecaoMaior=3 op_nova_ProjecaoIgual=1 op_nova_CursorAvancou=2 op_nova_CursorPermaneceu=2 op_nova_ComAlvo=3 op_nova_SemAlvo=1 op_nova_SemAlvoAtiva=1 op_nova_ZeroCruzamentos=2 op_nova_ComCruzamentos=2 op_nova_OcorrenciasSelecionadas=3 op_nova_SelecionadoPassagem=3 op_nova_SelecionadoInicio=1 op_nova_CommitPassagem=3 op_nova_CommitInicio=1 op_retry_RetryClaim=0 op_nova_bus_predecessor_avaliados=4 op_nova_bus_predecessor_valido=3 op_nova_bus_predecessor_ausente_invalido=1 op_nova_bus_predecessor_idade_ate40s=2 op_nova_bus_predecessor_idade_40a60s=1 op_nova_bus_predecessor_plausivel=3 op_nova_bus_predecessor_nao_plausivel=1 op_nova_brt_predecessor_avaliados=2 op_nova_brt_predecessor_valido=1 op_nova_brt_predecessor_ausente_invalido=1 op_nova_brt_predecessor_idade_60a180s=1 op_nova_brt_predecessor_plausivel=1 op_nova_brt_predecessor_nao_plausivel=1
```

O exemplo permite concluir três passagens na lista de commits, sem concluir três INSERTs novos ou labels materializados. Os dois movimentos normais sem cruzamento são visíveis em `ZeroCruzamentos`; nenhum total divide por veículos distintos.

## Custo e limites de capacidade

Estado adicional fixo: três arrays de `long`, por ciclo, para duas origens, resultados/ramos e três modais. Ordem de grandeza de poucos KiB incluindo cabeçalhos; não cresce com número de observações. Incrementos usam `Interlocked`, leituras `Volatile`, sem locks globais. Não há lista/array de GPS de diagnóstico, I/O adicional, SELECT, write, geocálculo novo, payload bruto ou logging individual novo.

Por observação, verificações escalares e incrementos; lista de eventos já existente é percorrida para tipar seleção e commit. Escopos `AsyncLocal` têm pequenas alocações limitadas pela chamada e são restaurados. Formatação em `StringBuilder` ocorre uma vez ao fechar o ciclo e adiciona campos fixos à linha já longa. Há custo de CPU, alocação do texto e bytes de log; o tamanho/buffer/limite de truncamento do coletor deve ser conferido antes da medição. Não foi medido throughput, RAM ou benchmark; não alegar overhead percentual.

## Testes e resultados

Testes usam a regra/serviço/polling/retry produtivos com dependências externas controladas; não se limitam a chamar incrementadores artificiais. Validam também nomes/valores do texto emitido pelo formatador do log.

- [GpsFunilOperacionalTests](../../NoPonto/5-Testes/GpsFunilOperacionalTests.cs): movimento maior/igual/menor sem cruzamento, uma/várias paradas, baseline, estado final/possível fim, cursor/geometria incompatíveis, wrap, adoção, tipos selecionados, exclusividade dos nove resultados, nulo, concorrência e isolamento de ciclos.
- [ViagemObservadaServiceTests](../../NoPonto/5-Testes/ViagemObservadaServiceTests.cs): polling real com BUS/ONIBUS/BRT/outro, predecessor presente/ausente, pendência e recuperação sem duplicação na população nova.
- [RetryOperacionalGpsTests](../../NoPonto/5-Testes/RetryOperacionalGpsTests.cs): retry inicial fora dos contadores antigos, repetição após ACK e claim descartado sem chamada de viagem.
- [IntegridadeCircularRegraTests](../../NoPonto/5-Testes/IntegridadeCircularRegraTests.cs): proteção/reancoragem e recuperação de volta, sem contagem dupla do início interno.
- [ViagemDuravelPostgresTests](../../NoPonto/5-Testes/ViagemDuravelPostgresTests.cs): preparados `Funil_CommitRealSeparaInicioPassagensEFim` e duas variantes `Funil_FalhaAntesCommitNaoConfirmaPassagens`, com fault injection antes/depois de inserir Outbox. **Não executados**: exigem serviços descartáveis. A fixture existente cria schema e executa migrations; não usar conexão de banco operacional ou existente nesta etapa. Nenhuma fixture de integração foi iniciada.

Primeira bateria: 111 aprovados e uma falha em `ViagemOperacionalRegraTests.Modelo_SnapshotSincronizado`. Reprodução seletiva mostrou `AddColumnOperation:Cor:FormasPagamento; AddColumnOperation:Icone:FormasPagamento`, correspondentes à alteração preexistente em `FormaPagamento.cs`, preservada. Não corrigida via schema/migration. Durante preparação, o compilador apontou argumentos enum de `InlineData` incompatíveis com parâmetro `int`; corrigidos com casts explícitos antes da validação final.

Bateria final com esse teste de snapshot excluído: **199 aprovados, zero falhas, zero ignorados**, duração reportada da execução de testes de 1 segundo (não inclui compilação). Cada bateria concluída em menos de cinco minutos. Build/test executados sem restore/download; nenhum servidor externo iniciado. Avisos anteriores de migration com nome minúsculo, entrypoint e assertions xUnit permaneceram fora do escopo.

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~GpsFunilOperacionalTests|FullyQualifiedName~GpsPerformanceMetricsTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalIntegracaoLogicaTests)&FullyQualifiedName!~Modelo_SnapshotSincronizado' --verbosity minimal
```

Não executar a suíte global: há fixtures dependentes de PostgreSQL/PostGIS/Redis e migrations. Validação de INSERT/rollback e materialização permanece pendente em infraestrutura inteiramente descartável; seleção tipada e ausência de contabilização confirmada antes de commit foram verificadas localmente no fluxo de regras.

## Plano manual de medição: 10–15 minutos

Não executado e não autoriza deploy. Após validação/release autorizada em etapa separada:

1. Identificar imagem/commit e valores efetivos das flags, inclusive mudança operacional; manter TTL 40, polling/sources/sampling existentes. Escolher janela UTC contínua e registrar início/fim, versão de aplicação e número de ciclos completos/incompletos.
2. Capturar linhas completas de `Performance GPS` por 10–15 minutos. Verificar campos finais/truncamento; não usar `--tail` que corte a janela inteira. Guardar só logs operacionais necessários, sem dump de ambiente/segredos. Exemplo seletivo manual, com placeholders:

   ```powershell
   $funilContainer = 'PREENCHER_CONTAINER'
   $funilInicio = 'PREENCHER_ISO_UTC'
   $funilFim = 'PREENCHER_ISO_UTC'
   docker logs --since $funilInicio --until $funilFim $funilContainer 2>&1 |
     Select-String -Pattern 'Performance GPS:|Outbox|GTFSRT GPS'
   ```

3. Somar por origem as categorias de resultado e decisões; validar partições descritas. Separar retry de nova observação. Comparar predecessor ausente/plausibilidade e faixas de idade entre BUS/BRT usando denominador `predecessor_avaliados` de cada modal, nunca volume bruto como taxa.
4. Para continuidade normal, calcular zero/com cruzamento por `NormalTransicoes`; frações/cursor por `NormalAvaliada`. Inspecionar `SemAlvoAtiva` e `SemAlvoPossivelFim` com estado/terminal; não chamar todo sem alvo de falha. Se candidatos/proteção/espera dominarem, analisar esses gates antes do detector.
5. Comparar selecionados e commitados **por tipo e origem**, reconciliando eventos fora do escopo legado. Consultar somente casos seletivos/journal/backlog via [roteiro read-only anterior](09-passagens-pos-gtfsrt-20261011.sql), com janela explícita, índices/timeouts e consideração de timestamp lógico, retry e deduplicação. Contadores de journal podem variar por interpolação/latência e recortes.
6. BUS/BRT: resultados/decisões/cruzamentos são por origem, não duplicados por modal nesta etapa. A comparação direta entre modais é precisa para predecessor/plausibilidade; para os demais estágios utilizar inspeção seletiva de versões/veículos já autorizada e origem do feed, sem inventar partições ausentes. Duplicar todas as dimensões do funil por modal foi evitado para conter custo/log.
7. Monitorar duração/excesso do ciclo, novas falhas e tamanho do log em relação à janela anterior comparável. Não atribuir diferença de oferta, cadência ou matching a uma causa isolada. Encerrar coleta após a janela, sem alterar flags/TTL ou relaxar proteções.

Primeiro investigar a categoria quantitativamente dominante: normal sem cruzamento legítimo, sem alvo em ativa, identidade divergente, espera/candidato/proteção ou selecionado sem commit. Se houver commit de passagem sem materialização, avançar para worker/journal. Não escolher correção antes dessa separação. Dataset ETA continua exigindo fronteiras/identidade e procedência confiáveis; estes agregados não certificam execução completa ou elegibilidade para treino.
