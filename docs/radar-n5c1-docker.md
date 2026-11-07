# Radar N5-C1 — preparação local, sem deploy

Base `987b287`, branch `feat/radar-notificacoes`. Nenhuma ativação, build, Docker, SSH ou teste operacional executado nesta etapa. Evidência N5-B em `radar-n5b.md`; pendências Android continuam abertas.

## Integração e estado desligado

O Dockerfile publica `NoPonto.csproj`; seus itens de compilação incluem os controllers, serviços, stores e workers N4-A/N4-B/N5-A/N5-B. `Program.cs` chama `AddRadar` e `MapControllers`. Não é necessário outro serviço, entrypoint, porta ou imagem Radar. A imagem efetivamente publicada ainda precisa ser conferida; leitura do código não comprova seu conteúdo.

`.deploy/radar.disabled.override.yml` é JSON válido como YAML Compose, seguindo o padrão dos overrides existentes. Aplicar **por último** na composição efetiva futura: fixa todas as flags abaixo em false, não cria mounts nem altera imagem, portas, recursos ou dependências. Não é carregado automaticamente. Configurações ausentes também permanecem desligadas pelos defaults C#; este arquivo explicita o estado e limites conservadores para uma futura revisão.

Não usar o `docker-compose.override.yml` local em produção: define Development, build local e porta adicional. O Compose base define Production, mas não representa necessariamente a composição publicada network-27 + ETA híbrido. Preservar a base efetiva, imagem imutável, overrides físicos/operacionais e configuração atual, sem substituí-los cegamente pelos arquivos deste checkout.

## Contrato real de configuração

As colunas de nomes usam as variáveis de ambiente .NET; `__` equivale a `:`. Não existe `RadarRedisConnection` na API principal: é opção exclusiva do DevHost.

| Variável | Default C# / valor no override desligado |
|---|---|
| `Radar__Enabled` | false / false; controla endpoints e motor |
| `Radar__WorkerEnabled` | false / false; consultas periódicas |
| `Radar__PushEnabled` | false / false; enrollment/envio/receipts |
| `Radar__Push__ControlledTestsEnabled` | false / false; manter false em Production |
| `Radar__Push__ExperimentalEnabled` | false / false |
| `Radar__Push__ExternalTransportEnabled` | false / false |
| `Radar__Push__ProjectId` | Guid.Empty / ausente; futuro projeto Expo autorizado |
| `Radar__Push__AllowedInstallationIds__0` (e índices seguintes) | array vazio / ausente; UUIDs individuais aprovados, máximo 100 |
| `Radar__Push__EncryptionKeyFile` | vazio / ausente; caminho absoluto dentro do contêiner |
| `Radar__Push__ExpoAccessTokenFile` | vazio / ausente; opcional, somente se enhanced push security exigir |
| `Radar__MaxLinhas`, `Radar__MaxContextos` | 20, 40 / 20, 40 |
| `Radar__MaxRegistrosPorDia`, `Radar__MaxSessoesAtivas` | 500, 2000 / 20, 20 |
| `Radar__IntervalSeconds` | 30 / 30, espera após o ciclo |
| `Radar__MaxParadasPorCiclo`, `Radar__MaxSessoesPorCiclo` | 20, 500 / 2, 10 |
| `Radar__MaxSessoesPorParada`, `Radar__MaxContextosPorParada` | 200, 80 / 10, 40 |
| `Radar__MaxEventosPorParada` | 500 / 500; saturação descarta decisão parcial |
| `Radar__MaxConcorrencia`, `Radar__QueryTimeoutSeconds` | 2, 5 / 1, 5 |
| `Radar__CandidateTtlSeconds` | 45 / 45, limitado pelos prazos reais |
| `Radar__Push__MaxRequestsPerMinute`, `Radar__Push__MaxPendingReceipts` | 60, 256 / 10, 32 |
| `Radar__Push__MaxAttempts`, `Radar__Push__MaxConcurrency` | 3, 2 / 3, 1 |
| `Radar__Push__MinIntervalSeconds` | 30 / 30 |

