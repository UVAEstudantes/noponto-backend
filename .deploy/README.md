# Deploy API MVP

Implementação baseada na auditoria 3A entregue na conversa: dois Compose obrigatórios, imagem por digest, API única, nenhuma recriação de dependências. Não chama `/home/guilherme/deploy.sh` nem envia Compose do Git ao servidor.

## Ativação pelo operador

O workflow preserva testes/publicação e adiciona deploy somente em push/main após build aprovado. O job exige a variável `NOPONTO_DEPLOY_ENABLED=true`; manter ausente/false até revisão, autorização e preparação do servidor. Usa o environment GitHub `production`, cujas proteções devem ser configuradas pelo operador. Após ativação, novos pushes aprovados fazem deploy automático; não há workflow_dispatch. Esta implementação não implanta build-108 nem qualquer outra imagem localmente.

Secrets reutilizados: `GHCR_USER`/`GHCR_TOKEN` para publicação, `TAILSCALE_OAUTH_CLIENT_ID`, `TAILSCALE_OAUTH_SECRET`, `SERVER_TAILSCALE_IP`, `SERVER_USER`, `SERVER_SSH_KEY`. Novo secret obrigatório: `SERVER_SSH_KNOWN_HOSTS`, com entrada do servidor verificada por canal confiável. Não executar ssh-keyscan e confiar automaticamente no resultado. Transporte mantém SSH porta 22 e identidade Tailscale `tag:ci` do mecanismo anterior; confirmar ACL/OAuth e porta antes de ativar.

Variáveis obrigatórias, sem defaults inventados:

- `NOPONTO_DEPLOY_ENV_FILE`: caminho absoluto do arquivo real de variáveis utilizado pela composição produtiva. Não é seu conteúdo. Caminho deve usar somente letras, números, `_`, `.`, `/` e `-`.
- `NOPONTO_DEPLOY_STATE_DIR`: diretório absoluto exclusivo para estado do deploy, com os mesmos caracteres permitidos. O usuário SSH deve conseguir criá-lo/escrever nele; usar diretório privado 0700, sem symlink ou acesso de outros usuários.

O usuário remoto necessita Bash, Python 3, flock, curl e Docker Compose V2 com `config --format json`/`--pull never`; leitura dos dois Compose e do env real; acesso ao Docker daemon. Não há sudo automático. Se o GHCR for privado, o usuário SSH precisa de login Docker previamente provisionado com credencial read:packages protegida (não incluída no script/log). O token de build do runner não é automaticamente credencial do servidor. Acesso ao daemon Docker é privilegiado; não conceder permissões genéricas adicionais como chmod 777.

Confirmar antes da primeira ativação que estes arquivos refletem a configuração efetiva do container, incluindo variáveis antes exportadas pelo shell, mounts e entrypoint; o script não reconstrói configurações que não estejam nesses insumos:

1. `/opt/stacks/noponto/docker-compose.yml`
2. `/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml`

Projeto `noponto`, serviço `api`, container `noponto_api`, HTTP local `http://127.0.0.1:8080/`, repository permitido `ghcr.io/guilhermedesales/noponto-api`. Essas identidades vêm do contexto informado; confirmar no servidor antes de autorizar.

## Fluxo e referência persistente

O workflow captura `steps.publish.outputs.digest`, sem resolver latest. Envia o script versionado por stdin SSH com host key obrigatória. Não instala script permanente nem modifica Compose/env produtivos.

`deploy-api.sh` recebe referência completa por digest, run_number, env real e diretório de estado. Sob lock flock, recusa sequência menor/igual à última promoção bem-sucedida. Valida configuração renderizada sem imprimi-la: imagem, Production, ausência de build/comando administrativo, GTFS-RT, ETA OFF e limites 1 CPU/768 MiB/256 PIDs. Não aplica migrations nem importa dados. A candidata utiliza a política de startup da Etapa 2; erro de verificação de migrations impede boot e leva a rollback.

Antes do pull guarda Image ID anterior sob tag local `noponto-api:rollback-<image-id>`, cópias dos dois Compose e env em `previous/`, com umask 077. Não exporta/copía esses arquivos para Actions: podem conter segredos. A tag local garante os bytes anteriores enquanto permanecer no daemon; não é um digest de registry. Não realizar prune dessas imagens nem apagar o estado. Preserva somente a referência anterior mais recente, sem sistema de retenção complexo.

