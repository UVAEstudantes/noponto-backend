# GTFS-RT — homologação integrada local e gates do canário

Branch `feature/gps-gtfsrt-migration`, base main `70d7bcb`. Nenhuma fonte ativada,
nenhum deploy autorizado. Este roteiro não é uma ordem de execução em produção.

## Crosswalk e tipo comercial

O importador real preserva `Linhas.TipoRota` e acrescenta identidades
`DATARIO_GTFS/ROUTE_AGENCY` (`route_id:agency_id`) e
`DATARIO_GTFS/ROUTE_ORIGIN_V1` (`route_id:BUS|BRT|UNKNOWN`) às tabelas existentes.
Não há migration nova nem classificação de todas as linhas regulares como BRT.
O lookup lê em lote READ ONLY/REPEATABLE READ com timeout SQL de 2s.

Política V1: agência 20001 + tipo 702 pertence à fonte BRT; exceções comprovadas
por route_id/código/agência/tipo completos: 20000281130/28/20001/700,
20000671130/67/20001/700, 20000681130/68/20001/700,
20000EXEC1110/ESP01/20001/200. Permanecem regular/regular/regular/frescao.
O0634AAA0A/634/20001/700 pertence a BUS; **MOBI-Rio não equivale a BRT**.
Agências 22002/22003/22004/22005 com tipo 700 ou 200 pertencem a BUS.
Agência desconhecida, exceção divergente ou metadados conflitantes falham fechados.
Não usar substring/prefixo numérico para adivinhar correspondência.

Metadados de outra agência/origem já registrados não são apagados no rerun:
conflito impede promoção e exige reconciliação explícita. Identidades preexistentes
ROUTE_ID sem novos metadados não são homologadas automaticamente por TipoRota.
direction_id presente precisa pertencer ao mesmo route_id e LinhaId via
GTFS_DIRECTION; ausência permanece ausência, não cria ida/volta fictícia.

GTFS oficial validado: SHA-256
`a99f925460e7628b6eeecb9952430542c06b3e2800afa8ba7f9765fd2e6f26f1`.
494 rotas estáticas: 34 BRT / 460 BUS. Não confundir essas contagens com veículos
observados, execução com matching aceito ou identidades do banco operacional.
As sete rotas BUS ausentes seguem desconhecidas:
O0222AAA0A, O0391AAA0A, O0439AAA0A, O0463AAA0A, O0498AAV0A,
O0797AAA0A, O0LECD157AAA0A. Log agregado `unresolved_route` contabiliza descarte;
`invalid_direction`, `invalid_speed`, `parser_rejected` são razões separadas.

## Velocidade e procedência

Nos testes locais BUS e BRT usam explicitamente `KilometresPerHour`, com base
na documentação Central/pareamentos/física registrados em
HOMOLOGACAO_BLOQUEADORES_GTFSRT.md. Unknown e flags false continuam defaults.
Ausente, não finita, negativa, sentinela ou >90 km/h não vira zero confiável;
bearing inválido permanece null. Mudança de produtor exige nova homologação.

Provider passa prospectivamente a GTFSRT_BUS/GTFSRT_BRT, participando do hash
ObservacaoId. VehicleDescriptor.id mantém identidade do veículo (prefixo BRT-).
Não reescrever histórico, fabricar catch-up, trocar labels/features ou certificar
viagens. Mudança de fonte/release/unidade/cadência/crosswalk exige novo perfil
prospectivo com cutoff, commit, imagem, hash estático e política V1. Não reutilizar
silenciosamente official-post-fix para treinar dados de origem nova.

## Reprodução em ambiente exclusivo

Nunca inicializar a API completa contra banco existente: startup executa Migrate.
O teste usa apenas serviços reais selecionados e migrações existentes em fixture
nova; importação e publicação estrutural existem somente nessa fixture.
HTTP GTFS e ETA são controlados; não são inferência real nem observação física.
SignalR é verificado por IHubContext capturado, sem cliente websocket real.
Ingress ML capturado verifica contrato/factory; 21 eventos são persistidos pelo repository real, com replay idempotente. Não promove dataset de treino.