O projectId Dev já validado é `ab3db7e8-2478-49d6-a339-e639dcce1a34` (identificador público). Não definir allowlist com string JSON, UUID vazio ou entrada vazia; usar índices .NET e remover índices antigos da configuração efetiva. A allowlist restringe enrollment/envio; ativar `Radar__Enabled` expõe cadastro/sessões anônimas com limites, não somente aos IDs da lista. Se o beta exigir restringir também essas rotas, controlar acesso no proxy antes da ativação.

Push experimental exige projeto, arquivo de chave existente e allowlist não vazia, além das flags. Motor exige Enabled+WorkerEnabled; fila/envio/receipts exigem também PushEnabled e política experimental. Enrollment pode enviar challenge com WorkerEnabled=false: essa flag isoladamente **não** impede todo envio. Manter as seis flags false no primeiro deploy. Opções são lidas no startup, não há reload operacional garantido.

## Chave, HTTPS e Redis

Chave AES-GCM: arquivo externo ao checkout/contexto de build, base64 de 32 bytes, persistente e igual entre instâncias; diretório 0700/arquivo 0600, legível pelo usuário efetivo da API. Nunca copiar para imagem/env, imprimir, regenerar no reinício ou usar a chave do laboratório automaticamente. Não fornecer chave privada Firebase ao backend. Perda/troca da chave exige reenrollment; rotação sobreposta não implementada.

Na futura configuração privada, usar bind somente leitura, sem criação automática da origem. Exemplo **não aplicado**, caminho do host a ser confirmado:

```yaml
services:
  api:
    volumes:
      - type: bind
        source: /caminho-privado-confirmado/push-encryption.key
        target: /run/secrets/radar-push-encryption.key
        read_only: true
        bind:
          create_host_path: false
    environment:
      Radar__Push__EncryptionKeyFile: /run/secrets/radar-push-encryption.key
```

ExpoAccessTokenFile, quando necessário, segue o mesmo tratamento. Não montar arquivo algum no estado desligado. `.dockerignore` passa a excluir também arquivos privados conhecidos do Radar/Android; isso não substitui armazená-los fora do contexto. O DevHost e seu JSON não fazem parte do projeto/imagem da API.

API escuta HTTP interno em 8080; HTTPS público depende do proxy existente e certificado válido. Não publicar outro listener nem transportar credencial por HTTP público. Conferir proxy confiável antes de usar forwarded headers: filtro atual usa RemoteIpAddress, podendo compartilhar cotas entre clientes atrás do proxy. Não registrar Authorization, corpos de enrollment ou tokens nos logs do proxy. Transporte de saída usa HTTPS Expo fixo, sem redirects; exige DNS/egress/certificados funcionais.

Redis da API reutiliza `REDIS_HOST`/`REDIS_PORT` e o multiplexer existente; stores usam DB padrão e prefixo fixo `noponto:radar:v1:{radar}:`. Não há opção de namespace/Redis separado do Radar na API atual. Índices limitados, TTL e Lua evitam SCAN; o isolamento é lógico, não de memória/CPU. Compose base usa noeviction e desativa RDB/AOF: reinício/perda do Redis pode apagar instalações/sessões/tokens/claims. Chave persistente sozinha não recupera esses dados. Não alterar persistência nem migrar dados do laboratório nesta etapa.

## Recursos, reinício e implantação futura

Primeiro beta: uma única instância com WorkerEnabled=true; outras permanecem false. Gate de ciclo é local ao processo, portanto duas instâncias podem repetir consulta por parada, apesar dos claims distribuídos protegerem envios. Dentro do ciclo há uma chamada ao serviço existente de eventos por parada; revalidação estrutural do envio usa cache curto por lote, não cache global de SQL.

