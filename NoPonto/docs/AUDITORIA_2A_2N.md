# Auditoria 2A.2N — 06/10/2026

Resultado: **REPROVADO PARA IMPLANTAÇÃO global**, por lacuna de recuperação entre aceite do GPS e commit operacional, incompatibilidade de execução com escritores antigos e ausência de validação de carga/cobertura adicional. Isso não invalida os cenários circulares já aprovados. Não houve implantação nem execução contra serviços reais nesta auditoria.

## Evidência e escopo

116 testes reais aprovados na 2A.2M são resultados informados pelo usuário, não executados novamente pelo agente: 5 circulares Postgres, 1 reprodução protegida, 5 ponta a ponta, 105 duráveis/outbox/operacionais. Chaos de parada real do Redis excluído. As 414 aprovações locais anteriores também pertencem à etapa L.

Inspecionados histórico H–L, mudanças rastreadas e arquivos novos relevantes, Program, regras/candidatos, codec, migration, PostgreSQL/Redis, fixtures, materializador e consumidores. Há diversas mudanças anteriores ainda não commitadas; foram preservadas. Não alterar migration existente para corrigir teste. A auditoria descreve o código atual; não certifica serviços de treino externos, dados de produção nem capacidade do servidor.

## Fluxo efetivamente registrado

1. `GpsPollingService.ProcessarCicloAsync`: reúne fontes/snapshot, escolhe posições mais recentes, consulta posição anterior e descarta timestamp menor/igual (linha 293). Atualiza índices de linha, busca contexto operacional e enriquece individualmente ou pelo executor batch. HTTP BRT passa por fonte/mapper; `GpsBrtClient` mantém identificador físico e código de linha (linhas 98–109).
2. `GpsPadraoRepository`, implementado em `4-Data/Repositories/GpsItinerarRepository.cs`: matching espacial/direcional sobre padrões publicados; consultas filtram `Linhas.Codigo`. `GpsEnriquecimentoService.EnriquecerCoreAsync` (82) combina matching global, consulta direcionada, continuidade, histerese e validação temporal. Ao construir o resultado, `MatchingOperacionalPlausivel` exige rota, validação temporal aceita e histórico físico plausível (aproximadamente linha 500). Não deriva da mera presença de UUID. A prova é efêmera e não sobrevive à serialização pública do DTO.
3. `GpsPollingService.ConfirmarPosicaoAsync` (1086): primeiro CAS da posição GPS Redis; só se aceito chama `ViagemObservadaService.AtualizarAsync`. Esta não processa ausência de versão/fração. O mapa e a telemetria observacional podem continuar mesmo quando o processamento operacional não avança.
4. `ViagemOperacionalRepository.TentarAtualizarInternoAsync`: valida entrada/timestamp, recupera contexto, rejeita duplicados, avalia candidato com flag ligada antes da projeção antiga, consulta estrutura relacional e transição de ocorrências. Candidatos congelam posição/cursor de A e avançam timestamp geral. Duas evidências coerentes confirmam mudança real de linha/sentido; baseline novo não emite passagens anteriores. Retorno válido cancela candidato. Versão geométrica isolada não define mudança operacional.
5. Para cancelamento circular, `DecidirIntegridadeCircular` examina prova física e risco. Prova suficiente conserva viagem, incrementa volta e aplica baseline sem passagens. Risco sem prova protege, congela cursor/volta e suprime passagens. Proteção durável continua com flag desligada. Duas evidências de recuperação podem iniciar execução nova, volta relativa 0, fim inferido e início sem passagem retroativa.
6. Commit durável: lock consultivo por veículo, leitura `FOR UPDATE`, CAS de versão e comparação da extensão; Estado/Integridade/Versao e outbox na mesma transação. Redis é projetado depois do commit. Caminho quente confiável sem alteração semântica usa Redis/checkpoint; não é toda atualização linear que escreve PostgreSQL.
7. `ViagemOutboxWorker.ProcessarLoteAsync`: claim com lease, valida JSON, materializa journal/histórico e confirma processamento numa transação. Falha de lote isola itens; reentrega é idempotente. `HistoricoEventoRepository.PersistirLoteAsync` insere somente eventos `PassagemParada` cuja identidade e ocorrência correspondam às relações publicadas. Fim/início não são transformados em passagens.
8. Após resultado operacional, polling chama ETA shadow e fábrica ML. ML exige correspondência operacional completa; durante ambiguidade mantém GPS/matching, mas omite viagem/volta/alvo operacional. O shadow rejeita ambiguidade e agora também rejeita as inconsistências adicionais descritas abaixo.

