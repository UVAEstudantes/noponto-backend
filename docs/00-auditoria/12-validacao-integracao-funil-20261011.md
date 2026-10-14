# Etapa 4B — Validação da instrumentação operacional

Data: 2026-10-11. Base: [relatório 4A](11-instrumentacao-funil-operacional-20261011.md). Consultados AGENTS.md, histórico, estado Git, fontes, testes e fixture. Somente este relatório e acréscimo em `NoPonto/HISTORICO.md` modificados na 4B.

## Classificação

**BLOQUEADA PARA HOMOLOGAÇÃO.** A revisão estática e 199 testes locais sustentam a preservação funcional da instrumentação, mas os três casos de integração real não foram executados: não há ambiente comprovadamente descartável. A divergência preexistente `Cor/Icone` entre modelo e snapshot também impede certificar o working tree como release exclusivamente diagnóstica. A entrega dos logs completos ainda exige verificar limite de mensagem no pipeline real.

Não foi encontrada regressão de regra provocada pela 4A. “Bloqueada” significa ausência das evidências necessárias para homologar, não falha demonstrada de commit/rollback. Não houve deploy, preparação de migration, correção funcional ou acesso ao homeserver/produção.

## Gate de isolamento: execução externa interrompida

Inspeções somente de metadados Docker locais, sem ler ambiente/credenciais dos containers, conectar ao banco/Redis ou executar migrations:

| Evidência | Resultado |
|---|---|
| Contexto Docker | `desktop-linux`, endpoint local `npipe:////./pipe/dockerDesktopLinuxEngine` |
| `transporte_postgres` | `postgis/postgis:16-3.4`, projeto Compose `noponto`, volume persistente em `/var/lib/postgresql/data`, porta host 5432 |
| `transporte_redis` | `redis:7`, projeto Compose `noponto`, volume persistente em `/data`, porta host 6380 |
| Outros containers encontrados | `auth-postgres`/`auth-redis`; não utilizados |
| `POSTGIS_TEST_CONNECTION` / `REDIS_TEST_CONNECTION` no processo | Ambas ausentes; valores secretos não foram impressos |

Tags compatíveis não comprovam versão efetiva ou isolamento. Os containers encontrados são preexistentes e persistentes, sem comprovação de uso exclusivo nesta etapa. Não podem ser utilizados. Conforme instrução 6, a execução externa parou nesse gate; não foram criados containers como substituição nem tentada migration.

[ViagemOperacionalFixture](../../NoPonto/5-Testes/ViagemOperacionalFixture.cs) cria schema `viagem3_<GUID>`, configura search path com `public`, executa `IMigrator.MigrateAsync()` e usa `REDIS_TEST_CONNECTION`, com fallback `localhost:6380`. Schema aleatório **não basta** para isolamento quando o banco/container é compartilhado. Sem variável Redis explícita, o fallback apontaria justamente para um serviço preexistente encontrado. Nenhuma fixture foi inicializada aqui.

## Resultados individuais da integração

| Teste real | Resultado nesta 4B | Expectativa contida no teste, não resultado observado |
|---|---|---|
| `Funil_CommitRealSeparaInicioPassagensEFim` | **NÃO EXECUTADO — isolamento não comprovado** | `CommitInicio=1`, `CommitPassagem=3`, `CommitFim=1`; Outbox legado 5; catch-up 3; três linhas Outbox de passagem; estado finalizado |
| `Funil_FalhaAntesCommitNaoConfirmaPassagens(false)` | **NÃO EXECUTADO — isolamento não comprovado** | Falha após write de estado e antes de Outbox; selecionadas 2 passagens; confirmadas 0; contador Outbox 0; catch-up 2; zero linhas Outbox de passagem |
| `Funil_FalhaAntesCommitNaoConfirmaPassagens(true)` | **NÃO EXECUTADO — isolamento não comprovado** | Falha após write de Outbox e antes de commit; mesma contabilidade esperada; INSERTs da transação devem desaparecer no rollback |

