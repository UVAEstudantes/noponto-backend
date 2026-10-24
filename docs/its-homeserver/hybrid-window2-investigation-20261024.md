# Investigação da segunda janela híbrida — 24/10/2026

Investigação local sobre75c9932. Produção não foi acessada, reconfigurada ou testada; nenhum push/merge/deploy/cron. Preservados os laboratórios e artefatos preexistentes. Último estado produtivo conhecido permanece release149 em GTFSRT_BUS/fallback vazio/FullFallback e GTFSRT_BRT. Nenhum novo feed real foi consultado.

## Conclusão e limites da evidência

A latência dominante está **dentro da aquisição ITS**, mas o componente responsável pelos48,554s históricos permanece aberto: não foram arquivados tempos de página, HTTP/body/JSON/normalização ou rejeições por motivo. Implementada a medição necessária, sem mudar a sequência ITS→GTFS, os intervalos HTTP, budgets, fallback, TTLs, filtros, matching ou decisões CAS. A deficiência de observação da898 está comprovada; não há prova histórica de falha de identidade, CAS ou de ausência de envio no SignalR.

| Medida histórica | Lote1 | Lote2 | Interpretação |
|---|---:|---:|---|
| ITS aquisição |8,235s|48,554s|Completas/Sucesso; não eram amostras truncadas|
| Páginas / janelas UTC |7/3|8/3|Sem tempos individuais arquivados|
| Registros brutos |24934|32488|Soma das páginas completas; não veículos únicos|
| Rejeições na normalização |1855|10082|Não inclui deduplicação normal nem expiração final|
| Registros válidos antes de deduplicar veículos |23079|22406|Caíram673 apesar de7554registros brutos adicionais|
| Veículos ITS finais |2374|2375|Cobertura quase igual, fornecedores escolhidos mudaram|
| ZIRIX / CONECTA escolhidos |789/1585|1572/803|Maior GPS físico prevalece; empate determinístico preservado|
| Idade ITS p50/p90 ao concluir |34,0/48,0s|63,4/78,4s|Segundo lote já chega mais antigo à composição|
| Composição total |8,928s|49,211s|Inclui ITS e suporte sequenciais|
| Resto após ITS |0,693s|0,658s|Não há evidência de49s gastos na leitura GTFS|

A fonte fixa três minutos a partir do UTC corrente após obter seu lock e consulta cada janela/página em série. A partir dos horários finais/durações, ambos os inícios são compatíveis com21:20UTC (primeiro≈21:20:19,8; segundo≈21:20:48,8). Assim, uma troca automática para outra faixa de três minutos não é explicação demonstrada; os anchors de cada requisição não foram arquivados para confirmação absoluta. O conjunto servido pode evoluir/backfill e as páginas podem trazer observações repetidas entre janelas; o histórico não permite separar essas possibilidades. O cliente só soma records depois de providers ok; corpos pending descartados não entram nesse total.

Sob os defaults versionados (1s mínimo entre inícios de GET,10s por request,90s por aquisição,5000registros/página,3janelas), oito respostas e32488registros exigem pelo menos sete páginas completas: caberia no máximo uma resposta pending descartada, com2s de espera explícita. Sem contenção adicional, o intervalo mínimo causaria no máximo cerca7s, não explica sozinho48,6s. Os parâmetros efetivos completos não foram arquivados; estes são limites condicionados aos defaults, não medição histórica do servidor. HTTP longo, leitura/parsing/normalização e agendamento continuam hipóteses abertas.

**Retry-After HTTP429/503 não é um retry interno silencioso de uma aquisição Sucesso.** O código encerra a aquisição em falha/parcial e bloqueia GET futuros até o prazo. Portanto não atribuir o segundo Sucesso a uma espera interna por esse Retry-After. Provider pending, intervalo mínimo e contenção são mecanismos diferentes; agora são medidos separadamente. Nenhum deles foi desativado.

As rejeições antigas significam Normalize retornando null: modal/sistema/serviço, GPS físico inválido/futuro/antigo ou payload/provedor/identidade/velocidade inválidos. O contador não informa qual desses casos ocorreu. Teste determinístico com as mesmas páginas e GET1s versus8s reproduz o mecanismo de envelhecimento: seis páginas levam6s/48s, registros antes válidos cruzam180s, duas rejeições passam a physical_stale e um candidato expira apenas no Finish. Isso prova o mecanismo possível, não reproduz a resposta externa histórica nem atribui as10082rejeições a GPS antigo.

