# Deploy API MVP

Implementação baseada na auditoria 3A entregue na conversa: tres Compose obrigatorios, imagem por digest, API única, nenhuma recriação de dependências. Não chama `/home/guilherme/deploy.sh` nem envia Compose do Git ao servidor.

## Ativação pelo operador

O workflow preserva testes/publicação e adiciona deploy somente em push/main após build aprovado. O job exige a variável `NOPONTO_DEPLOY_ENABLED=true`; manter ausente/false até revisão, autorização e preparação do servidor. Usa o environment GitHub `production`, cujas proteções devem ser configuradas pelo operador. Após ativação, novos pushes aprovados fazem deploy automático; não há workflow_dispatch. Esta implementação não implanta build-108 nem qualquer outra imagem localmente.

Secrets reutilizados: `GHCR_USER`/`GHCR_TOKEN` para publicação, `TAILSCALE_OAUTH_CLIENT_ID`, `TAILSCALE_OAUTH_SECRET`, `SERVER_TAILSCALE_IP`, `SERVER_USER`, `SERVER_SSH_KEY`. Novo secret obrigatório: `SERVER_SSH_KNOWN_HOSTS`, com entrada do servidor verificada por canal confiável. Não executar ssh-keyscan e confiar automaticamente no resultado. Transporte mantém SSH porta 22 e identidade Tailscale `tag:ci` do mecanismo anterior; confirmar ACL/OAuth e porta antes de ativar.

Variáveis obrigatórias, sem defaults inventados:

- `NOPONTO_DEPLOY_ENV_FILE`: caminho absoluto do arquivo real de variáveis utilizado pela composição produtiva. Não é seu conteúdo. Caminho deve usar somente letras, números, `_`, `.`, `/` e `-`.
- `NOPONTO_PHYSICAL_ETA_OVERRIDE`: caminho absoluto real do terceiro Compose `physical-eta.override.yml`. Obrigatorio; nao ha default nem caminho remoto presumido. Caminho confirmado pelo operador nesta tarefa: `/home/guilherme/.noponto-deploy/physical-eta.override.yml`. A configuracao do GitHub ainda exige acao autorizada do operador; nao foi alterada nesta PR.
- `NOPONTO_DEPLOY_STATE_DIR`: diretório absoluto exclusivo para estado do deploy, com os mesmos caracteres permitidos. O usuário SSH deve conseguir criá-lo/escrever nele; usar diretório privado 0700, sem symlink ou acesso de outros usuários.

O usuário remoto necessita Bash, Python 3, flock, curl e Docker Compose V2 com `config --format json`/`--pull never`; leitura dos tres Compose e do env real; acesso ao Docker daemon. Não há sudo automático. Se o GHCR for privado, o usuário SSH precisa de login Docker previamente provisionado com credencial read:packages protegida (não incluída no script/log). O token de build do runner não é automaticamente credencial do servidor. Acesso ao daemon Docker é privilegiado; não conceder permissões genéricas adicionais como chmod 777.

Confirmar antes da primeira ativação que estes arquivos refletem a configuração efetiva do container, incluindo variáveis antes exportadas pelo shell, mounts e entrypoint; o script não reconstrói configurações que não estejam nesses insumos:

1. `/opt/stacks/noponto/docker-compose.yml`
2. `/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml`
3. O caminho confirmado em `NOPONTO_PHYSICAL_ETA_OVERRIDE` (modelo versionado em `.deploy/physical-eta.override.yml`).

Projeto `noponto`, serviço `api`, container `noponto_api`, HTTP local `http://127.0.0.1:8080/`, repository permitido `ghcr.io/guilhermedesales/noponto-api`. Essas identidades vêm do contexto informado; confirmar no servidor antes de autorizar.

## Fluxo e referência persistente

O workflow captura `steps.publish.outputs.digest`, sem resolver latest. Envia o script versionado por stdin SSH com host key obrigatória. Não instala script permanente nem modifica Compose/env produtivos.

`deploy-api.sh` recebe referência completa por digest, run_number, env real, diretorio de estado e caminho absoluto do override fisico. Sob lock flock, recusa sequência menor/igual à última promoção bem-sucedida. Valida configuração renderizada sem imprimi-la: imagem, Production, ausência de build/comando administrativo, GTFS-RT, PhysicalEta ON/90s, ML/EtaV2 OFF e limites 1 CPU/768 MiB/256 PIDs. Não aplica migrations nem importa dados. A candidata utiliza a política de startup da Etapa 2; erro de verificação de migrations impede boot e leva a rollback.

Antes do pull guarda Image ID anterior sob tag local `noponto-api:rollback-<image-id>`, copias dos tres Compose e env em `previous/`, com umask 077. Não exporta/copía esses arquivos para Actions: podem conter segredos. A tag local garante os bytes anteriores enquanto permanecer no daemon; não é um digest de registry. Não realizar prune dessas imagens nem apagar o estado. Preserva somente a referência anterior mais recente, sem sistema de retenção complexo.