Fontes: [ViagemDuravelPostgresTests](../../NoPonto/5-Testes/ViagemDuravelPostgresTests.cs). Nos casos de falha, o nascimento inicial ocorre antes de instalar o agregador do teste. Portanto existe evento inicial previamente persistido fora desse recorte; “Outbox zero” acima refere-se ao contador do recorte e às linhas do tipo passagem, não à tabela inteira.

Não marcar os três como aprovados/ignorados por execução de outra bateria. Nenhuma falha de integração foi observada porque eles não rodaram. Para homologação, registrar separadamente cada caso, conexão/versões descartáveis e contabilidade real. O teste de sucesso tem quatro atualizações, não um único commit com os três tipos: os tipos são agregados ao longo dessas transações.

## Revisão de preservação funcional

Diff local contra HEAD, incluindo os dois arquivos novos não rastreados da 4A. Corpos de `Decidir` e `AplicarAvaliacaoMudanca` comparados textualmente com HEAD, desconsiderando whitespace e a renomeação da chamada interna de início: preservados. A renomeação evita contabilizar duas decisões numa recuperação, sem alterar estado/eventos retornados.

| Área | Verificação e limite |
|---|---|
| Regras de passagem/baseline/terminal/mudança/integridade | Wrappers observam o resultado dos corpos anteriores. Guardas, IDs, timestamp, estado, cursor e lista de eventos anteriores preservados |
| Matching, ETA, ML, sampling, endpoints, SQL de ocorrências | Não alterados pelo diff da 4A |
| Repositório/transações | Somente dois pontos diagnósticos novos: projeção pinada consumida e eventos após `CommitAsync`; sem SELECT, write, lock ou caminho de commit novo |
| Commit/rollback | `RegistrarEventosCommit` chamado somente depois de `CommitAsync` retornar com sucesso. Fault injections anteriores não alcançam esse ponto. Confirmação real no banco permanece pendente |
| Concorrência de counters | Arrays finitos; incrementos `Interlocked`, leituras `Volatile`; sem locks globais ou coleção por GPS |
| `AsyncLocal` | Contexto diagnóstico com agregador/origem restaurado por `using`; fluxo herdado pelas tarefas do lote; retry temporário não deve contaminar nova observação |
| Retry inicial | Agregador criado antes dele, mas `Current` legado continua ausente; stopwatch anterior continua depois do retry. Novos contadores incluem essa origem separadamente |
| Retry dentro do lote | Escopo legado permanece; origem nova/retry separada. Claims sem chamada ao serviço não são classificados como resultado nulo de matching |
| Logging | Campo textual fixo acrescentado à linha anterior; nenhum log individual de GPS ou label por identidade novo |

Instrumentação adiciona CPU/alocações, mas nenhum valor diagnóstico alimenta decisões operacionais. As hipóteses gerais de falha de memória/logger continuam sendo limites de execução, não prova de comportamento idêntico sob qualquer falha de processo. Não houve teste de carga integral ou execução concorrente com banco real nesta etapa.

### Seleção, commit, Outbox e materialização

- `Selecionado*`: lista devolvida pela decisão, antes de validações/transação; não garante persistência.
- `Commit*`: lista incluída na transação cujo commit foi confirmado ao cliente. Não conta necessariamente INSERT inédito: Outbox deduplica por EventId.
- `catchup_passagens_total`: seleção de ocorrências anterior ao commit, preservada; pode ficar positivo após rollback.
- `viagem_outbox_events`: soma antiga de eventos confirmados no escopo legado. Retry inicial fica fora; `CommitEventosForaEscopoLegado` permite explicar essa diferença.
- Journal/histórico/ACK: etapa posterior do worker, com transação própria. Os três testes novos não invocam o worker, portanto **não validam materialização**, mesmo quando vierem a passar.

Revisado [ViagemOutboxWorker](../../NoPonto/2-Application/Services/BackgroundServices/ViagemOutboxWorker.cs); nenhuma alteração nesta etapa. Erro depois de commit operacional não deve apagar contagem já confirmada; erro/ambiguidade no próprio commit não permite afirmar confirmação observada. Isso permanece distinto de retorno `Updated` ou `InfrastructureFailure`.