Para os veículos finalmente selecionados, subtrair a duração do p50 final dá idade retrospectiva ao início aproximada25,8s/14,8s. Isso não comprova disponibilidade desses registros no início. A nova métrica age_at_start_p50 usa explicitamente essa coorte final; age_at_finish_p50 mede a seleção ao concluir, sem mudar os timestamps físicos.

## Fluxo e frescor

ITS espera o conjunto completo antes de entregar observações normalizadas. No Hybrid o wrapper só consulta GTFS depois dessa conclusão; uma consulta de suporte barata pode ocorrer tarde porque a barreira anterior demorou. O cliente GTFS mantém mínimo30s, cache e304/Retry-After; reutilizar body preserva GPS físico e ReceivedAtUtc originais, não usa hora da requisição como GPS novo. Teste com adaptador/parser/crosswalk reais verifica898 exclusiva, cache aos29s e novo download aos30s.

O coletor não adquire outro lote enquanto existe geração pendente. Após aquisição/handoff ele ainda respeita o descanso10s; no modo snapshot não inventa watermark histórico. O polling desperta com geração nova quando está em descanso saudável, sem mudar o intervalo15s nem o tratamento de falhas. ACK vem depois do processamento/publicação saudável; a composição não cria escritor adicional. Logo, intervalo entre aquisições inclui duração, handoff/ACK e descanso, não equivale a um GET a cada15s.

Na janela: primeira composição pronta21:20:28,791; segunda21:21:38,069, separadas69,3s. A segunda estava pronta cerca85,2s após o início da ativação; os novos GPS898 foram vistos no REST aos97,6s. Isso é compatível com aquisição tardia seguida de processamento; não representa97,6s de CAS ou SignalR. Ciclos delimitados tiveram máximo12,272s, matching máximo4,458s, dois consumos e zero CAS infra, sem prova de ACK pendente90s. IDs de geração e etapas individuais não foram arquivados: não é possível reconstruir retrospectivamente cada veículo aceito/rejeitado.

TTL ativo40s e recente180s contam da escrita, enquanto idade física conta do GPS. Repetir snapshots completos no SignalR não renova GPS nem representa novo CAS; uma posição pode estar presente e antiga. Leituras REST/socket recebem métricas físicas próprias, incluindo mediana/p90; os TTLs e a política de visibilidade permanecem intactos. Descartes espaciais/timestamp são verificados pelos testes de predecessores/CAS, inclusive no híbrido; nenhum foi relaxado para aumentar cobertura.

## 898: prova insuficiente, não falha comprovada

A sonda externa baixava seu próprio feed cerca a cada60s e observava REST/socket cerca a cada20s; o backend tinha outro GET/cache de30s. Não eram o mesmo snapshot. A correlação exigia igualdade de identidade/GPS/coordenadas num cache de observações, podendo perder o par mesmo quando o backend publicava corretamente. A leitura REST adicional também ocorria depois do resumo socket; uma publicação entre essas leituras muda o resultado. Durante rollback, duas ProbeError impediram registrar novas amostras completas. A correspondência posterior do socket não prova que a renovação só começou depois do rollback.

Os12GPS físicos posteriores à ativação vistos no REST aos97,6s provam renovação além de simples retenção do baseline. Zero correspondências no socket naquele instante é **falta de prova da sonda**, não prova de zero publicações. O gate90s foi conservador e permanece90s: uma prova detectada depois não autoriza aprovação retroativa.

## Instrumentação implementada

