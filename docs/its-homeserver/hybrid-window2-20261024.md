# Segunda janela híbrida — 24/10/2026

**INCONCLUSIVO; não aprova permanência nem outra ativação automática.** A janela foi interrompida pelo operador porque a prova exigida de novos GPS físicos da 898 em REST e SignalR não foi obtida dentro de 90 s. Novos GPS correlacionados ao GTFS apareceram no REST em 97,6 s; a correlação no socket só apareceu após o retorno a GTFS. Isso não prova ausência de publicação no intervalo: a observação independente não usa o exato snapshot HTTP cacheado pelo backend e pode perder coincidências. Não declarar defeito na identidade/898 ou aprovação com base apenas na seleção agregada.

**Estado final:** release149, mesma imagem `ghcr.io/guilhermedesales/noponto-api@sha256:44b006a8e7f515572b4fe457b6d56cc757cac306cfc9964d0ad05031c3725835`, BUS GTFSRT_BUS/fallback vazio/FullFallback explícito, BRT GTFSRT_BRT. Uma API escritora, zero restart/OOM, Redis PONG/PostgreSQL pronto. Cron temporário removido; override GTFS preservado nos labels. Sem push/merge/publicação de imagem/migration/limpeza/alteração de TTL ou dependências.

## Proteções e execução

Supervisor/sonda/overrides aprovados copiados byte a byte, hashes iguais aos do preflight. Diretório privado novo `hybrid-window-20261024-b5e2b75c68`. Crontab original ausente; instalado somente um job marcado. Prova real via cron: ancestrais python3/sh/cron/cron/systemd, independent_cron=true. Lock testado por abertura independente recusada, depois mantido pelo supervisor durante baseline/troca/rollback. O watchdog permaneceu agendado até phase=complete e lock livre; não foi necessário acioná-lo por falha da sessão. Rollback utiliza imagem local sem pull e parada da API anterior antes de recriar.

Após o deploy149 havia quatro arquivos Compose efetivos, não cinco: base, overlay GTFS, PhysicalETA e Shadow. Todos preservados e validados pelo controlador. O rollback desta janela acrescentou o quinto arquivo privado gtfs.rollback.override.yml; ele foi retido por estar referenciado pelos labels. Os sete containers dependentes preservaram imagem, StartedAt, restart e OOM.

O supervisor executou baseline de pelo menos120s (loop120s e overhead), oito leituras entre21:18:03 e21:19:57UTC; ativação solicitada21:20:12,833UTC (18:20local). Observação independente registrou sete amostras GTFS e cinco Hybrid com mediana/p90. Uma leitura em trânsito pode conter Redis/cache anterior à troca; configuração não reetiqueta instantaneamente todas as posições.

Tempos verificados por estado e eventos Docker:

- Hybrid iniciou no container às21:20:15,829UTC.
- Pedido de interrupção/registro RuntimeError às21:21:59,260UTC,106,4s após início da ativação. O registro genérico é compatível com interrupção de subprocesso pelo SIGTERM enviado ao grupo verificado; comando/status não foram arquivados, portanto não atribuir a erro HTTP ou ao provedor. O motivo operacional foi o gate898 não comprovado; o controlador guardou exception_RuntimeError.
- Container Hybrid terminou às21:22:30,227UTC: **134,4s de execução**,137,4s desde o início da ativação. Parada levou o timeout30s e foi concluída antes de iniciar outra API.
- GTFS iniciou às21:22:30,814UTC; saúde/rollback confirmado21:22:41,039UTC,148,2s após ativação. Nenhuma aproximação ao teto600s.
- Cron removido somente depois de complete e verificação independente da fonte/saúde; conteúdo anterior vazio restaurado, sem apagar outros jobs. Diretório privado e overrides preservados.

## Publicação por linha

Tabelas usam médias de veículos e contagens até40/60/180s por leitura REST, nas sete amostras GTFS e cinco Hybrid da sonda independente. Idades são a mediana das medianas e a mediana dos p90 por leitura, não percentis globais de observações reunidas. Amostra curta de sábado, sem comparação com dias úteis; não é benchmark nem prova de melhoria consistente.

### Ônibus