## Bateria local e concorrência

Executada sem restore, download, inicialização da API ou serviço externo, em menos de cinco minutos:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~GpsFunilOperacionalTests|FullyQualifiedName~GpsPerformanceMetricsTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalIntegracaoLogicaTests' --verbosity minimal
```

Resultado atual: **200 executados, 199 aprovados, uma falha, zero ignorados**. Duração reportada de testes 1 segundo; comando inteiro aproximadamente 6,5 segundos. Build incremental concluído. A falha é exclusivamente `Modelo_SnapshotSincronizado`, descrita abaixo. Não foi necessário repetir removendo o teste para mascarar o resultado.

`LotesConcorrentesECiclosIsoladosNaoPerdemContadores` passou: dois ciclos simultâneos com 1.000 e 700 chamadas produtivas de regra, contagens independentes e contexto restaurado. Testes de retry inicial, claim descartado, retry após GPS repetido, proteção/recuperação circular e resultado exclusivo também passaram. Essa evidência cobre processamento local paralelo e restauração de contexto, não substitui corrida de commits entre instâncias/banco/Redis.

`git diff --check`: sem erros; avisos de normalização LF/CRLF em arquivos anteriores não são erros de whitespace. Sem execução de suíte global ou testes externos.

## `Cor/Icone`: bloqueio separado da 4A

Erro reproduzido na bateria:

```text
AddColumnOperation:Cor:FormasPagamento; AddColumnOperation:Icone:FormasPagamento
```

O diff preexistente de [FormaPagamento.cs](../../NoPonto/3-Domain/Entities/FormaPagamento.cs) acrescenta `string? Cor` e `string? Icone`. A configuração EF não as ignora; convenções incluem as propriedades no modelo. [Snapshot atual](../../NoPonto/Migrations/TransporteDbContextModelSnapshot.cs) não contém essas propriedades nessa entidade. Busca por nomes exatos em migrations não encontrou migration correspondente. Último commit local da entidade é `9abe89f`, anterior à instrumentação; acréscimos estão no working tree já observado antes da 4A.

Conclusão: **alteração preexistente com representação de schema pendente neste checkout**, não regressão introduzida pela instrumentação. A intenção funcional pode ser legítima, mas não está certificada por esta auditoria; não decidir por conta própria se deve persistir, ser ignorada ou virar migration. Também não inferir schema real de produção a partir disso.

A fixture chama EF9 `MigrateAsync` sem supressão explícita de modelo pendente. Há risco concreto de `PendingModelChangesWarning`/exceção antes dos cenários, mesmo num banco novo; isso é inferência da versão/fluxo, **não erro externo observado**. O teste local já prova divergência. Não suprimir warnings nem modificar FormaPagamento, snapshot ou migrations para passar testes. Resolução da feature/schema requer etapa própria do responsável; release exclusivamente diagnóstica precisa de composição de artefato explicitamente revisada, sem incluir mudanças pendentes de modelo por acidente.

## Tamanho e custo da linha de performance

Avaliação sem iniciar a aplicação: assembly compilado carregado por reflexão em PowerShell; chamada direta a `FormatarFunilOperacional`. Arrays preenchidos apenas no objeto sintético local, sem tocar estado operacional. Template completo extraído dos literais existentes de `GpsPollingService`, com placeholders substituídos sinteticamente. O texto completo **não foi emitido por logger real**, portanto os números não incluem prefixo, JSON estruturado, escaping, collector ou transporte.

| Medida | Resultado |
|---|---:|
| Slots fixos/novos campos | 164 |
| Payload dos três arrays de `long` | 1.312 bytes, sem cabeçalhos |
| Sufixo com zeros | 5.337 caracteres/bytes UTF-8 |
| Sufixo com cinco dígitos em todos os counters | 5.993 caracteres/bytes UTF-8 |
| Template da linha completa | 181 placeholders, incluindo sufixo |
| Linha renderizada sintética, zeros/datas ISO | 10.107 bytes UTF-8 |
| Linha renderizada sintética, cinco dígitos/datas ISO | 11.475 bytes UTF-8 |

Todos os counters em 10.000 são um envelope sintético para dimensionar, não uma população operacional válida. Booleanos, nulos, enums, datas, tempos decimais e prefixos reais alteram o tamanho; esses dois cenários não são limite máximo garantido. Console multilinha e logger JSON podem produzir mensagens maiores ou fracionadas. O novo sufixo contém caracteres ASCII.

Com polling de 15 segundos e um log por ciclo: 40–60 ciclos em 10–15 minutos, 240/hora e 5.760/dia se cadência sustentada. **Somente o acréscimo** corresponde a aproximadamente 213–360 KB por 10–15 minutos, 1,28–1,44 MB/h e 30,74–34,52 MB/dia por instância, antes de overhead de transporte/retention. Ciclos demorados ou incompletos mudam frequência; não multiplicar esses números por GPS.

Medição exploratória local: 100 warmups e 1.000 chamadas ao formatador por delegate, em laço PowerShell, aproximadamente **42,37 ms** e **44.128.448 bytes alocados na thread** no conjunto (cerca de 44 KB/chamada, incluindo overhead do laço/PowerShell). Runtime do host **.NET 10.0.12**, assembly alvo .NET9/Debug. Não é benchmark de produção nem medida exclusiva do método: não extrapolar throughput/percentual de CPU/RAM da API. Ordem de grandeza observada indica formatação limitada por ciclo; serialização/logger/collector e custo por GPS/AsyncLocal não foram medidos aqui.

**Risco de truncamento concreto:** linha completa supera 8 KiB nos cenários; até o novo sufixo sozinho excede 4 KiB. Um limite de 8 KiB corta campos finais. Limite de 16 KiB pode comportar estes cenários textuais, mas não garante JSON/prefixos/valores máximos. Capacidade real de console→Docker→collector→armazenamento está desconhecida; não acessada em produção. Antes de homologar, confirmar preservação do último campo (`op_retry_outro_predecessor_nao_plausivel`) e do corpo completo em cada estágio. Não alterar configuração produtiva para presumir sucesso; se houver corte, propor ajuste diagnóstico separado e revisar compatibilidade dos logs.

## Comandos manuais para integração descartável

**Não executados.** Usar somente host Docker local de teste. Não apontar as variáveis para containers listados acima. Os comandos abaixo criam recursos novos com nomes/labels únicos e sem volumes persistentes; não fazem pull, não alteram Compose e não iniciam a API. Se imagem local faltar, parar, sem download automático. Rodar cada etapa e conferir os resultados/exit codes antes de avançar.

```powershell
# 1. Confirmar contexto local e imagens já existentes; parar se qualquer comando falhar.
docker context show
docker context inspect --format '{{.Endpoints.docker.Host}}'
docker image inspect postgis/postgis:16-3.4 --format '{{.Id}}'
docker image inspect redis:7 --format '{{.Id}}'