## Achados por criticidade

Referências principais (caminhos relativos a NoPonto; linhas desta revisão):

| Arquivo | Método/linha | Responsabilidade |
|---|---|---|
| `Program.cs` | 500, 557–569 | Registro efetivo do repositório operacional/ETA/outbox |
| `2-Application/Services/GPS/GpsPollingService.cs` | 293, 1086 | Filtro anterior e aceite separado GPS/viagem |
| `2-Application/Services/GPS/GpsEnriquecimentoService.cs` | 82, 353, 501 | Matching, histerese e prova efêmera |
| `4-Data/Repositories/ViagemOperacionalRepository.cs` | 32, 119, 265, 350, 374, 392 | Autoridade, avaliação, locks/CAS, extensão e conflito outbox |
| `2-Application/Services/GPS/ViagemOperacional.Integridade.cs` | 15, 75, 100 | Validação, prova de wrap e proteção/recuperação |
| `4-Data/Repositories/ViagemOperacionalRepository.Integridade.cs` | 8 | Consulta da prova geométrica |
| `4-Data/Repositories/ViagemOperacionalCodec.cs` | 8, 49, 62 | 27 posições e validações independentes |
| `2-Application/Services/GPS/TelemetriaMl.cs` | 109 | Elegibilidade da identidade ML |
| `2-Application/Services/GPS/EtaV2Shadow.cs` | 130 | Elegibilidade shadow corrigida |
| `4-Data/Repositories/EtaV2Repository.cs` | 78 | Fechamento de previsão por identidade completa |
| `4-Data/Repositories/HistoricoEventoRepository.cs` | 45, 188 | Materialização e validação de evento/motivo |
| `Migrations/20261006180000_IntegridadeCircularDuravel.cs` | 11 | DDL aditiva e rollback bloqueado |
| `5-Testes/MudancaOperacionalPontaAPontaTests.cs` | 137, 168, 201 | Novos casos reais ainda pendentes |

| Prioridade | Achado | Evidência / efeito | Situação |
|---|---|---|---|
| Alta, bloqueador | Aceite GPS não é atômico com viagem | Polling 1093 aceita Redis antes de 1101; filtro 293 elimina repetição. Falha PG pode perder uma evidência ou evento daquele timestamp. | Confirmado pela sequência de código; teste real de caracterização preparado, não executado. Sem refatoração ampla. |
| Crítica em implantação mista | Escritor antigo ignora IntegridadeCircular | Array continua legível em binário antigo, mas proteção não está nele. Escritor antigo pode reabrir cursor/emitir identidade ambígua ou mudar estado deixando extensão antiga incompatível. | Bloquear coexistência; flag false não resolve. |
| Alta em implantação mista | Consumer antigo descarta motivo de fim | Deserializar evento em contrato antigo e serializar novamente omite `motivo_fim`; pode materializar fim sem causa ou conflitar no journal em reentrega por versão nova. | Atualizar também materializadores/consumers antes de novos eventos. |
| Alta para qualidade ETA | Shadow aceitava identidade incoerente | Quatro regressões vermelhas demonstraram veículo/timestamp/padrão/versão de alvo divergentes aceitos na fila. | Corrigido; nove controles de identidade adicionados. Não houve demonstração de dados contaminados em produção. |
| Média/alta para disponibilidade | Binário novo depende da coluna | `LerDuravelAsync` e UPSERT citam coluna; ausência causa erro SQL. Cache linear pode adiar essa falha até leitura/commit durável. | Migration primeiro; não presumir que flag false torna schema opcional. |
| Alta para capacidade, ainda sem medição | Escrita circular frequente | Âncora nova altera semântica e força commit; leitura autoritativa também é adicional por contexto circular. | Carga/WAL/pool/latência precisam homologação. Não demonstrada saturação. |
| Média, cobertura | Código distinto e sobreposição reais insuficientes na M | Casos anteriores de mudança ponta a ponta usam mesmo código/sentidos; nova cobertura preparada abaixo. | Quatro casos reais novos pendentes. |

### Falha entre Redis GPS e PostgreSQL

Rollback de estado/outbox é correto e não torna o aceite GPS reversível. Se A está em `.26`, B `.27` é aceito como posição GPS e a transação que gravaria candidato falha, A continua durável. Repetir B com timestamp igual é eliminado antes da viagem. B seguinte `.28` pode virar primeira evidência, atrasando confirmação. Se o GPS parar nesse ponto, não há garantia de reconstruir a tentativa perdida. Cruzamento de parada posterior pode permitir catch-up, mas depende de nova observação, continuidade/cursor e regras; não é garantia de entrega. Não afirmar perda de toda passagem em qualquer falha.