```powershell
Set-Location D:\repositorio_github\NoPonto\noponto-backend-gps-gtfsrt
$suffix = Get-Date -Format 'yyyyMMdd-HHmmss'
$pg = "noponto-gtfsrt-canary-pg-$suffix"
$rd = "noponto-gtfsrt-canary-redis-$suffix"
# Use somente portas loopback livres e nomes novos; nunca reutilizar banco operacional.
docker run -d --name $pg --cpus 1 --memory 2g -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=gtfsrt_fixture_canary -p 127.0.0.1:58543:5432 postgis/postgis:16-3.4
if ($LASTEXITCODE -ne 0) { throw "Falha ao criar PostgreSQL exclusivo" }
docker run -d --name $rd --cpus 0.5 --memory 256m -p 127.0.0.1:58544:6379 redis:7-alpine
if ($LASTEXITCODE -ne 0) { throw "Falha ao criar Redis exclusivo" }
docker exec $pg pg_isready -U postgres
if ($LASTEXITCODE -ne 0) { throw 'Aguardar readiness antes de executar testes' }
$env:POSTGIS_TEST_CONNECTION='Host=127.0.0.1;Port=58543;Database=gtfsrt_fixture_canary;Username=postgres'
$env:REDIS_TEST_CONNECTION='127.0.0.1:58544'
$env:GTFSRT_OFFICIAL_ZIP='D:\repositorio_github\NoPonto\gps-homologacao-20261008\official-schedule.zip'
$env:GTFSRT_INTEGRATED_FIXTURE='1'
$env:GTFSRT_LOAD_3000='1'
dotnet test NoPonto/NoPonto.csproj --no-restore --filter FullyQualifiedName~GtfsRealtimeIntegratedTests --verbosity minimal
# Apenas os recursos criados acima; não remover containers/volumes preexistentes.
docker stop $pg $rd
```

Trust existe somente no recurso descartável de loopback; não é configuração
operacional. O teste verifica hash do ZIP, host, nome do banco e porta Redis
exclusiva antes de preparar schema. Carga 3.000 usa posições sintéticas na rota
oficial 006; não prova distribuição geográfica/física real de 3.000 veículos.

## Canário futuro: BRT primeiro, BUS depois

Pré-condições: aprovação separada de release/perfil e implantação; comprovação
operacional READ ONLY das identidades de todas as rotas selecionadas, incluindo
agência/origem/direção e versões publicadas; unidade explicitamente aprovada;
capacidade/saúde local e critério de rollback revisados. Esta tarefa não consulta
nem preenche o banco operacional. Não usar CrosswalkValidated para substituir
essa prova. Reexecutar importador completo não é um backfill mínimo autorizado.

Configuração proposta para BRT (não aplicada):

```text
GpsSources__BusPrimarySource=ZIRIX_DIRECT
GpsSources__BrtPrimarySource=GTFSRT_BRT
GtfsRealtimeGps__BusEnabled=false
GtfsRealtimeGps__BrtEnabled=true
GtfsRealtimeGps__BrtSpeedUnit=KilometresPerHour
GtfsRealtimeGps__CrosswalkValidated=true
GtfsRealtimeGps__IntervalSeconds=30
```

Após canário BRT aceito, em uma mudança separada e novo registro de procedência:
BusPrimarySource=GTFSRT_BUS, BusEnabled=true, BusSpeedUnit=KilometresPerHour.
Não coexistir dois escritores operacionais para o mesmo modal. Seleção é por
modal inteiro: não existe allowlist de veículos ou percentual canário implementado.
Para canário de frota parcial seria necessária mudança aprovada separadamente;
não improvisar múltiplas APIs sobre o mesmo Redis/banco.

