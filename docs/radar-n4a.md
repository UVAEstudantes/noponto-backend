# Radar de Espera — N4-A

Base `6955389`, branch `feat/radar-notificacoes`. Etapa de instalações e sessões anônimas, desabilitada por padrão. Sem push, FCM, Expo Push Service, worker, migrations ou mudança de GPS/ETA/SignalR. Nenhuma instância produtiva foi acessada.

## Habilitação e limites

Configuração própria, a ser aplicada somente em ambiente autorizado:

```json
{
  "Radar": {
    "Enabled": false,
    "MaxLinhas": 20,
    "MaxContextos": 40,
    "MaxRegistrosPorDia": 500,
    "MaxSessoesAtivas": 2000
  }
}
```

Sem `Enabled=true`, as rotas retornam 404 antes do binding de modelos e de acessar o store Radar. Não há tarefas, timers ou serviço hospedado novo. Reutiliza `IConnectionMultiplexer` e `IEstruturaLeituraV2Repository`; não cria conexão ou catálogo paralelos.

Inspecionados, somente por leitura via Git, `useRadarEspera.ts`, `preferenciasRadarEspera.ts` e `radarEsperaSection.tsx` do frontend `3c035ac`. A seleção acompanha as linhas disponíveis e não possui limite numérico explícito. Portanto **20 linhas e 40 contextos são proteções novas do backend**, não regras atribuídas ao aplicativo. Limitam consultas de itinerário e tamanho de sessão; devem ser alinhadas com o frontend na integração. Configuração aceita no máximo 50 de cada, com contextos >= linhas. Uma linha pode ter vários sentidos/padrões; há ao menos um contexto por linha selecionada e nenhum contexto de linha não selecionada.

O corpo tem teto de 16 KiB. O teto de 2000 sessões limita o conjunto ativo; sessões encerradas/expiradas têm um único registro por instalação, com retenção limitada. Cadastro global limitado a 500/dia, credencial de 180 dias: no máximo aproximadamente 90 mil instalações dentro desse envelope. São limites conservadores para o host de 4 GB, **não um benchmark de capacidade**. Medir `MEMORY USAGE`, latência Lua/SQL e convivência com ETA/GPS em ambiente descartável antes de habilitar em produção. Não alterar os limites Redis globais nesta etapa.

## Autenticação da instalação

`POST /radar/instalacoes` não recebe identidade nem token do celular. O servidor cria UUID e 32 bytes criptograficamente aleatórios; entrega `UUID_sem_hifens.SEGREDO_HEX` uma única vez. O cliente envia `Authorization: Bearer <credencial>` nas demais operações. O UUID é apenas um localizador: conhecer o UUID ou o identificador da sessão não autoriza nada. Não existem rotas para consultar o proprietário informado pelo celular.

Redis guarda somente SHA-256 do segredo de alta entropia, nunca o segredo. Não necessita salt/KDF de senha porque o segredo possui 256 bits aleatórios. Comparação Lua percorre todo o hash sem saída antecipada por byte. Toda mutação reautentica dentro do mesmo script que altera sessão e índices; revogar durante uma validação impede a escrita posterior. Não há JWT de usuário, login ou contas.

Credencial expira em 180 dias pelo relógio do Redis. `POST /radar/instalacao/renovar`, autenticado com credencial ainda válida, estende o prazo por 180 dias e mantém o segredo. A renovação é repetível sem risco de perder uma credencial nova se a resposta se perder. Consulta não renova instalação nem sessão. Recomenda-se ao frontend renovar explicitamente quando restarem <=30 dias, durante uso em primeiro plano. Credencial vencida/revogada não pode renovar: cadastrar outra instalação, descartando a credencial antiga. Não há recuperação de identidade anônima nem rotação sobreposta nesta etapa; em caso de suspeita, revogar e cadastrar novamente. TLS é obrigatório no uso real.