Após restart, Redis GPS preservado continua filtrando o timestamp; Redis perdido permite novo processamento se a fonte repetir observação ainda elegível. Enriquecimento em memória também pode perder histórico. Se o commit PG aconteceu e apenas projeção Redis falhou, a autoridade/outbox continuam disponíveis; isso é outro cenário e não implica duplicação. Eventos repetidos idênticos são aceitos; payload diferente no mesmo EventId lança `EventoViagemPayloadConflictException : FormatException`, classificado como `InvalidState` atualmente.

Recomendação para etapa específica: definir aceite operacional independente do aceite de visualização, retenção limitada/durável de tentativa com identidade da observação, retry por veículo e tratamento explícito de commit com resposta perdida. Reprocessamento deve reconstruir evidência física confiável e reler CAS; reenviar DTO serializado não recupera a prova efêmera. Testar falha pré/pós-commit, timestamp igual/novo, restart e concorrência. Não desfazer cache GPS cegamente nem permitir todos os duplicados: isso cria corridas/duplicidade e altera disponibilidade do mapa.

## Persistência, compatibilidade e circularidade

`20261006180000_IntegridadeCircularDuravel.Up` adiciona JSONB anulável e CHECK objeto/NULL. É aditiva; não atualiza históricos nem remove dados. CHECK verifica forma, não semântica: `IntegridadeCircular.Validar` exige contrato 1, identidade/versão/volta coincidentes, timestamps e coordenadas válidas, consistência de ambiguidade e recuperação. Registros antigos com NULL continuam aceitos, sem fabricar âncora. Não equivale a reconstruir evidência histórica. Alteração de tabela/CHECK toma locks: precisa janela, mesmo sem backfill. `Down` bloqueia remoção deliberadamente.

Codec conserva 27 strings nas mesmas posições: 1 viagem, 2 veículo, 12 fase, 25 volta, 27 topologia (numeração humana). Extensão é outra coluna da mesma linha, não uma posição reaproveitada. Snapshot inválido não deve ser convertido em novo estado confiável. Redis circular é hidratado da autoridade; proteção PG prevalece sobre snapshot quente. Extensão muda atomicamente com estado/outbox. Locks são por veículo, não globais; colisão de hash pode serializar veículos distintos, sem autorizar dois commits sobre o mesmo veículo. CAS e lease não garantem entrega do GPS anterior.

`WrapComprovado` exige mesma execução/versão, âncora com timestamp físico, matching confiável, geometria simples fechada, pontos/direção no corredor, movimento mínimo, plausibilidade de velocidade e orçamento que exclua regressão inversa/volta adicional. A consulta `BuscarProvaCircularAsync` usa PK da versão e não varre histórico. Falha de critérios deve proteger, não aumentar contador porque a fração diminuiu. Geometrias abertas/autointerseções, ruído, versão diferente e múltiplas voltas possíveis não recebem prova positiva. A prova usa duas observações e corredor geométrico; não demonstra cada ponto percorrido fisicamente.

O tratamento novo concentra-se no cancelamento/proteção; o wrap normal mantém a lógica existente de `OcorrenciaParadaRepository` e validação temporal do enriquecedor. Não afirmar garantia universal de contagem de voltas durante qualquer perda longa de GPS. O controle de tempos/ausência global não foi reformulado nesta etapa. Persistência corrupta ou alteração manual da geometria publicada também estão fora das garantias.

Ordem segura futura: aplicar migration revisada em janela própria; drenar/interromper escritores; atualizar todos os escritores e consumers que preservam motivo; retomar com flag false; validar saúde/schema/proteções/outbox e carga; somente depois avaliar ativação. Não há allowlist nova nesta implementação. Rollback permitido apenas para binário que compreenda a extensão e o motivo. Preservar coluna/markers/journal/outbox; desligar flag não apaga proteção nem desfaz eventos.

## ML, labels e outros consumidores

`EventoTelemetriaMlFactory.IdentidadeCompativel` verifica status, fase, estado observável igual, veículo/timestamp/código/linha/sentido/padrão/versão/topologia e confiabilidade. Próxima ocorrência operacional deve pertencer à versão. Campo ocorrência observacional pode permanecer preenchido sem viagem/volta; esse UUID sozinho não autoriza join operacional de ETA. Distância ou matching disponível também não prova passagem.

