# NoPonto — stacks e tecnologias

**Finalidade:** relacionar versões, responsabilidades, adequação técnica e limitações das tecnologias efetivamente identificadas.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Verificação:** manifests e código; versões de runtime produtivo quando observadas.

## Backend e dados

| Tecnologia/versão | Responsabilidade e uso | Adequação observável | Limites/trade-offs |
|---|---|---|---|
| .NET/ASP.NET Core `net9.0` | host web, DI, controllers, SignalR e hosted services | reúne HTTP, realtime e workers com tipagem e cancelamento | processo único compartilha falhas/recursos |
| C# | domínio, pipelines e contratos internos | records, nullable e async ajudam contratos/concorrência | versão de linguagem não está fixada explicitamente |
| EF Core 9.0.10 | mapping, migrations e consultas estruturais | integridade/modelo evolutivo | hot paths usam SQL direto para maior controle |
| Npgsql 9.0.2 | driver PostgreSQL e datasource concorrente | acesso nativo, parâmetros e batches | SQL especializado aumenta acoplamento ao schema |
| NetTopologySuite 2.5.0 | tipos/transformações geométricas .NET | compatível com geometria PostGIS | exige cuidado com SRID/ordem de coordenadas |
| PostgreSQL 16 | persistência durável e integridade | transações, FKs, JSONB e índices | crescimento de histórico/outbox exige operação |
| PostGIS 3.4 | LineString/Point, GIST e matching | executa localização/proximidade perto dos dados | consultas espaciais dependem de índices e qualidade estrutural |
| Redis 7 | snapshots, TTL, CAS, índices e streams | baixa latência e expiração natural | configuração atual é efêmera/noeviction; indisponibilidade afeta realtime |
| StackExchange.Redis 2.12.1 | comandos Redis e multiplexer | scripts/CAS/streams além do cache simples | cliente e chaves tornam runtime Redis-específico |
| SignalR (framework .NET 9) | push rodoviário por grupos de linha | reconexão/fallback e abstração de transporte | contrato não versionado; estado de grupo é process-local |
| System.Net.Http/JSON | providers e ML | clients tipados, timeout e DI | dependência de contratos externos |
| Swashbuckle 7.2.0/OpenAPI 1.6.22 | descoberta/documentação HTTP | superfície verificável | Swagger não substitui contrato versionado/testes |
| xUnit 2.9.3/Test SDK 18.10.0 | testes unitários/integração | extensa cobertura temática no repo | projeto web e testes não estão isolados; não executados nesta etapa |

## Frontend

| Tecnologia/versão | Responsabilidade e uso | Adequação observável | Limites/trade-offs |
|---|---|---|---|
| Expo 55.0.23 | toolchain e build mobile | fluxo EAS/React Native multiplataforma | versão publicada não verificada |
| React Native 0.83.6 / React 19.2 | UI e estado por hooks | componentes declarativos e integração nativa | telas centrais concentram bastante orquestração |
| TypeScript 5.9.2 | contratos e serviços | reduz divergência entre DTOs e UI | tipagem não valida payload em runtime por si só |
| Expo Router 55.0.14 | rotas por arquivos/tabs | navegação alinhada à estrutura `app/` | `favoritos.tsx` nomeia rota que hoje apresenta Rotinas |
| React Navigation 7.x | infraestrutura de navegação/foco | usado pelo Router e tela de linhas | sobreposição de abstrações aumenta dependências |
| React Native WebView 13.16.0 | host do mapa HTML/JS | permite MapLibre GL completo sem módulo nativo específico | ponte serializa dados e depende de ciclo da WebView |
| MapLibre GL JS 3.6.1 | mapa, fontes, camadas e marcadores | controle incremental de geometria/realtime | carregado de CDN; contexto web separado do RN |
| OpenStreetMap/estilos configurados | base cartográfica | dados/tiles interoperáveis | disponibilidade/termos da fonte devem ser documentados |
| SignalR Client 10.0.0 | atualização GPS | reconexão automática e invocação de hub | versão major difere do servidor framework 9; compatibilidade deve ser testada |
| AsyncStorage 2.2.0 | preferências/histórico local | persistência simples sem backend de usuário | sem sincronização e migração formal de schema |
| NativeWind 4.2.1/Paper 5.14.5/Lucide | estilo e componentes | produtividade visual | coexistência de abordagens exige consistência |
| React Query 5.90.12 | dependência instalada | poderia gerenciar cache/requests | nenhum uso identificado; não integra a arquitetura vigente |
| `react-native-maps` 1.27.2 | dependência instalada | alternativa cartográfica possível | nenhum import identificado; candidato à revisão, não parte do mapa atual |
| Zod 4.2.1 | dependência instalada | validação runtime possível | uso não confirmado nos fluxos centrais |

## ETA/ML em migração

O serviço histórico usa Python, FastAPI 0.115.12, Uvicorn 0.34.2, XGBoost 2.1.4, scikit-learn 1.6.1, pandas 2.2.3, SQLAlchemy 2.0.41, psycopg2 2.9.10, Joblib 1.4.2 e PyArrow 19.0.1. Ele expõe `/eta` e `/eta/batch`, mas está intencionalmente desligado e seu modelo não é a arquitetura ETA V2 final. O ETA V2 atual é interno ao backend C#, com baseline longitudinal, channel bounded e persistência PostgreSQL; ML supervisionado permanece futuro.

## Infraestrutura e entrega

| Tecnologia | Uso | Observação |
|---|---|---|
| Debian 12 | host de produção | servidor compartilhado |
| Docker Engine 29.4.3 | isolamento de processos | containers API/PostGIS/Redis |
| Docker Compose 5.1.3 | topologia/configuração | stack `noponto`; não é orquestrador distribuído |
| Docker Buildx/GHCR | build e registry API | workflow gera `latest`, timestamp-SHA e build-number |
| GitHub Actions | CI/CD backend e build APK | deploy somente por `workflow_dispatch`; testes não aparecem no workflow examinado |
| EAS Build | APK Android preview | build automatizado; artefato/distribuição não auditados |
| Tailscale no deploy | conectividade privada do runner ao servidor | detalhes de segurança ficam para Etapa 2.4 |

## Justificativas: evidência versus inferência

É evidência que PostGIS executa queries espaciais, Redis mantém snapshots/TTL, SignalR distribui por linha e MapLibre opera na WebView. É inferência técnica que essas escolhas reduzem latência ou aumentam portabilidade; não foi localizada uma comparação formal entre alternativas. Não há benchmark nesta etapa.

## Limitações, referências e pendências

Versões vêm de manifests, imagens e produção observada; bibliotecas instaladas não são automaticamente usadas. Fontes: `NoPonto.csproj`, `package.json`, `requirements.txt`, Dockerfiles e workflows. Pendente: SBOM/lock de runtime, política de atualização, compatibilidade SignalR 9/10 e inventário de dependências sem uso.