Na integração React Native/Expo: persistir a credencial com **expo-secure-store** (Android Keystore), nunca AsyncStorage, preferências do Radar, URL, deep link, telemetria ou logs. Guardar o registro com sucesso antes de ativar. Tratar reinstalação/perda do armazenamento como instalação nova; não restaurar sessão automaticamente a partir de preferências locais. Fazer a primeira consulta autenticada para reconciliar estado após retomada. Nunca embutir credencial real nos exemplos `.http` ou no Git. Todas as respostas Radar têm `Cache-Control: no-store`.

`pushDisponivel=false` é o marcador explícito da capacidade atual. Não existe campo/endpoint para registrar um token push arbitrário. A associação futura deverá exigir esta autenticação **e um protocolo de comprovação apropriado para o provedor**; possuir/enviar um ExpoPushToken não substitui a credencial nem prova posse.

## Contrato HTTP

| Método e rota | Sucesso | Comportamento |
|---|---|---|
| POST `/radar/instalacoes` | 201, Location `/radar/instalacao` | Corpo vazio; credencial somente aqui |
| GET `/radar/instalacao` | 200 | Metadados da própria instalação |
| POST `/radar/instalacao/renovar` | 200 | Corpo vazio, renova somente credencial |
| DELETE `/radar/instalacao` | 204 | Revoga credencial, apaga sessão e remove índices |
| POST `/radar/sessao` | 201, Location `/radar/sessao` | Ativa; revisão 0, sem sessaoId |
| GET `/radar/sessao` | 200 ou 204 | Sessão corrente ou nenhuma disponível |
| PUT `/radar/sessao` | 200 | Substituição integral; sessaoId e revisão obrigatórios |
| DELETE `/radar/sessao/{sessaoId}` | 204 | Encerramento idempotente da própria sessão indicada |

Location pode ser absoluta conforme o host. Revogação torna a credencial inválida imediatamente: repetir a revogação com ela recebe 401. Encerramento, por sua vez, retorna 204 também para sessão ausente, já encerrada, expirada ou ID que não corresponde à sessão atual daquela instalação. Assim uma requisição antiga nunca encerra uma nova ativação, nem revela existência de sessões alheias.

Registro (segredo ilustrado por placeholder; a resposta real contém 64 caracteres hexadecimais):

```json
{
  "instalacaoId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "credencial": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.<SEGREDO_GERADO_NO_SERVIDOR>",
  "credencialExpiraEm": "2027-05-05T15:00:00+00:00"
}
```

GET instalação/renovação nunca devolve credencial ou hash:

```json
{
  "instalacaoId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "credencialExpiraEm": "2027-05-05T15:00:00+00:00",
  "pushDisponivel": false
}
```

POST sessão, usando IDs reais obtidos no catálogo estrutural existente:

```json
{
  "paradaId": "20000000-0000-0000-0000-000000000001",
  "contextos": [{
    "linhaId": "10000000-0000-0000-0000-000000000001",
    "sentidoId": "30000000-0000-0000-0000-000000000001",
    "padraoOperacionalId": "40000000-0000-0000-0000-000000000001",
    "padraoVersaoId": "50000000-0000-0000-0000-000000000001"
  }],
  "linhasSelecionadas": ["10000000-0000-0000-0000-000000000001"],
  "linhasPrioritarias": [],
  "antecedenciaMinutos": 10,
  "revisaoEsperada": 0
}
```

Resposta de sessão (mesma configuração validada, com os campos adicionais abaixo):