A substituição usa `up -d --no-deps --no-build --pull never api`, com projeto/diretório/arquivos/env explícitos. Aguarda até 180 s, exige Image ID esperado, running, sem OOM/restarts e HTTP 200 em três amostras. `/` é smoke de boot, não readiness de PostgreSQL/Redis/workers; não há endpoints novos. Falhas no up/boot/HTTP/persistência tentam uma restauração com arquivos capturados e imagem anterior, sem pull. Rollback também tem verificação e timeout; erro continua sendo falha do job mesmo quando rollback funciona. HUP/INT/TERM tentam recuperação; SIGKILL, falta de energia e daemon indisponível não são recuperáveis por traps.

`active.env` preserva `GTFSRT_API_IMAGE`; `active.run` guarda a última sequência promovida. Os dois temporários são preparados depois do pull, sem alterar o estado ativo ou substituir a API se a preparação falhar. Após saúde aprovada, publica active.env e, por último, active.run. Falhas tratáveis restauram a referência e o contador anteriores, permitindo repetir a sequência não promovida. O diretório original da composição permanece imutável também quando o rollback usa snapshots em outro diretório, preservando referências relativas. Escrita individual com rename, sem journal transacional: SIGKILL/queda entre arquivos ainda pode exigir conferência manual. active.env não significa que a API esteja saudável quando rollback falha; erro de restauração do estado é diagnosticado explicitamente.

Qualquer invocação posterior de Compose precisa carregar o env produtivo **e**, depois, `$STATE_DIR/active.env`, mantendo os tres `-f`, projeto `noponto` e project-directory `/opt/stacks/noponto`. Remover variáveis GTFSRT_API_IMAGE exportadas anteriormente para não sobrescrever o arquivo. Exemplo conceitual, não executado:

```bash
env -u GTFSRT_API_IMAGE docker compose --project-name noponto \
  --project-directory /opt/stacks/noponto \
  --env-file "$REAL_ENV_FILE" --env-file "$STATE_DIR/active.env" \
  -f /opt/stacks/noponto/docker-compose.yml \
  -f /home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml \
  -f "$NOPONTO_PHYSICAL_ETA_OVERRIDE" \
  config --quiet
```

Nenhuma ferramenta pode garantir persistência se outra automação ignorar active.env ou usar apenas o Compose base/latest. Desativar acionadores legados e coordenar alterações manuais com o mesmo lock. Se a configuração original for alterada durante um deploy, conferir o snapshot usado no rollback antes de nova invocação; o MVP não sobrescreve os arquivos originais.

## Limites e bloqueios operacionais

Sem preflight SQL separado: o gate de schema é o startup fail-closed da candidata. A API única tem indisponibilidade durante recriação e interrupção SignalR; rollback não desfaz writes operacionais ou schema.

A imagem anterior build-102 pode executar `Database.Migrate()` no startup: rollback não acrescenta a política da Etapa 2 a um binário legado. Conferir o artefato/compatibilidade e autorizar explicitamente esse risco ou homologar baseline de rollback com a nova política antes de ativar. O script nunca executa migrations administrativas. Banco/Redis indisponíveis podem impedir candidata e rollback; não há tentativa de recriar dependências. O socket Docker existente da API permanece intocado.

Os testes com fakes não certificam credenciais, rede Tailscale/SSH, engine Debian, schema ou funcionamento produtivo. Não foi feito deploy real. Reexecução do mesmo run promovido é recusada; reset do contador só por intervenção revisada caso mude a numeração do workflow. Timeout do job/SSH não é garantia de recuperação diante de encerramento abrupto.

## Testes

`for script in .deploy/*.sh; do bash -n "$script"; done`; `python3 -B -m unittest discover -s .deploy -p 'test_*.py' -v`. As operações Docker/HTTP são fakes, sem daemon, banco ou Redis. O validate executa essas regressões antes da publicação. Variáveis NOPONTO_BASE_COMPOSE/NOPONTO_OVERLAY_COMPOSE e NOPONTO_HEALTH_TIMEOUT existem para fixtures locais; não são enviadas pelo workflow ao servidor.


## Preflight e ativacao apos esta PR

Nenhum deploy nem alteracao de variavel remota faz parte desta PR. PhysicalEta continua OFF por default no codigo; o terceiro arquivo explicita a politica produtiva ON/90s. O script nao envia/substitui Compose no servidor.

O override deve mudar **somente** `services.api.environment.PhysicalEta__Enabled` e `PhysicalEta__MaxAgeSeconds`. O preflight compara renderizacoes completas com/sem o terceiro arquivo, incluindo demais ambientes/servicos; divergencia extra falha sem imprimir os valores. Ambos os gates sao exigidos no resultado final, assim como ML legado/EtaV2/Shadow OFF. Nenhuma referencia de imagem por tag e aceita.