Saúde: aquisição sem falhas recorrentes, GPS monotônico e fresco, contagens raw /
promoted / unresolved por modal, sem ambiguidades novas, sem crescimento de retry /
outbox, continuidade de ViagemId, ETA e broadcasts presentes, CPU/RSS/pool sem
saturação. Repetição do mesmo snapshot não pode gerar nova viagem/telemetria.
Ausência/falha conserva estado, mas não garante cobertura histórica entre GPS.
Gatilho de interrupção: zero promoção com feed não vazio, perda estrutural inesperada,
duplicação/rollback de identidade/timestamp, falhas duráveis ou orçamento excedido.

Rollback proposto: BUS=ZIRIX_DIRECT, BRT=BRT_CURRENT, flags GTFS false. Não apagar
Redis, viagens ou telemetrias. Não há fallback automático. Antes do canário verificar
que a fonte legada de rollback está disponível; se não estiver, não prometer recuperação
automática e definir procedimento SemSinal/versão anterior separadamente.

## Resultados observados em 08/10/2026

**BLOQUEADO para canário; integração local aprovada nos cenários executados.**

- Regressões finais: 370/370; regressões conectadas: 163/163, sem falhas/ignorados.
- Replay completo: 1/1; importador/publicador reais, 494 rotas, 961 cadeias
  route → LinhaId → sentido/padrão/versionamento, importação 132,68s.
- Parser → fonte → collector → PostGIS → Redis → viagens → ETA HTTP controlado
  → SignalR capturado → persistência ML: repetição, falha503/ausência/recuperação,
  timestamp antigo, troca de fonte e restart exercitados. 13 chamadas ETA,
  39 broadcasts, 21 eventos ML. Não é smoke de transporte websocket/modelo real.
- Carga BUS: 3.000/3.000 aceitos, 45,034s, CPU processo9,109s,
  228.725.448 bytes alocados, RSS900.612.096, pico1.759.764.480 bytes.
- Carga BRT: 370/370 aceitos, 6,923s, CPU processo1,922s,
  30.219.568 bytes alocados, RSS1.633.161.216, pico1.683.099.648 bytes.
  Replay focado em10 padrões, executado simultaneamente às regressões conectadas;
  não substitui a prova estrutural completa nem constitui benchmark isolado.
- Build final: zero erros/avisos. `git diff --check` executado no fechamento.

Memória inclui fixture/importação e runtime de testes; CPU acima exclui banco/Redis.
PostgreSQL limitado a1CPU/2GiB e Redis0,5CPU/256MiB; processo .NET sem limite.
Não foi medido orçamento agregado de uma stack completa limitada a4GB, nem soak.
Carga usa veículos sintéticos na mesma rota/ponto, matching em lote habilitado e
EnriquecerTodasLinhas=true. 45s excede polling20s/feed30s nesse cenário;
configuração produtiva e distribuição real não foram executadas.

Cobertura do snapshot público anterior, não nova aquisição nesta tarefa:
BUS2.939/2.962 veículos (99,22%),350/357 rotas (98,04%); BRT370/370 veículos,
32/32 rotas. São correspondências estáticas, não prova do banco operacional.
Sete rotas BUS/23 veículos continuam excluídos. Separação SPPO/Sistema RIO
não é comprovável pelo feed sem tag confiável. 634 é contraexemplo **estático**,
não foi observado nesse snapshot. Nenhuma rota válida do ZIP foi rejeitada
na fixture; isso não garante matching em todos os pontos/geometrias.

No restart da rota006, ViagemId e GPS foram preservados, mas matching rejeitado:
bearing instantâneo53,130° versus suavizado332,738°, diferença80,392°,
acima do limite existente80°. ML mantém identidade operacional nula quando não
há matching; não foi relaxada tolerância nem inventada associação. As quatro
rotas BRT exercitadas preservaram matching/viagem após restart.

