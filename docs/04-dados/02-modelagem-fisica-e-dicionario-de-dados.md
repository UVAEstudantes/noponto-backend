# Modelagem física e dicionário de dados

**Análise:** 2026-10-06 · **Commit backend:** `53567bd` · **Estado:** modelo EF/migrations verificado; volumes são fotografia da auditoria anterior, não nova leitura.

## Convenções físicas

Chaves são majoritariamente `uuid`; entidades derivadas de `BaseEntity` incluem `Id`, `Ativo`, `CreatedAt` e `UpdatedAt` conforme migrations. Timestamps operacionais usam `timestamp with time zone`; relatórios/metadados usam `jsonb`; geometria usa SRID 4326. Tipos/nulabilidade abaixo provêm de entidade e mapping, mas o snapshot/migration é a autoridade física quando há divergência.

## Dicionário central

| Tabela / entidade | Finalidade e chaves | Relações, índices e constraints | Escrita / leitura | Estado e volume datado |
|---|---|---|---|---|
| `Modais` / `Modal` | categoria; PK `Id`, `Nome` | 1:N linhas | importação/APIs | atual; 2 em 06/10 |
| `Linhas` / `Linha` | código/nome/tipo/consórcio; PK | FK modal; sentidos/identidades | importação, estrutura, GPS, rail | atual; 502 |
| `Sentidos` / `Sentido` | direção lógica; PK | FK linha | importação/matching | atual; 951 |
| `Paradas` / `Parada` | local Point 4326, tipo/plataforma/pai | GiST localização; chave canônica unique parcial; check tipo; FKs modal/pai restrict | importações/APIs/matching | atual; 7.798 |
| `PadroesOperacionais` / `PadraoOperacional` | identidade estável; PK | unique `(SentidoId,Chave)`; FK sentido; FK composta da versão atual | importação/runtime | atual; 980 |
| `PadroesVersoes` / `PadraoVersao` | LineString, número, hash, comprimento, topologia, validação | unique padrão+número/hash; GiST; checks número/comprimento/confiança/topologia | importação; matching lê | atual; 980 |
| `OcorrenciasParadasPadroes` / `OcorrenciaParadaPadrao` | visita ordenada; PK | unique versão+ordem; índice versão+fração/parada; checks fração/distâncias | importação; viagem/ETA leem | atual; 54.873 |
| `FontesEstruturais` | proveniência | código unique | importadores | atual; 2 |
| `ImportacoesEstruturais` | execução/hash/algoritmo/relatório | check status; unique parcial fonte+hash+algoritmo concluído | importadores/auditoria | atual; 2 |
| `*IdentidadesExternas` | ponte fonte↔interno | unique `(FonteEstruturalId,Tipo,ExternalId)`; checks confiança/origem | importação/normalização | atual; contagens por alvo na auditoria |
| `PadroesVersoesImportacoes` | N:N versão/importação/papel | PK tripla; check papel | importação/auditoria | atual |
| `OverridesOcorrenciasPadroes` | ajuste manual rastreável | check ação/ordem; índice padrão+ativo | importação/reconciliação | atual |

## Viagem, histórico e avaliação

| Tabela | Campos importantes e unidade | Integridade/índices |
|---|---|---|
| `ViagensOperacionais` | veículo, viagem, estado serializado/contexto, versão e timestamps duráveis (criada em `ViagemDuravelPostgres`) | unicidade/versão por veículo e concorrência por advisory lock; índice `UpdatedAt` foi posteriormente removido |
| `OutboxViagens` (entidade `ViagemOperacionalOutbox`) | evento pendente ligado à transação durável | processamento/retenção por status e tempo; nome físico confirmado no migration |
| `EventosViagem` | PK textual `EventId`, `Tipo`, `Payload jsonb`, `TimestampEvento` | índice timestamp; insert `ON CONFLICT DO NOTHING`, conflito de payload detectado |
| `HistoricoPassagens` | veículo/linha/parada, viagem/sentido/versão/ocorrência/volta, fração, timestamps, km/h | unique parcial `(ViagemId,Ocorrencia,Volta)`; índices por linha/veículo/parada/versão/sentido e tempo; FKs restrict |
| `TelemetriasVeiculoMl` | observação SHA-256, origem/provedor, coordenadas, velocidades, timestamps e contexto nullable | `ObservacaoId` unique; índices veículo+GPS, linha+GPS e viagem+GPS parcial; FKs estruturais restrict |
| `PrevisoesEtaV2` | contexto exato, metros/km/h/segundos, preditor, status e ground truth | cinco FKs restrict; checks status/contexto/predição/ground truth; índices de fechamento, sampling, tempo, linha e modelo |
| `PositionCorrectionShadowOrigins` | amostras/resultados JSONB e versões da política | PK textual, FKs versão/ocorrência; índices timestamp/observação/política |

Colunas legadas `TempoDesdeParadaAnteriorSegundos` e `DistanciaTrechoMetros` existem na entidade histórica, mas o materializador V2 atual não as preenche; não devem ser tomadas como target V2 disponível.

## Schedule ferroviário

| Tabela | PK/FKs e campos | Integridade |
|---|---|---|
| `RailScheduleVersions` | UUID; linha, provider, datas de referência, schema/builder/hash, ativa, JSONB | unique linha+hash; unique parcial uma ativa por linha; check schema |
| `RailSchedulePatterns` | versão, linha, sentido, external id, assinatura, mapeamento opcional V2 | unique versão+external id; check status; cascade da versão, demais restrict |
| `RailScheduledRuns` | versão/pattern/linha/sentido, calendário, horários/day offsets, terminais | unique versão+run externo; checks calendário/offset; cascade da versão |
| `RailScheduledStops` | run, parada, sequência, hora/day offset/minuto absoluto | unique run+sequência e run+parada; check temporal; cascade do run |

`ExpectedRun`, bindings, trackers e snapshots ferroviários não aparecem no dicionário físico porque são objetos de runtime.

## Tabelas auxiliares e legado

`Veiculos`, `PosicoesVeiculo`, `Pois`, `PoiParadas` e `Tarifas` continuam no `DbContext`, em graus diferentes de uso. Migrations antigas também contêm `Itinerarios`/`ParadasItinerario`; a migration `RemoverLegadoEstruturalBusBrt` e exclusões de compilação afastam essas estruturas da modelagem vigente. Não se recomenda drop nesta etapa.

## Índices, crescimento e limitações

B-tree compostos refletem filtros por identidade/tempo; unique/partial sustentam idempotência; GiST atende busca espacial. Não há particionamento identificado para telemetria/histórico/previsões, logo crescimento e manutenção de índices são riscos futuros. A auditoria anterior evitou contar tabelas volumosas; ~19,3 milhões de telemetrias era estimativa histórica, não contagem atual.

## Testes, referências e pendências

Referências: `DbContext.cs`, model snapshot, migrations `EstruturaTransporteV21` a `RailScheduleFoundation`, entidades/repositories. Testes PostgreSQL/estrutura/ETA/rail/telemetria dão cobertura parcial e não foram executados. Um dicionário gerado automaticamente do catálogo seria útil, mas não foi produzido nem consultado nesta tarefa.

