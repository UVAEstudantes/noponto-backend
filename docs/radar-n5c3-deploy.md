# N5-C3 — compatibilidade e comandos de publicação

Verificação local sobre `ebea27e`, branch `feat/radar-notificacoes`. Sem build, Docker, SSH, deploy ou testes demorados. Os15casos C# N5-C2 foram aprovados pelo operador, conforme confirmação nesta etapa; não repetidos.

## Base e comparação concluídas

- Publicação documentada: `network-27` + overlay ETA funcional `823057cd2a885029904429ac6bb59e9ba1d5226e`; imagem `noponto-api:eta-hybrid-2i-823057cd2a88`, ID `sha256:946a0bb63230681bd31397718d483a3c3bb759310c941fba10d8c58346112ce7`. Evidência: `artifacts/eta-hybrid-v1-step2i/report.md` local e histórico de05/11. Sem confirmação nova ao vivo do servidor.
- A base Git correta é **`6955389dbe86560fa9538021ae072707bda504ba`**, ancestral desta branch, que já reconstruiu esse snapshot + ETA. A branch ETA/main isolada não contém todos os componentes network-27 e não é o baseline de comparação adequado.
- Recalculados os hashes normalizados CRLF dos613arquivos em `docs/consolidacao/backend-proveniencia.json`: **612idênticos/zero ausentes**. `Program.cs` coincide também ao remover somente a linha de registro `AddRadar`. Proveniência e receita original em `docs/consolidacao/README.md`.
- Diferenças posteriores na API são adições Radar, seu registro DI, histórico/contratos e proteção do contexto Docker. Nenhuma alteração nos componentes existentes GPS, BRT, trem, ETA físico/híbrido, presença/localização, SignalR, schema/migrations, csproj/dependências, recursos proto/JSON, Dockerfile ou Compose base.

Conclusão: **nenhuma correção de código funcional necessária**. Não copiar fontes de main ou do checkout de laboratório, nem fazer overlay somente de DLL antiga: usar a imagem completa desta branch, com dependências já declaradas. Equivalência de fontes não significa identidade binária ou medição de recursos; build/imagem e operação da API completa continuam sujeitos à validação do operador.

## Executor e checkpoints

O executor N5-C2 atende este caminho manual: composição efetiva congelada e privada, drift de imagem/ambiente/mounts/portas, apenas opções Radar/binds readonly, imagem fixa local, lock compartilhado, API-only, saúde e restauração. Não depende de DevHost, Windows ou allowlist no público. O executor CI genérico continua fora deste caminho. Não reaplicar Compose do checkout nem override Development no Debian.

Usar **dois diretórios de release**: o primeiro guarda a imagem/configuração original e publica a API nova OFF; o segundo ativa o público mantendo a mesma imagem. Isso preserva os dois checkpoints: voltar ao OFF e voltar à imagem original. Não reutilizar diretórios de releases anteriores. Não executar Python com `-O`/PYTHONOPTIMIZE nem prune das imagens.

Pré-requisitos mantidos: uma única API/worker, composição publicada atual íntegra em `active.manual.json`, chave32bytes/base64 persistente externa com arquivo0600/diretório0700 legível pela API, HTTPS e projeto Expo/FCM compatíveis. O overlay OFF tem sete flags false. Limites e riscos Redis/recursos/apresentação Android permanecem os de `radar-n5c2-public.md`. Nenhum segredo é transportado por estes comandos; snapshots no Debian contêm ambiente privado e não podem voltar ao Git.

## 1. Windows — build e transferência posteriores

Executar na raiz deste backend, apenas após revisar o commit final. Comandos não executados pelo agente. Endereço/identidade abaixo são os já usados no laboratório; não há chave privada incluída no repositório.

```powershell
if ((git branch --show-current) -ne 'feat/radar-notificacoes') { throw 'Branch incorreta' }
$commit = (git rev-parse HEAD).Trim()
$tag = "noponto-api:radar-$($commit.Substring(0,12))"
docker build --platform linux/amd64 --pull=false --label "org.opencontainers.image.revision=$commit" -f NoPonto/Dockerfile -t $tag NoPonto
if ($LASTEXITCODE -ne 0) { throw 'Build falhou' }
$archive = Join-Path $env:TEMP 'noponto-radar-n5c3.tar'
$commitFile = Join-Path $env:TEMP 'noponto-radar-n5c3-commit.txt'
docker save --output $archive $tag
if ($LASTEXITCODE -ne 0) { throw 'Exportacao falhou' }
Set-Content -LiteralPath $commitFile -Value $commit -Encoding ascii -NoNewline
$server = 'guilherme@100.122.72.14'
$identity = Join-Path $env:USERPROFILE '.ssh/id_ed25519_noponto'
$remoteDir = "/home/guilherme/.noponto-deploy/radar-$($commit.Substring(0,12))"
ssh -i $identity $server "umask 077; mkdir -m 700 '$remoteDir'"
if ($LASTEXITCODE -ne 0) { throw 'Diretorio ausente ou ja utilizado; conferir antes de prosseguir' }
scp -i $identity $archive "${server}:$remoteDir/api.tar"
scp -i $identity $commitFile "${server}:$remoteDir/commit.txt"
scp -i $identity .deploy/eta-hybrid-release.py "${server}:$remoteDir/eta-hybrid-release.py"
scp -i $identity .deploy/radar.disabled.override.yml "${server}:$remoteDir/off.json"
ssh -t -i $identity $server "cd '$remoteDir' && exec bash"
```

Conferir o sucesso de cada scp antes de seguir; não desativar validação da host key SSH. Tar/imagem não incluem chave ou arquivo privado de configuração.

## 2. Debian — congelamento e preflight OFF

Na sessão aberta no diretório de release acima:

```bash
set -euo pipefail
umask 077
RELEASE_DIR="$PWD"
COMMIT=$(cat commit.txt)
test "${#COMMIT}" -eq 40
chmod 600 commit.txt off.json eta-hybrid-release.py api.tar
docker load --input api.tar >/dev/null
TAG="noponto-api:radar-${COMMIT:0:12}"
IMAGE_REF=$(docker image inspect --format '{{.Id}}' "$TAG")
test "$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$TAG")" = "$COMMIT"
ORIGINAL_COMMIT=$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' noponto_api)
ACTIVE_CONFIG=$(python3 -I -c 'import json; print(json.load(open("/home/guilherme/.noponto-deploy/active.manual.json"))["configuration"])')
docker compose -p noponto --project-directory /opt/stacks/noponto --env-file /dev/null -f "$ACTIVE_CONFIG" config --format json > "$RELEASE_DIR/base.json"
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-activate --config "$RELEASE_DIR/base.json" --radar-overlay "$RELEASE_DIR/off.json" --image "$IMAGE_REF" --commit "$COMMIT" --check
```

Não imprimir `base.json`/config/env. Se o estado atual divergir ou o manifesto manual não existir, parar e reconciliar a composição; não contornar o preflight. Carregar a imagem não troca serviço. Nenhum destes comandos habilita Radar.

## 3. Publicação OFF e ativação pública posteriores

Somente após preflight aprovado e autorização de troca:

```bash
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-activate --config "$RELEASE_DIR/base.json" --radar-overlay "$RELEASE_DIR/off.json" --image "$IMAGE_REF" --commit "$COMMIT"
```

Conferir smoke, GPS/linhas/BRT/trem/eventos/SignalR existentes e Radar404 antes de ativar. Se regressão, usar rollback da imagem original abaixo. A troca recria somente API, com interrupção de suas conexões; não há promessa de zero downtime.

Provisionar a chave fora do checkout sem reaproveitar automaticamente a chave do laboratório. Exportar `RADAR_KEY_HOST_FILE` com seu **caminho real absoluto**; não seu conteúdo. Usar o mesmo projeto Expo da APK; o ID abaixo é o projeto já validado. Se enhanced push security exigir access token, preparar também seu bind/ExpoAccessTokenFile conforme N5-C2 antes do preflight público.

```bash
: "${RADAR_KEY_HOST_FILE:?Exporte o caminho real da chave persistente provisionada}"
export RADAR_KEY_HOST_FILE
PUBLIC_DIR="$RELEASE_DIR/public"
mkdir -m 700 "$PUBLIC_DIR"
cp "$RELEASE_DIR/eta-hybrid-release.py" "$PUBLIC_DIR/eta-hybrid-release.py"
python3 -I - <<'PY'
import json,os,pathlib
root=pathlib.Path.cwd()
c=json.loads((root/'off.json').read_text()); a=c['services']['api']; e=a['environment']
for k in ('Radar__Enabled','Radar__WorkerEnabled','Radar__PushEnabled','Radar__Push__PublicEnabled','Radar__Push__ExternalTransportEnabled'): e[k]='true'
e['Radar__Push__ProjectId']='ab3db7e8-2478-49d6-a339-e639dcce1a34'
e['Radar__Push__EncryptionKeyFile']='/run/secrets/radar-push-encryption.key'
a['volumes']=[{'type':'bind','source':os.environ['RADAR_KEY_HOST_FILE'],'target':e['Radar__Push__EncryptionKeyFile'],'read_only':True,'bind':{'create_host_path':False}}]
(root/'public.json').write_text(json.dumps(c))
PY
python3 -I "$PUBLIC_DIR/eta-hybrid-release.py" radar-activate --config "$RELEASE_DIR/radar-active.json" --radar-overlay "$RELEASE_DIR/public.json" --commit "$COMMIT" --check
# Somente após autorizar envio público:
python3 -I "$PUBLIC_DIR/eta-hybrid-release.py" radar-activate --config "$RELEASE_DIR/radar-active.json" --radar-overlay "$RELEASE_DIR/public.json" --commit "$COMMIT"
```

## 4. Reversão

Cada comando aceita `--check` para conferir antes. Restaurar o último checkpoint público volta à imagem nova OFF:

```bash
python3 -I "$PUBLIC_DIR/eta-hybrid-release.py" radar-restore --config "$PUBLIC_DIR/radar-active.json" --commit "$COMMIT"
```

Para restaurar a imagem/configuração original: antes de ativar público ou depois de retornar ao OFF, usar o executor do primeiro diretório:

```bash
test "${#ORIGINAL_COMMIT}" -eq 40
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-restore --config "$RELEASE_DIR/radar-active.json" --commit "$ORIGINAL_COMMIT"
```

Se precisar voltar diretamente da configuração pública à original, passar `--config "$PUBLIC_DIR/radar-active.json"` nesse último comando; a imagem pública é a mesma do checkpoint OFF, permitindo o gate de restauração. Os valores de variáveis são os da sessão acima; se ela for perdida, recuperá-los dos metadados privados, sem adivinhar configuração/imagem. Não remover Redis/chaves ou usar docker compose down. Desligamento não recolhe notificações já exibidas.

## Limites desta preparação

Executor revisado sem alteração funcional nesta etapa; seus sete testes Radar e três ETA já passaram na N5-C2. Os15 testes C# foram confirmados pelo operador. Nesta etapa apenas comparação de fontes/hashes e diff; sem nova prova da imagem real, configuração ativa atual ou deploy/rollback no Debian. APK pública ainda precisa da integração HTTPS/pacote/FCM documentada na N5-C2; o Dev pode validar a API HTTPS. Nenhum segredo, serviço ou frontend alterado.