Correções encontradas durante teste: SQL varchar[] versus text[] (cast explícito),
DTO da fonte BRT herdava tag onibus (agora brt sem alterar tipo comercial da linha).
Fixture de observação concorrente passou a sincronizar listas para não perder
resultados. Publicação estrutural real foi acrescentada ao roteiro da fixture;
expectativa de matching no restart foi corrigida para respeitar o guard existente.

### Gates restantes e reprodução BRT

1. Comprovar no banco operacional, por operador autorizado, agência/origem/direção
   e versões publicadas. Metadados novos precisam de reconciliação aditiva aprovada;
   esta tarefa não verificou existência nem autorizou importação operacional.
2. Aprovar perfil prospectivo/release/cutoff e registrar unidade/feed/hash estático.
3. Homologar orçamento na configuração candidata; BUS com enriquecimento integral
   de3.000 veículos não passou orçamento20/30s. BRT teve latência local favorável,
   mas falta comprovação do orçamento agregado equivalente ao homeserver.
4. Confirmar disponibilidade das fontes de rollback antes de qualquer ativação.

Para repetir a carga BRT nos mesmos recursos **exclusivos** antes de pará-los:

```powershell
Remove-Item Env:GTFSRT_LOAD_3000 -ErrorAction SilentlyContinue
$env:GTFSRT_LOAD_BRT_370='1'
$env:GTFSRT_FOCUSED_REPLAY='1' #10 padrões; remover para estrutura completa
 dotnet test NoPonto/NoPonto.csproj --no-restore --filter FullyQualifiedName~GtfsRealtimeIntegratedTests --verbosity minimal
```

TRXs e medição do bearing estão fora do Git, em
`D:\repositorio_github\NoPonto\gps-canary-local-20261008`.
Recursos desta execução são parados, preservados, sem remoção de bancos/volumes.
Defaults permanecem legados/flagsOFF/Unknown; nenhuma migration nova, ativação,
produção, MLrepo, ETA público, sampling, contrato de treino ou label alterados.


## Desempenho — decomposição e correção pontual (08/10/2026)

A aquisição/parser/crosswalk não foram refeitos. A carga anterior não isolava
parser: criava3.000 viagens novas, enriquecia todas as linhas, forçava checkpoint0,
usava matching batch e concentrava posições na geometria complexa006.
Importação permanece fora do cronômetro, mas a memória do importador/fixture
permanece no processo de teste. A rodada atual chegou a~1,6GiB de RSS e Windows
com~0,5GiB livres; não comparar45,03s antigos como um A/B controlado.

Configuração verificável no código/appsettings/compose: polling20s,
EnriquecerTodasLinhas=false, checkpoint60s, paralelismo20,
GPS_MATCHING_BATCH_ENABLED=false. O compose aceita overrides de ambiente;
não havia .env correspondente na worktree. Valores efetivos do homeserver não
foram consultados e não podem ser afirmados. Benchmark explicitamente usa batch,
mudança operacional habilitada e enriquecimento integral nos casos de carga.
Telemetry ingress é observador em memória, sem sampling, não worker SQL real
em carga; persistência de telemetria já homologada na etapa anterior não é refeita.

Reprodução concentrada atual:70,041s total, matching18,253s, commit de viagens/CAS
44,676s, leitura inicial/contextos6,471s, ETA87ms. Parsing12,543ms medido à parte,
crosswalk10,505ms. Matching30comandos,3.000criações duráveis,33.049comandos PG
observados. A fase de viagens é dominante; WAL/COMMIT aguardando escrita foi
observado em amostra local. O númeroPG inclui BEGIN/COMMIT/DISCARD e observadores,
não somente SELECT. Tempos cumulativos de operações concorrentes não somam
ao tempo de parede; o paralelismo permanece limitado20.