```json
{
  "sessaoId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "instalacaoId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "paradaId": "20000000-0000-0000-0000-000000000001",
  "contextos": [{
    "linhaId": "10000000-0000-0000-0000-000000000001",
    "sentidoId": "30000000-0000-0000-0000-000000000001",
    "padraoOperacionalId": "40000000-0000-0000-0000-000000000001",
    "padraoVersaoId": "50000000-0000-0000-0000-000000000001"
  }],
  "linhasSelecionadas": ["10000000-0000-0000-0000-000000000001"],
  "linhasPrioritarias": [],
  "antecedenciaMinutos": 10,
  "estado": "ativa",
  "criadaEm": "2026-11-06T15:00:00+00:00",
  "expiraEm": "2026-11-06T16:00:00+00:00",
  "revisao": 1
}
```

PUT repete a configuração integral acrescentando `"sessaoId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"` e `"revisaoEsperada":1`. Preserva identidade, criação e expiração; incrementa revisão para 2. Troca deliberada de parada e linhas usa o mesmo PUT. Dois PUT concorrentes da revisão 1: apenas um sucede. Duas ativações concorrentes: uma 201 e uma 409. Depois de encerramento/expiração, POST cria identidade nova e revisão 1; repetir um POST cuja resposta se perdeu recebe 409 se ainda houver sessão ativa. Reconciliar com GET.

Estados: `ativa`, `encerrada`, `expirada`. Prazo fixo `criadaEm + 1 hora`, calculado pelo relógio Redis. GET e PUT não prorrogam. A retenção de metadados permite observar encerramento/expiração; após descarte, GET retorna 204.

Erros de domínio usam ProblemDetails com `status`, `title` e `code`, por exemplo:

```json
{"status":422,"title":"contexto_incompativel_ou_desatualizado","code":"contexto_incompativel_ou_desatualizado"}
```

| HTTP | Códigos/casos |
|---|---|
| 400 | Binding/anotações; `limites_ou_identificadores_invalidos`, `revisao_invalida` |
| 401 | `credencial_invalida`, com `WWW-Authenticate: Bearer realm="radar"` |
| 404 | Flag desabilitada |
| 409 | `sessao_ja_ativa`, `sessao_nao_ativa`, `revisao_desatualizada` |
| 413 | Corpo acima de 16 KiB (camada HTTP) |
| 422 | `prioridade_nao_selecionada`, `parada_inexistente`, `contexto_incompativel`, `contexto_incompativel_ou_desatualizado` |
| 429 | `limite_de_requisicoes`, `limite_de_operacoes`, `limite_de_registros`, `limite_de_sessoes`; Retry-After |
| 503 | `radar_temporariamente_indisponivel`, sem detalhes da conexão Redis |

Validação automática do ASP.NET pode usar ValidationProblemDetails, com `errors`, em vez de `code`. GET sessão não realiza SQL. Antes de POST/PUT, a aplicação autentica, verifica limites/duplicatas e prioridades, verifica existência da parada e reutiliza o itinerário por versão: linha, sentido, padrão, versão atual e ocorrência da parada devem coincidir. Input inválido não altera sessão/índice (a autenticação conta para rate limit). Uma publicação estrutural após a validação pode tornar um contexto válido obsoleto: o futuro consumidor deverá revalidar a versão antes de usar a sessão. Não há transação distribuída PostgreSQL/Redis.

## Rate limiting

- Antes de binding: 5 cadastros/10min e 120 demais requisições/min por bucket de IP, em janelas fixas. 256 buckets por categoria, sem reter IPs; colisões podem restringir clientes diferentes. Memória constante mesmo sob IPs aleatórios.
- Dentro de Lua: 120 operações autenticadas/min por instalação (configurar consome 2: pré-autenticação e mutação); 500 registros/dia UTC e teto de 2000 sessões ativas, configuráveis.
- A cota HTTP é local à instância e reinicia com ela; cotas Redis sobrevivem ao processo. Retry-After é conservador: 600s para cadastro HTTP, 86400s para cadastro diário, 60s nos demais casos.
- O gate usa `RemoteIpAddress`; não confia em `X-Forwarded-For` arbitrário. Na implantação futura, conferir o proxy confiável e a política existente de forwarded headers para evitar que todos compartilhem a cota do proxy. Nada dessa infraestrutura foi alterado aqui.

