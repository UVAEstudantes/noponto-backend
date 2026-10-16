# Shadow: preparação e ativação posterior

**Nenhum comando de servidor deste roteiro foi executado nesta PR.** Instalação, merge/deploy e ativação exigem autorização operacional separada. Não habilitar inferência. PhysicalEta permanece true/90; ML legado e EtaV2 permanecem false.

## Contrato

Ordem: base → GTFS-RT → **PhysicalEta existente** → Shadow. O launcher usa `NOPONTO_SHADOW_OVERRIDE`, opcional; ausente/vazio, deriva `$NOPONTO_DEPLOY_STATE_DIR/historical-eta-shadow.override.yml`. Nenhuma variável GitHub precisa ser criada. O arquivo remoto deve existir **antes do merge**, pois o deploy automático já está habilitado: ausência falha fechada antes de pull/up. Não alterar triggers, aprovação ou `NOPONTO_DEPLOY_ENABLED`.

Os modelos `.yml` são JSON válido, também aceito como YAML pelo Compose. São aceitos somente:

| Modo | Enabled / OfflineExport.Enabled | MlEnabled / HistoricalEnabled | Bind novo |
|---|---|---|---|
| OFF | false / false | false / false | nenhum |
| captura | true / true | false / false | diretório privado → `/var/lib/noponto-shadow` |

Gates obrigatórios: QueueCapacity=64, BatchSize=16, SinkCapacity=128, TimeoutMs=500, MaxAgeSeconds=90. Exportador: fila128, arquivo1MiB, total16MiB, até32 arquivos, retenção24h, Fixture=false. O preflight compara integralmente os três arquivos com o quarto: somente essas variáveis e o bind explícito podem mudar. Outros ambientes, serviços, mounts e recursos permanecem iguais. Aliases de configuração, inferência, fixture, limites divergentes e criação automática do diretório são recusados.

JSONL `noponto-eta-shadow-offline-v2`: captura causal já integrada na PR69, sem novo código GPS/API/Redis/passagens. Sem inferência, registra o físico quando disponível e motivos de indisponibilidade histórico/ML. Identidades incompatíveis são recusadas. Não é comprovação de precisão nem contém chegadas auditadas automaticamente.

## 1. Preparação do servidor — NÃO EXECUTADA

Usar os arquivos desta revisão aprovada, em uma cópia local limpa. Primeiro, entrar por `ssh guilherme@100.122.72.14` e executar como guilherme:

```bash
set -euo pipefail
umask 077
s=/home/guilherme/.noponto-deploy
test "$(id -un)" = guilherme
test -d "$s" && test ! -L "$s"
test "$(stat -c %U "$s")" = guilherme
test "$(stat -c %a "$s")" = 700
# Se já existirem, parar e revisar; não sobrescrever versões/artefatos anteriores.
test ! -e "$s/shadow-tools" && test ! -e "$s/shadow-capture-v2"
exec 9>"$s/deploy.lock"
flock -n 9
install -d -m 700 "$s/shadow-tools" "$s/shadow-capture-v2"
exec 9>&-
```

No PowerShell, a partir da worktree desta PR (sem senha embutida):

```powershell
scp .deploy/deploy-api.sh .deploy/historical-eta-shadow.off.yml .deploy/historical-eta-shadow.capture.yml .deploy/SHA256SUMS.shadow guilherme@100.122.72.14:/home/guilherme/.noponto-deploy/shadow-tools/
```

Voltar ao Debian; instalar **somente OFF**, sem recriar container:

```bash
set -euo pipefail
umask 077
s=/home/guilherme/.noponto-deploy
exec 9>"$s/deploy.lock"
flock -n 9
(cd "$s/shadow-tools" && sha256sum --check --status SHA256SUMS.shadow)
bash -n "$s/shadow-tools/deploy-api.sh"
chmod 600 "$s/shadow-tools/"*
test ! -e "$s/historical-eta-shadow.override.yml"
install -m 600 "$s/shadow-tools/historical-eta-shadow.off.yml" "$s/historical-eta-shadow.override.yml"
exec 9>&-
df -h "$s/shadow-capture-v2"
```

