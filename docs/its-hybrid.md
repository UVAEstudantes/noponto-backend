# Ônibus híbridos ITS + GTFS-RT —24/10/2026

Estado26/10: [terceira janela bloqueada no preflight — captura com input_queue_overflow](its-homeserver/hybrid-window3-20261026.md). **NO-GO para nova ativação** até correção local/testes e nova provaGTFS. ITS não foi ativada; produção permanece GTFS-only/traceOFF. Este resultado posterior substitui o GO limitado da prova de24/10.

Atualização local após release151: [captura dedicada e preflight da terceira janela](its-homeserver/log-capture-preflight-20261024.md). Este procedimento prevalece sobre comandos/hashes históricos abaixo: cinco arquivos operacionais, captura independente obrigatória antes do baseline, digest151 exato, gate89890s e rollback GTFS/FullFallback/trace=false. Nenhuma terceira janela autorizada/instalada/executada.

Revisão instrumentada implantada na release151, com operação exclusivamente GTFS e trace desligado: [resultado e pré-condições da terceira janela](its-homeserver/instrumented-release-deploy-20261024.md). Nenhuma terceira janela executada.

Investigação da segunda janela, medições e rastreabilidade opt-in preparadas localmente: [hybrid-window2-investigation-20261024.md](its-homeserver/hybrid-window2-investigation-20261024.md). Sequência de aquisição, filtros, TTLs e fallback operacional preservados; nenhuma terceira janela executada.

## Arquitetura e configuração

Auditoria dirigida dos contratos: ITS_CURRENT e GTFSRT_BUS já entregam `ISnapshotGpsSource`, GPS físico e observações normalizadas. GTFS passa pelo parser limitado e crosswalk validado, com unidade km/h explicitamente configurada, HTTP/cache/304/Retry-After próprios. `GpsSourceResolver` já cria `ItsFallbackGpsSource`; coletor/store/ACK e polling/CAS/Redis/SignalR são comuns. O problema era retornar imediatamente quando ITS estava saudável. Não há necessidade de outro coletor, HTTP por linha ou outro escritor.

O resolver agora aceita `BusSupportMode=Hybrid` exclusivamente para ITS_CURRENT + GTFSRT_BUS. O mesmo wrapper serializa a aquisição ITS e a leitura GTFS, compõe um único lote e entrega ao coletor existente. O nome operacional continua ITS_CURRENT, preservando o wakeup do snapshot. Cada observação conserva sua fonte/provedor reais e os metadados da observação escolhida; não é reetiquetada como ITS quando veio do GTFS.

Proposta de configuração, **ainda não aplicada**:

```text
GpsSources__BusPrimarySource=ITS_CURRENT
GpsSources__BusFallbackSource=GTFSRT_BUS
GpsSources__BusSupportMode=Hybrid
```

Rollback no mesmo binário:

```text
GpsSources__BusPrimarySource=GTFSRT_BUS
GpsSources__BusFallbackSource=
GpsSources__BusSupportMode=FullFallback
```

O padrão do modo é FullFallback, mantendo os consumidores anteriores, inclusive o fallback legado explícito. O modo híbrido recusa combinação com ZIRIX_DIRECT e configurações de modo inválidas antes de adquirir. Standalone GTFS não precisa registrar ITS ou a API antiga. Fonte BUS padrão, BRT, DI, matching, ETA, TTL, viagens, CAS, Redis, SignalR e migrations não mudaram.

## Regras de composição