Correção funcional única: ViagemOperacionalRepository reutiliza a prova estrutural
obtida para **a mesma observação** na avaliação de mudança, evitando repetir
EstruturaAsync na continuidade. Mantém prova de transição, caminho separado de
projeção operacional, candidato/histerese, CAS/fencing existentes, outbox e commits.
Não cria cache entre ciclos/veículos nem confia apenas nos hints do GTFS.
A leitura inicial passou a constar no contador de leiturasPG (antes subcontada).
Regressão RED esperava2leituras e encontrou3; GREEN mantém ViagemId e
checkpoint quente, com exatamente prova estrutural+transição. Flag de mudança
desligada preserva o caminho anterior. Não resolve custo inevitável de milhares
de criações/transações por meio de remoção de durabilidade ou novos batches.

Fixtures: observadores de telemetria agora sincronizam publicação concorrente;
a rodada inicial perdia entradas somente no List do teste. Corrigido antes do
comparativo final; isso não era perda no ingress produtivo. Instrumentação registra
parsing separado, aquisição/collector, lookup, todas as etapas existentes,
comandos via pg_stat_statements e publicação de telemetria (soma concorrente;
fábrica permanece incluída no commit, não é contabilizada separadamente).

Benchmark BUS distribuído:3.000 veículos/7 rotas oficiais/6 pontos por rota.
BRT:370 veículos/4 rotas/6 pontos. Dois GPS sucessivos com posição preservada
e um snapshot repetido; não é replay cinemático completo de uma frota real.
GTFS estático pinado, importação seletiva de padrões; não repetir importação
completa961padrões. PostgreSQL16/PostGIS3.4:1CPU/2GiB; Redis7:0,5CPU/256MiB,
ambos exclusivos loopback; processo.NET sem teto. Não habilitar batch ou
enriquecimento total em produção somente porque esses testes passaram.

Cadências independentes: clienteGTFS limita aquisição nova a30s; polling usa20s.
Após ciclo normal acima do intervalo, delay planejado é0; o processamento não
se sobrepõe. BUS tem vaga única/ACK: produtor aguarda liberação, não acumula
fila ilimitada; isso não comprova recuperação de GPS intermediários. Repetição
sem timestamp novo não avança viagem nem produz nova telemetria no cenário.
Ao exceder TTL40s, cache físico pode expirar durante o ciclo: no replay concentrado
517posições reapareceram como novas no filtro, mas nenhuma viagem avançou.
Nenhum TTL ou filtro foi relaxado para esconder o orçamento excedido.

Reprodução, usando **somente novos recursos exclusivos** nas portas livres58543/58544:

```powershell
# PostgreSQL exclusivo: acrescentar ao docker run do roteiro acima:
# Usar POSTGRES_DB=gtfsrt_fixture_perf e comando:
# postgres -c shared_preload_libraries=pg_stat_statements
# Aguardar inicialização completa (incluindo reinício do entrypoint) e validar TCP.
# Nunca executar a instrução abaixo em banco operacional.
docker exec $pg psql -U postgres -d gtfsrt_fixture_perf -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION pg_stat_statements;'
$env:POSTGIS_TEST_CONNECTION='Host=127.0.0.1;Port=58543;Database=gtfsrt_fixture_perf;Username=postgres'
$env:REDIS_TEST_CONNECTION='127.0.0.1:58544'
$env:GTFSRT_INTEGRATED_FIXTURE='1'
$env:GTFSRT_FOCUSED_REPLAY='1'
$env:GTFSRT_PERFORMANCE='1'
$env:GTFSRT_PERFORMANCE_COMPARISON='1' # somente BUS/BRT distribuídos
# GTFSRT_OFFICIAL_ZIP conforme o roteiro anterior; mesmo SHA obrigatório.
dotnet test NoPonto/NoPonto.csproj --no-restore --filter FullyQualifiedName~GtfsRealtimeIntegratedTests --verbosity normal
# Sem COMPARISON: inclui carga concentrada e EnriquecerTodasLinhas=false sem assinantes.
```

Evidências/TRXs externas: D:\repositorio_github\NoPonto\gps-perf-local-20261008.
A tentativa inicial foi descartada por desconexão durante inicialização do recurso
descartável; benchmarks válidos somente após readiness. Nenhum recurso operacional
foi lido, alterado ou parado; os recursos exclusivos são parados no fechamento.