## Redis

Namespace fixo `noponto:radar:v1:{radar}:`, sem compartilhar chaves com GPS, ETA ou SignalR. Hash tag própria permite operações no mesmo slot. Uso previsto no Redis existente standalone; a movimentação entre paradas usa chaves derivadas dentro de Lua, e Redis Cluster exigirá homologar esse contrato antes de adoção.

| Sufixo | Tipo / TTL | Dados |
|---|---|---|
| `installation:{idN}` | Hash / 180 dias | verificador, expiração ms, janela/contador de requisições |
| `session:{idN}` | String JSON / até expiração +24h | identidade interna, proprietário, configuração, estado, revisão, tempos ms |
| `stop:{paradaIdD}` | Sorted set / maior expiração +60s | instalação em formato D → expiração da sessão em ms |
| `stops` | Sorted set | parada → maior prazo conhecido naquele índice |
| `active` | Sorted set | instalação → expiração; controla teto global |
| `registrations` | Hash / 48h | dia UTC e total do cadastro diário |

`N` é UUID sem hífens; `D` com hífens. Sessão usa chave estável por proprietário e guarda sessaoId interno: isso impede duas sessões ativas por instalação. Lua autentica, compara revisão/identidade e altera sessão, proprietário e índices sem interleaving. Revogação apaga hash/sessão e remove membro. Encerramento conserva estado por 24h. Expiração é lógica pelo relógio Redis, inclusive antes da remoção física: consulta marca expirada e retira índices sem estender retenção. Sem consultas, TTL do índice de parada e scores excluem sessões vencidas; TTL não dispara worker.

Ativação poda expirados de `active`, remove até 100 referências vencidas de `stops`, e poda scores vencidos da parada destino. Tamanho vivo de `active` <= teto; os índices de parada também expiram sem tráfego. Referências de parada com prazo antigo podem permanecer em `stops` até a próxima escrita; não representam sessões elegíveis. Remover a sessão de maior prazo de uma parada pode deixar em `stops` um limite superior antigo, nunca estender a validade de uma sessão. Não há `SCAN`, `KEYS`, tabela nova ou histórico ilimitado de sessões.

## Integração seguinte

Frontend: alinhar limites → cadastrar/salvar em SecureStore → buscar metadados e sessão → montar contextos com catálogo atual → POST para ativar → serializar PUT usando última revisão → em 409 consultar/reconciliar → DELETE da identidade conhecida para encerrar → renovar credencial explicitamente antes de vencer. Preferências locais continuam independentes. Definir depois a experiência de coexistência entre notificação local e push, sem duplicar notificações.

Futuro N4-B (não implementado): paginar `ZRANGEBYSCORE stops (agora +inf LIMIT ...`; para cada parada, podar scores vencidos e consultar os membros futuros de `stop:{id}`, fazendo GETs em pipeline das chaves fixas de sessão. Validar estado, proprietário, prazo, identidade e contexto; remover referências ausentes/vencidas com comparação apropriada e TTL. Reutilizar a consulta de eventos/ETA existente **uma vez por parada/contexto compartilhado**, sem polling por usuário. Revalidar revisão/revogação imediatamente antes de qualquer decisão de envio. Iterar com cursor estável por score+membro ou ZRANGE lex compatível para não perder empates. Índices não garantem entrega nem exatamente uma execução; o worker precisará de deduplicação, backpressure e protocolo de associação push. Não existe endpoint público para enumerar esses índices.

## Validação e pendências

O projeto usa xUnit v2/VSTest, testes compilados no `.csproj` existente. Testes HTTP usam host Kestrel efêmero em loopback, catálogo e store sintéticos; não executam `Program.cs`, bootstrap/migrations ou hosted services.

