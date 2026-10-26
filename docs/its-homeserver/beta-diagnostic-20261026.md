# Janela beta diagnóstica ITS + GTFS — 26/10/2026

**INCONCLUSIVA para snapshot completo, ganho de frota e GPS no mapa.** Uma única tentativa autorizada foi executada e interrompida pelo bloqueio crítico de indisponibilidade HTTP, não pelo gate89890s, frescor, cobertura ou atraso da ITS. GTFS-RT foi restaurado obrigatoriamente; não houve segunda ativação. A ITS respondeu com dados reais de toda a consulta SPPO: três páginas de uma janela UTC, **13.956 registros brutos**, antes da interrupção.

Modo executado: commit local `cf672fe466731424d03d75ab7c970715ec2ce6fb`, sobre a base `4b945dfb6ff6903c7bab5a089a32fab7fbe032a8`. Imagem preservada da release151, `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`. A correção de recuperação descrita adiante é **posterior, somente local e não executada no servidor**. Horários UTC, segunda-feira26/10; não comparar quantidades com sábado24/10 sem controlar a operação.

## Respostas às cinco perguntas

| Pergunta | Resultado que a evidência permite |
|---|---|
| ITS entrega snapshot completo? | **Não comprovado nesta tentativa.** A primeira janela UTC22:35 foi percorrida com três páginas e o adaptador iniciou22:34; o snapshot exige a aquisição completa das janelas configuradas e foi cancelado antes disso. Não promover registros parciais como frota completa. |
| Quanto demora e por quê? | Na primeira janela UTC: headers5,751s, body2,739s, parse0,569s, normalização0,206s, soma≈9,266s. Observou-se≈10,69s do início do primeiro HTTP ao encerramento da página interrompida seguinte. **Não é duração de aquisição concluída.** Espera HTTP/transferência dominou o trecho disponível; não há evidência para atribuir o caso histórico48,6s ao parser. |
| Quantos veículos adicionais? | **Não mensurável.** 13.956 são registros de posição, com repetições por veículo/minuto/provedor. Não houve resumo de deduplicação/composição ou snapshotITS completo; não há contagem confiável de veículos únicos/adicionais. |
| Híbrido melhora o mapa? | **Não mensurável nesta janela.** Nenhuma amostra REST completa foi arquivada depois da subida da API ITS; a única `its_sample` ainda observava GTFS durante a parada. A indisponibilidade/reconexão na troca é efeito operacional real, mas não prova qualidade inferior dos dados ITS. |
| Quais mudanças trazem benefício? | Prioridade imediata: corrigir a rechecagem de recuperação HTTP (feito apenas localmente), medir/reduzir a parada/recriação da API e desacoplar a aquisição GTFS da espera ITS com concorrência limitada, mantendo um compositor/escritor. Depois avaliar redução de payload repetido por veículo/janela mediante contrato. Não há motivo aqui para otimizar primeiro os≈206ms de normalização ou alterar TTL/regrasGPS. |

## Modo beta e testes

O padrão permanece estrito. Só `experiment_mode=beta_diagnostic` junto de `beta_authorized=true` libera o modo explicitamente autorizado por janela. O prazo89890s vira diagnóstico, com prova tardia registrada separadamente; ausência inicial de contribuição ITS, lentidão/ACK/frescor e cobertura não disparam sozinhos rollback. Ambas as fontes ainda precisam contribuir para classificar `healthy`; suporte saudável sozinho é `degraded`. Nenhuma validação de identidade, coordenadas, GPS físico, timestamps, CAS ou regras do backend mudou.

Continuam críticos: API/observação persistentemente indisponível, fontes sem posição utilizável, publicação inválida/duplicada/regressiva, escritores múltiplos, CAS de infraestrutura, OOM/restart inesperado, falha grave de dependência/pressão de memória, parada manual e600s. Watchdog/lock/identidade do controlador e rollback permanecem. O modo estrito conserva os gates anteriores.

Antes da execução: **89/89 supervisor +33/33 captura**. Houve uma falha de timing no teste existente de fechamento de fila em execução paralela (`pending=0`); repetição isolada33/33 passou, sem alterar o capturador. Após a investigação local: **91/91 supervisor**, capturador inalterado33/33. Provas POSIX anteriores não foram repetidas; .NET/CI remota não executados porque C#/imagem não mudaram.

## Preflight, baseline e segurança

Preflight rápido: release/digest151, BUSGTFSRT_BUS/fallback vazio/FullFallback/tracefalse e BRTGTFSRT_BRT; umaAPI, lock livre, RedisPONG/PostgreSQLaceitando/HTTP/SignalRsaudáveis, nenhum supervisor/janela concorrente. Host quatroCPUs, RAMdisponível2.259.992KiB, swaplivre4.894.456KiB, disco19.404.582.912bytes. Oito containers semOOM.