| Linha | Veículos GTFS→Hybrid | Até40 s | Até60 s | Até180 s | Mediana/p90 GTFS→Hybrid (s) |
|---|---:|---:|---:|---:|---:|
| 838 | 13.3→18.2 | 0.6→0.0 | 6.9→2.6 | 13.3→18.0 | 59.1/80.1→76.0/109.0 |
| 898 | 11.7→12.0 | 0.0→2.2 | 5.9→3.8 | 11.4→12.0 | 60.6/72.0→66.1/75.4 |
| 006 | 0.0→0.0 | 0.0→0.0 | 0.0→0.0 | 0.0→0.0 | —/—→—/— |
| 457 | 18.0→20.2 | 0.4→0.4 | 7.0→4.0 | 17.4→20.2 | 66.1/103.0→75.1/105.0 |
| 483 | 31.0→44.2 | 1.3→0.0 | 15.0→7.6 | 30.6→44.0 | 62.1/74.0→72.1/87.0 |
| 355 | 6.0→9.2 | 0.0→0.2 | 3.0→1.8 | 6.0→8.8 | 54.6/64.1→72.0/83.1 |
| 805 | 2.0→2.0 | 0.0→0.0 | 1.0→0.4 | 2.0→2.0 | 58.5/62.0→78.6/82.1 |

838/457/483/355 ganharam cobertura média, mas reduziram a quantidade média até60s e tiveram idades maiores. 898 preservou/aumentou cobertura, com frescor oscilante; prova do suporte por veículo ficou incompleta no prazo. 805 manteve cobertura sem ganho consistente de frescor. 006 permaneceu legitimamente vazia,404 com mensagem exata, sem tratar esse vazio como falha da API. Não se publicaram todos os veículos adquiridos nem se alteraram filtros para isso.

### BRT, somente controle

| Linha | Veículos GTFS→Hybrid | Até40 s | Até60 s | Até180 s | Mediana/p90 GTFS→Hybrid (s) |
|---|---:|---:|---:|---:|---:|
| 35 | 4.7→5.0 | 1.9→2.0 | 4.1→3.8 | 4.6→5.0 | 42.1/60.0→42.4/51.4 |
| 60 | 18.7→17.8 | 5.3→7.0 | 14.1→12.8 | 18.1→17.4 | 46.1/117.0→44.4/53.4 |
| 67 | 9.0→9.0 | 3.3→3.6 | 8.6→7.0 | 9.0→9.0 | 42.1/53.1→42.4/53.4 |

As três linhas continuaram em GTFSRT_BRT. Flutuações de quantidade/idade não foram atribuídas à ITS. Não houve reinício de dependências ou mudança da fonte BRT; não houve degradação HTTP sustentada documentada. Autenticação `/` retornou403 no baseline e durante Hybrid; latência medida0,651/0,014/0,054s. Esse probe verifica estabilidade da rota, não autenticação de usuário, e não produz falha de login artificial.

## Aquisição versus aceitação versus publicação

Dois lotes completos ITS/Sucesso, três janelas UTC cada:7páginas/24934registros/1855rejeitados/2374veículos em8,235s;8páginas/32488registros/10082rejeitados/2375veículos em48,554s. Rejeitados são registros da validação, não veículos únicos nem todos falhas do sensor. Idade ITS p50/p90 passou34,0/48,0s→63,4/78,4s. O segundo lote tinha zero veículos até40s e909até60s antes da composição. Motivo interno da demora48,6s não foi isolado; não atribuir a paginação, Retry-After ou fornecedor sem evidência adicional.

| Lote | ITS entrada | GTFS entrada | Selecionados | ITS escolhidos | GTFS escolhidos | Compartilhados | Conflitos coord./linha/sentido |
|---|---:|---:|---:|---:|---:|---:|---:|
|1|2374|1409|2373|1702 (71,7%)|671 (28,3%)|1354|31/28/0|
|2|2375|1385|2431|1046 (43,0%)|1385 (57,0%)|1329|0/0/0|

Soma por lotes4804escolhas:2748ITS (57,2%)/2056GTFS (42,8%). Isso repete veículos entre snapshots; não é frota única nem distribuição das posições publicadas. No primeiro lote,56identidades foram excluídas da união; coord./linha podem sobrepor. Dados inválidos na composição=0; validações anteriores e matching ainda descartam posições. As duas fontes estavam saudáveis nos dois lotes, sem fallback completo automático observado. Retorno final foi rollback operacional.

