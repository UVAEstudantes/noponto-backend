# ETA — limitações, testes e roadmap

## Finalidade, baseline e estado

Síntese operacional da Etapa 2.3C em 2026-10-06. O legado HTTP continua fail-open, o serviço Python está desligado intencionalmente, telemetria V2 existe, ETA V2 shadow/canary está implementado mas OFF e o modelo supervisionado V2 permanece pendente. Os critérios abaixo são propostas técnicas, não thresholds aprovados.

## Matriz de falhas

| Cenário | Detecção/responsável | Implementado e impacto no GPS | Comportamento desejado / teste pendente |
|---|---|---|---|
| GPS inválido/atrasado/duplicado | validação e estado causal GPS | rejeita ou evita avanço; não deve gerar ETA V2 elegível | medir motivos e garantir ausência no dataset |
| matching/padrão errado | enriquecimento/viagem | inconsistência torna V2 inelegível; erro não detectado contamina posição/label | auditoria amostral e teste diferencial por versão |
| viagem sem identidade | `EtaV2ShadowService` | ineligible; GPS segue | métrica por motivo, não apenas total |
| ocorrência/volta errada | viagem + chaves de fechamento | match estrito reduz associação indevida | testes circulares/repetição/restart |
| histórico incompleto | stream/outbox/worker | retry/pending/DLQ; GPS não bloqueia | alerta de cobertura e reconciliação |
| telemetria perdida | `TryWrite`/Redis | drop quando channel cheio; GPS segue | SLO de drops e capacidade medida |
| Redis indisponível | publisher/workers | retry/falha isolada; dados podem ficar em memória/drop | teste de recuperação e backlog |
| PostgreSQL indisponível | batch ETA/histórico | ETA retenta; channel pode saturar; GPS segue | medir duração tolerável e shutdown |
| ground truth ausente | maintenance | previsão expira | reportar taxa de fechamento por grupo |
| passagem interpolada incorreta | `TimestampPassagem` | pode fechar várias previsões com label enviesado | persistir método/confiança; estudo de sensibilidade |
| predição expirada | maintenance | `EXPIRADA`, sem erro | distinguir atraso de processamento de não chegada |
| linha desconhecida no legado | FastAPI encoder | usa código 0; backend ignora flag | rejeitar/fallback explícito em contrato futuro |
| drift/linha nova | métricas futuras | não detectado automaticamente | monitorar features, cobertura e erro temporal |
| overfitting/leakage | pipeline offline | legado usa split aleatório | split temporal/viagem, auditoria de features |
| inferência Python falha | `GpsEtaClient` | cooldown 30 s, ETA vazio; GPS íntegro | telemetria de disponibilidade e fallback versionado |
| modelo pesado | runtime futuro | não implementado | orçamento CPU/memória/latência antes do canary |
| mudança estrutural/schema | IDs/constraints | fechamento exige versão; bundle ML não tem negociação | schema/model manifest e rejeição incompatível |
| falha durante promoção | flags/contrato futuro | promoção não existe | kill switch, rollback e shadow paralelo |
| frontend não entende ETA | DTO/app | hoje campos opcionais | compatibilidade, ausência/freshness e teste E2E |

## Inventário de testes e evidências

| Área | Evidência | Tipo / limite |
|---|---|---|
| cliente legado | `GpsPerformanceMetricsTests` | unitário/contrato HTTP simulado; não valida Joblib real |
| telemetria/factory | `TelemetriaMlTests`, `GpsBrtTelemetriaTests` | unitário |
| sampling | `TelemetriaMlSamplingTests` | determinismo, blocos e percentuais |
| stream/backpressure | `RedisStreamBoundedTests`, `TelemetriaMlIntegracaoTests` | integração depende de Redis/ambiente |
| persistência/retention | `TelemetriaMlRetentionTests` | regras e infraestrutura simulada/integrada |
| viagem/passagem | `ViagemOperacionalRegraTests`, `ViagemOperacionalIntegracaoTests` | transições, ocorrências e circularidade |
| outbox/histórico | `ViagemOutboxBatchTests`, `ViagemOutboxCleanupPolicyTests`, `ViagemDuravelPostgresTests` | batch/idempotência/PostgreSQL |
| ETA baseline/shadow | `EtaV2FoundationTests` | fórmula, elegibilidade e ingress |
| channel/canary/batch | `EtaV2HardeningTests` | capacidade, determinismo, batching/retry |
| fechamento | `EtaV2PostgresIntegrationTests` | chaves, realizado e casos errados |
| dataset offline | `tests/test_dataset_v1.py` | regras e leakage estrutural |
| posição/validação | testes Python de posição e temporal | experimento em metros, não ETA |
| FastAPI/Joblib | não localizado | falta teste de contrato e smoke versionado |
| treinamento/estatística ETA V2 | não localizado | pendente |
| E2E GPS→UI com V2 | não localizado | pendente |

