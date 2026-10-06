# NoPonto — integrações externas

**Finalidade:** catalogar fontes e serviços externos, distinguindo aquisição primária, shadow, importação, legado e planejamento.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Verificação:** clients/configuração/call sites; valores secretos não consultados.

## Catálogo

| Fonte/estado | Domínio/finalidade | Protocolo/formato e estratégia | Client/normalização | Erros, autenticação e limites |
|---|---|---|---|---|
| Zirix direto — PRIMÁRIA | GPS ônibus | HTTP GET JSON por janela; coletor com overlap/catch-up | `GpsSppoClient` → `ZirixGpsSource` → contrato comum | timeout próprio, HTTP/JSON; autenticação não identificada no código examinado; dependência externa |
| endpoint BRT current — PRIMÁRIA | GPS BRT | HTTP GET JSON periódico | `GpsBrtClient` → `BrtCurrentGpsSource`; conversão flexível de direção | timeout/HTTP; contrato de campos próprio |
| Data.Rio agregada — SHADOW | comparação GPS ônibus/BRT e hints | HTTP GET JSON paginado/cursor | `GpsDatarioClient` → `DatarioGpsSource`; converte tipos flexíveis | não é fallback automático; status de providers e payload inválido tratados |
| GTFS Data.Rio — IMPORTAÇÃO | linhas, sentidos, shapes e paradas | arquivos GTFS/streams, parse e plano | `GtfsFeedParser`, projeção, persister/publication | execução controlada; não confundir código registrado com job automático |
| ArcGIS SPPO — IMPORTAÇÃO/RECONCILIAÇÃO | geometria/paradas e cross-check estrutural | HTTP GeoJSON/ArcGIS paginado | `ArcGisSppoSnapshotClient`, serviços V2/reconciliador | parte de configuração histórica precisa limpeza cuidadosa |
| ArcGIS BRT — IMPORTAÇÃO | estações BRT | HTTP ArcGIS | `ImportacaoParadasBrtService` | estado de execução atual não verificado |
| recursos/ArcGIS ferroviários — ESTRUTURA | ramais/estações | HTTP/recursos embarcados | clients nomeados/estrutura Trem V2 | coexistem configurações antigas; uso chave a chave pendente |
| provider Trens RJ — PRIMÁRIA DE EVIDÊNCIA | próximas viagens e planejamento ferroviário | GET `/trips/next`, POST `/trips/plan`, JSON | `TrensRjRealtimeClient/PlanClient`, normalizer | budget por minuto, concorrência, single-flight, 429/timeout/payload inválido; sem credencial explícita observada |
| coletor externo de schedules — IMPORTAÇÃO | horários ferroviários | HTTP → JSONL/CSV → import command | scripts `extrair-trem`, builder, `RailScheduleImport` | ferramenta fora de Git; schedules não equivalem a realtime |
| serviço ETA FastAPI — LEGADO | inferência rodoviária antiga | HTTP JSON `/eta/batch`, lote até contrato do serviço | `GpsEtaClient` | timeout 3 s, cooldown 30 s, fail-open; container intencionalmente off |
| MapLibre CDN — ATUAL | engine/cartografia web | HTTPS JS/CSS | `buildMapHtml` | CDN é ponto externo; versão 3.6.1 fixada no HTML |
| tiles/estilos OSM — ATUAL conforme estilo | base do mapa | HTTPS tile/style | MapLibre/WebView | termos, rate limits e disponibilidade precisam inventário específico |
| Lucide CDN — ATUAL | ícones no mapa web | HTTPS script | WebView HTML | dependência externa adicional |
| Overpass/OSM POI — LEGADO_INATIVO | pontos de interesse | HTTP Overpass | clients/workers excluídos | frontend ainda chama POIs; funcionalidade não operacional completa |
| Metrô — PLANEJADO | estrutura/operação | não definido | nenhum provider confirmado | não inventar contrato, GPS ou schedule |

## Fontes primárias versus shadow

Em produção, ônibus usa Zirix direto como primário e BRT usa o endpoint BRT current. Data.Rio agregada é shadow para ambos: coleta/avaliação paralela pode comparar dados, mas seu cadastro como shadow não prova promoção automática quando o primário falha. Qualquer fallback exige política e call path explícitos.

## Normalização

Cada provider possui campos e semântica próprios. Adapters convertem identificador de veículo, linha, coordenadas, timestamp, velocidade, direção e hints externos para contratos comuns. Validação posterior impede que a aceitação do JSON seja confundida com observação operacional válida. Identidades externas na estrutura permitem reconciliar IDs sem incorporá-los como chaves de domínio.

## Integração ferroviária

Quatro conjuntos devem permanecer separados:

1. **estrutura:** linhas, sentidos, estações e geometria;
2. **schedule:** patterns, runs e stops programados importados;
3. **evidência:** respostas momentâneas do provider consultadas pelo scanner;
4. **estimativa interna:** binding/tracker/preditor transformam schedule+evidência em snapshot.

O marcador ferroviário é resultado do quarto conjunto. Não é coordenada GPS fornecida pelo operador. `PositionSource`, `PositionQuality`, freshness e evidência recente comunicam essa distinção.

## Autenticação

Os clients examinados configuram base URL, timeout e User-Agent; não foi encontrado mecanismo de autenticação explícito nos fluxos centrais. Isso não prova que todos os endpoints são públicos: gateways ou configuração externa podem existir. Nenhuma credencial deve ser documentada.

## POIs e metrô

Overpass/POI pertence à arquitetura anterior e está excluído do build, enquanto o frontend mantém calls que retornam vazio em erro. Metrô é somente visão de expansão. Ambos devem aparecer como parcial/planejado, não como integração vigente.

## Limitações e documentos relacionados

Frequências exatas são configuráveis e podem mudar; valores produtivos sensíveis não são reproduzidos. Licenças/termos e SLAs dos providers não foram auditados. Ver [componentes](03-componentes-backend-frontend.md) e [contratos](05-contratos-e-comunicacao.md).

## Evidências e pendências

Clients GPS/rail, `Program.cs`, services GTFS/ArcGIS, scripts ferroviários, HTML do mapa e opções. Pendente: registrar termos/licenças, versionar o extrator e confirmar autenticação/limites oficiais por provider.
