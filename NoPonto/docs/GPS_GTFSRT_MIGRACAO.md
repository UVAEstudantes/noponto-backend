# GPS GTFS-Realtime — implementação local, ativação bloqueada

Base: main `70d7bcb8041e167df063237b4fdcafd4971b01d6`. Branch exclusiva
`feature/gps-gtfsrt-migration`. Nenhum cutover autorizado.

## Aquisição e domínio

`GtfsRealtimeGpsClient` compartilha parser entre os dois endpoints municipais.
Schema oficial pinado em `Services/GPS/Proto/SOURCE.md`; Google.Protobuf 3.36.2,
Grpc.Tools 2.84.0 somente no build. Classes geradas em obj, não versionadas.
O binding NuGet antigo GtfsRealtimeBindings 0.0.4 foi evitado: schema oficial
com runtime mantido preserva Has/Clear de campos proto2.

Somente Header 2.0/FULL_DATASET; DIFFERENTIAL, versão desconhecida, deletes e
IDs de envelopes duplicados falham explicitamente. VehicleDescriptor.id é
veículo; entity.id é envelope; trip_id é hint externo, nunca ViagemId interno.
VP.timestamp é GPS Unix-segundos; header.timestamp é apenas geração.
Direction 0/1 é identidade externa, nunca interpretação universal ida/volta.
Campos ausentes não são preenchidos pelo header nem pelo relógio.

Defaults: resposta descomprimida 2 MB, 5.000 entidades (contadas antes da
alocação do grafo Protobuf), orçamento de aquisição 15s, cadência mínima 30s,
GPS até 300s e tolerância futura existente de 120s. Uma aquisição serial por
modal; sem retry/fallback. Cache-Control/max-age/Age, ETag/304, Last-Modified e
Retry-After respeitados. No-store não retém corpo e não rompe rate limit.
Os 30s são candidato conservador apoiado no cache observado, não SLA aprovado.

Velocidade bruta permanece nullable fora do domínio. Unidade Unknown impede
seleção operacional. Somente unidade explicitamente homologada permite km/h
ou conversão m/s × 3,6; resultado finito em [0,90]. Ausência, NaN, negativos,
sentinela 999 e valores acima do teto rejeitam a observação operacional:
o DTO legado usa double, portanto não pode representar ausência sem inventar 0.
Bearing inválido vira null; 360 vira 0, preservando fallback geométrico existente.

## Crosswalk

Reaproveita as identidades persistidas pelo importador estático:
`FontesEstruturais.Codigo=DATARIO_GTFS`, `LinhasIdentidadesExternas.Tipo=ROUTE_ID`.
Uma consulta parametrizada ANY por lote de routes distintos, READ ONLY /
REPEATABLE READ, statement_timeout 2s. Cache de um único snapshot normalizado,
até 30s; não há query adicional por veículo. Ambiguidade, ID vazio, código
ausente/0 e modal incompatível rejeitam promoção. Não cria estrutura nem usa
substring. Código comercial copiado literalmente, inclusive 006/SV/SN/SR/SP.
TipoRota=brt separa BRT da parte ônibus dessa fonte estrutural.

Route/direction/trip continuam em ObservacaoEstrutural para o resolver existente;
shape ausente continua ausente. Matching/versão/ocorrência/volta NoPonto são
autoridade operacional, não o TripDescriptor. Não foi encontrado GTFS ZIP,
routes.txt ou trips.txt local compatível com os feeds atuais. Cobertura real
do crosswalk permanece NÃO MEDIDA; fixture não comprova estrutura instalada.

## Integração e configuração

GpsSources continua selecionando ZIRIX_DIRECT para BUS e BRT_CURRENT para BRT.
Novas fontes registradas GTFSRT_BUS/GTFSRT_BRT exigem simultaneamente seleção
explícita, flag do modal, unidade conhecida e CrosswalkValidated. Defaults:

```json
{
  "GtfsRealtimeGps": {
    "BusEnabled": false,
    "BrtEnabled": false,
    "BusSpeedUnit": "Unknown",
    "BrtSpeedUnit": "Unknown",
    "CrosswalkValidated": false
  }
}
```

Flags são uma declaração administrativa futura, não certificação automática.
Não habilitar antes da confirmação do produtor/crosswalk e aprovação separada.
BUS reaproveita handoff imutável de uma vaga/ACK, com JanelaInicio/JanelaFim e
watermark NULOS para snapshot. Não executa catch-up/janelas fictícias nem
substitui geração pendente. BRT reutiliza gate/status/cache da mesma fonte.
Polling não precisou alteração: dedupe, predecessor estritamente crescente,
CAS/TTL, viagem, publicação e telemetria continuam sendo os gates existentes.
Ausência de veículo/vazio/falha não remove Redis nem finaliza viagem.