# 2. Recursos novos exclusivos; não reutilizar nomes/conexões de outro ensaio.
$funilRun = 'noponto-4b-' + [Guid]::NewGuid().ToString('N')
$funilPg = $funilRun + '-pg'
$funilRedis = $funilRun + '-redis'
$funilPassword = [Guid]::NewGuid().ToString('N') # efêmera; não imprimir/gravar no relatório.
docker network create --internal --label "noponto.audit=$funilRun" $funilRun
docker run --pull=never -d --name $funilPg --network $funilRun --label "noponto.audit=$funilRun" --memory 768m --tmpfs /var/lib/postgresql/data:rw,size=512m -p 127.0.0.1::5432 -e POSTGRES_USER=noponto4b -e "POSTGRES_PASSWORD=$funilPassword" -e POSTGRES_DB=noponto4b postgis/postgis:16-3.4
docker run --pull=never -d --name $funilRedis --network $funilRun --label "noponto.audit=$funilRun" --memory 128m --tmpfs /data:rw,size=64m -p 127.0.0.1::6379 redis:7 redis-server --save '' --appendonly no
```

Antes de qualquer teste/migration, comprovar identidade, labels, destinos tmpfs, rede exclusiva, portas loopback e versões efetivas. Não inspecionar `Config.Env` ou imprimir credenciais. As tags sozinhas não certificam versões. Aguardar manualmente prontidão dos novos serviços; sem loop ilimitado.

```powershell
# 3. Inspecionar exclusivamente os nomes criados no passo 2.
docker inspect $funilPg $funilRedis --format '{{.Name}} image={{.Config.Image}} audit={{index .Config.Labels "noponto.audit"}} mounts={{range .Mounts}}{{.Type}}:{{.Destination}} {{end}} networks={{range $name,$network := .NetworkSettings.Networks}}{{$name}} {{end}}'
docker port $funilPg 5432/tcp
docker port $funilRedis 6379/tcp
docker exec $funilPg pg_isready -U noponto4b -d noponto4b
docker exec $funilPg psql -U noponto4b -d noponto4b -Atc 'SELECT current_database(), current_user, version(), postgis_full_version();'
docker exec $funilRedis redis-cli INFO server

