# Publicação híbrida com operação exclusivamente GTFS — 24/10/2026

Deploy autorizado apenas da API concluído. ITS e Hybrid não foram ativados; nenhum cron/supervisor foi instalado ou armado. A segunda janela depende de autorização separada.

- Candidata `6e7425fd7e2438cd734506e26f34bfc09660b252`, implementação `7bf00fc`, [PR 84](https://github.com/UVAEstudantes/noponto-backend/pull/84). CI PR [148](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40129995501): 1057/1057 offline, zero falhas/ignorados. Árvore de teste e merge conferidas contra HEAD, sem diferença. Workflows/deploy/Dockerfile/migrations preservados; laboratórios não rastreados não publicados.
- Merge `6f2c8d66380865812210f4ea70e4cb72a6ea991c`, árvore `984c18c1839563d6937f85025f7305b106aca294`.
- [Workflow 149](https://github.com/UVAEstudantes/noponto-backend/actions/runs/40130221350): CI main 1057/1057 offline, build/publicação e deploy aprovados. Environment production aprovado somente após conferir revisão/digest/escopo; nenhuma proteção contornada.
- Imagem `ghcr.io/guilhermedesales/noponto-api@sha256:44b006a8e7f515572b4fe457b6d56cc757cac306cfc9964d0ad05031c3725835`. Referência e ID da imagem em execução coincidem; active.env e active.run=149 consistentes. Deploy confirmado pelo workflow às 20:37:56 UTC; API iniciou às 20:37:45 UTC, zero restart/OOM.
- Imagem anterior preservada pelo mecanismo de rollback: `ghcr.io/guilhermedesales/noponto-api@sha256:3ea342210229e489f2e987ee8b919a15ea9d20ded23929905bf2b50200c345a3`. Rollback não foi necessário.

## Configuração e segurança

BUS `GTFSRT_BUS`, fallback vazio, modo efetivo `FullFallback`. A variável BusSupportMode continua ausente: o padrão explícito da nova classe é FullFallback, conferido no código; não se acrescentou configuração ao servidor. BRT `GTFSRT_BRT`. Nenhuma aquisição/publicação híbrida habilitada, zero entradas BUS hybrid no log de startup. GTFS BUS presente nos logs. Shadows diagnósticos existentes preservados; fonte operacional de posições BUS exclusivamente GTFS.

Usado somente o mecanismo versionado ssh-deploy.sh/deploy-api.sh pelo Actions, com guard GTFS, deploy.lock e rollback local. Render de quatro arquivos do deploy equivale ao render dos cinco labels anteriores, após normalizar apenas as duas flags BUS opcionais vazias/padrão; conexões, demais serviços e limites iguais. API recriada pelo Compose, uma única escritora verificada antes/depois. Sete dependências preservaram imagem/StartedAt/restarts/OOM/saúde. Nenhum Compose down/prune, migration, limpeza Redis/banco ou comando administrativo de dados; escritas GPS normais continuaram.

## Verificação operacional limitada

Antes: três amostras a partir de 20:30:08 UTC. Depois: três a partir de 20:38:09 UTC, em conexão SignalR nova após deploy; última conferência de logs 20:39:21 UTC. São instantes distintos do sábado; números não constituem comparação de fontes ou benchmark híbrido. Leituras somente em loopback/SSH, sem payload/identidades individuais arquivados.

Cada célula contém veículos REST / veículos com GPS físico até 60 s na última amostra de cada período:

| Linha | Antes | Depois |
|---|---:|---:|
| BUS 838 | 14 / 11 | 19 / 11 |
| BUS 898 | 8 / 7 | 9 / 6 |
| BUS 006 | 0 / 0 | 0 / 0 |
| BUS 457 | 16 / 10 | 15 / 9 |
| BUS 483 | 26 / 17 | 24 / 14 |
| BUS 355 | 6 / 3 | 6 / 5 |
| BUS 805 | 0 / 0 | 2 / 1 |
| BRT 35 | 4 / 0 | 6 / 5 |
| BRT 60 | 17 / 0 | 18 / 16 |
| BRT 67 | 10 / 0 | 9 / 8 |

006 permaneceu 404 com mensagem exata de vazio nas amostras; não é falha da API. 805 voltou a apresentar veículos. As seis linhas BUS não vazias têm GPS até 60 s; não declarar sete linhas ativas nem eliminar filtros para produzir posições. 898 mantém cobertura e GPS recente: 8/7 antes, 9/6 depois; todos os nove até 180 s depois. Continuidade observada por sucessivas amostras/publicações, sem afirmar rastreamento individual persistido entre conexões distintas. BRT já tinha GPS envelhecido antes e voltou a renovar nas três linhas, sem mudança da fonte.

HTTP raiz 200, Redis PONG e PostgreSQL pg_isready aceitando conexões; Redis/Postgres Docker healthy. SignalR pós-deploy: uma conexão, 18 listas não vazias, zero erros/regressões/IDs duplicados por lista/coordenadas inválidas. Dois ciclos com timestamp posterior ao início da observação: máximo 8,146 s, 1498 aceites CAS e zero falhas CAS de infraestrutura. Antes: três ciclos, máximo 9,341 s, 1725 aceites. Contagens não são veículos únicos nem garantem ausência de falhas fora da amostra.

API pós-deploy na última amostra: 0,82% CPU e 164,7 MiB/768 MiB; limites 1 CPU/768 MiB/256 PIDs preservados. Amostras agregadas completas no resumo; não são picos nem teste de carga. Logs desde startup até 20:39:21 UTC: zero entradas fail/crit, zero marcadores de execução de migration, zero BUS hybrid. Nenhum job its_window.py no crontab.

## Próxima autorização

Pronto para solicitar autorização separada da janela híbrida de 10 minutos, não para iniciá-la automaticamente. Falta confirmar estado/lock imediatamente antes da janela, preparar diretório privado com arquivos da revisão, provar cron independente em modo proof e coletar baseline imediato das sete BUS/três controles. Exigir evidência de renovação física da 898 via suporte e monitoramento humano dos critérios de interrupção. Retorno obrigatório a GTFS/FullFallback ao fim; não alterar TTLs, BRT, banco ou dependências.

[Resumo e hashes de evidências locais](gtfs-hybrid-binary-deploy-20261024.summary.json). Relatório de conclusão mantido localmente, sem novo push/merge ou segundo deploy.