Redis real local indisponível: Docker Desktop não estava em execução; não foi iniciado. Usado fakeredis 2.40.0 + lupa 2.8 em processo temporário loopback, com adaptador RESP2 para o cliente .NET. O script Lua de produção, o adaptador Redis .NET e as regras de serviço são executados; **não é prova de Redis real, durabilidade, carga, failover, latência nem Cluster**. O modo emulado desliga apenas comandos de handshake não necessários (`ECHO/INFO/CONFIG/CLIENT/CLUSTER/SUBSCRIBE`) no fixture, sem alterar o store produtivo.

Os testes Redis exigem opt-in `NOPONTO_RADAR_TEST_REDIS` com endpoint explicitamente loopback e base descartável. Sem variável, são ignorados com motivo. Não usam configuração real da aplicação, não fazem FLUSH/SCAN e limpam apenas IDs sintéticos. A contagem global de cadastros permanece na instância de teste até seu descarte. Não apontar para Redis compartilhado. Deixar `NOPONTO_RADAR_TEST_EMULATED` ausente para Redis real. Executar novamente em Redis real descartável antes de habilitar a feature; PostgreSQL real/publicação concorrente, dispositivo Android, precisão de ETA, persistência/failover e integração push não foram exercitados.

### Comandos e resultados de 2026-11-06

```powershell
dotnet build NoPonto/NoPonto.csproj -c Release --no-incremental -v:q

# Somente uma instância emulada/descartável própria na porta abaixo:
$env:NOPONTO_RADAR_TEST_REDIS = '127.0.0.1:6403,abortConnect=true,connectTimeout=3000,syncTimeout=5000'
$env:NOPONTO_RADAR_TEST_EMULATED = '1'
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --filter 'FullyQualifiedName~NoPonto.Tests.Radar' --logger 'trx;LogFileName=n4a-final.trx' --results-directory artifacts/n4a -v:q
```

Build: **exit 0, zero erros, seis avisos preexistentes** (CS8981 em duas declarações de migration, CS7022 do test SDK e três xUnit2031 em testes existentes). Testes: **Passed: 49, Failed: 0, Skipped: 0**. TRX conferido: 49 executados/aprovados, sem abortados/timeouts/inconclusivos. Rodada offline, removendo ambas as variáveis: **Passed: 34, Failed: 0, Skipped: 15**, em `artifacts/n4a/n4a-offline.trx`. São rodadas sobrepostas, não 83 casos distintos. Não executada suíte completa, testes de produção, Redis real ou banco real.

Para reproduzir o emulador opcional, instalar dependências somente em pasta temporária e executar o helper em outro terminal; nenhum pacote Python é adicionado ao backend:

```powershell
$radarDeps = Join-Path $env:TEMP 'noponto-n4a-python'
python -m pip install --target $radarDeps 'fakeredis[lua]==2.40.0' 'lupa==2.8'
$env:PYTHONPATH = $radarDeps
python tools/radar/redis_emulator.py --port 6403
# Ctrl+C encerra e descarta todos os dados da instância emulada.
```

As primeiras rodadas detectaram incompatibilidades RESP2 do servidor TCP padrão do fakeredis e uma asserção de Location que esperava URL relativa. Corrigido o harness de emulação (PING/SET/float e handshake) e a asserção para conferir o caminho da URL absoluta. Nenhuma falha ficou pendente na rodada final. O emulador e seus processos foram encerrados após os testes. Sintaxe Python e `git diff --check` também aprovados.

### Matriz de evidências

Nomes abaixo são métodos nas classes `RadarTests` / `RadarRedisTests`; teorias incluem partições de entrada. Redis nesta matriz significa a execução emulada descrita acima.