Para verificacao manual somente leitura, usar o script desta revisao em uma maquina que disponha dos insumos reais, sem executa-lo de uma revisao antiga:

```bash
bash .deploy/deploy-api.sh "$EXACT_IMAGE_DIGEST_REFERENCE" "$RELEASE_SEQUENCE" \
  "$NOPONTO_DEPLOY_ENV_FILE" "$NOPONTO_DEPLOY_STATE_DIR" \
  "$NOPONTO_PHYSICAL_ETA_OVERRIDE" --check
```

`--check` nao cria diretorios, lock, snapshots, tags nem marcadores; nao faz pull/up e nao altera containers. Nao reserva o lock: e uma observacao pontual e deve ser seguida de nova validacao dentro do deploy normal. Verifica renderizacao e identidade do container Compose existente, nao acesso de pull ao GHCR, schema ou readiness de banco. A sequencia ja promovida e permitida nesse modo, pois nada e promovido.

O deploy normal repete os gates sob o lock existente, captura tambem `previous/physical-eta.override.yml` e utiliza exatamente essa copia em caso de rollback. O rollback nunca retira a flag fisica por omitir o terceiro arquivo e nao depende do override original apos capturado. O diretorio original do projeto e preservado para caminhos relativos. O comando de recriacao permanece exclusivamente `up -d --no-deps --no-build --pull never api`. PostgreSQL e Redis nao recebem comandos de recriacao. Evitar alterar os insumos durante a execucao; snapshot nao e uma transacao atomica entre arquivos.

Checklist final do operador antes de reativar:

1. CI desta PR aprovado e revisao/merge autorizado separadamente; `NOPONTO_DEPLOY_ENABLED` permanece false durante a preparacao. Merge em main publica GHCR mesmo com deploy desabilitado.
2. Confirmar o caminho do override existente e comparar somente as duas configuracoes publicas com o modelo desta PR. Nao sobrescrever o arquivo remoto por suposicao. Configurar `NOPONTO_PHYSICAL_ETA_OVERRIDE` no mesmo escopo das variaveis usadas pelo job, com caminho real.
3. Confirmar os valores efetivos das variaveis em Settings > Secrets and variables > Actions e no environment production (inclusive precedencia entre escopos), sem publicar secrets. Confirmar env/state reais, permissoes, active.run e mesma exclusao mutua para intervencoes manuais.
4. Preflight --check com o digest e a sequencia reais; confirmar rollback local compativel, acesso SSH/host key, ACL Tailscale tag:ci, credencial GHCR de leitura no servidor e recursos do host.
5. Environment production deve exigir revisor, limitar branches a main e evitar autoaprovacao conforme politica do projeto. Verificar rulesets/protecao da main e CI obrigatorio na interface.
6. Somente apos validacao e autorizacao do operador, definir NOPONTO_DEPLOY_ENABLED=true no escopo efetivo. Isto habilita futuros pushes/main; nao recupera automaticamente um job antes ignorado. Nao ha workflow_dispatch adicionado. Nao provocar push vazio ou rerun de release antiga para contornar o gate de sequencia.
7. Acompanhar a proxima promocao autorizada e conferir API/HTTP/imagem, flags Physical=true/90 e ML/EtaV2/Shadow=false, PostgreSQL/Redis, GPS e recursos. Health `/` nao certifica precisao do ETA nem integridade das passagens.

Consulta publica desta preparacao: environment production expoe regra required_reviewers. Variavel NOPONTO_DEPLOY_ENABLED e protecao da main retornaram HTTP 401; valores/controles nao foram inferidos. A confirmacao final e feita pelo operador na UI autenticada. Nao foram impressos secrets ou conteudo de .env.

Valor proposto para a variavel GitHub `NOPONTO_PHYSICAL_ETA_OVERRIDE`: `/home/guilherme/.noponto-deploy/physical-eta.override.yml`, informado pelo operador. O conteudo remoto nao foi acessado; a renderizacao --check deve comprovar true/90 antes da ativacao.

## Quarto override Shadow (preparação posterior à PR69)

O contrato e a assinatura atuais acrescentam um sexto argumento obrigatório ao `deploy-api.sh`: o arquivo Shadow. Os exemplos anteriores de cinco argumentos descrevem a etapa PhysicalEta e devem agora acrescentar esse arquivo antes de `--check`. O terceiro override permanece intacto. O quarto aceita OFF ou captura sem inferência, nunca ML/histórico habilitados. O launcher deriva seu caminho do diretório de estado se `NOPONTO_SHADOW_OVERRIDE` não estiver definido. Instalar o modelo OFF antes de autorizar merge com deploy automático habilitado; não há envio automático de configuração nesta PR.

Roteiro completo de instalação privada, ativação/desativação com mesma imagem/sequência, quotas/retenção, health e rollback: [SHADOW-CAPTURE.md](SHADOW-CAPTURE.md). Todos os comandos de servidor ali são futuros e não executados.