- Consumir somente resultados completos `Sucesso`/`Vazio`. Um corpo parcial/falho não contribui com posições. Suporte saudável complementa ITS mesmo quando ITS está vazia; a 898 permanece coberta pelo GTFS, sem adicionar RIO/MAXTRACK ao adaptador SPPO.
- Canonicalizar identidade por trim/maiúsculas, preservar a original disponível. Antes de arbitrar, rejeitar identidade inválida, modal/fonte incorretos, serviço ausente, coordenada inválida, velocidade negativa/não finita, GPS futuro, epoch/sentinela e idade acima 300 s. Permanecem as validações mais restritas dos adaptadores e os filtros operacionais posteriores. Não se amplia a idade do ITS. O limite comum 300 s é só o teto de segurança; TTLs 40/180 s permanecem iguais.
- União dos exclusivos; identidade compartilhada usa maior `GpsTimestamp`. Hora de recebimento, envio ou servidor nunca substitui o GPS físico, nunca renova GPS e não arbitra preferência temporal. Movimento/linha/sentido do registro escolhido seguem ao pipeline existente.
- Nos candidatos empatados no maior GPS, diferenças de latitude/longitude maiores que 0,00001 grau, serviços distintos ou sentidos conhecidos incompatíveis tornam a identidade ambígua e a retiram daquele lote. Não recuar para um GPS mais antigo para disfarçar o conflito. Coordenadas dentro da tolerância acomodam precisão float do GTFS, aproximadamente um metro. Sentido ausente é desconhecido; `direction_id` não é inferido de azimute nem do rótulo raw `sentido`.
- Empate compatível prefere GTFS pela estrutura validada, seguido de campos estáveis do payload. Não há prioridade geral de GTFS/ITS/CONECTA/ZIRIX contra um GPS mais novo. Uma posição não é montada misturando coordenadas de uma fonte com horário de outra.
- Sem estado temporal paralelo ou fila de escrita nova. CAS e retenção existentes impedem regressão dentro de sua janela; não se promete memória histórica eterna após expiração/reinício. Um empate com metadados melhores não força reescrita do mesmo GPS nem renovação de TTL. Conflitos podem reduzir a cobertura momentaneamente; o estado anterior expira naturalmente.

## Falhas, recuperação e custo

ITS falha/parcial/timeout conserva a máquina existente de fallback: apenas GTFS, sondagens ITS espaçadas pelo padrão 60 s, duas saudáveis para recuperar a composição; falha reinicia sequência. Não aproveitar uma sondagem saudável intermediária nem guardar payload parcial para publicar depois. GTFS falho com ITS saudável permite o snapshot completo ITS, marcado com `FailureReason=hybrid_support_unavailable` e status de cada feed nos agregados. Com GTFS sozinho por falha ITS, o motivo é `hybrid_primary_unavailable`. Sem posições utilizáveis e com feed indisponível, retornar falha, não um vazio fabricado. Vazio real exige ambos completos/vazios; conflito/invalidade que elimina todos também é falha. Uma degradação parcial continua utilizável, mas não é anunciada como duas fontes disponíveis.

Aquisição sequencial, sem polling concorrente por fonte/linha. ITS mantém três janelas UTC, budgets/páginas/registros/bytes e intervalo mínimo originais. Suporte usa o cliente GTFS existente: intervalo HTTP mínimo 30 s, cache sem renovar GPS, Retry-After e limite externo padrão 10 s do wrapper. ITS padrão 90 s + suporte 10 s formam teto de espera deI/O de aproximadamente 100 s, além de processamento/ACK; não é meta de latência. A aquisição pode ultrapassar o polling 15 s, mas não cria outro ciclo sobreposto: coletor aguarda, publica uma geração e respeita o ACK/backpressure. Cancelamento é propagado e I/O tardio não cooperativo é observado/descartado, bloqueando nova leitura sobreposta.

Novo log agregado `BUS hybrid`: status das duas fontes, motivo de degradação, entradas, escolhidos por fonte, compartilhados, empates/conflitos por categoria, inválidos, até 40/60/180 s e duração. Não registra veículos ou coordenadas individuais. Duração do wrapper mede aquisição; o custo de composição/pipeline está contido nas medições externas do coletor/ciclo. Não foram ajustados TTLs, intervalos ou limites do homeserver para obter aprovação.

## Validação

Validação direcionada 326/326, offline oficial 1057/1057 e sonda/supervisor 24/24, sem falhas/ignorados nas execuções finais. Offline em container SDK 9.0, UID/GID 1000:1000, `--network none`, sem capacidades, workspace somente leitura; o verificador oficial confirmou todas as classes. Apenas os finais de linha dos scripts shell do checkout Windows foram normalizados paraLF em `/tmp` do container, sem alterar os scripts ou guardas da CI. Primeiras tentativas pararam nos guardas de CRLF/UID antes dos testes. Dois testes de integração opt-in fora do alvo foram ignorados numa seleção exploratória; a seleção direcionada final é offline e não os inclui. Allowlist/exclusões oficiais não mudaram.

