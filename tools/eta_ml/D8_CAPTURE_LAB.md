# D8 — captura descartável, exclusivamente sintética

O modo `-CaptureD8` estende `test_shadow_disposable.ps1`. Não usa Compose,
`Program.cs`, appsettings, `.env`, API ou loops de serviços GPS. Reutiliza as
fixtures Protobuf/HTTP de `HistoricalEtaGtfsPollingTests` e chama duas coletas
e dois ciclos manualmente. HTTP é interceptado em memória. Matching PostGIS,
CAS/Lua Redis, repositório durável de viagem, adapter/fila Shadow e exportador
são os componentes existentes da aplicação. A resposta ETA da fixture é
80 segundos/confiança `alta`; não há treino, inferência ExtraTrees ou métricas.

## Comando futuro — não executado na preparação D8

Exige autorização posterior para criar os containers e preparar seu schema.
O switch separado `-PrepareCaptureSchema` autoriza somente o DDL/seed nos
recursos criados pela própria invocação. Sua ausência recusa D8 antes de Docker.

```powershell
$result = Join-Path (Get-Location) ('NoPonto/lab-output/d8-run-' + [guid]::NewGuid().ToString('N'))
pwsh -NoProfile -File tools/eta_ml/test_shadow_disposable.ps1 `
  -Run -CaptureD8 -PrepareCaptureSchema -AllowD8BridgeEgress -ResultsDirectory $result