| Requirement | Evidence |
|---|---|
| Registro e verificador sem segredo persistido | `RegistroGeraCredencialForteEGuardaSomenteHash`, `AtivacaoConsultaETtlFixosDeUmaHora` |
| Instalação alheia / ID conhecido sem credencial | `OutraInstalacaoEIdConhecidoNaoPermitemAcesso`, `IdConhecidoSemCredencialNaoAcessaStore` |
| Ativação, consulta, TTL de uma hora | `AtivacaoConsultaETtlFixosDeUmaHora` |
| Reconfiguração, identidade e revisão | `ReconfiguracaoPreservaIdentidadePrazoERecusaRevisaoAntiga`, `AtualizacoesConcorrentesAceitamUmaUnicaRevisao` |
| Mudança de parada/linha e índices | `MudancaDeParadaELinhasMoveIndiceAtomicamente` |
| Prioridade inválida e ausência de mutação | `EntradasInvalidasSaoRejeitadasAntesDoCatalogo`, `ConfiguracaoInvalidaPreservaSessaoEIndice` |
| Contexto incompatível, obsoleto, inexistente | `ContextoIncompativelNaoEscreveNoStore` |
| Expiração e reativação | `ExpiracaoNaoRenovaPrazoERemoveIndice` |
| Encerramento idempotente / requisição antiga | `EncerramentoIdempotenteNaoEncerraNovaSessao` |
| Duas ativações concorrentes | `DuasAtivacoesConcorrentesCriamUmaUnicaSessao` |
| Falha Redis sanitizada e HTTP 503 | `RedisDesconectadoRetorna503Sanitizado`, `HttpRegistroLocationCacheErrosELimite` |
| Limites de entrada e rate limiting | `JanelaInvalidaNaoAlteraSessao`, `LimitesInclusivosDaJanelaSaoAceitos`, `LimitesConfiguradosSaoAplicados`, `RateLimitLimitaCadastroETentativasEResetaNaJanela`, `RateLimitRedisLimitaCredencialERegistrosGlobais` |
| Isolamento, renovação e revogação | `InstalacoesIsoladasRenovacaoERevogacao`, `CredencialExpiradaNaoPodeRenovarNemConsultar`, `RevogacaoConcorrenteComAtivacaoNaoDeixaSessaoOrfa` |
| Teto global sem estado parcial / limpeza de referências | `TetoGlobalDeSessoesNaoCriaEstadoParcial`, `ReferenciasExpiradasSaoPodadasSemScan` |
| Flag, status, payload, cache e verbos HTTP | `HttpFlagDesabilitadaRetorna404SemAcessarRedis`, `HttpSessaoRetornaContratoMinimoEVerbosCorretos`, `HttpRegistroLocationCacheErrosELimite` |

## Arquivos da entrega

- `NoPonto/Program.cs`: uma chamada de registro DI do módulo, sem hosted service.
- `NoPonto/1-API/Controllers/RadarController.cs`: endpoints.
- `NoPonto/1-API/Extensions/RadarRegistration.cs`: opções e DI.
- `NoPonto/1-API/Middlewares/RadarHttpFilter.cs`: flag, erros, cache e limitação HTTP.
- `NoPonto/2-Application/DTOs/Radar/RadarContratos.cs`: entradas e respostas.
- `NoPonto/2-Application/Services/Radar/RadarService.cs`: credenciais, regras, validação e interfaces do módulo.
- `NoPonto/2-Application/Services/Radar/RadarRateGate.cs`: buckets de IP limitados em memória.
- `NoPonto/4-Data/Repositories/RadarRedisStore.cs`: script atômico e tradução de indisponibilidade.
- `NoPonto/5-Testes/RadarTests.cs`, `NoPonto/5-Testes/RadarRedisTests.cs`: testes HTTP, serviço, segurança e Redis.
- `NoPonto/radar.http`: exemplos de chamadas com placeholders.
- `tools/radar/redis_emulator.py`: harness opcional descartável, não usado pela aplicação.
- `docs/radar-n4a.md`: contrato, estrutura, limitações, evidências e plano de integração.
- `NoPonto/HISTORICO.md`: registro incremental da entrega.