RED inicial do híbrido: seis falhas esperadas por não complementar/normalizar/conferir empates; GREEN 6/6 após implementação. A ampliação incluiu cliente/parser/crosswalk reais com HTTP controlado, cache físico 29/30 s,429/503/Retry-After 60 s, suporte não cooperativo, cancelamento, ambas falhas, recuperação e histórico espacial. O teste de publicador inicialmente não tinha assinatura: a sonda foi corrigida para usar um snapshot explícito de assinaturas, evitando estado estático global. Publicador/polling/CAS reais com repositórios em memória verificam linha 898 exclusivaGTFS, empate incompatível, histórico, timestamp e listas socket sem duplicação de identidade; socket de rede real é verificado separadamente no E2E.

| Requirement | Evidence |
|---|---|
| ITS saudável,898 GTFS, exclusivoITS e backpressure/ACK | `Hybrid_HealthyItsIncludes898AndItsExclusiveThroughSingleAckHandoff` |
| GPS físico mais recente, identidade e metadados | `Hybrid_LatestPhysicalGpsWinsWithoutRenewingSourceMetadata`, `Hybrid_PhysicalAgeBoundaryDoesNotUseReceiptAge` |
| Empates, inválidos e deduplicação | `Hybrid_EqualTimeConflictsAreWithheldDeterministically`, `Hybrid_CompatibleTieIsOrderIndependentAndPrefersValidatedGtfs`, `Hybrid_InvalidNewerCandidateCannotSuppressValidPeer` |
| Falhas, vazio e recuperação | `Hybrid_OneUnavailableSourceDoesNotPromoteItsPartialPositions`, `Hybrid_BothUnavailableCannotPublishOrClaimEmpty`, `Hybrid_EmptyItsStillConsultsSupportAndDoesNotHideUnavailableFeed`, `Hybrid_RecoveryStillRequiresTwoSpacedHealthyProbes` |
| Limites, cadência, Retry-After e cancelamento | `Hybrid_GtfsHttpCadenceAndCachedPhysicalTimestampArePreserved`, `Hybrid_HealthyItsRespectsSupportRetryAfterWithoutRenewingFailedData`, `Hybrid_SupportTimeoutOrCancellationNeverOverlapsLateIo` |
| Continuidade, CAS e SignalR | `ItsGtfsFallbackAndRecovery_KeepNewestGpsAndPhysicalPredecessor` nos dois modos, `Hybrid_HealthySwitchAndConflictingTieKeepCasAndSignalRMonotonic` nas linhas 42/898; `ItsEndToEnd_Real` com socket real |
| Seleção e rollback sem legado | `Hybrid_InvalidPairFailsSelectionBeforeAcquisition`, `Hybrid_StandaloneGtfsRollbackNeedsNoItsOrLegacySource` |

O E2E descartável com os dois feeds reais passou 1/1 em 357,96 s, incluindo 123,94 s de importação estrutural pública. O ZIP representativo preserva 459 linhas/873 padrões/53214 ocorrências; **sete linhas assinadas**, não um benchmark de 459 assinaturas. Redis 7 e PostGIS 16 foram criados em portas aleatórias de loopback e dados tmpfs, com guardas; os quatro containers locais anteriores permaneceram iguais e os dois containers/rede do ensaio foram removidos. Não houve uso de conexão produtiva. Estrutura real, unidades e pares de identidade/tempo/coordenadas foram verificados pelos adaptadores existentes, sem fabricar trajetória.

Três snapshots saudáveis iniciais:

| Snapshot | ITS / GTFS normalizados | União escolhida | Escolhidos ITS / GTFS | GPS<=40 /60 /180 s | Aquisição total |
|---|---:|---:|---:|---:|---:|
|1|2440 /1515|2463|981 /1482|1997 /2300 /2460|15,11 s|
|2|2418 /1515|2433|2050 /383|2192 /2325 /2433|8,03 s|
|3|2434 /1508|2429|1352 /1077|2085 /2304 /2429|10,50 s|

A retirada conservadora por empate incompatível afetou 33/41/62 identidades nesses snapshots; contadores de coordenada e linha podem se sobrepor. No terceiro a união ficou cinco veículos abaixo da ITS isolada: mais entradas não significam publicação obrigatória de ambiguidade. A 898 foi assertivamente encontrada como contribuição GTFS no lote saudável e na recuperação, com contribuição ITS simultânea. O socket de sete linhas foi medido em agregado; não se arquivaram contagens por linha no E2E, nem identidades/payloads individuais. Não se atribui todo o resultado do socket especificamente à 898.