```

Não usar `ExecutionPolicy Bypass`. A política local deve permitir o script por
procedimento autorizado. As tentativas de testar a recusa sem os switches
obrigatórios foram bloqueadas pela política local antes de carregar o script;
o ensaio conectado não foi tentado. O parser, compilação e testes offline
foram executados.

## Isolamento e preparo explícito

- Docker Desktop Windows `desktop-linux` com endpoint `npipe`, imagens locais
  `postgis/postgis:16-3.4` e `redis:7.4-alpine`. Sem pull/build de imagem.
- Containers, bridge não interna e banco `noponto_d8_<guard>` exclusivos por execução.
  Portas atribuídas dinamicamente e publicadas apenas em `127.0.0.1`.
  Dados em tmpfs; nenhum volume nomeado/anônimo ou bind mount é aceito.
  Redis sem persistência. PostgreSQL usa autenticação trust só no laboratório
  efêmero; não recebe credenciais operacionais.
- Antes de DDL/dados, conferir labels dos containers, mounts, bindings e markers
  iguais no PostgreSQL e Redis. Os testes recusam defaults, bancos comuns,
  hosts externos, portas usuais e diretórios existentes/compartilhados por links.
- `D8_PrepareSchemaExplicitly` exige marcador de autorização separado. Gera DDL
  do modelo EF e lê apenas as definições SQL existentes de tabelas duráveis
  (`ViagemDuravelPostgres`, `IntegridadeCircularDuravel` e remoção do índice
  antigo). Não chama `Migrate`, `EnsureCreated`, startup ou histórico EF;
  não reproduz migrations de limpeza/truncamento. Geração offline não executa SQL.
  A execução futura aplica esse DDL somente ao banco novo comprovadamente vazio
  e guardado, seguido do seed estrutural sintético e marker de preparo concluído.
- Só após sucesso e validação do TRX de preparo, retirar sua autorização do
  ambiente e selecionar `D8_CaptureThroughActualGpsCasAndShadow`.
  A remoção usa `Remove-Item Env:NOPONTO_SHADOW_FIXTURE_D8_PREPARE` e verifica
  ausência no ambiente de processo. String vazia continua sendo presença e é
  recusada pelo teste de captura. No PowerShell 7 local, o setter .NET com $null
  reproduziu string vazia herdada pelo filho; o finally usa NullString ao
  restaurar ausência anterior dessa variável, preservando vazio/token anteriores.
  Os dois testes conectados não pertencem à allowlist offline `.ci`.
- Flags só nos objetos do laboratório: Shadow, OperationalDtoCapture e
  OfflineExport ativos; `Fixture=true`; ML/Histórico desativados. Os defaults da
  aplicação continuam desligados. O diretório novo recebe ACL privada do usuário
  atual; o subdiretório `capture-<guard>` recebe o JSONL. Limites atuais do
  exportador são preservados, inclusive 4096 bytes por linha, fila, quotas e retenção.

## GO/NO-GO e verificações

GO para executar somente com autorização de containers/DDL, checkpoint revisado,
imagens/cache locais disponíveis, política PowerShell permitindo o script e
diretório novo e aceite explícito do risco de saída com `-AllowD8BridgeEgress`.
Acessibilidade host→portas loopback da bridge exclusiva e
execução real do DDL/repos ainda precisam ser comprovadas no primeiro ensaio;
não há fallback para redes ou serviços existentes.

## Escolha de rede e redução explícita de isolamento

O Engine local consultado é 28.5.1 Linux. A segunda tentativa mostrou pedido
de publicação em HostConfig e nenhum binding efetivo em NetworkSettings, com
consulta válida. É o mesmo sintoma relatado no [Moby #53256](https://github.com/moby/moby/discussions/53256),
que cita Engines 29.x. Rede exclusivamente --internal é a causa provável,
não uma causalidade demonstrada por comparação A/B local.

| Alternativa | Decisão |
|---|---|
| Cliente .NET dentro da rede interna | Mantém bloqueio de saída, mas exige outro container/imagem SDK, disponibilizar build/cache/fontes e adaptar a fixture que exige 127.0.0.1. Não há cliente containerizado D8 existente; maior alteração. |
| Bridge exclusiva não interna | Escolhida: menor correção para o cliente no host e publicação normal em loopback. |
| Redes interna e não interna simultâneas | Também permite saída pela rede normal, adiciona recursos e muda a regra de uma única rede; sem vantagem demonstrada para estes dois bancos. |

**Esta bridge não equivale a --internal e não bloqueia tráfego de saída.**
NAT/roteamento pode permitir acesso dos containers à internet, LAN e serviços
alcançáveis no host, inclusive portas publicadas de outras stacks. Rede exclusiva
impede associação direta às redes operacionais; não comprova bloqueio desses
caminhos de saída. Os bancos recebem só dados sintéticos e não há workloads
externos deliberados, mas isso não substitui firewall de egress.
Ver [bridge](https://docs.docker.com/engine/network/drivers/bridge/) e
[publicação de portas](https://docs.docker.com/engine/network/port-publishing/).

Por isso o runner recusa D8, antes de qualquer Docker, sem
`-AllowD8BridgeEgress`. A próxima autorização deve aceitar expressamente essa
diferença. Se bloqueio absoluto de saída for requisito, NO-GO para este modo;
usar futuramente cliente na rede interna, sem executar esse plano.

O plano usa driver bridge explícito e default de bind 127.0.0.1, além dos -p
explícitos já existentes. `d8-network.json` comprova consulta/identidade/ownership,
driver local, Internal=false e default de bind antes de iniciar os containers;
o receipt identifica a política. Cada container precisa estar somente nessa rede
por ID. O guard de endpoints efetivos 127.0.0.1, tmpfs e limpeza não é relaxado.
Sem host networking, firewall global, rede operacional ou Compose.

```powershell
pwsh -NoProfile -File tools/eta_ml/test_shadow_network_offline.ps1
```

Sucesso requer dois GPS aceitos (watermark Lua e DTO em Redis), mesma viagem no
PostgreSQL/Redis, versão e ocorrência sintéticas esperadas, duas observações no
Shadow e dois registros escritos. A política normal de checkpoint da viagem
continua válida: o timestamp durável pode preceder o snapshot quente, mas ambos
devem manter a identidade da viagem. O CAS real também deve rejeitar a primeira
observação quando reapresentada após a segunda, sem alterar o watermark final.

Verificar JSONL `noponto-eta-shadow-offline-v2`, `data_kind=fixture`, ETA/confiança
originais na extensão D4, SHA-256 canônico, snapshot D7 e sua quantidade de
amostras. O pareamento usa identidade, não ordem dos arquivos. Status esperado
`model_unavailable` com zero HTTP do Shadow; o PostGIS resolveu a geometria.
As fontes sintéticas e a resposta ETA são interceptadas por handlers, sem sockets.
Nenhum descarte, falha de escrita ou encerramento incompleto é aceito.

Arquivos retidos: `d8-resources.json` (IDs/labels para recuperação), TRXs de
preparo/captura, `capture-<guard>/eta-shadow-*.jsonl` e
`d8-verification.json`. Os TRXs conectados são conferidos pelo runner, exigindo
exatamente o teste esperado e um resultado Passed por fase. O verificador
`.ci/offline_tests.py` continua reservado à seleção offline existente.

NO-GO diante de falha de ownership, configuração, schema/seed, identidade,
captura, contador ou TRX. Não promover um ensaio parcial a sucesso.

Antes das validações de isolamento, `d8-endpoints.json` registra, para cada
container, resultado/código do inspect, bindings solicitados em
`HostConfig.PortBindings` e bindings atribuídos em `NetworkSettings.Ports`,
separadamente, com status e motivo da decisão. A consulta usa projeção restrita;
não salva inspect completo, stderr, environment, credenciais ou dados GPS.
Ausência, consulta falha, JSON malformado e inconsistência são recusados e
distintos de publicação insegura. Falha ao gravar a evidência também interrompe.

Só é aceito um binding IPv4 `127.0.0.1` por porta esperada, sem outras portas
publicadas. Porta vazia/zero no HostConfig solicita alocação dinâmica; não
comprova publicação. A porta efetiva precisa constar de NetworkSettings, ser
válida para a fixture e coincidir com a configuração quando esta é explícita.
Todos os elementos são conferidos, inclusive bindings adicionais. `0.0.0.0`,
`::` e endereços não locais são recusados. `::1` continua sem suporte, pois a
fixture e suas conexões usam exclusivamente IPv4; não há fallback IPv6.

Teste offline dessa interpretação, sem executar Docker:

```powershell
pwsh -NoProfile -File tools/eta_ml/test_shadow_endpoints_offline.ps1
```

## Encerramento e limites

O teste para Shadow/exportador graciosamente; o runner restaura as variáveis do
processo e para somente containers cujos nomes exclusivos e labels comprovem o guard atual.
`--rm`/tmpfs eliminam seus dados; só a rede do mesmo guard é removida. Não há
limpeza de volumes, pastas existentes ou outros serviços. Se o processo for
interrompido à força, usar o receipt e conferir cada label antes da recuperação
manual; nunca parar/remover recursos por prefixo ou nomes de outro ensaio.

O receipt registra também os nomes exclusivos antes da criação, permitindo
verificar uma falha anterior ao retorno do ID. A limpeza confere inventário,
ownership, código de saída e estado final: ausência comprovada, remoção ou
container comprovadamente parado são resultados aceitos. Falha de comando,
recurso ainda ativo/rede presente ou ownership inconclusivo retornam erro;
nenhuma mutação é tentada sem ownership. `d8-cleanup.json` preserva o resultado
por recurso e o erro original, reportado separadamente dos erros de limpeza.
Se o processo for morto à força, o finally pode não completar: a recuperação
continua manual, a partir do receipt, sem declarar sucesso do ensaio.

Validação offline do encerramento (Docker substituído por função simulada,
sem carregar o corpo operacional do runner):

```powershell
pwsh -NoProfile -File tools/eta_ml/test_shadow_cleanup_offline.ps1
```

Teste offline da separação de autorizações, com filhos PowerShell reais lendo
somente a variável sintética (Docker e dotnet test não são executados):

```powershell
pwsh -NoProfile -File tools/eta_ml/test_shadow_environment_offline.ps1
```

As duas primeiras tentativas foram encerradas pelo guard antes do preparo;
a terceira comprovou bridge não interna, endpoints em loopback e preparo do
schema, mas a captura recusou a autorização de preparo ainda presente.
A limpeza foi comprovada nas três. A remoção explícita e a herança no filho
foram agora validadas offline; uma quarta tentativa depende de nova autorização.
A primeira captura validará o transporte/persistência do DTO sintético, não a
acurácia. O ETA legado da fixture não fornece emissão: `CalculatedAt=null`;
`FeatureCutoffUtc=null` e replay científico bloqueado. D1/C0, ground truth
independente e disponibilidade completa das demais features continuam pendentes.