Diretório privado novo `/home/guilherme/.noponto-deploy/beta-diagnostic-20261026T223000Z`, modo0700/arquivos0600. Cinco blobs GitUTF8/LF conferidos; nova captura única PID1033147/nascimento12473143/sessão própria/inode3670640, independenteSSH/lock. Cronproof real às22:32:01 mostrou ancestralidadecron/systemd. Estado beta explícito, contrato de captura preservado ao armar scheduled; supervisor real adotou o mesmo leitor. Não se repetiu a bateria POSIX isolada.

Oito amostras formais GTFS:22:33:04,670→22:34:59,514. **130,27s entre a primeira amostra concluída e a solicitação ITS**. Frota83818–19 e89815;006/805 vazias legitimamente. Baseline terminou com88mensagens SignalR e zero erro de integridade.

Solicitação ITS: **22:35:14,942**. Novo container híbrido passou a ser seguido às22:35:46,805. Primeiro HTTPITS disponível terminou22:35:56,017. Interrupção ocorreu por volta deT46,8s, inferida pelo timer e erros; o instante exato de solicitação do rollback não foi arquivado. Página em andamento encerrou22:36:01,966; stream híbrido encerrou22:36:03,241. GTFS foi iniciado22:36:03,621 e confirmado saudável em **22:36:14,119**.

**Solicitação→GTFSsaudável:59,18s**, incluindo parada/boot e rollback. O stream do container híbrido foi observado por≈16,44s; não é um `StartedAt` exato do container removido. Retorno estimado≈12,4s a partir do timer, não uma medição precisa do início do rollback. Nenhum limite de600s foi estendido.

## Motivo da interrupção e correção local posterior

O observador registrou erros reais de transporte em `GET /`:T16,83s eT31,87s, ambos durante a troca. O guard usou o primeiro erro para o limite de30s. A próxima tentativa integral estava agendada para aproximadamenteT46,87s, ligeiramente depois de vencer o timer. **Não existe uma terceira observação para provar indisponibilidade contínua ou recuperação entre as leituras.** HTTP da ITS funcionando no processo não prova, por si só, que o listener da API estava acessível. Não atribuir o aborto ao fornecedor ou declarar indisponibilidade contínua comprovada com apenas esses dois pontos.

A reprodução local usou o `observe()` real e relógio falso: erros em16/31, backend recuperado em36, próxima amostra46; o teste ficou vermelho com `backend_unavailable`. A correção local faz rechecagem de recuperação **a cada2s somente enquanto há falha no beta**, verifica a raiz antes da coleta mais longa de linhas/stats/logs e distingue indisponibilidade HTTP (`backend_unavailable`) de impossibilidade persistente de observar (`observation_unavailable`). Mantém30s,15s de observação normal,600s e cadências do backend/GPS; não consulta o provedor extra. O teste passou observando a recuperação em36s. Erro real persistente ainda encerra em30s.

Testes novos: `test_beta_rechecks_recovery_before_outage_deadline_between_full_samples` e `test_beta_metadata_failure_is_not_misreported_as_backend_http_outage`. A correção **não foi aplicada aos arquivos da janela nem usada para nova ativação**. O resultado da tentativa continua inconclusivo. O tempo de parada observado também precisa investigação própria; melhorar amostragem não demonstra que o backend tenha ficado disponível durante a troca.

## Aquisição ITS registrada

| Janela UTC / página | HTTP / cursor | Headers ms | Body ms | Parse ms | Normalização ms | Registros |
|---|---|---:|---:|---:|---:|---:|
|22:35 /1|200 /ausente|3.214,11|1.234,60|288,04|48,31|5.000|
|22:35 /2|200 /presente|1.470,47|913,20|161,13|112,19|5.000|
|22:35 /3|200 /presente|1.066,87|591,11|120,19|45,99|3.956|
|22:34 /seguinte|200 /ausente, body interrompido|1.211,19|198,59|0|N/D|N/D|

Primeiras três páginas:8.668.279bytes de resposta; página interrompida registrou451.710bytes. Esses são volumes HTTP, **não** tamanho do arquivo de logs. A transição do adaptador para22:34 confirma o término da janela22:35, mas não o snapshot completo. Nenhum provider pending ou timeout do fornecedor foi registrado nesse trecho; o último body foi interrompido durante o rollback. Não há resumo/timing final para totalizar o restante ou idades físicas no início/fim.

Registros brutos por provedor: **ZIRIX7.342 / CONECTA6.614**; rejeições **service426 / physical_stale142**, total568. Restam13.388registros após essas rejeições conhecidas, **antes de deduplicação/validação final**; não equivalem a veículos. ZIRIX aqui é provedor daITS, sem uso operacional da API legadaZIRIX_DIRECT. Não houve resumo `BUS hybrid`, contribuição por fonte, compartilhados/exclusivos/conflitos, geração898 ou CAS/REST/SignalR correlacionados do híbrido. Esses valores são N/D, não zero.

## Mapa antes e depois

Último baseline e leitura final REST em GTFS: veículos / até40/60/180s / idade mediana/p90. Não são comparação de Hybrid saudável: a coluna durante não existe. Variações depois da volta aGTFS não podem ser atribuídas a ganho ITS.