ETA shadow agora verifica viagem não vazia, veículo/timestamp/padrão, versão do alvo e, quando presente, snapshot/fase/código/linha/sentido/topologia operacional. Mantida compatibilidade com resultados legados sem EstadoOperacional; não inventa confirmação de confiança nesses resultados. Na aplicação registrada o repositório operacional fornece o estado nos resultados aceitos. Nenhum predictor alterado.

`EtaV2Repository.ClosePassageAsync` só fecha `PassagemParada`; associa veículo+viagem+versão+ocorrência+linha+sentido+padrão+volta e previsão não posterior à passagem. Execução inferida nova não fecha previsão da anterior. Pending de viagem diferente é invalidado na persistência de nova previsão; sem nova previsão, expira depois. Fim inferido sozinho não realiza ETA. O label implementado aqui subtrai **TimestampPrevisao**, não TimestampGps; não mudar isso silenciosamente. Interpolação de passagem pode anteceder o instante de enqueue/persistência e reduzir coverage; validação quantitativa permanece necessária.

`HistoricoEventoRepository` não preenche `TempoDesdeParadaAnteriorSegundos` nem `ParadaAnteriorId`. Encontradas propriedades/migrations e consultas estatísticas desses campos, mas não cálculo ativo que conecte execuções. Não há garantia para treino externo via `/admin/ml/retreinar`: implementação externa não está disponível neste repositório. Para dataset derivado de GPS, exigir identidades operacionais completas e join por viagem/versão/ocorrência/volta, passagem posterior ao GPS, sem cruzar execução inferida nem preencher intervalo protegido como chegada. Não somar tempos simplesmente por Ordem. Dados históricos existentes não foram corrigidos/reprocessados.

GPS/mapa continuam aceitos durante proteção porque seu cache precede decisão operacional. `ContextoOperacional.PodeProjetar` não projeta viagem ambígua. Estado causal/correção shadow usa observações de GPS; `CorrecaoTemporalPosicaoCoordinator.CriarObservacao` fornece ViagemId nulo, portanto não herda indevidamente a identidade protegida nesse caminho. Não confundir esse shadow observacional com labels operacionais. ETA externo chamado por `GpsEtaClient` não foi auditado internamente.

## Cobertura acrescentada e limitações

- `EtaV2FoundationTests`: nove regressões de identidade; quatro primeiras falharam antes da correção. Controle antigo de proteção/flag mantido.
- `GpsBrtTelemetriaTests`: 42 e 43 preservam `BRT-902090` e códigos distintos, usando handler HTTP local.
- `MudancaOperacionalPontaAPontaTests.CodigosDistintosSobrepostos_ABB_ConfirmaNovaExecucaoSemPassagem`: dois casos individual/batch, padrões reais com geometria igual, códigos/linhas exclusivos, prova produzida pelo enriquecedor, candidato seguido de nova execução, fim/início idempotentes sem passagens.
- `CodigosDistintosSobrepostos_ABCBA_NaoAcumulaEvidenciasEntreCandidatos`: troca B/C/B substitui evidência; retorno A cancela sem trocar identidade nem emitir passagem.
- `FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampNaoReprocessaCandidato`: caracteriza risco de retry utilizando hook de falha existente, rollback real e filtro produtivo. Não injeta resultados.

Os quatro casos reais reutilizam ViagemOperacionalFixture, SearchPath/schema exclusivo e limpeza existente; callback de falha fica restrito ao harness e desativado em finally. Só executar com conexões comprovadamente descartáveis: a fixture cria schema, migra e remove com CASCADE. Novos testes foram preparados/compilados, não executados pelo agente. Geometria sobreposta por códigos próprios não equivale a testar rede BRT 42/43 real; adapter BRT local e pipeline SPPO real são evidências separadas. Sobreposição entre padrões do mesmo código continua dependendo da histerese: teste existente `Histerese23_UsaDistanciasAtuais_EPreservaThresholds` e controles de consulta direcionada foram mantidos. Não há certificação de todas as variantes/serviços BRT. Diferença só de padrão/topologia no mesmo sentido é rejeitada por política ainda não definida.

Resultado local final: **426 aprovados, zero falhas/ignorados**, com --no-restore e compilação dos arquivos novos, incluindo os reais sem executá-los. Uma rodada anterior teve falha intermitente em `Worker_FlushesAtMaximumDelay` preexistente; passou isoladamente e na repetição integral. Não modificada a espera de 2s, nem removido o teste; causa não demonstrada. Quatro regressões de identidade foram vermelhas antes da correção. Cinco avisos preexistentes na recompilação; nenhuma validação exclusivamente --no-build. Resultado manual M e resultado local N são evidências distintas.

