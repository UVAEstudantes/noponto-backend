# N5-C2 — MVP público do Radar, implementação local

Base backend `c514cf7`, branch `feat/radar-notificacoes`. Nenhum deploy, build, Docker, SSH ou envio real executado nesta etapa. A imagem `radar-c1` é anterior a esta implementação: precisa ser recompilada e validada pelo operador. Pendências de apresentação Android não foram corrigidas.

## Autorização e configuração

Nova opção `Radar:Push:PublicEnabled=false` por padrão (`Radar__Push__PublicEnabled` no ambiente). Somente Production pode habilitá-la. Os modos público e experimental são exclusivos; DI rejeita conflito, ambiente incorreto, projeto/chave ausentes e limites inválidos. A política também falha fechada se configurada diretamente sem DI.

Modo público dispensa allowlist, mas mantém credencial N4-A em cada operação, challenge único de 120s, limite de provas, confirmação de posse antes da associação/envio, exclusividade do token, revogação, revisão da sessão e prazo de uma hora. Não existe endpoint para disparo arbitrário. Push/receipts/fila usam o mesmo caminho N5-B, sem DevHost/Testing. Hashes, AES-GCM e scripts Lua permanecem; o store exige `confirmed=1` nos dois modos de rede. Confirmação retorna `mode=public`; os contratos HTTP permanecem os de N5-B.

“Instalação legítima” significa credencial válida e posse confirmada do token, **não** atestado de integridade do APK. Um cliente modificado pode cadastrar instalações; continuam aplicáveis limites por IP, registros diários, sessões ativas, challenge por instalação, orçamento global e fila limitada. Não foi implementado Play Integrity. Proxy deve limitar abuso e não expor Authorization/corpos; cotas por IP podem se agrupar atrás do proxy até sua configuração confiável ser validada.

Para futura ativação pública, exigir estes valores no contêiner:

| Variável | Valor |
|---|---|
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | Production / ausente ou Production |
| `Radar__Enabled`, `Radar__WorkerEnabled`, `Radar__PushEnabled` | true |
| `Radar__Push__PublicEnabled`, `Radar__Push__ExternalTransportEnabled` | true |
| `Radar__Push__ExperimentalEnabled`, `Radar__Push__ControlledTestsEnabled` | false |
| `Radar__Push__ProjectId` | projeto Expo da APK; Dev validado: `ab3db7e8-2478-49d6-a339-e639dcce1a34` |
| `Radar__Push__EncryptionKeyFile` | `/run/secrets/radar-push-encryption.key` |
| `Radar__Push__ExpoAccessTokenFile` | ausente/vazio, ou `/run/secrets/radar-expo-access-token` se o projeto exigir |

Não é necessário `AllowedInstallationIds` no público; no desenvolvimento manter PublicEnabled=false e ExperimentalEnabled=true com allowlist. `.deploy/radar.disabled.override.yml` agora desativa **sete** flags, incluindo a nova pública; os seis gates descritos em N5-C1 passam a incluir este sétimo gate.

Limites conservadores permanecem no override desligado: 20 sessões ativas, 20 registros/dia, 2 paradas/10 sessões por ciclo, intervalo30s, consulta5s, concorrência1, 10 requests push/min e32pendências. Ao preparar o overlay público privado, copiar esses limites e alterar deliberadamente apenas flags/projeto/mounts. Serviço público aqui é aberto sob cotas; ao atingir teto retorna erro/limitação, não capacidade ilimitada. Não ampliar sem medição.

Reutilizar API, Redis/namespace `noponto:radar:v1:{radar}:`, proxy HTTPS e FCM/EAS existentes. Uma única instância do motor no primeiro deploy; claims distribuídos deduplicam envio, não consultas entre processos. Redis atual sem RDB/AOF pode perder cadastros/sessões ao reiniciar; chave persistente não restaura esses dados. Não alterar persistência nesta etapa. Demais opções e limites em `radar-n5c1-docker.md`.

## Executor existente e proteção da base produtiva

`.deploy/eta-hybrid-release.py` ganhou modos `radar-activate`, `radar-disable`, `radar-restore` e `--check`. Reutiliza lock `/home/guilherme/.noponto-deploy/deploy.lock`, Compose congelado, API-only `up --no-deps --no-build --pull never api` e saúde da imagem exata. Modos ETA anteriores continuam separados; argumentos Radar são recusados nesses modos. O executor CI `.deploy/deploy-api.sh` **não** deve ser usado para esta publicação sem futura integração equivalente; ele não lê automaticamente este overlay.

O novo caminho exige JSON completo renderizado da composição **atualmente publicada**, não o Compose base do checkout. Confere ambiente, imagem, mounts e portas contra o contêiner atual; recusa drift. Aceita apenas alterações de opções Radar conhecidas e dois binds readonly de segredos, com `create_host_path=false`; preserva ETA/GPS/SignalR, serviços, limites/portas/redes da base. Imagem candidata deve existir no daemon por ID `sha256:...` ou digest GHCR fixo. Não faz pull/build nem troca Redis/PostgreSQL.

Arquivo de configuração/overlay e diretório de release devem ser privados, sem symlink, pertencentes ao usuário executor, sem permissões de grupo/outros (0700/0600). Chave deve estar fora do checkout, ser base64 de32bytes, persistente e legível pela API; usar bind em `/run/secrets/radar-push-encryption.key`. Token Expo opcional recebe bind equivalente no segundo caminho. Não montar chave privada Firebase nem copiar `dev-host.json`. Preflight verifica metadados dos arquivos; a API valida o conteúdo da chave no startup.