Logs estruturados registram última resposta, falha/intervalo e cobertura
intermediária desconhecida. São evidência operacional em memória/log, NÃO
certificação durável de completude; restart não prova continuidade. Snapshot
não recupera posições intermediárias. Provedor GTFSRT_BUS/BRT identifica o
feed de transporte, não inventa fabricante AVL. Timestamp de recepção não é
renovado ao reutilizar cache. ObservacaoId usa essa nova proveniência.

Sampling, ingress, worker, SQL3A, labels, contratos ETA/ML e Shadow permanecem
intactos. Novo perfil prospectivo de coleta precisa de release/imagem/cutoff,
unidade homologada por modal, feed estático/hash/crosswalk, cadência e política
de lacunas aprovados. Não misturar ao perfil oficial antigo nem certificar 3G.

## Validação em 08/10/2026

52 casos novos de parser/HTTP/adapter/collector; 498 regressões aprovadas e
52/52 repetidos após guard adicional de Cache-Control no-cache,
zero falhas/ignorados, ~11s nos testes. Incluem Redis exclusivo: 89 testes CAS,
TTL, formatos e monotonicidade, e testes ETA público/telemetria/viagens legados.
PostGIS exclusivo: um teste conectado aprovado, lotes 1/10/50/200, crosswalk
seletivo e ambiguidade. View-fixture só retorna linhas se transaction_read_only
for on. Nenhuma migration ou API completa iniciada. Houve RED inicial em body
excedente, corrigido para falha sem publicação, com regressão GREEN.
Build final aprovado; git diff --check aprovado. Cinco warnings preexistentes
na recompilação; build incremental final sem warnings.

Parser sintético, dez repetições no PC, p50/p90 em ms para 1/10/50/200/2800:
0,005/0,008; 0,054/0,056; 0,120/0,154; 0,839/0,912; 13,983/16,786.
2.800 entidades: ~2,67 MB alocados por parse; RSS do processo de testes ~179 MB,
não memória exclusiva do adapter. SQL: 264,923ms primeira consulta fria,
5,659/4,781/8,321ms para 10/50/200 rotas. Dados sintéticos mínimos, PC com outras
suítes concorrentes; não benchmark de produção/PostGIS completo/homeserver.

Três amostras públicas pontuais, espaçadas >=31s, sem salvar IDs/coordenadas:
2.646 pares físicos ônibus e 369 BRT. Deslocamento médio km/h / média de speed
bruto: mediana 0,920 / 1,013. Pares exatos veículo+timestamp: zirix 931, conecta
123, sonda 471; razões medianas 1. Legados: ônibus 862 e BRT 82, razão mediana 1.
Central ônibus parcial em uma rodada, vazia na primeira. Isso favorece km/h,
mas média física versus velocidade instantânea não comprova unidade/cobertura
de cada produtor. Norma diz m/s: confirmação contratual continua pendente.

## Reprodução e gates futuros

Offline (não iniciar API):

```powershell
Set-Location D:\repositorio_github\NoPonto\noponto-backend-gps-gtfsrt
dotnet build NoPonto/NoPonto.csproj
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter 'FullyQualifiedName~GtfsRealtimeGpsTests|FullyQualifiedName~GpsSppoCollectorTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~GpsPerformanceMetricsTests' --verbosity quiet
git diff --check
```

Teste conectado requer recurso NOVO, loopback, banco gtfsrt_fixture_* em porta
não 5432 e GTFSRT_FIXTURE_CONNECTION explicitamente definida, sem senha em argv.
O teste cria suas três tabelas e view; rerun precisa banco novo. Não utiliza
POSTGIS_TEST_CONNECTION nem bancos operacionais. REDIS_TEST_CONNECTION deve
apontar exclusivamente ao Redis descartável criado para esta homologação.

Antes de ativar: confirmar unidade por modal/produtor; homologar GTFS estático
compatível e cobertura/códigos de TODOS routes promovidos; reconciliar veículo
e trips externos (não exigir igualdade trip_id entre fornecedores); validar
canário em pico/vale, Redis/viagens/SignalR e ETA ponta a ponta com geometrias
completas em fixture, medir pool/ciclo no orçamento 4 GB e aprovar novo perfil.
Custos de matching completo/continuidade operacional PostGIS ainda não medidos.
Não habilitar uma flag para contornar esses pendentes.

Rollback futuro: voltar seleção por modal para ZIRIX_DIRECT/BRT_CURRENT e flags
false, sem apagar chaves/controle/telemetria ou recuperar lacunas fictícias.
Depende de disponibilidade dos endpoints antigos. Após descontinuação, rollback
exige versão anterior homologada da fonte nova ou SemSinal conservador.

Referências: https://gtfs.org/documentation/realtime/reference/ e
https://gtfs.org/documentation/realtime/proto/. Relatório detalhado externo:
D:\repositorio_github\NoPonto\IMPLEMENTACAO_MIGRACAO_GPS_GTFSRT.md.