|Linha|Baseline GTFS|Após retorno GTFS|
|---|---:|---:|
|BUS838|18 /0/6/17 /64,8/165,3|18 /0/9/18 /60,5/90,0|
|BUS898|15 /0/11/15 /53,3/71,3|14 /0/7/14 /63,0/70,0|
|BUS006|0 /0/0/0 /—|1 /0/0/1 /64,0/64,0|
|BUS457|21 /0/8/21 /63,3/70,3|23 /0/8/23 /65,0/73,0|
|BUS483|26 /0/12/25 /60,8/72,3|26 /0/15/26 /59,0/73,0|
|BUS355|6 /0/1/6 /65,3/178,3|5 /0/1/5 /65,0/70,0|
|BUS805|0 /0/0/0 /—|0 /0/0/0 /—|
|BRT35|12 /0/10/11 /50,8/78,3|11 /0/9/9 /52,0/193,0|
|BRT60|25 /1/23/25 /48,3/54,3|25 /0/21/24 /52,0/131,0|
|BRT67|9 /2/9/9 /51,3/55,3|11 /0/10/11 /52,0/57,0|

Houve uma desconexão SignalR observada durante a troca; não se obteve série completa do novo backend antes do corte. O período não chegou90s, portanto a métrica de correlação898 não foi concluída e não motivou interrupção. Não houve publicação inválida/regressiva/duplicada nas sondas pré/pós; não extrapolar isso para prova completa de publicação durante Hybrid.

## Recursos, evidência e estado final

Não houve amostra completa de CPU/RAM do novo backendITS; os recursos mostrados pelo monitor ainda pertenciam à última amostra GTFS. Não apresentá-los como custo híbrido. Host final: RAMdisponível2.269.244KiB, swaplivre4.899.832KiB, disco19.426.402.304bytes; nenhumOOM ou reinício inesperado. Sete containers dependentes com IDs/configurações/inícios/reinícios idênticos antes/depois. API foi recriada apenas nas duas trocas autorizadas, mesma imagem151, RestartCount0. BRT permaneceuGTFSRT_BRT. Não houve intervenção administrativa emRedis/PostgreSQL; pipeline normal permaneceu responsável por suas escritas.

Captura297,74s,1.304.075bytes de logs lidos,4.814bytes/14registros arquivados; três IDs seguidos nas duas trocas, zero overflow/gap/truncamento/duplicatas. Footer: `integrity_valid=true`, `archive_complete=true`, `evidence_complete=false`, uma aquisição sem timing/resumo pelo cancelamento. SHA256 `9e6a40b3dacfeac631ac9ab996aa1a64042d1f800161281d076dc0ffa0d8f588`. Sem URL completa, cursor, token, identidade de veículo, coordenada ou payload individual. Arquivo encerrado/verificado uma vez depois deGTFS; não se atrasou rollback para salvar logs.

Cron removido22:37:53 após confirmarcomplete/GTFS/saúde; nenhum processo temporário e lock livre. Sonda final22:38:36: HTTP/RedisPONG/PostgreSQL/SignalRsaudáveis,18listas em35s, zero erros de integridade. Fonte finalBUSGTFSRT_BUS/fallback vazio/FullFallback/tracefalse; BRTinalterado. Overrides referenciados pelos labels, evidências privadas e laboratórios preservados. Nenhum push/merge/novaimagem/migration/limpeza ou segunda janela.

## Revisões e próximos benefícios técnicos

Executado: supervisor SHA256 `0265d508abade04581e3feaafecfda7d458fe505cc6203a8ad05fa20be193259` (commitcf672fe). Correção local posterior: `e713e30385433ddcdc683d2eeb418199e227451caa23b9dc46108a8fb1c15b62`, não instalada. Capturador permanece `055fa54b065d08d44b54d697e9b7924792af34323f27bc9631dc9146919d49ac`; os outros três hashes constam no [JSON](beta-diagnostic-20261026.summary.json).

O resolver atual espera `primary.GetResultAsync` antes de `ReadFallbackAsync` em `ItsFallbackGpsSource.cs`. Portanto, investigar aquisição GTFS independente/concorrência estritamente limitada é uma melhoria concreta para não prender o suporte à conclusão ITS; preservar compositor, backpressure, matching/CAS e escritor único. A parada/recriação também dominou esta tentativa: reduzir downtime com shutdown/cancelamento cooperativo ou outra troca operacional segura tem prioridade; sua causa interna não foi determinada aqui. Dados por minuto são volumosos e repetidos: avaliar contrato de observação mais recente por veículo/backfill sem perda de cobertura pode reduzir I/O, mas não implementar consulta truncada nem alterar filtros para obter números melhores.

Não há base para aprovar o híbrido no mapa, nem para declarar que a ITS é incapaz de entregar frota completa. A entrega comprova paginação real e normalização de volume significativo e identifica um limitador de supervisão/startup. A correção local está testada; **nenhuma nova janela foi solicitada ou iniciada automaticamente**.