# 4. Somente após comprovação: bindings IPv4 loopback, extraídos dos containers novos.
$funilPgBinding = (docker port $funilPg 5432/tcp).Trim()
$funilRedisBinding = (docker port $funilRedis 6379/tcp).Trim()
if ($funilPgBinding -notmatch '^127\.0\.0\.1:\d+$' -or $funilRedisBinding -notmatch '^127\.0\.0\.1:\d+$') { throw 'Binding não comprovado' }
$funilPgPort = $funilPgBinding.Split(':')[1]
$funilRedisPort = $funilRedisBinding.Split(':')[1]
```

Confirmar PostgreSQL 16/PostGIS 3.4/Redis 7, banco `noponto4b` recém-criado e nenhuma reutilização de volume/container. O schema específico é criado pela fixture dentro desse banco novo e aparece no seu `Schema`. Anotar evidência sem segredo. **O bloqueio de modelo `Cor/Icone` deve ter resolução/composição revisada em etapa própria antes de considerar esta execução conclusiva.** Não criar migration nem excluir propriedades silenciosamente para executar este roteiro.

```powershell
# 5. Só executar com os gates anteriores satisfeitos. A fixture aplica migrations EXISTENTES.
# Não imprime conexão; restaura variáveis do processo mesmo se testes falharem.
$funilOldPg = $env:POSTGIS_TEST_CONNECTION
$funilOldRedis = $env:REDIS_TEST_CONNECTION
try {
    $env:POSTGIS_TEST_CONNECTION = "Host=127.0.0.1;Port=$funilPgPort;Database=noponto4b;Username=noponto4b;Password=$funilPassword"
    $env:REDIS_TEST_CONNECTION = "127.0.0.1:$funilRedisPort,defaultDatabase=0"
    dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~ViagemDuravelPostgresTests.Funil_CommitRealSeparaInicioPassagensEFim|FullyQualifiedName~ViagemDuravelPostgresTests.Funil_FalhaAntesCommitNaoConfirmaPassagens' --logger 'console;verbosity=normal' --verbosity minimal
} finally {
    $env:POSTGIS_TEST_CONNECTION = $funilOldPg
    $env:REDIS_TEST_CONNECTION = $funilOldRedis
}
```

O filtro seleciona exatamente um Fact e duas variantes de Theory. Guardar nomes/resultados individuais e falhas de fixture separadamente; não aceitar total zero como sucesso. Parar a bateria se ultrapassar aproximadamente cinco minutos. Não rodar suíte global. Limpeza posterior deve atingir somente recursos desse `funilRun`, após reconferir labels; nenhuma limpeza foi executada pelo agente.

## Arquivos e pendências

**Alterados nesta 4B:** este relatório novo e `NoPonto/HISTORICO.md` por acréscimo. Nenhum fonte/teste/schema/configuração alterado. Artefatos locais de compilação foram atualizados pelo teste, sem mudança rastreada de projeto/dependência.

Trabalho preservado da 4A: agregador/contexto, classe parcial nova, polling/retry/serviço, wrappers de regra/mudança/integridade, dois hooks diagnósticos do repositório, testes novos/estendidos e relatório 11. Preservadas ainda alterações anteriores em `FormaPagamento.cs`, documentação de schedules, histórico e auditorias 09/10. Não houve stage/commit/push/reset/restart.

Para retirar o bloqueio: (1) provar serviços descartáveis e versões e executar os três casos; (2) resolver separadamente a composição/modelo pendente sem correção artificial; (3) verificar entrega integral de logs; (4) revisar evidências de seleção/commit/Outbox sem confundir materialização. Somente então reclassificar prontidão de release diagnóstica. Nenhum desses gates autoriza alterar regras, TTL, flags ou implantar.

## Complemento — Etapa 4B.1 (2026-10-11)

A avaliação 4B acima permanece como histórico. Nesta etapa, o usuário autorizou comentar temporariamente os dois campos futuros de FormaPagamento e ajustar exclusivamente a emissão dos contadores novos. AGENTS.md, histórico, relatórios 11/12 e estado Git foram consultados; alterações anteriores foram preservadas.

### Modelo e validação local

Somente as declarações `Cor` e `Icone` foram comentadas, com tipos, nomes e conteúdo preservados. A busca de referências ativas em fontes C# não encontrou consumidores dessas propriedades. Nenhuma configuração EF, snapshot, migration ou contrato foi alterado. Uma execução inicial, antes de efetivar o comentário (um patch com contexto incompatível não tinha sido aplicado), reproduziu a falha já conhecida nos dois campos. Após o comentário e recompilação, `Modelo_SnapshotSincronizado` passou; nenhuma outra divergência apareceu.

Bateria relevante com recompilação e `--no-restore`: **203/203 aprovados**, zero falhos/ignorados, tempo de testes 4,52 segundos. Inclui snapshot, regras de viagem/mudança/integridade, performance, serviço observado, retry e funil; concorrência e separação selecionado/confirmado continuam aprovadas. Após acrescentar o ensaio de overhead, a suíte de logs foi recompilada e executada: **4/4 aprovados**, 2,52 segundos (três cenários de recomposição e um ensaio). São 204 casos distintos aprovados nesta etapa, sem executar fixtures externas. Avisos preexistentes de compilação permanecem (CS8981, CS7022 e xUnit2031).

Comando da bateria:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~GpsFunilLogsTests|FullyQualifiedName~GpsFunilOperacionalTests|FullyQualifiedName~GpsPerformanceMetricsTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalIntegracaoLogicaTests' --verbosity minimal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter FullyQualifiedName~GpsFunilLogsTests --logger 'console;verbosity=detailed' --verbosity minimal
```

