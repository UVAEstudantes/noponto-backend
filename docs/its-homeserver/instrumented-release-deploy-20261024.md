# Deploy da revisão instrumentada — release151,24/10/2026

Deploy GTFS-only concluído e saudável. A terceira janela não foi executada nem agendada. Sem instalação/cópia de supervisor novo, cron, ativação ITS ou trace. Produção foi consultada somente para preflight/health/configuração e alterada exclusivamente pelo deploy autorizado da API.

## Publicação e validação

- Candidata `0fdf0d6417c13126fcecd40fe33b3921ef296f15`, branch feat/its-current-sppo. Preservados artifacts/, tools/ItsShadowLab/ e tools/ItsShadowLab.Tests/, sem incluir conteúdo não rastreado. Hashes de código/evidências conferidos. O diff inclui instrumentação e relatos locais prévios; nenhuma alteração em workflow/.deploy/Dockerfile/migrations.
- [PR85](https://github.com/UVAEstudantes/noponto-backend/pull/85), [CI150](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40138761884): restore/build **Release** aprovados, zero erros;1076/1076 offline, zero falhas/ignorados, verificador confirmou todas as classes. GpsPipelineTraceTests incluída na allowlist; métodos novos também entram nas classes existentes. Isso resolve a pendência de build Release que não pôde ser executado localmente na etapa anterior.
- Merge `15ad5ccfa850bb3a03c8f2130a57341e79381ff7`. Árvore `5e5339a855a1b6f7ec874ccb5754dfea377d9460`, igual à candidata e à árvore de teste da PR; expected_head_sha conferido antes do merge.
- [Workflow151](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40138978575): CI main com build Release e1076/1076 offline aprovados, build/publicação GHCR e deploy bem-sucedidos. Environment production aprovado após conferir revisão, digest, fonte, lock e escopo, sem contornar proteções.
- Imagem implantada: `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`. Config.Image, ID/digest em execução, active.env e active.run=151 consistentes. API iniciou22:38:01,659UTC; deploy confirmado22:38:12,939UTC.
- Imagem anterior preservada pelo rollback existente: `ghcr.io/guilhermedesales/noponto-api@sha256:44b006a8e7f515572b4fe457b6d56cc757cac306cfc9964d0ad05031c3725835`. Rollback de imagem não foi necessário.

## Configuração e isolamento operacional

Uma API escritora. BUS GTFSRT_BUS/fallback vazio, FullFallback efetivo; GpsPipelineTrace.Enabled=false efetivo. As variáveis opcionais BusSupportMode/TraceEnabled ficaram ausentes na composição de quatro arquivos; valores padrão da revisão e appsettings foram conferidos, sem outro setting que os habilite e sem .env dentro do container. Não foram inseridas flags em arquivos produtivos apenas para repetir os defaults. BRT permaneceu GTFSRT_BRT.

Preflight22:28UTC e imediatamente antes da aprovação22:37UTC: release149 saudável, deploy.lock livre e crontab ausente. Os cinco Compose existentes foram renderizados em memória e comparados aos quatro do deploy; equivalência após normalizar somente os defaults opcionais, inclusive conexões/limites/demais serviços. O quinto override histórico da segunda janela foi preservado no disco, embora a nova API volte aos quatro labels versionados. Não apagar arquivos de janelas antigas nesta entrega.

Usado exclusivamente Actions → ssh-deploy.sh/deploy-api.sh, guardGTFS, lock e rollback de imagem. Nenhum Compose down/prune, migration, limpeza Redis/SQL, reinício dirigido a dependência ou alteração deTTL/matching/fonteBRT. Os sete containers dependentes mantiveram imagem, StartedAt, reinícios, OOM e saúde. Escritas GPS operacionais normais continuam.

## Verificação pós-deploy

HTTP raiz200; RedisPONG, PostgreSQL pg_isready aceitando conexões e ambos Docker healthy. API zero reinício/OOM. Sonda de três amostras a partir de22:39:10UTC:27listas SignalR, uma conexão, zero erros/regressões/IDs duplicados por lista/coordenadas inválidas. Dois ciclos com início posterior à sonda: máximo7,361s,1261aceites CAS, zero infra. Antes: três ciclos, máximo8,178s/1525aceites. São contagens do runtime BUS+BRT, não veículos únicos.

Logs desde startup até22:40:01UTC: zero fail/crit, zero GPS flow, zero BUS hybrid e zero ITS HTTP page; GTFSRT_BUS presente. Zero marcadores de execução de migration. Nenhum novo diagnóstico sensível persistido pelo trace: está OFF, não houve coorte registrada/exportada nem mudança de contrato/armazenamento. Crontab continua ausente e zero jobs de janela.

CPU API amostrada1,71–27,19%, RAM171,1–177,6MiB/768MiB; limites1CPU/768MiB/256PIDs preservados. Host disponível acima2,27GiB e SwapFree4853752KiB constante nas três leituras. PostgreSQL teve pico amostrado de uma CPU, sem perda de saúde/CAS/latência sustentada demonstrada; não houve mudança de banco. Amostras não são benchmark ou picos absolutos.

Cada célula abaixo é veículos REST / veículos com GPS físico até60s. Antes é a última amostra da sonda prévia; depois é leitura curta de seguimento22:41:08UTC, ainda GTFS. Os períodos são diferentes em sábado à noite; os números não comparam fontes nem provam melhoria de frescor.

|Linha|Antes|Depois|
|---|---:|---:|
|BUS 838|13 / 0|14 / 12|
|BUS 898|11 / 0|12 / 9|
|BUS 006|0 / 0|0 / 0|
|BUS 457|16 / 0|16 / 12|
|BUS 483|29 / 0|28 / 24|
|BUS 355|4 / 0|6 / 4|
|BUS 805|2 / 0|2 / 2|
|BRT 35|6 / 6|5 / 0|
|BRT 60|17 / 15|14 / 6|
|BRT 67|9 / 9|8 / 1|

898 manteve fluxo e renovação:11veículos na sonda inicial/pós-deploy,12na leitura posterior, nove até60s. 006 retornou o404 exato de linha vazia; não é falha da API. As seis BUS não vazias tinham GPS até60s na leitura posterior. BRT35 tinha cinco veículos, todos até180s e mediana68,2s, portanto nenhum até60s naquele instante; BRT60/67 também renovaram posições, com idades variáveis. Não declarar frescor perfeito nem alterar filtros para melhorar as contagens.

## Terceira janela: revisão documental, sem instalação

O binário instrumentado está implantado e validado em Release, com trace desligado. Isso prepara a instrumentação, **não libera a terceira janela**. Antes de armar:

1. Obter autorização específica e reinspecionar imagem151/estado/lock/escritor único/recursos. Nova janela de até600s, sem permanência autorizada.
2. Garantir captura limitada, somente leitura, dos novos logs `ITS HTTP page`, `ITS normalized page`, `ITS timing` e `GPS flow`. **O parse_logs/sample atual do supervisor não inclui as três primeiras famílias no events.jsonl**; arquiva aquisição resumida e flow. O rollback recria a API e destrói o container anterior: depender de consultar seus logs depois pode perder a prova por página. Preparar/verificar captura reconectável e limitada antes da janela, ou retenção comprovada dos agregados antes da recriação. Não adiar rollback para salvar logs. Isso pode ser uma sonda de leitura, sem outra imagem/backend escritor; nenhuma foi instalada agora.
3. Copiar arquivos revisados para novo diretório privado e conferir hashes contra0fdf0d6. Preservar os Compose efetivos. Provar cron independente em modo proof, lock e escritor único antes de scheduled. Não reutilizar state/PID/prova de janelas anteriores.
4. Baseline mínimo120s: BUS838/898/006/457/483/355/805, controlesBRT35/60/67; idades40/60/180, mediana/p90, REST/socket, CAS/ACK, CPU/RAM/swap. Vazio legítimo006 não é defeito.
5. Durante Hybrid usar as três flags BUS e toggle diagnóstico dos templates novos, mantendo GTFSRT_BRT. Exigir fontes saudáveis contribuindo, detalhar HTTP/body/parse/normalização por página/janela/provedor e rejeições/expiração. Preservar budgets, limites e cadências.
6. Provar a mesma observação898 do GTFS na geração consumida/CAS/REST/envio concluído/ACK via linked_complete_new, mais renovação física recebida pelo cliente. O envio do servidor não é ACK do cliente. **Gate90s permanece estrito**; não aprovar retroativamente prova tardia nem usar mera contagem selected_gtfs/cache como prova.
7. Validar --request-stop e motivo operacional separado de CommandFailure/ProbeError nos arquivos novos. PID individual verificado para parada humana; watchdog independente de grupo para travamento. Nenhum agendamento/execução nesta tarefa.
8. Retorno obrigatório ao mesmo digest151 em GTFSRT_BUS/fallback vazio/FullFallback/trace=false, no máximo600s ou antes pelos critérios do runbook. Confirmar fonte real, uma API, saúde e phase=complete antes de remover somente o cron marcado; reter overrides referenciados. Não alterar TTLs, banco ou BRT.

Pendência real antes de GO: **captura/retensão dos logs detalhados por página ainda não comprovada**, além de autorização e preflight/prova/baseline específicos. A causa interna dos48,6s continua aberta e é objetivo da próxima investigação, não motivo para suavizar o gate. A revisão documental não mudou código, supervisor, serviços ou configurações produtivas.

[Resumo e hashes](instrumented-release-deploy-20261024.summary.json), artefatos agregados locais em artifacts/instrumented-release-20261024. Relatório de conclusão somente local, sem novo push/merge/deploy.