Ciclos com timestamp entre ativação e confirmação do rollback (o final pode incluir transição GTFS):8, máximo12,272s,4255aceites CAS, zero infra e dois consumos de snapshot/pending=false. No baseline delimitado:12ciclos, máximo8,354s,6148aceites CAS e quatro consumos. Matching máximo4,458s híbrido/5,187s baseline. ACK/handoff inferidos de consumo/pending=false, **IDs de geração e ACK individual não arquivados**. Contagens incluem BUS+BRT e repetições temporais, não veículos únicos. Não há evidência de fila pendente90s, ciclo>60s ou três ciclos>24s.

SignalR da sonda independente acumulou117listas até a última amostra baseline e171até a última amostra Hybrid:54listas adicionais entre essas leituras. Zero regressões/IDs duplicados na mesma lista/coordenadas inválidas. Houve sete falhas transitórias de conexão na recriação de entrada e mais sete no rollback, com reconexão; não tratadas como zero falhas totais. Ao final, uma conexão nova recebeu27listas/zero erros, sem usar somente as listas retidas da conexão anterior. Duas ProbeError da sonda auxiliar ocorreram durante rollback; rota/status não foram guardados nesse agregado. Não atribuí-las ao404 legítimo006.

### Prova específica898

Antes da ativação, a sonda GTFS limitada (FULL_DATASET2.0, limites de resposta/entidades, rota validada O0898AAA0A, normalização de identidade, GPS físico, coordenadas, velocidade/direção válidas) correlacionou até12posições publicadas REST/socket. Nenhuma identidade ou payload individual foi persistido. Consultas GTFS adicionais espaçadas no mínimo45s, na prática cerca60s; cache de observações somente em memória.

Aos97,6s, REST898 tinha12veículos com identidade/GPS/coordenadas correspondentes ao GTFS e **timestamp físico posterior à ativação**. O socket então tinha zero correspondências dessa renovação na sonda; após rollback,12novas correspondências apareceram no socket. Isso comprova renovação no REST além de mera retenção de baseline, mas não satisfaz a prova conjunta dentro90s. Contagem global selected_gtfs não foi usada como substituto. A amostragem/caches podem perder coincidências; não concluir que o backend jamais publicou essas posições em Hybrid.

## Recursos e estado final

CPU API amostrada atingiu103,46% em um pico, dentro da semântica de cerca de uma CPU; não houve saturação sustentada com degradação comprovada. RAM API nas leituras independentes Hybrid141,2–192,3MiB/768MiB. Host disponível nessas leituras sempre acima2,18GiB; amostras do supervisor também permaneceram confortáveis. Swap livre caiu cerca43MiB em relação ao fim do baseline, sem OOM; variação deve entrar na investigação, não afirmar swap zero. PostgreSQL atingiu cerca100% de uma CPU em amostras, com picos também no baseline. Não são perfis contínuos de CPU nem picos absolutos.

Verificação independente após remoção do cron, três leituras a partir de21:23:24UTC: uma API, release149/active.env consistente, fontes corretas, zero restart/OOM, dependências idênticas;27listas socket sem erros de integridade; três ciclos, máximo8,588s/1516aceites CAS/zero infra. Última leitura898:12veículos,12até60s, todos até180s. API0,66%CPU/180,1MiB; Redis/PostGIS operacionais. Sem rollback de imagem; imagem149 saudável preservada.

## Resultado e próximos critérios

**INCONCLUSIVO para homologar Hybrid**, encerrado com retorno obrigatório cumprido. Há evidência de ganho de cobertura e participação real das duas fontes, mas não de ganho consistente de frescor/publicação ao longo de10min. A aquisição48,6s e a falta de prova898 conjunta dentro90s impedem aprovação. Não são prova de cobertura completa, defeito de identidade ou necessidade de ajustar TTL/cadência.

Antes de propor outra janela, esclarecer o caminho/latência da segunda aquisição e completar instrumentação para correlacionar a mesma observação898 no snapshot selecionado, CAS e socket, incluindo geração/ACK explícitos e motivo da interrupção. Melhorar rastreabilidade da sonda sem suavizar os gates; qualquer alteração de código deve ser testada/publicada separadamente. Não reativar por esta autorização. Produção permanece exclusivamente GTFS.

Testes curtos: supervisor24/24 antes da instalação, hashes/compilação da sonda e verificações de integridade dos agregados. .NET/offline/E2E não repetidos por não haver alteração operacional de código; permanecem as evidências anteriores da release149. Relatório/resumo locais, sem novo push/merge/deploy. [Resumo verificável](hybrid-window2-20261024.summary.json); artefatos agregados em artifacts/hybrid-window2-20261024.
