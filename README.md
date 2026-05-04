# NoPonto Backend

Backend da plataforma **NoPonto**, focado em mobilidade urbana com:

- catálogo de linhas, sentidos, itinerários e paradas;
- relacionamento geoespacial de paradas ↔ itinerários;
- ingestão e associação de POIs (OpenStreetMap/Overpass);
- stream de GPS em tempo real com enriquecimento via PostGIS + Redis + SignalR.

- 🎨 [Repositório do Frontend](https://github.com/UVAEstudantes/noponto-frontend)

---

## Sumário

1. [Visão geral da arquitetura](#visão-geral-da-arquitetura)
2. [Stack técnica](#stack-técnica)
3. [Estrutura do projeto](#estrutura-do-projeto)
4. [Modelagem de domínio (resumo)](#modelagem-de-domínio-resumo)
5. [APIs externas consumidas](#apis-externas-consumidas)
6. [Fluxos principais](#fluxos-principais)
7. [Fluxo de GPS em tempo real (detalhado)](#fluxo-de-gps-em-tempo-real-detalhado)
8. [Fluxo de POIs (detalhado)](#fluxo-de-pois-detalhado)
9. [Fluxo de relacionamento Paradas x Itinerários](#fluxo-de-relacionamento-paradas-x-itinerários)
10. [Endpoints expostos](#endpoints-expostos)
11. [Configuração e variáveis de ambiente](#configuração-e-variáveis-de-ambiente)
12. [Como rodar localmente](#como-rodar-localmente)
13. [Observabilidade e logs](#observabilidade-e-logs)
14. [Próximos passos do projeto](#próximos-passos-do-projeto)

---

## Visão geral da arquitetura

A aplicação segue um desenho em camadas:

- **1-API**: controllers HTTP, middleware global de exceção e hub SignalR.
- **2-Application**: regras de negócio, serviços de importação, jobs e serviços de background.
- **3-Domain**: entidades centrais do domínio de transporte.
- **4-Data**: DbContext e repositórios (EF Core + SQL/PostGIS).

A inicialização (`Program.cs`) registra:

- DI para repositórios/serviços;
- `DbContext` PostgreSQL + NetTopologySuite;
- cache distribuído em Redis;
- serviços hospedados (`BackgroundService`) para GPS, importações e fila de POIs;
- SignalR para push de posição em tempo real;
- Swagger com documentação XML.

---

## Stack técnica

- **.NET 9 (ASP.NET Core Web API)**
- **Entity Framework Core 9**
- **PostgreSQL 16 + PostGIS**
- **Redis 7**
- **SignalR**
- **NetTopologySuite**
- **Npgsql + NpgsqlDataSource**
- **Swashbuckle/Swagger**

> O `docker-compose` do repositório sobe PostgreSQL (com PostGIS) e Redis.

---

## Estrutura do projeto

```text
/workspace/noponto-backend
├── README.md
├── NoPonto.sln
└── NoPonto/
    ├── 1-API/
    │   ├── Controllers/
    │   ├── Hubs/
    │   └── Middlewares/
    ├── 2-Application/
    │   ├── DTOs/
    │   ├── GPS/
    │   ├── Interfaces/
    │   ├── Services/
    │   │   └── BackgroundServices/
    │   └── ...
    ├── 3-Domain/Entities/
    ├── 4-Data/
    │   ├── Context/
    │   ├── Interfaces/
    │   └── Repositories/
    ├── Migrations/
    ├── Program.cs
    └── docker-compose.yml
```

---

## Modelagem de domínio (resumo)

Principais entidades:

- **Modal** → ex.: Ônibus
- **Linha** (`Codigo`, `Nome`, `ModalId`)
- **Sentido** (`LinhaId`, `Nome`)
- **Itinerario** (`SentidoId`, `Geometria`, `DistanciaMetros`)
- **Parada** (`Codigo`, `Nome`, `Localizacao`)
- **ParadaItinerario** (join com `Ordem`, `PosicaoLinha`, `DistanciaMetros`)
- **Poi** (`Nome`, `Categoria`, `Prioridade`, `Localizacao`)
- **PoiParada** (join com `DistanciaMetros`)

Pontos importantes de modelagem geoespacial:

- `Itinerario.Geometria` é `geometry(LineString,4326)`;
- `Parada.Localizacao` e `Poi.Localizacao` são `geometry(Point,4326)`;
- índices **GIST** para operações espaciais eficientes.

---

## APIs externas consumidas

### 1) ArcGIS (metadados e geometria de itinerários)

Usada por `ArcGisClientService` e importadores para:

- paginar metadados (`servico`, `destino`, `direcao`, `shape_id`, `extensao`);
- buscar geometria GeoJSON por `shape_id`.

### 2) ArcGIS de Paradas (GeoJSON)

`ImportacaoParadasService` busca paradas por paginação (`resultOffset/resultRecordCount`) e converte para entidades locais.

### 3) API pública de GPS SPPO (Mobilidade Rio)

`GpsSppoClient` consulta janela temporal com overlap retroativo para evitar perda de eventos com atraso de envio.

### 4) Overpass API (OpenStreetMap)

`OverpassClient` importa POIs por bbox/tile, com:

- taxonomia de categorias + prioridade;
- deduplicação por OSM id;
- retry exponencial em `429`/`504`;
- filtro de qualidade por nome/categoria.

---

## Fluxos principais

### A) Carga de base de transporte

1. Importação agendada de itinerários roda diariamente no horário configurado.
2. Metadados vêm do ArcGIS.
3. Para cada shape, a geometria é buscada e salva no banco.
4. Na sequência, ocorre importação paginada de paradas.

### B) Relacionamento Paradas ↔ Itinerários

1. Job usa PostGIS para buscar candidatos por proximidade da geometria da rota.
2. Algoritmo escolhe melhor parada por vértice (reduz falsos positivos ida/volta).
3. Relações são persistidas em `ParadasItinerario` com ordem e posição na linha.

### C) POIs

1. Fase 1: importa POIs OSM em tiles para o banco local.
2. Fase 2: matching local (sem HTTP) para ligar POI à parada mais próxima por itinerário.
3. Endpoints de consulta retornam POIs por parada, por itinerário e diagnósticos.

### D) GPS em tempo real

1. Polling em loop consulta API SPPO por janela de tempo.
2. Sistema deduplica por veículo e filtra pontos antigos.
3. Enriquecimento geoespacial roda principalmente para linhas com assinantes SignalR.
4. Estado é gravado no Redis (`ativo`/`recente` + índices por linha).
5. Broadcast envia `PosicaoAtualizada` para grupos SignalR por código da linha.

---

## Fluxo de GPS em tempo real (detalhado)

### Componentes envolvidos

- `GpsPollingService` (worker principal)
- `GpsSppoClient` (integração HTTP GPS)
- `GpsEnriquecimentoService` (bearing, velocidade média, rota/próxima parada)
- `IGpsItinerarioRepository` + `GpsItinerarioRepository` (SQL PostGIS)
- `GpsHub` (assinaturas por linha)
- Redis (`IDistributedCache`) para estado efêmero

### Estratégias aplicadas

- **Janela retroativa** no polling para tolerar atrasos de envio da API externa.
- **Deduplicação por veículo**: mantém a posição GPS mais recente.
- **Filtro de idade máxima** (`MaxIdadeGpsSegundos`) para evitar “veículos fantasmas”.
- **Dois TTLs no Redis**:
  - `veiculo:{ordem}:ativo`
  - `veiculo:{ordem}:recente`
- **Status de retorno**:
  - `Ativo`: posição atual no ciclo curto;
  - `SemSinal`: não apareceu no último ciclo, mas ainda no TTL longo.
- **Enriquecimento seletivo**: linhas sem assinantes podem pular query PostGIS para reduzir custo.
- **Paralelismo controlado** no enriquecimento com `SemaphoreSlim`.
- **Uso de `NpgsqlDataSource`** no repositório GPS para evitar disputa de conexão em consultas paralelas.

### O que o enriquecimento calcula

- `bearing` (azimute entre posição anterior e atual, com filtro de deslocamento mínimo);
- `velocidadeMedia` com janela móvel e descarte de outliers;
- `itinerarioId` mais provável para aquela posição;
- `posicaoNaRota` (0..1) com `ST_LineLocatePoint`;
- `proximaParada` e distância até ela.

### Endpoints de GPS

- `GET /veiculos/{ordem}`
- `GET /veiculos/linha/{codigoLinha}`
- `GET /veiculos/itinerario/{itinerarioId}/geometria`

### Protocolo SignalR

- Cliente → servidor:
  - `InscreverseLinha(codigoLinha)`
  - `CancelarLinha(codigoLinha)`
- Servidor → cliente:
  - `PosicaoAtualizada(posicoes[])`

---

## Fluxo de POIs (detalhado)

### Visão macro

A arquitetura de POIs foi desenhada para **evitar custo alto de rede**:

- primeiro importa POIs uma vez para base local (Fase 1);
- depois faz matching em lote local (Fase 2), sem chamar Overpass a cada itinerário.

### Fase 1 — Importação OSM em tiles

- Calcula bbox geral com base nas paradas locais.
- Divide em tiles (~3km x 3km).
- Faz requisição Overpass por tile.
- Executa upsert/dedupe por chave lógica (`nome + categoria`) na área.

### Fase 2 — Matching local POI → Parada

- Para cada itinerário, carrega suas paradas ordenadas.
- Busca POIs na bbox expandida pelo raio configurado.
- Para cada POI, escolhe parada mais próxima dentro do raio.
- Se já houver vínculo concorrente melhor, mantém o de menor distância.
- Persiste em lote na tabela `PoiParadas`.

### Fila assíncrona

`PopularPoisQueue` (canal bounded) + `PopularPoisWorker` processam jobs:

- `ImportacaoOsm`
- `Matching`

### Endpoints relevantes de POI

- Consultas:
  - `GET /pois`
  - `GET /pois/por-parada/{paradaId}`
  - `GET /pois/por-itinerario/{itinerarioId}`
  - `GET /pois/contagem-por-itinerario`
  - `GET /pois/por-ponto`
- Processamento:
  - `POST /pois/importar-osm`
  - `POST /pois/popular`
  - `POST /pois/popular/parada/{paradaId}`
  - `POST /pois/popular/itinerario/{itinerarioId}`
- Limpeza:
  - `DELETE /pois/por-parada/{paradaId}`
  - `DELETE /pois/por-itinerario/{itinerarioId}`
  - `DELETE /pois/popular`

---

## Fluxo de relacionamento Paradas x Itinerários

### Objetivo

Encontrar, para cada itinerário, as paradas que realmente pertencem à rota e em qual ordem aparecem.

### Técnica usada

Serviço `RelacionarParadasItinerariosService` combina SQL + PostGIS com estratégia em 2 passos:

1. Para cada parada candidata, encontra o vértice mais próximo do LineString.
2. Para cada vértice, mantém apenas a parada mais próxima.

Esse desenho reduz erro comum de “paradas frente a frente” (ida/volta) mapeadas no mesmo trecho.

Além disso, o score considera:

- distância ao vértice;
- distância à linha;
- componente perpendicular (lado da via);
- posição relativa na linha (`PosicaoLinha`) para ordenação final.

### Endpoints

- `POST /relacionamento/paradas-itinerarios`
- `POST /relacionamento/paradas-itinerarios/{itinerarioId}`
- `DELETE /relacionamento/paradas-itinerarios/{itinerarioId}`
- `DELETE /relacionamento/paradas-itinerarios`

---

## Endpoints expostos

> Prefixo base: sem versionamento explícito (`/linhas`, `/paradas`, etc.).

### Modais

- `GET /modais`

### Linhas

- `GET /linhas`
- `GET /linhas/por-parada/{paradaId}`
- `GET /linhas/{linhaId}/detalhes`

### Sentidos

- `GET /sentidos`

### Itinerários

- `GET /itinerarios/por-linha/{linhaId}`
- `GET /itinerarios/{itinerarioId}/mapa`

### Paradas

- `GET /paradas`
- `GET /paradas/por-itinerario/{itinerarioId}`
- `GET /paradas/proximas?lat=&lng=&raio=`
- `GET /paradas/{paradaId}/linhas`

### POIs

- `GET /pois`
- `GET /pois/por-parada/{paradaId}`
- `GET /pois/por-itinerario/{itinerarioId}`
- `GET /pois/contagem-por-itinerario`
- `GET /pois/por-ponto?latitude=&longitude=&raioMetros=`
- `POST /pois/importar-osm`
- `POST /pois/popular`
- `POST /pois/popular/parada/{paradaId}`
- `POST /pois/popular/itinerario/{itinerarioId}`
- `DELETE /pois/por-parada/{paradaId}`
- `DELETE /pois/por-itinerario/{itinerarioId}`
- `DELETE /pois/popular`

### Relacionamento

- `POST /relacionamento/paradas-itinerarios`
- `POST /relacionamento/paradas-itinerarios/{itinerarioId}`
- `DELETE /relacionamento/paradas-itinerarios/{itinerarioId}`
- `DELETE /relacionamento/paradas-itinerarios`

### Veículos/GPS

- `GET /veiculos/{ordem}`
- `GET /veiculos/linha/{codigoLinha}`
- `GET /veiculos/itinerario/{itinerarioId}/geometria`

---

## Configuração e variáveis de ambiente

A aplicação usa `DotNetEnv` (`Env.Load()`), então normalmente lê variáveis de ambiente (ou `.env`).

### Banco e cache

Obrigatórias para subir:

- `POSTGRES_PORT`
- `POSTGRES_DB`
- `POSTGRES_USER`
- `POSTGRES_PASSWORD`
- `REDIS_PORT`

### CORS

- `CORS__ORIGINS` (array em configuração; se não definido, aceita qualquer origem com credenciais)

### GPS

- `GPS__API__BASE_URL` (default: `https://dados.mobilidade.rio/gps/sppo`)
- `GPS__HTTP_TIMEOUT_SECONDS` (default: `15`)
- `GPS__HUB__ROUTE` (default: `/hub/gps`)
- seção `GpsPolling`:
  - `IntervaloSegundos`
  - `TtlAtivoSegundos`
  - `TtlRecenteSegundos`
  - `TtlLinhaSegundos`
  - `VelocidadeMaximaKmh`
  - `JanelaVelocidadeLeituras`
  - `JanelaRetroativaSegundos`
  - `DistanciaMaximaRotaMetros`
  - `GrauParalelismoEnriquecimento`
  - `MaxIdadeGpsSegundos`

### Importação ArcGIS

- `IMPORTACAO_HORA` / `IMPORTACAO_MINUTO` (ou `ARCGIS__ITINERARIOS__HORARIO_IMPORTACAO`)
- `IMPORT__BATCH_SIZE`
- `ARCGIS__ITINERARIOS__BASE_URL`
- `ARCGIS__ITINERARIOS__WHERE`
- `ARCGIS__ITINERARIOS__OUT_FIELDS`
- `ARCGIS__ITINERARIOS__PAGE_SIZE`
- `ARCGIS__PARADAS__BASE_URL`
- `ARCGIS__PARADAS__WHERE`
- `ARCGIS__PARADAS__OUT_FIELDS`
- `ARCGIS__PARADAS__PAGE_SIZE`

### POI

- `POI__DISTANCIA_MAXIMA_METROS` (default em código: `150`)

---

## Como rodar localmente

## 1) Pré-requisitos

- .NET SDK 9
- Docker + Docker Compose

## 2) Subir infra

```bash
cd NoPonto
docker compose up -d
```

## 3) Aplicar migrations

```bash
dotnet ef database update --project NoPonto/NoPonto.csproj
```

> Se você executar dentro da pasta `NoPonto`, use apenas `dotnet ef database update`.

## 4) Executar API

```bash
dotnet run --project NoPonto/NoPonto.csproj
```

Por padrão (perfil http local): `http://localhost:5166`.

## 5) Abrir Swagger

- `http://localhost:5166/swagger`

---

## Observabilidade e logs

O projeto possui logs ricos em pontos críticos:

- importação ArcGIS (paginação, lotes e contadores);
- ciclo de polling GPS (quantidade de posições, descartes por idade, tempo do ciclo);
- progresso de matching de POIs e relacionamento paradas-itinerários;
- warnings de dados inválidos/ruído geográfico;
- middleware global para padronizar respostas de erro.

---

## Próximos passos do projeto

A evolução planejada do NoPonto segue uma priorização clara, com foco imediato em **qualidade de GPS** e, em seguida, expansão multimodal:

### 1) Prioridade máxima: evoluir o sistema de GPS

Objetivo: reduzir erros de posição, melhorar previsibilidade de deslocamento e aumentar confiança da experiência em tempo real.

- Aplicar abordagem de **ML (Machine Learning)** para enriquecer o cálculo de posição e previsão de progresso na rota.
- Treinar modelos considerando variáveis como:
  - velocidade média por trecho da linha;
  - histórico de lentidão em regiões com cruzamentos/semáforos;
  - padrões por faixa de horário;
  - efeitos de contexto urbano (ex.: áreas próximas a escolas com tráfego sazonal em horários de entrada/saída);
  - comportamento histórico por linha/sentido e recorrência de perda de sinal.
- Usar esses sinais para melhorar dead-reckoning, estimativa de tempo e qualidade do status de veículo.

### 2) Expansão de modais (nesta ordem)

Após consolidar o GPS dos ônibus, o plano é aplicar o mesmo ecossistema para novos modais:

1. **BRT**
2. **Metrô**
3. **Trem**

A ideia é reaproveitar a base arquitetural já existente (itinerários, paradas, geoprocessamento, cache e streaming) e adaptar regras de negócio para cada modal.

### 3) Melhorias contínuas de POIs e paradas por itinerário

- Refino do algoritmo de matching de **POIs ↔ paradas** para reduzir falsos positivos e melhorar relevância contextual.
- Evolução do relacionamento **paradas ↔ itinerários** com calibração mais fina de critérios geoespaciais e validação operacional.

### 4) Sistema de mensageria e notificações

- Construir camada de mensageria para eventos de operação em tempo real.
- Habilitar notificações como “veículos próximos da parada”, alertas por linha e eventos de mudança de estado.

> Resumo estratégico: o foco principal é **melhorar o sistema de GPS primeiro**; em seguida, avançar para os demais modais e expandir funcionalidades de experiência operacional.