Diretório persistente: `/home/guilherme/.noponto-deploy/shadow-capture-v2`, dono guilherme, 0700, sem links em qualquer ancestral. Arquivos JSONL 0600; a imagem atual executa como root, portanto a leitura/exportação dos arquivos pode exigir sudo. Não abrir permissões para contornar isso. Não colocar outros dados nesse diretório. OFF não monta nem toca nele. ON usa bind com `create_host_path=false`; arquivos sobrevivem à recriação/rollback e não entram na imagem/snapshot de configuração.

## 2. Preflight, antes do merge — NÃO EXECUTADO

Debian, como guilherme. Os valores abaixo são lidos dos marcadores existentes, nunca de uma tag ou de `latest`:

```bash
set -euo pipefail
s=/home/guilherme/.noponto-deploy
export NOPONTO_SHADOW_OVERRIDE="$s/historical-eta-shadow.override.yml"
IFS= read -r active < "$s/active.env"
case "$active" in GTFSRT_API_IMAGE=ghcr.io/guilhermedesales/noponto-api@sha256:*) ref=${active#GTFSRT_API_IMAGE=} ;; *) exit 1 ;; esac
IFS= read -r sequence < "$s/active.run"
(cd "$s/shadow-tools" && sha256sum --check --status SHA256SUMS.shadow)
bash "$s/shadow-tools/deploy-api.sh" "$ref" "$sequence" \
  /opt/stacks/noponto/.env "$s" "$s/physical-eta.override.yml" \
  "$s/historical-eta-shadow.override.yml" --check
```

`--check` é somente leitura: render/inspect, sem lock, mkdir, snapshot, tag, pull/up ou arquivo de estado. Não certifica escrita pelo container nem cobertura real. Com CI/revisão aprovados e arquivo OFF instalado, o merge autorizado publica e promove a API pelo fluxo existente, com Shadow ainda OFF. Nenhum merge faz parte desta tarefa.

## 3. Ativar captura depois — NÃO EXECUTADO

Após a primeira promoção OFF saudável, repetir o bloco de leitura dos marcadores acima (imagem/sequência podem ter avançado). Não editar o arquivo ativo à mão. O modo explícito `--configure-shadow` exige **mesmo digest, mesma sequência ativa e mesma imagem local executada**; não faz pull, não usa sequência inventada e não interfere no próximo número de release.

```bash
bash "$s/shadow-tools/deploy-api.sh" "$ref" "$sequence" \
  /opt/stacks/noponto/.env "$s" "$s/physical-eta.override.yml" \
  "$s/shadow-tools/historical-eta-shadow.capture.yml" --check
# AUTORIZAÇÃO SEPARADA: recria exclusivamente API, sob deploy.lock.
bash "$s/shadow-tools/deploy-api.sh" "$ref" "$sequence" \
  /opt/stacks/noponto/.env "$s" "$s/physical-eta.override.yml" \
  "$s/shadow-tools/historical-eta-shadow.capture.yml" --configure-shadow
```

O mesmo lock cobre preflight, snapshot, troca, health, publicação atômica do arquivo ativo e retorno em caso de erro. Um workflow concorrente respeita esse lock e falha sem intervir; não há garantia contra comandos externos que ignorem o lock. Se a release mudar antes de adquiri-lo, a reconfiguração é recusada: reler os marcadores e revisar, sem forçar. O arquivo ativo só muda após health aprovado, para que os próximos deploys automáticos preservem o modo escolhido.

Health exige imagem correta, Running=true, OOM=false, reinícios0 e três GET200 consecutivos em `http://127.0.0.1:8080/`, até180s. Além disso, inspect confirma as variáveis Shadow e o bind efetivamente usados pelo container. A API mantém 768MiB/1CPU/256PIDs. Comandos Compose continuam `up -d --no-deps --no-build --pull never api`: PostgreSQL, Redis, redes e volumes não são recriados.

## 4. Observar e desligar — NÃO EXECUTADO

Começar com cinco minutos supervisionados, sem prometer rollback agendado:

```bash
docker inspect --format '{{.State.Running}}/{{.State.OOMKilled}}/{{.RestartCount}}' noponto_api
docker stats --no-stream noponto_api
docker logs --since 5m --tail 400 noponto_api 2>&1 | grep 'HistoricalEtaShadowMetrics' | tail -n 3
du -sh "$s/shadow-capture-v2"
```

