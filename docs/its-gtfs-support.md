# ITS principal e GTFS-RT de suporte

Atualização24/10: há um modo opt-in [híbrido por veículo](its-hybrid.md), `GpsSources:BusSupportMode=Hybrid`, mantendo o mesmo coletor. O restante deste documento registra a decisão anterior de fallback completo, ainda disponível e padrão como `FullFallback`. Não confundir as evidências históricas com validação do novo modo.

Atualização: [ensaio conectado dos dois feeds reais e prontidão da janela no homeserver](its-homeserver/validation.md) concluído. A pendência histórica de homologação do par abaixo foi atendida; ativação produtiva continua dependente de autorização específica e do procedimento controlado.

## Decisão e auditoria dirigida

Esta revisão substitui a recomendação de fallback ITS → ZIRIX. A configuração operacional nova usa somente `ITS_CURRENT` e `GTFSRT_BUS`. O caminho legado permanece disponível para consumidores explícitos e para reproduzir testes históricos; não é dependência deste par. Nenhuma configuração produtiva foi alterada.

| Fronteira existente | Constatação relevante |
| --- | --- |
| DI/seleção | `Program.cs` já registra um cliente GTFS-RT por modal. BUS usa `https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions`. Não há coletor HTTP adicional a criar. Shadows não fazem fallback. |
| Aquisição | `GtfsRealtimeGpsClient`: aquisição serial, FULL_DATASET, resposta/entidades limitadas (padrões 2 MB/5.000), timeout 15 s, intervalo mínimo configurado 30 s, cache HTTP/304 e Retry-After. Falha descarta corpo/validadores operacionais; cache não renova o GPS. |
| Timestamp | GPS vem de `VehiclePosition.timestamp`, nunca do header, relógio de recepção ou sondagem. O adaptador reaplica idade máxima inclusive em cache; conserva recepção original. Valores padrão GTFS: idade 300 s, tolerância futura 120 s; ITS continua estrita contra futuro e limitada a 180 s. Esses filtros não foram alterados. |
| Identidade | `vehicle.id` (não entity.id, trip.id ou label), trim + uppercase em BUS; preserva zeros e pontuação. ITS usa a mesma transformação. Não se inventou alias por placa/prefixo nem tradução heurística. O ID da entidade permanece metadado separado. |
| Duplicatas | Parser escolhe GPS físico mais recente; conflitos de conteúdo no mesmo timestamp são rejeitados conservadoramente. O wrapper mantém redução por identidade; polling e CAS continuam rejeitando timestamps antigos/iguais entre fontes. |
| Linha/modal | Crosswalk existente `DATARIO_GTFS/ROUTE_ID`, com leitura seletiva em lote, transação read-only e validação de agência/modal/direção; conversão de velocidade exige unidade explícita. Falha do crosswalk não fornece posições. Linhas não resolvidas e leituras inválidas não são promovidas. |
| Pipeline | `ISnapshotGpsSource` → coletor SPPO → store/ACK → polling → matching/CAS/Redis/viagens/SignalR. Metadados GTFS seguem na observação; não há novo escritor, histórico, matching ou publicador. BRT não muda. |

Escolha: **fallback completo**, reutilizando `ItsFallbackGpsSource`. Complementação por veículo permitiria aproveitar GTFS exclusivo/mais recente mesmo com ITS saudável, mas exigiria aquisição simultânea das duas fontes, regra adicional de completude, conflitos de linha/identidade e arbitragem por veículo. Não há necessidade de introduzir esses mecanismos para retirar ZIRIX do suporte. A opção conservadora mantém uma fonte efetiva por ciclo e o estado espacial existente.

Consequência explícita: enquanto ITS estiver saudável, não se consulta GTFS-RT para procurar veículos ausentes ou timestamps melhores. Não se afirma que ITS vence todos os pares. Durante a transição, a leitura mais recente **já aceita** prevalece no polling/CAS; se o GPS da fonte recuperada for antigo, ele não substitui o estado. Essa proteção segue a retenção atual do cache/watermark, não constitui um arquivo permanente de timestamps após expiração ou reinício sem cache.

## Estado e recuperação

- ITS com sucesso ou vazio operacional válido: usa ITS, sem adquirir suporte. Vazio não limpa o estado nem significa prova de ausência de ônibus na rede.
- ITS com falha, parcial, timeout ou transporte indisponível: descarta qualquer amostra parcial e adquire GTFS-RT no mesmo ciclo pelo contrato de snapshot.
- Em fallback: sonda ITS a cada 60 s por padrão, contados após a conclusão da sondagem. Duas sondagens saudáveis consecutivas recuperam ITS; falha/parcial zera a sequência. A primeira sondagem saudável não é misturada ao GTFS-RT. Vazio válido também é saudável.
- GTFS-RT com sucesso: entrega somente suas observações operacionais normalizadas, sem watermark histórico. Vazio válido: sem handoff. Feed quebrado/HTTP/crosswalk indisponível: falha sem handoff; o estado anterior segue seus TTLs naturais.
- Cancelamento do chamador se propaga. Suporte tem deadline externo configurável (`BusFallbackTimeoutSeconds`, padrão 10 s), além do orçamento interno GTFS. I/O não cooperativo permanece observado e impede outra requisição concorrente enquanto pendente; resultado tardio não chega ao store.
- Coletores continuam respeitando backpressure/ACK; snapshot com mesmas identidades e GPS não produz outra geração. O wakeup ITS já corrigido funciona também com suporte GTFS-RT. Não há alteração de polling, TTLs, filtros, matching, viagens, ETA ou SignalR.

A primeira detecção de falha pode consumir o orçamento ITS antes da aquisição de suporte. Não foi feita otimização de latência nesta revisão. O `IntervalSeconds` do cliente GTFS continua limitando requisições mesmo se coletor/polling forem mais frequentes.

