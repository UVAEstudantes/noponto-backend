# Primeira janela produtiva ITS — 24/10/2026

**Deploy concluído; janela ITS interrompida, sem aprovação de dez minutos. Produção terminou em GTFSRT_BUS.** A nova imagem permanece saudável. Nenhuma tentativa de reativar ITS foi feita após o rollback.

## Revisão implantada

- Revisão validada: `d56f6d82dbfdd631146d2ea893c569adced2a774`.
- [PR83](https://github.com/UVAEstudantes/noponto-backend/pull/83), merge `bb774fffdfa708bb561cfe16347a07f41b5da725`; árvore idêntica à revisão validada: `e71570ab189667d1e242b593422fa485769e44f3`.
- Imagem: `ghcr.io/guilhermedesales/noponto-api@sha256:3ea342210229e489f2e987ee8b919a15ea9d20ded23929905bf2b50200c345a3`.
- [Workflow147](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40107661744): CI1015/1015, build/publicação e deploy aprovados; aprovação legítima do environment protegido, conforme autorização explícita do operador. Gatilhos/variáveis herdadas e proteções foram conferidos antes do push/merge. Não foi removido guard de deploy.

Primeiro foi implantado o binário com GTFSRT_BUS e GTFSRT_BRT, usando os quatro Compose, digest e mecanismo existentes. Boot, Redis/PostGIS saudáveis, GPS e socket SignalR foram confirmados antes da janela ITS. A imagem anterior foi preservada pelo mecanismo de deploy; não houve falha de implantação que exigisse restaurá-la.

## Supervisão e retorno independente

Cron já estava ativo no host; `Linger=no` tornava inadequado depender somente de timer de usuário. Foi acrescentado um único agendamento temporário privado, sem reiniciar/configurar serviços do host. Sua execução foi comprovada pela cadeia de processos `python3 → sh → cron → cron → systemd`, sem depender da sessão SSH.

A rotina tomou o mesmo `deploy.lock`, coletou baseline de pouco mais de dois minutos, parou a API anterior antes de recriar a candidata, monitorou a cada aproximadamente15–18s e possuía prazo máximo600s e watchdog de cron para supervisor sem heartbeat/prazo vencido. Os overrides foram renderizados em memória e comparados antes da ativação. Não houve outra API escritora. Onze testes determinísticos do controle verificaram falhas de stop/consulta de processos, proibição de iniciar com escritora remanescente, memória, ciclos, fontes e integridade do socket; sem Docker real nesses testes.

Horários UTC (Fortaleza=UTC−3):

| Evento | Horário |
| --- | --- |
| Início da ativação ITS | 15:48:13,639 / 12:48:13,639 local |
| Exceção da sonda e início do retorno | 15:51:11,231 / 12:51:11,231 local |
| GTFS-RT confirmado saudável | 15:51:51,391 / 12:51:51,391 local |

Houve **2min57,6s até o abort e3min37,8s até confirmar o rollback**, incluindo parada/recriação/health. A rotina retornou por conta própria, antes da próxima intervenção via SSH. Não foram completados os dez minutos; não há aprovação para manter ITS.

## Resultado observado

Foram oito amostras de baseline e dez de ITS. Quantidade é a mediana das listas, não número de registros adquiridos. Frescor é a média de veículos com GPS físico até60s na amostragem; não é idade da recepção HTTP ou TTL.

| Linha | Veículos GTFS / ITS | GPS até60s GTFS / ITS, média |
| --- | --- | --- |
| 838 | 20 /24 | 2,5 /13,8 |
| 898 | 9 /7 | 2,625 /0,3 |
| 006 | 2 /2 | 1 /2 |

A 838 ganhou quantidade/frescor e a006 ganhou frescor nesta amostra. **A898 degradou**: os sete veículos da última amostra estavam acima180s de GPS, com máximo aproximado228s. Isso exige investigação; não foi atribuída causa a um bug de normalização, ao provedor ou a matching sem evidência suficiente.

ITS produziu oito aquisições completas observadas,2351–2453 veículos por snapshot, três janelas UTC e5–8 páginas. Aquisição4,91–10,23s; duração de polling é outra medição. Todos os agregados observados foram `Sucesso`; **nenhum fallback automático ITS→GTFS foi observado**. O retorno final foi o rollback operacional, não um fallback da fonte.

Nos ciclos com início dentro das janelas delimitadas: baseline p95/máximo7,810s;ITS p95/máximo12,947s. Durante ITS,13568 aceites CAS observados e zero falhas de infraestrutura;sete consumos de snapshot com pendente=false/CAS saudável permitem inferir ACK. Esses totais incluem o pipeline rodoviário BUS+BRT e são limites observados, pois a última operação em andamento não foi amostrada após o erro. Não confundir com veículos únicos ou somente ITS.

As amostras ITS atingiram61,71%CPU da API e208,8MiB de768MiB;RAM disponível mínima do host2296224KiB, aproximadamente2,19GiB. São amostras, não prova de ausência de picos entre elas. Não houve OOM/restarts inesperados ou alteração dos outros containers. Socket observado durante baseline/ITS:78 listas acumuladas,zero regressões,duplicações dentro da lista ou coordenadas inválidas;seis falhas transitórias de conexão acompanharam a recriação planejada, com reconexão. Snapshots repetidos não foram tratados como eventos GPS duplicados.

## Motivo do abort e limite da conclusão

A rotina encontrou `urllib.error.HTTPError` ao consultar o backend e encerrou a janela conservadoramente. **A instrumentação registrou somente a classe, sem código/status e rota**: não se pode afirmar que houve500,429 ou queda da API. O controlador existente retorna404 quando uma linha fica sem posições; a perda de frescor/expiração da898 é compatível com essa hipótese, mas o status/endpoint do evento não foi preservado para confirmá-la.

Essa é uma limitação do monitor: ele tratava qualquer HTTP não2xx como falha fatal, incluindo o404 legítimo de linha vazia. A janela interrompida não comprova falha do binário nem valida ITS por dez minutos. Antes de nova autorização, registrar status/rota controlados, distinguir vazio operacional de indisponibilidade e esclarecer a lacuna da898. Não foi feita correção em produção nem reaberta a janela para mascarar a interrupção.

## Estado final

- Mesma imagem nova por digest, release147;primária `GTFSRT_BUS`,fallback vazio/desabilitado;BRT `GTFSRT_BRT` inalterado. Sem dependência operacional de ZIRIX_DIRECT.
- Uma API em execução,zero OOM/restarts inesperados;PostgreSQL eRedis saudáveis. Nova aquisição GTFS confirmada em logs,1497–1498 posições promovidas nas leituras finais.
- REST após rollback retornou200:838 com17 veículos/16 até60s;898 com7/5 até60s;006 com2/2 até60s. São leituras de um instante e o frescor continua oscilando. Socket posterior recebeu12 listas,sem erro/regressão/duplicação,com19/7/2 veículos no último conjunto observado.
- Imagem/StartedAt/restarts dos sete outros containers preservados. Foram apenas as recriações previstas da API. Sem migrations,DDL,DML administrativo,limpeza de Redis ou alteração de frontend/BRT/trem/metrô;escritas operacionais normais de GPS ocorreram pelo pipeline existente.
- Agendamento temporário removido e ausência original de crontab restaurada. Arquivos privados de auditoria/override GTFS foram conservados, pois a composição atual os referencia. Não há ITS programada nem tarefa remanescente para reativá-la.

Evidência agregada e hashes: [resumo](production-window-20261024.summary.json). Arquivos locais em `artifacts/its-production-20261024`, sem posições/identidades/coordenadas ou credenciais. Manter GTFS-RT; qualquer nova janela ou mudança de código requer nova autorização, conforme o limite desta execução.