## Desempenho e medição necessária

Circular confiável com flag ligada atualiza âncora; igualdade semântica inclui extensão. Consequências: leitura PG extra no contexto circular, escrita durável potencialmente a cada GPS, serialização de 27 campos+JSONB, WAL/tuplas mortas e lock por veículo. Proteção também persiste evidência/timestamp. Linear confiável conserva cache/checkpoint; estrutura/transição já consultavam PG antes. Com flag false e sem extensão antiga, não começa âncora nova; proteção existente não é ignorada.

Há índices por versão/ordem nas ocorrências, PK veículo/versão geométrica e GiST de Geometria no modelo. Consulta de prova por PK processa uma geometria; custo depende de número de vértices. Não presumir uso do GiST de geometry numa condição `Geometria::geography`: verificar plano/expression index. `EstruturaAsync` e matching fazem consultas espaciais; transição valida sequência inteira da versão, não histórico completo. Novas conexões/roundtrips e alocações precisam ser contabilizados mesmo sem crescimento de trilha no JSONB.

Procedimento futuro em ambiente isolado: comparar mesmo replay GPS com flag false/true, grupos linear/circular confiável/candidato/proteção, warm-up e sequência de falhas. Fixar versão, quantidade de veículos, cadência e complexidade geométrica; iniciar pequeno, aumentar até a carga prevista. Registrar contadores `GpsCicloPerformance` de leituras/escritas/locks, outbox backlog, p50/p95/p99 do ciclo e commit, uso de pool/CPU/RSS, RAM/swap e IO do sistema. Capturar diferenças de `pg_stat_database`, `pg_stat_wal.wal_bytes` e `pg_stat_user_tables` antes/depois, sem resetar estatísticas compartilhadas. Usar EXPLAIN (ANALYZE, BUFFERS) apenas para SELECTs da prova/estrutura em banco descartável; jamais analisar DML sem entender que ANALYZE executa a operação. Se pg_stat_statements não estiver disponível, não instalar automaticamente nesta auditoria.

Critérios: sustentar pico esperado com margem acordada e ciclo abaixo da cadência, sem crescimento indefinido de backlog, timeouts/pool exaurido, swap/OOM ou conflito de payload. 4 GiB compartilhados não permitem concluir capacidade por aprovação de testes. Se custo for excessivo, discutir retenção de âncora/checkpoints mantendo invariantes; não simplesmente reduzir persistência e perder proteção após restart.

## Próximos passos e comandos manuais

1. Resolver contrato de retry de observação operacional e executar regressões pré/pós-commit/restart. É bloqueador técnico independente do algoritmo circular.
2. Executar os quatro casos novos abaixo e regressões reais 2A.2M, sequencialmente em conexões de teste verificadas.
3. Validar cobertura BRT do source/polling e variantes reais anonimizadas; incluir mesma linha com padrões sobrepostos e falha da consulta direcionada.
4. Medir carga e revisar rollout coordenado/schema/rollback compatível.
5. Auditar joins de treino externo e coverage/latência dos labels antes de certificação ETA.

```powershell
# Conexões POSTGIS_TEST_CONNECTION e REDIS_TEST_CONNECTION devem apontar
# exclusivamente para serviços descartáveis previamente identificados.
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~CodigosDistintosSobrepostos' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampNaoReprocessaCandidato' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~IntegridadeCircularPostgresTests|FullyQualifiedName~WrapDuranteCandidatoIntegracaoTests|FullyQualifiedName~MudancaOperacionalPontaAPontaTests)' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~ViagemOperacionalIntegracaoTests|FullyQualifiedName~ViagemOutboxBatchTests|FullyQualifiedName~ViagemDuravelPostgresTests)&FullyQualifiedName!~Chaos_RedisPara_ReiniciaVazio_ContinuaMesmaViagem' --verbosity normal
```

Não executados nesta etapa. Os testes de caracterização não corrigem o risco demonstrado; sua aprovação não remove o bloqueador de retry. Resultado final de compilação/testes locais registrado em HISTORICO.md. Arquivos alterados nesta etapa: EtaV2Shadow.cs, EtaV2FoundationTests.cs, GpsBrtTelemetriaTests.cs, MudancaOperacionalPontaAPontaTests.cs, este relatório e HISTORICO.md. Sem schema/migration/configurações/infraestrutura/feature flag alterados.