Conferir `offline_export.faulted`, `written`, `write_failure`, `quota_drop`, filas/drops e circuito nas métricas limitadas. Ausência de registros pode significar falta de observações elegíveis; não declarar comparação real só porque HTTP está saudável. Falha do exportador não derruba GPS/API; por isso HTTP200 **não** prova captura funcionando. Se houver fault persistente, fila saturada, degradação ou exceções novas, desligar captura. Limites de recursos operacionais adicionais dependem do baseline do operador; esta PR não inventa tolerâncias nem mede custo em produção.

Para desligar, reler os marcadores do bloco2, validar hashes e usar o modelo OFF:

```bash
bash "$s/shadow-tools/deploy-api.sh" "$ref" "$sequence" \
  /opt/stacks/noponto/.env "$s" "$s/physical-eta.override.yml" \
  "$s/shadow-tools/historical-eta-shadow.off.yml" --check
bash "$s/shadow-tools/deploy-api.sh" "$ref" "$sequence" \
  /opt/stacks/noponto/.env "$s" "$s/physical-eta.override.yml" \
  "$s/shadow-tools/historical-eta-shadow.off.yml" --configure-shadow
```

Esse retorno mantém imagem e PhysicalEta e remove o bind do container, preservando os arquivos no host. O exportador gira arquivos por tamanho, recusa novos registros ao atingir quotas e remove somente arquivos próprios expirados, na inicialização/escrita e a cada minuto enquanto ativo. **OFF não executa limpeza: arquivos permanecem privados até reativação ou remoção autorizada pelo operador.** Retenção24h não é promessa de apagar em 24h enquanto o worker está desligado; se essa obrigação for necessária, não ativar antes de definir o procedimento de descarte. Nenhum timer/cron de exclusão foi instalado.

## Rollback e recuperação

Antes de substituir API, o deploy captura base, GTFS, terceiro override físico, interpolação, imagem local anterior e o **Shadow efetivamente em execução**, não o modo solicitado. `previous/shadow.override.json` contém gates explícitos OFF quando antes ausentes e o bind anterior quando existente. Em falha tratável/SIGHUP/SIGTERM/SIGINT, tenta recriar somente API com os quatro arquivos congelados e imagem local preservada, verifica health e modo real, e restaura os marcadores. Reconfiguração também restaura o arquivo Shadow ativo anterior. A primeira tentativa ON falha retornando OFF, nunca reaplicando o pedido ON.

`previous/candidate-shadow.override.yml` é evidência do pedido, **não usar como retorno**. Não reexecutar deploy para fazer rollback consumindo sequência fictícia. O retorno operacional da captura é o comando OFF acima; falha de release usa o trap existente. SIGKILL, queda do host e perda abrupta do processo não têm recuperação garantida. Preservar os insumos privados e recorrer ao procedimento manual existente sob o mesmo lock, sem tocar PostgreSQL/Redis; não utilizar um `previous` sobrescrito por deploy posterior sem revisão de sua imagem/configuração.

## Validação desta PR

Sem mudança de runtime, schema, dados, TTL, matching, passagens, ETA público, modelos ou flags ML/EtaV2/PhysicalEta. Regressões shell usam Docker/HTTP/SSH falsos; Compose real usa somente `config` com arquivos sintéticos. Testes C# offline incluem falhas de gravação, arquivo bloqueado, quotas, fila cheia, retenção, reinício e GPS/PhysicalEta preservados. Resultados finais e CI constam na PR. Nenhuma conexão ao homeserver, variável GitHub alterada ou ativação produtiva realizada.

Resultados locais: build Release 0 erros/5 warnings preexistentes; 529 C# aprovados e TRX verificado; 26 Python do avaliador aprovados. Seleção `.deploy`: 35 aprovados no Linux sem rede (Compose real ignorado nesse container sem Docker CLI); os dois testes de hash/Compose real passaram também no Windows. Sintaxe Bash e `git diff --check` aprovados. Seleção `.ci` no Windows: 6 aprovados/4 testes POSIX ignorados; CI Linux executa estes últimos. Evidências externas em `D:/repositorio_github/NoPonto/shadow-capture-deploy-20261015`; preservação do workspace original: HEAD/branch/status/índice iguais e 70 hashes sem divergências.