Fila volátil de 128 candidatos, DropOldest, sem backlog durável. Depois de reiniciar API, ciclos recompõem conteúdo atual se Redis/chave persistirem; não garantem entrega de atualização perdida. Motor, dispatcher e receipts podem iniciar juntos; limites de parada e transporte são independentes e compartilham CPU/RAM/SQL/Redis com GPS/ETA/SignalR. Valores conservadores acima são proposta, **não capacidade medida** para 4GB. Monitorar duração/cancelamentos/saturação, requests, memória/OOM, Redis usado/erros e latência dos serviços existentes antes de ampliar.

Production usa autenticação/enrollment normais, sem permissões do DevHost nem capacidade Testing. Startup verifica migrations pendentes e bloqueia se necessário, sem aplicá-las; Development executa migrations, portanto deve ser recusado. Bootstrap Redis normal continua existente. `/`=200 é smoke de boot, não readiness de workers/SQL/Redis.

O executor `.deploy/deploy-api.sh` congela arquivos e faz troca API-only sob lock, com imagem anterior para rollback; seus overrides têm validação restrita. **Não aceita genericamente este novo overlay**: não reutilizar o argumento shadow/physical para injetar Radar. `.deploy/eta-hybrid-release.py` é específico de ETA e rollback de sua flag não desliga Radar. Uma etapa futura precisa incorporar Radar no snapshot/candidate/rollback do executor escolhido e verificar compatibilidade com a base publicada. Este commit não altera executores, CI ou a configuração real e não autoriza deploy.

Sequência futura: confirmar base efetiva e artefatos necessários → validar imagem/configuração Radar OFF → autorizar implantação API-only com snapshot completo → conferir regressões existentes/404 Radar → resolver bloqueios Android e só então autorizar chave/allowlist e ativação em uma instância. Recriar API interrompe suas conexões, inclusive SignalR; não há promessa de zero downtime.

Rollback futuro: sob o mesmo lock/procedimento autorizado, aplicar flags false preservando demais configurações e recriar somente API com imagem já aprovada; se houver regressão da imagem, restaurar imagem **e** configuração congeladas anteriores. Não apagar Redis/chave nem recriar dependências. Preservar mounts/chave no snapshot privado, sem copiar conteúdo ao Git. Desligamento não recolhe notificações já exibidas nem desfaz envio aceito pelo Expo.

## Comandos manuais posteriores — não executados

Em ambiente de validação autorizado, usando a composição **efetiva completa** congelada pelo operador (pode conter segredos; nunca imprimir ou versionar):

```bash
# FROZEN_COMPOSE: caminho absoluto privado, sem usar override Development.
docker compose -f "$FROZEN_COMPOSE" -f .deploy/radar.disabled.override.yml config --quiet
docker compose -f "$FROZEN_COMPOSE" -f .deploy/radar.disabled.override.yml config --format json |
  python3 -c 'import json,sys; e=json.load(sys.stdin)["services"]["api"]["environment"]; keys=["Radar__Enabled","Radar__WorkerEnabled","Radar__PushEnabled","Radar__Push__ControlledTestsEnabled","Radar__Push__ExperimentalEnabled","Radar__Push__ExternalTransportEnabled"]; assert all(str(e.get(k)).lower()=="false" for k in keys); assert e.get("ASPNETCORE_ENVIRONMENT")=="Production"; print("Production e seis flags Radar OFF conferidas")'
# Build futuro local/isolado: não inicia API nem acessa bancos.
docker build --pull=false -f NoPonto/Dockerfile -t noponto-api:radar-c1 NoPonto
# Somente após implantação separadamente autorizada, com URL HTTPS real:
curl --silent --output /dev/null --write-out '%{http_code}\n' "$API_HTTPS/"
curl --silent --output /dev/null --write-out '%{http_code}\n' "$API_HTTPS/radar/instalacao"
```

Esperado: configuração válida/OFF; build sem arquivos privados; smoke200 e Radar404. Não iniciar a API completa contra banco não isolado para validar a imagem. Não executar `compose config` sem --quiet/filtro em terminal compartilhado, `inspect` integral ou logs de corpos. Nenhum comando acima realiza a futura troca/ativação; validar montagem/chave, saúde de dependências e ausência de ciclos Radar exigirá procedimento operacional posterior.