### Emissão, correlação e integridade

O template e os argumentos anteriores de `Performance GPS` foram restaurados, removendo somente o sufixo novo `funil_operacional`. Após essa linha, no mesmo encerramento de ciclo, são emitidos seis registros `Funil GPS`: resultados, decisões, movimento, passagens, predecessor nova e predecessor retry. A separação do predecessor por origem mantém margem confortável de tamanho, incluindo BUS/BRT/outro em cada registro.

Cada registro novo contém `cycle_id` (GUID criado uma vez por agregador/ciclo), `inicio_utc` em UTC, `parte=1..6`, `partes=6`, `tema` e `contadores`. O identificador é apenas correlação de log, sem labels métricas ou IDs por GPS/veículo. A linha legada mantém `inicio`, correspondente ao mesmo início do ciclo, para associação temporal; seu template não recebeu novos campos. Preservados os 164 nomes e valores, inclusive zeros, novas/retry, selecionados/confirmados e eventos fora do escopo legado. Cada contador é emitido exatamente uma vez entre os segmentos.

O teste captura o ILogger real, recompõe os campos, valida unicidade, resultados e todos os enum contadores, preservação de zeros, UTC, identidade estável na repetição e distinta entre ciclos. Remover qualquer uma das seis partes ou duplicar uma parte torna o conjunto incompleto no exemplo de consumidor. O consumidor deve agrupar por `cycle_id`, exigir as partes 1..6 exatamente uma vez e não interpretar ausência como zero. Numeração detecta entrega parcial; se todos os registros novos desaparecerem, só a comparação com a linha legada/início permite perceber a ausência. Nenhum consumidor de produção foi alterado.