### Antes/depois efetivamente observados

Mesmas rotas/pontos/quantidades, parâmetros de matching/checkpoint/paralelismo e
limites dos recursos. Rodadas sequenciais únicas; pressão de memória/IO do host
variou. Observador ML foi sincronizado antes da rodada final (3000/370 eventos
completos); a perda no observador anterior invalida comparação de contagens ML.
Não atribuir toda diferença de tempo ao patch nem prometer p90 ou throughput
produtivo. Garantia direta da regressão: remove exatamente a segunda consulta
estrutural da mesma observação, mantendo a consulta de transição.

| Cenário | Antes s | Depois s | PG antes/depois | CPU .NET depois s |
|---|---:|---:|---:|---:|
| BUS3000 criação fria | 36.593 | 57.010 | 33067/33048 | 13.062 |
| BUS3000 atualização | 42.488 | 31.046 | 16061/13423 | 7.344 |
| BUS3000 snapshot repetido | 0.585 | 0.131 | 7/7 | 0.203 |
| BRT370 criação fria | 4.777 | 6.371 | 4085/4085 | 1.609 |
| BRT370 atualização | 5.484 | 3.944 | 1865/1495 | 1.359 |
| BRT370 snapshot repetido | 0.010 | 0.022 | 1/1 | 0.000 |

Depois, BUS atualização: matching8,891s, commit20,871s, leitura/contextos1,106s,
ETA12ms; parsing10,257ms à parte, normalização1ms; publicação ML cumulativa
4,579ms. 2503/3000 matching elegíveis, mesma contagem antes/depois; nenhum
filtro foi alterado para fazer as497 outras posições passarem. 2175 escritas
duráveis foram evitadas pela política existente;328 decisões ainda exigiram
persistência semântica. São dados técnicos sintéticos, não certificação.

BRT atualização: matching1,222s, commit2,624s, leitura/contextos66ms, ETA1ms,
parsing0,680ms à parte, publicaçãoML cumulativa0,404ms;370/370 matching,
370/370 atualizações quentes sem escrita durável. Cold continua370/370 com
370 transações. Sem conflitos/falhas de infraestrutura nos cenários finais.

Controle adicional **antes** com EnriquecerTodasLinhas=false e zero assinantes:
BUS3000 aquisição/cache2,302s frio/1,816s atualização/0,055s repetição,
nenhuma query de matching ou escrita durável de viagem. Esse controle mantém
GPS no mapa, mas não oferece cobertura operacional/ML de viagens dessas linhas;
não é uma otimização que possa substituir silenciosamente a coleta integral.

RSS final1,52–1,55GiB, pico1,61GiB no processo de testes pós-importação.
BUS atualização alocou215.150.184bytes; BRT27.326.544bytes. Esses números não
são previsão da memória de API estável nem aprovação do orçamento agregado4GB.
BRT não mostrou gargalo impeditivo em370veículos; BUS integral permanece
impeditivo para o orçamento20/30s (31,046s atualização,57,010s criação).
Criação fria não foi acelerada pelo patch:30batches de matching +3000transações
continuam preservadas, e o tempo frio aumentou nesta rodada.

Testes finais:89/89 regressões relacionadas,18/18 cadência/circular e benchmark
pós-correção1/1 (2m50s), sem falhas/ignorados. Benchmark diagnóstico1/1(5m07s);
RED esperado1falha(3leituras vs2). Build final e git diff --check no fechamento;
avisos de recompilação preexistentes registrados nos TRXs. Não foram repetidos
parser/crosswalk/clientes, homologação estrutural completa ou centenas de testes.

Próxima ação objetiva: manter BUS legado, considerar somente BRT no roteiro de
canário anterior **após** gates de identidade/procedência/rollback já registrados.
Não aprovar BUS integral nem ativar flags para esconder essa limitação; melhoria
adicional de bulk/estrutura exigiria escopo e prova próprios, não implementados.