Falha ITS, parcial e timeout produziram GTFS sozinho 1508 posições; aceites novos 40/0/0, demonstrando que reentregar o snapshot em fases controladas não duplica GPS no CAS. Ambas falhas: zero lote/geração/aceites. Primeira sondagem saudável após 61 s: somenteGTFS 1498; segunda após outros 61 s: híbrido 2476 (1014 ITS/1462 GTFS),2473 aceites e ACK. Recuperação levou 18,55 s de aquisição e 22,93 s com pipeline nessa fase. Não se reduziram os intervalos das sondagens.

Evidência total:10077 aceites CAS/2667 veículos únicos, zero par veículo/GPS aceito duas vezes, zero falhas CAS de infraestrutura;7035 posições com histórico,388 com matching e 384 próximas paradas. PostgreSQL local:96 viagens,272 eventos/outbox e 176 passagens; outbox 153 batches concluídos/zero falhas. Redis mostrou chaves ativo/recente com TTLs máximos amostrados 38,257/178,255 s, sem renovação de GPS. As amostras de chaves são limitadas, não contagem completa da frota. Doze ciclos registrados, máximo 9,167 s, nove consumos de snapshot/ACK. Esses números incluem fases saudáveis, falhas e recuperação.

SignalR real:77 listas com posições,7 vazias,1099 itens/104 veículos, zero regressão de GPS;682 repetições de GPS são permitidas em snapshots completos. O teste determinístico separado confere identidade única dentro de cada lista. Até 40/60/180 s no aceite:7847/9623/10063; no socket:614/1017/1094, zero futuros. Populações são diferentes: frota escrita no CAS versus sete linhas assinadas. Alguns ativos têm idade física>40 s, pois o TTL operacional conta da escrita; a semântica existente não mudou.

Testhost incluindo importação/observadores:127,47 sCPU, pico 590974976 bytes, não uma medição do container produtivo. Custo novo é consultar/mapear o suporte a cada composição e executar a união em memória, com GET limitado/cacheado pelo mesmo cliente; não há sete requisições por linha. Os tempos 15,11 s iniciais e 18,55 s na recuperação mostram que aquisição pode exceder o polling 15 s. Não houve sobreposição de escritores: execução serial, uma geração e ACK. Sem extrapolar CPU/RAM ou frescor desta máquina para o homeserver.

[Resumo e hashes verificáveis](its-hybrid.summary.json), evidências em `artifacts/its-hybrid` e `artifacts/its-hybrid-e2e`. Compilação Release pelos testes aprovada com cinco avisos anteriores; não foi necessária alteração de dependências. O código operacional usado no E2E é o mesmo validado nos testes finais; alterações posteriores foram testes/supervisão/documentação.

## Segunda janela proposta

[Procedimento e supervisão](its-homeserver/activation.md), com overrides locais atualizados para as três variáveis BUS. A imagem produtiva anterior não implementa Hybrid; exige build/publicação de nova revisão/digest validado e autorização específica. Não ativar uma variável nova em binário antigo e assumir sucesso.

Usar BUS 838/898/006/457/483/355/805, mesmas sete linhas da investigação, e BRT 35/60/67 somente como controles emGTFSRT_BRT. Baseline GTFS no mesmo horário/dia, publicação REST/socket e frescor físico separados da aquisição. Confirmar 898 no suporte saudável e as duas contribuições nos logs antes de considerar o modo saudável. Observar conflitos e descartes legítimos, em vez de exigir publicar toda a frota adquirida.

Supervisor preparado exige fonte/modo/BRT corretos e prova `BUS hybrid` com ambas saudáveis/contribuindo. Conserva lock/escritor único, watchdog fora do SSH, prazo 10 min e retorno obrigatório GTFS. Acrescenta interrupção com três aquisições novas consecutivas de suporte falho e duas de ambas falhas, sem recontar logs. Controles BRT ficam separados dos critérios de frescor BUS; integridade do socket e degradação dos demais serviços continuam protegidas. Linha vazia 404 específica segue legítima; cobertura/frescor sustentadamente degradados continuam provocando rollback.

Sem publicação, merge, deploy, acesso SSH ou mudança produtiva nesta implementação. A prontidão funcional local não é autorização para ativação. Permanecem os riscos de correlação entre fornecedores, divergências em GPS empatado, direções desconhecidas, carga real do homeserver e garantia temporal limitada à retenção existente. RIO/MAXTRACK e ITS/SONDA/BRT são contratos separados, não integrados por esta mudança.