| Valor sintético em todos os campos | Maior mensagem UTF-8 | Soma das seis mensagens |
| --- | ---: | ---: |
| 0 | 1.228 bytes | 6.193 bytes |
| 10.000 | 1.336 bytes | 6.849 bytes |
| long.MaxValue | 1.714 bytes | 9.145 bytes |

Medidas incluem o template renderizado, timestamp e cycle_id, antes de envelopes/prefixos/JSON do coletor. Todas as novas mensagens ficaram abaixo de 4 KiB, inclusive no cenário extremo. A soma maior que 8 KiB não constitui uma única mensagem. A linha legada continua aproximadamente 4,8–5,5 KB nos sintéticos da 4B: a preferência por menos de 4 KiB foi aplicada à instrumentação nova, preservando compatibilidade. Capacidade e entrega efetiva do pipeline continuam desconhecidas; o teste local não certifica coletores reais.

### Concorrência, semântica e overhead

As alterações desta etapa não tocam regras, retornos, matching, cursor, eventos, ETA/ML, SQL, transações ou os hooks após CommitAsync. Arrays, Interlocked/Volatile e escopos AsyncLocal de novas/retry permanecem como revisados na 4B. A emissão ocorre depois do trabalho do ciclo, lê os mesmos agregados e não registra contadores. A classificação em temas não é novo denominador operacional. Não há logs individuais nem custo novo por GPS.

Ensaio local .NET 9/Debug: 100 aquecimentos, 1.000 emissões completas, aproximadamente **91,63 ms e 66.176.040 bytes alocados** (0,092 ms/66,2 KB por ciclo). Inclui renderização, captura e dicionários do logger de teste; não é benchmark do logger produtivo nem comparação controlada com a 4B. Repetir a emissão não altera decisões ou passagens confirmadas. Os arrays mantêm 1.312 bytes de payload; há um GUID/string adicional por ciclo e buffers/argumentos transitórios para seis mensagens.

Em ciclos de 15 segundos, são seis registros adicionais por ciclo (24/minuto), aproximadamente 0,41–0,46 KB/s de texto nos cenários zero/cinco dígitos, além da linha legada e dos envelopes. A repetição de metadados acrescenta 856 bytes por ciclo ao antigo sufixo; o ganho é limitar o tamanho por mensagem. O custo observado é pequeno e fixo por ciclo, mas não estabelece percentual de overhead da API nem latência do sink.

### Pendências e classificação atual

**BLOQUEADA PARA HOMOLOGAÇÃO**, agora sem o bloqueio local Cor/Icone e com o risco de tamanho das mensagens novas mitigado. Permanecem sem execução os três casos reais: `Funil_CommitRealSeparaInicioPassagensEFim`, `Funil_FalhaAntesCommitNaoConfirmaPassagens(false)` e `Funil_FalhaAntesCommitNaoConfirmaPassagens(true)`. Não há nova comprovação de containers descartáveis, portanto nenhum PostgreSQL/Redis foi acessado e nenhuma migration foi executada. Usar o procedimento manual de isolamento da 4B antes desses casos. Validação de entrega pelo coletor e materialização posterior ao Outbox também permanecem pendentes.

Arquivos efetivamente alterados nesta 4B.1: `NoPonto/3-Domain/Entities/FormaPagamento.cs`, `NoPonto/2-Application/Services/GPS/GpsPollingService.cs`, `NoPonto/2-Application/Services/GPS/GpsCicloPerformance.Operacional.cs`, novo `NoPonto/5-Testes/GpsFunilLogsTests.cs`, este relatório e `NoPonto/HISTORICO.md`. Os demais diffs anteriores foram preservados. `git diff --check` sem erros; avisos anteriores LF/CRLF. Sem API completa, serviços externos, produção, deploy, commit/push, configuração ou infraestrutura alterada.