- GpsDatarioClient.Bounded mede headers (inclui espera HTTP/conexão), body e JSON por página, status, fase e bytes, com acquisition aleatório, minuto UTC e somente booleano de cursor. Não exporta URL/cursor/body/headers/tokens.
- ITS mede gate/intervalo/pending/normalização, conta provedores e rejeições por página/janela, motivos globais, expiração final e idades da coorte no início/fim. HTTP completed significa body/JSON completos, não atesta por si só completude dos provedores.
- GpsPipelineTrace, OFF por padrão, vincula a geração realmente entregue pelo store aos estágios selected, polling, age_valid, timestamp_new, matched, cas_accepted/cas_rejected_timestamp/cas_infrastructure_failure, signalr_completed, rest_returned e ack.
- Somente a coorte898 por padrão; até900s,8gerações,128observações/geração e256logs. Cap excedido suprime a geração diagnóstica inteira, não exporta amostra truncada como prova. Chaves de identidade/GPS/coordenada ficam em memória limitada; DTO, Redis, SQL e mensagens públicas não recebem IDs de rastreio nem novos campos.
- Logs contêm sessão aleatória/geração/linha/família da fonte, contagens, faixas de idade e tempos. linked_complete_new é a **interseção da mesma observação** nos estágios seleção/CAS/REST/envio/ACK, com GPS físico posterior ao início do processo e não futuro. Veículos diferentes em estágios diferentes não fabricam uma prova.
- signalr_completed só ocorre depois de SendAsync concluir; isso comprova conclusão do envio no servidor, não ACK do cliente. A sonda continua verificando recebimento/renovação física da898 no socket. rest_returned é leitura oficial do runtime, não confirmação de entrega HTTP ao cliente. Essas limitações ficam explícitas.
- Metadado since_body_received_p50 usa RecebidoEmUtc existente; para ITS esse metadado é atribuído durante normalização e não deve ser interpretado como cronômetro HTTP. Os tempos HTTP/body/parse explícitos do cliente são a medida por página. Nenhum deles participa da escolha de GPS.

A amostragem 4C7 existente continua intacta: tem outros consumidores, razões de matching/BRT e orçamento de amostras. O novo módulo agrega gerações BUS e não a substitui. Falha do sink diagnóstico não altera lote, CAS, ACK ou publicação; corrigida compatibilidade do construtor de polling com fixtures antigas que não fornecem store para testar operações isoladas.

## Sondas e próxima execução, somente preparada

A sonda distingue HTTP/transport/contrato do backend, valid_empty, present_stale/present_aging/present_recent, conexão/reconexão e correlação unproven/server_only/client_only/proven. A ausência de prova não vira ausência de ônibus. Erros de comando guardam apenas ferramenta/operação/código de saída ou timeout, sem stderr/argumentos sensíveis.

Na janela passada, SIGTERM ao grupo podia atingir docker logs junto do controlador, produzindo RuntimeError genérico. Não há exit code histórico para concluir qual comando foi interrompido. Agora o pedido humano grava motivo permitido em arquivo separado e sinaliza somente o PID verificado do supervisor; watchdog conserva sua proteção de grupo para travamento. Motivo operacional e causa técnica são registrados separadamente.

Comando futuro no diretório privado revisado, **não executado no servidor**:

```bash
python3 "$TRIAL/its_window.py" --request-stop gate_898_unproven
```

O gate exige interseção de geração no servidor e renovação física no cliente até90s quando baseline898 é relevante. Aos90s sem prova ele pede rollback, antes de iniciar outra amostragem; uma prova aos90,001s não passa. Tempo de uma chamada já em curso pode atrasar a avaliação; esse limite de execução de comandos não foi removido nem anunciado como retorno instantâneo.

Templates futuros agora mudam as três flags BUS **mais uma flag diagnóstica**: GpsPipelineTrace__Enabled=true no Hybrid/false no rollback. validate permite exatamente esse toggle adicional, preservando os demais serviços/limites. Não foram aplicados. Não usar hashes históricos do preflight como se fossem os arquivos novos; hashes atuais no resumo. Release149 não contém a nova instrumentação: nova imagem/CI e autorização de publicação continuam necessárias antes de preparar instalação remota. Rollback permanece GTFSRT_BUS/fallback vazio/FullFallback e desliga o trace.

Há justificativa técnica para **preparar** terceira janela com esta observação melhorada. Ainda NO-GO para executá-la: falta validar a revisão em CI/Release, publicar/implantar sob autorização separada, confirmar trace na revisão instrumentada e provar supervisor/cron/baseline novamente. O objetivo deve ser medir a decomposição da aquisição e completar a prova898, sem declarar previamente a ITS como causa raiz nem alterar polling/TTL. Nenhuma nova janela está autorizada por esta tarefa.