Existência de teste não significa execução/aprovação. Nenhum teste foi executado nesta tarefa. Métricas históricas de posição têm commit/contexto próprios e não validam ETA V2.

## Integração futura com o aplicativo

Hoje somente o legado pode preencher `EtaProximaParadaSegundos`/`EtaConfianca` no DTO. O V2 é shadow e não participa da escolha pública. Uma transição segura teria estados:

1. legado opcional/fail-open;
2. V2 shadow persistindo resultados sem UI;
3. canary interno maior, ainda sem UI;
4. comparação congelada e decisão de promoção;
5. canary público explicitamente separado, com fallback;
6. promoção gradual e monitorada;
7. remoção do legado apenas após compatibilidade e rollback comprovados.

Antes de substituir o campo, verificar: unidade/target, ocorrência, preditor e versão, `generatedAt`, idade/freshness, confiança calibrada se houver, motivo de ausência, fallback, compatibilidade do frontend, frequência de atualização e comportamento offline. Alterações de API são propostas; não estão implementadas.

## Critérios propostos de promoção

Sem inventar números aprovados, um gate futuro deve definir previamente:

- período e holdout independentes suficientes;
- MAE, mediana, P90/P95 e viés melhores ou não inferiores ao baseline nos grupos relevantes;
- cobertura e taxa de fechamento mínimas, reportadas junto à precisão;
- ausência de regressões graves por linha, horizonte e horário;
- latência p95/p99 e consumo dentro do orçamento medido;
- drops, falhas, disponibilidade e fallback aceitáveis;
- estabilidade em drift e linha/versão nova;
- bundle reproduzível e compatível com schema;
- shadow/canary sem impacto no ciclo GPS;
- rollback/kill switch ensaiados;
- contrato/frontend validados.

O conjunto comparado precisa ser idêntico ou ter interseção e cobertura transparentes. ML só é promovido se demonstrar benefício; pode complementar ou não substituir o baseline.

## Roadmap incremental

1. Persistir qualidade/origem do timestamp de passagem.
2. Definir/exportar dataset ETA V2 com manifesto e split por viagem/tempo.
3. Habilitar shadow em janela aprovada, observando drops e custo.
4. Obter cobertura suficiente de previsões realizadas.
5. Avaliar V0 por linha/horizonte e auditar labels.
6. Treinar baseline tabular supervisionado e comparar justamente.
7. Instrumentar drift, bundle/schema e inferência offline.
8. Testar canary interno e falhas.
9. Decidir contrato público e fallback.
10. Só então estudar sequências, relações entre veículos e grafos.

## Participantes para diagramas posteriores

- aquisição: provider GPS, `GpsPollingService`, matcher/enriquecedor, Redis;
- legado: `GpsEtaClient`, FastAPI, XGBoost, DTO;
- V2: viagem observada, `EtaV2ShadowService`, canary, baseline, channel, batch worker, repository;
- ground truth: `ViagemOperacional`, stream/outbox, histórico, `ClosePassageAsync`, maintenance;
- dataset/ML: PostgreSQL read-only, exportador, manifesto, splits, treinador, avaliador, registry/bundle futuro;
- integração: seletor/fallback futuro, SignalR/DTO e frontend.

Transições de previsão: candidata → inelegível/skipped/dropped ou pendente → realizada/expirada/invalidada. Esses participantes são suficientes para os diagramas solicitados, que não são gerados nesta etapa.

## Privacidade e segurança

Telemetria de veículos ainda pode revelar padrões operacionais. Aplicar finalidade, acesso mínimo, retenção, pseudonimização de identificadores, criptografia/backup adequados e outputs agregados. Não unir dados de favoritos, contas ou rotinas pessoais ao dataset sem análise específica. Segredos e DSNs não pertencem a manifestos ou documentação.

## Referências

`GpsEtaClient.cs`, `GpsPollingService.cs`, `TelemetriaMl*.cs`, `ViagemOperacional.cs`, `HistoricoEventoRepository.cs`, `EtaV2Shadow.cs`, workers/repositório ETA V2, migrations e suítes citadas no inventário; repositório ML `server.py`, `treinar.py`, `dataset_v1.py`, `posicao_corrigida.py` e `validacao_temporal.py`.

## Conclusão

A arquitetura contém os elementos para medir um baseline causal, mas não evidência para promover ML supervisionado. O caminho correto é qualidade do label → dataset reproduzível → baseline → comparação → shadow/canary → eventual integração.

**Veredito documental: `READY_FOR_STAGE_2_4`.**