Antes da troca salva configuração integral anterior com imagem por ID em `radar-previous.json`, sob diretório privado; após sucesso publica `radar-result.json` e `active.manual.json`. Falha tratável tenta restaurar imagem/configuração e marcador anteriores. Não imprimir/versionar snapshots: contêm ambiente privado. Não usar Python `-O`/`PYTHONOPTIMIZE`: validações do executor existente usam asserts. Não executar prune das imagens de rollback. Queda de energia/SIGKILL ou falha do daemon exigem recuperação manual; não há transação distribuída entre Docker e arquivos. Saúde `/`200 não comprova entrega push.

O preflight não comprova equivalência do **código** à base manual network-27 + ETA híbrido: confirmar a origem da imagem/reconciliação dessas funcionalidades antes de publicar. Substituir a API interrompe suas conexões; não há zero downtime. Não há nova aprovação de consumo nos4GB.

## Procedimento manual posterior — não executado

1. Executar testes abaixo e build Release/imagem a partir da base de produção reconciliada. Disponibilizar a imagem aprovada no daemon do Debian por mecanismo autorizado (sem alterar dependências).
2. Criar diretório de release privado e exclusivo; copiar para ele a versão deste executor. Congelar a composição efetiva completa em JSON privado0600, sem imprimir. Usar os mesmos arquivos/projeto da release ativa; nunca o override local Development. Confirmar chave persistente/read-only e HTTPS.
3. Copiar o override OFF para um JSON privado de ativação, mantendo limites. Alterar as cinco flags públicas para true, deixar Experimental/ControlledTests false e acrescentar ProjectId, EncryptionKeyFile e bind readonly no formato do N5-C1. Não colocar conteúdo de segredos no JSON. Confirmar uma única API/worker.
4. Definir no terminal do servidor `RELEASE_DIR`, `FROZEN_CONFIG`, `PUBLIC_OVERLAY`, `IMAGE_REF` e `COMMIT` completos e aprovados. `FROZEN_CONFIG` é o JSON renderizado corrente, não apenas o overlay. Rodar primeiro com sete flags OFF/imagem nova e só depois autorizar a ativação pública.

```bash
# Nova imagem, Radar OFF (OFF_OVERLAY é a cópia privada do override desligado).
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-activate --config "$FROZEN_CONFIG" --radar-overlay "$OFF_OVERLAY" --image "$IMAGE_REF" --commit "$COMMIT" --check
# Remover apenas --check após aprovação do preflight e da publicação.
# Com a nova API OFF, usar seu radar-active.json como configuração corrente:
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-activate --config "$RELEASE_DIR/radar-active.json" --radar-overlay "$PUBLIC_OVERLAY" --commit "$COMMIT" --check
# Remover apenas --check após autorização explícita de ativação pública.
```

O primeiro deploy OFF tem rollback da imagem antiga. A ativação pública seguinte tem rollback do estado OFF na mesma imagem. Não reaplicar configuração congelada antiga depois da troca. Para desativar ou restaurar **a última operação**:

```bash
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-disable --config "$RELEASE_DIR/radar-active.json" --commit "$COMMIT" --check
# Sem --check: desliga sete flags, conserva imagem/chave e recria somente API.
python3 -I "$RELEASE_DIR/eta-hybrid-release.py" radar-restore --config "$RELEASE_DIR/radar-active.json" --commit "$COMMIT" --check
# Sem --check: restaura snapshot anterior completo; pode reativar Radar se esse era o estado anterior.
```

Após cada operação autorizada: confirmar imagem/flags sem exibir segredos; smokeHTTPS200 e Radar404 quando OFF; quando ON, registrar instalação de teste e validar enrollment/ativação/endpoints autenticados, push físico e receipts sem confundi-los com entrega. Verificar logs agregados, Redis/SQL, GPS/ETA/SignalR e recursos. Falha de apresentação Android continua conhecida; desligar backend não remove notificações já exibidas.

## Validação local e APK

```powershell
dotnet test NoPonto/NoPonto.csproj -c Release --filter "FullyQualifiedName~NoPonto.Tests.RadarPublicTests" --logger "trx;LogFileName=radar-public.trx" --results-directory artifacts/n5c2
python -B .deploy/test_radar_release.py
python -B .deploy/test_eta_hybrid_release.py
git diff --check
```

**Executado:** sete testes unitários/mocked do executor Radar e três regressões unitárias do executor ETA, todos aprovados; nenhum subprocesso Docker/HTTP/SSH real. Incluem restauração explícita, recusa de outra release e rollback de falha de saúde. Uma falha inicial dos fixtures Linux no Windows levou ao uso de PurePosixPath para caminhos do servidor; rodada final limpa. **Não executado:** testes C# (15 casos direcionados adicionados), compilação, suites Redis/HTTP existentes, build Docker, preflight/rollback reais, produção ou Android. Os197 testes anteriores não foram repetidos nem declarados validação desta mudança.

Frontend não alterado. Para APK pública ainda é necessário: remover deliberadamente o bloqueio ao pacote Dev, fornecer URL Radar HTTPS/opt-in no build, configurar SecureStore e arquivo Android/FCM V1 do applicationId público no EAS, gerar e validar a APK. O pacote Dev atual pode testar a API HTTPS preservando seu projeto. Não há dependência Windows/DevHost no backend público, mas não declarar APK pública conectada antes dessa integração e teste físico. TTL/bandeja, substituição e comportamento ao toque permanecem para a etapa Android.