## Validação

Resultados finais:464/464 direcionados;1076/1076 seleção offline oficial em Docker SDK9, --network none, usuário1000:1000/workspace somente leitura, com verificador de classes;4/4 Redis real descartável/loopback/porta aleatória;36/36 Python. Zero falhas/ignorados nas execuções finais. O offline usou a assembly **Debug já compilada**, não build Release. Nenhuma consulta PostgreSQL, feed real ou E2E completo foi necessária.

Tentativas anteriores registradas: quatro testes Redis selecionados antes da infraestrutura falharam em localhost:6380, depois passaram no container isolado; uma asserção existente de timer virtual falhou transitoriamente, passou isolada6/6 e na seleção final sem mudar a regra; offline identificou15fixtures com store nulo no construtor, corrigido acesso opcional e suíte repetida aprovada; fixture nova de fluxo omitiu inicialmente a dependência BRT, corrigida sem mudar BRT operacional. Container de teste removido e laboratórios preservados. Docker Desktop local foi iniciado para disponibilizar infraestrutura; nenhum comando de restart foi dirigido aos containers preexistentes.

A tentativa de recompilar Release foi recusada pela revisão automática (aprovação exigida, indisponível nesta sessão). A recusa foi respeitada: testes adicionais executaram a assembly Debug existente, sem tentar outro comando para recompilar Release. CI/Release é pendência antes de publicação, não resultado aprovado local.

| Requirement | Evidence |
|---|---|
| ITS rápida/lenta, paginação e envelhecimento | DelayedPagesMeasureHttpAndPhysicalAgingWithoutRenewingGps (1s/8s por página); BoundedPageSeparatesHeadersAndBodyTime |
| Rejeições antigas/futuras | RejectionTaxonomySeparatesPhysicalStaleAndFuture |
| Sequência e898 exclusiva do suporte | SequentialHybridWaitsForItsBeforeReadingSupportAndKeepsPhysicalGps; RealCachedGtfs898RetainsOldPhysicalTimeUntilNextPermittedDownload |
| Geração consumida e snapshot divergente | SameConsumedGenerationLinksPhysicalAgeCasRestSignalRAndAckWithoutPayload; DifferentObservedSnapshotCannotProveConsumedGenerationOrCas |
| Identidade comum entre estágios | DifferentVehiclesAtEachStageCannotFabricateCompleteCorrelation; CompleteSnapshotsDoNotRecountRepeatedPublicationOrCoordinateConflicts |
| Pipeline/matching/CAS/ACK/publicação reais | FlowTraceFollowsActualHybrid898CollectorPollingCasAckAndPublications |
| Atraso/falha/recuperação SignalR | FlowTraceWaitsForSuccessfulSignalRSendAndCorrelatesRestWithoutProviderPayload; FailedSignalRSendCannotBecomeCompletedTraceAndRetryCanRecover |
| Timeout/cancelamento/backpressure/recuperação | TimeoutBudgetAndCancellationBoundNonCooperativeIo; BudgetDuringPaginationDropsAlreadyAcquiredRecords; GtfsFallback_TwoSpacedProbesRecoverAndPartialResetsStreak |
| Falhas diagnósticas e limites | BrokenDiagnosticSinkCannotBlockSnapshotOrAck; CapacityNeverExportsTruncatedGenerationAndEvictsOldGeneration; DisabledOrExpiredTraceDoesNotExport |
| Classificação e limite90s | test_gate_898_keeps_90_second_boundary; test_server_generation_and_client_renewal_both_required (90/90,001); test_http_failure_and_contract_failure_stay_distinct |
| Parada segura e conexões | test_interrupted_command_keeps_exit_and_safe_operation; test_request_stop_only_signals_verified_controller_and_keeps_reason; test_connection_state_is_separate_from_empty_data |

Comandos das seleções, configuração de isolamento, contagens TRX e hashes constam no [resumo](hybrid-window2-investigation-20261024.summary.json) e nos artefatos locais artifacts/its-latency-tests. Nenhum teste listado é aprovação produtiva.