## Configuração para desenvolvimento

Somente após verificar conexões locais e o crosswalk importado/publicado no banco de desenvolvimento:

```powershell
$env:GpsSources__BusPrimarySource = 'ITS_CURRENT'
$env:GpsSources__BusFallbackSource = 'GTFSRT_BUS'
$env:GtfsRealtimeGps__BusEnabled = 'true'
$env:GtfsRealtimeGps__BusSpeedUnit = 'KilometresPerHour'
$env:GtfsRealtimeGps__CrosswalkValidated = 'true'
# Opcional: orçamento externo do suporte, independente do timeout interno GTFS.
# $env:GpsSources__BusFallbackTimeoutSeconds = '15'
```

As flags de unidade/crosswalk são afirmações operacionais que precisam corresponder ao feed e ao banco usados. O resolver valida esses opt-ins ao construir o par, antes de iniciar a aquisição ITS. Unidade desconhecida, modal desabilitado ou crosswalk não confirmado interrompem a seleção. Não se deve habilitar a flag para contornar uma validação pendente. Opções são capturadas na inicialização: requer novo processo.

Rollback da nova arquitetura para GTFS-RT direto, conservando os opt-ins GTFS acima:

```powershell
$env:GpsSources__BusPrimarySource = 'GTFSRT_BUS'
Remove-Item Env:GpsSources__BusFallbackSource -ErrorAction SilentlyContinue
```

Não depende do endpoint ZIRIX. Os padrões legados de configuração não foram alterados nem aplicados ao servidor; a migração é explícita. Consumidores legados identificados: adaptador `ZirixGpsSource`, seleção primária antiga, testes da janela/fallback e ferramenta histórica `tools/gps/ItsE2eLab.cs`. Essa ferramenta ainda reproduz o ensaio ITS/ZIRIX de 23/10; seu resultado não aprova o novo par ITS/GTFS-RT. Não foi removida nem executada nesta revisão.

## Validação e limites

Testes novos dirigem o adaptador GTFS real com HTTP protobuf/crosswalk controlados: falha/parcial/timeout/transporte ITS, entrega/ACK e supressão de snapshot repetido, vazio legítimo, feed truncado, HTTP 503/429/Retry-After, indisponibilidade do crosswalk, recuperação do suporte, duas sondagens ITS com quebra de sequência, duplicatas mais antigas, veículos exclusivos GTFS e independência da ordem do feed. Deadline com relógio virtual verifica I/O não cooperativo, bloqueio de sobreposição e descarte tardio; cancelamento não aciona suporte.

Testes de continuidade dirigem resolver/wrapper/coletor/polling/enriquecedor reais, com repositórios e cache expirável em memória: CONECTA ou ZIRIX **dentro da ITS** → GTFS-RT → ITS, GPS antigo/empatado com coordenadas diferentes, retenção do predecessor após expiração ativa, recuperação com ITS ainda mais antiga, novo GPS após recuperação e rejeição operacional de salto espacial. Os provedores ITS não são chamadas à API ZIRIX_DIRECT. Nenhum evento aceito duplicado por `(veículo, GPS)`; os testes existentes do publicador preservam a semântica de snapshots SignalR, que pode repetir GPS já aceito em ciclos distintos.

Resultados: **464 direcionados e 1.015 offline oficiais aprovados**, zero falhas/ignorados. O verificador TRX confirmou todas as classes da allowlist existente; não houve exclusão de testes. São 24 casos adicionais sobre os 991 offline anteriores: 18 de suporte, quatro de continuidade e dois de wakeup. RED dirigido anterior ao patch funcional: 12 falhas/3 aprovados, todos os erros funcionais pela restrição antiga a ZIRIX; GREEN inicial: 15/15. A primeira tentativa de compilação do teste novo exigiu corrigir acessibilidade de um parâmetro enum interno, antes do RED funcional.

Comandos executados com .NET SDK 9.0.306, xUnit/VSTest e Release:

```powershell
$directed = 'FullyQualifiedName~NoPonto.Tests.ItsCurrentGpsSourceTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GpsPhysicalPredecessorTests.|FullyQualifiedName~NoPonto.Tests.GpsSourceAbstractionTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.|FullyQualifiedName~NoPonto.Tests.GpsSignalRPublisherTests.'
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter $directed --logger 'trx;LogFileName=its-gtfs-directed.trx' --results-directory artifacts/its-gtfs-support
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { throw 'Seleção offline falhou' }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=its-gtfs-offline.trx' --results-directory artifacts/its-gtfs-support
if ($LASTEXITCODE -ne 0) { throw 'Testes offline falharam' }
python .ci/offline_tests.py verify artifacts/its-gtfs-support/its-gtfs-offline.trx
```

Artefatos locais em `artifacts/its-gtfs-support/`. Compilação do projeto pelos testes aprovada com cinco avisos preexistentes; build independente da solução não executado. Nada depende de PostgreSQL/Redis, HTTP externo, Docker ou produção nesta revisão. Esses testes determinísticos não são apresentados como homologação conectada do novo par.

Antes da ativação no homeserver, falta homologar **este par** com feed real e infraestrutura local: crosswalk da base efetiva, contrato/unidade/identidade do GTFS usado, falhas e recuperação com Redis/PostGIS/SignalR reais, frescor e latência durante a indisponibilidade ITS. A homologação anterior comprova o pipeline ITS e a infraestrutura, mas sua transição era ZIRIX. Permanece a pendência de capacidade na carga real de assinaturas identificada naquele ensaio; desempenho não foi alterado nesta revisão. Não foi feito deploy, push ou merge.