A substituição usa `up -d --no-deps --no-build --pull never api`, com projeto/diretório/arquivos/env explícitos. Aguarda até 180 s, exige Image ID esperado, running, sem OOM/restarts e HTTP 200 em três amostras. `/` é smoke de boot, não readiness de PostgreSQL/Redis/workers; não há endpoints novos. Falhas no up/boot/HTTP/persistência tentam uma restauração com arquivos capturados e imagem anterior, sem pull. Rollback também tem verificação e timeout; erro continua sendo falha do job mesmo quando rollback funciona. HUP/INT/TERM tentam recuperação; SIGKILL, falta de energia e daemon indisponível não são recuperáveis por traps.

`active.env` preserva `GTFSRT_API_IMAGE`; `active.run` guarda a última sequência promovida. Os dois temporários são preparados depois do pull, sem alterar o estado ativo ou substituir a API se a preparação falhar. Após saúde aprovada, publica active.env e, por último, active.run. Falhas tratáveis restauram a referência e o contador anteriores, permitindo repetir a sequência não promovida. O diretório original da composição permanece imutável também quando o rollback usa snapshots em outro diretório, preservando referências relativas. Escrita individual com rename, sem journal transacional: SIGKILL/queda entre arquivos ainda pode exigir conferência manual. active.env não significa que a API esteja saudável quando rollback falha; erro de restauração do estado é diagnosticado explicitamente.

Qualquer invocação posterior de Compose precisa carregar o env produtivo **e**, depois, `$STATE_DIR/active.env`, mantendo os dois `-f`, projeto `noponto` e project-directory `/opt/stacks/noponto`. Remover variáveis GTFSRT_API_IMAGE exportadas anteriormente para não sobrescrever o arquivo. Exemplo conceitual, não executado:

```bash
env -u GTFSRT_API_IMAGE docker compose --project-name noponto \
  --project-directory /opt/stacks/noponto \
  --env-file "$REAL_ENV_FILE" --env-file "$STATE_DIR/active.env" \
  -f /opt/stacks/noponto/docker-compose.yml \
  -f /home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml \
  config --quiet
```

Nenhuma ferramenta pode garantir persistência se outra automação ignorar active.env ou usar apenas o Compose base/latest. Desativar acionadores legados e coordenar alterações manuais com o mesmo lock. Se a configuração original for alterada durante um deploy, conferir o snapshot usado no rollback antes de nova invocação; o MVP não sobrescreve os arquivos originais.

## Limites e bloqueios operacionais

Sem preflight SQL separado: o gate de schema é o startup fail-closed da candidata. A API única tem indisponibilidade durante recriação e interrupção SignalR; rollback não desfaz writes operacionais ou schema.

A imagem anterior build-102 pode executar `Database.Migrate()` no startup: rollback não acrescenta a política da Etapa 2 a um binário legado. Conferir o artefato/compatibilidade e autorizar explicitamente esse risco ou homologar baseline de rollback com a nova política antes de ativar. O script nunca executa migrations administrativas. Banco/Redis indisponíveis podem impedir candidata e rollback; não há tentativa de recriar dependências. O socket Docker existente da API permanece intocado.

Os testes com fakes não certificam credenciais, rede Tailscale/SSH, engine Debian, schema ou funcionamento produtivo. Não foi feito deploy real. Reexecução do mesmo run promovido é recusada; reset do contador só por intervenção revisada caso mude a numeração do workflow. Timeout do job/SSH não é garantia de recuperação diante de encerramento abrupto.

## Testes

`for script in .deploy/*.sh; do bash -n "$script"; done`; `python3 -B -m unittest discover -s .deploy -p 'test_*.py' -v`. As operações Docker/HTTP são fakes, sem daemon, banco ou Redis. O validate executa essas regressões antes da publicação. Variáveis NOPONTO_BASE_COMPOSE/NOPONTO_OVERLAY_COMPOSE e NOPONTO_HEALTH_TIMEOUT existem para fixtures locais; não são enviadas pelo workflow ao servidor.
