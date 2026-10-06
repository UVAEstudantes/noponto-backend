# PostGIS, consultas, índices e transações

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Estado:** SQL e mappings examinados; nenhum `EXPLAIN ANALYZE` ou benchmark executado.

## Tipos geoespaciais

`Parada.Localizacao`, `Poi.Localizacao` e `PosicaoVeiculo.Localizacao` são `geometry(Point,4326)`; `PadraoVersao.Geometria` é `geometry(LineString,4326)`. Longitude é X e latitude Y. SRID 4326 armazena coordenadas angulares e permite funções topológicas/fração. Para distância/comprimento/azimute métrico, SQL converte para `geography`, que interpreta a superfície terrestre e retorna metros.

Essa combinação é deliberada: `ST_LineLocatePoint` e `ST_LineInterpolatePoint` operam convenientemente na LineString geometry; `ST_DWithin`, `ST_Distance` e `ST_Length(...::geography)` fornecem filtros/medidas métricas. A fração geometry multiplicada por comprimento geography é aproximação, reconhecida no enriquecedor.

## Pipeline físico de matching

Queries individual, batch e combined set-based seguem a mesma estrutura:

1. recebem lat/lon, linha/hints, distância máxima, bearing e janela causal;
2. criam ponto geometry e geography;
3. pré-selecionam versões atuais compatíveis por joins estruturais;
4. opcionalmente recortam `ST_LineSubstring(fracao_min,fracao_max)`;
5. filtram proximidade com `ST_DWithin` ou `ST_Distance≤dist_max`;
6. calculam fração `ST_LineLocatePoint`;
7. calculam bearing local por azimute entre pontos interpolados próximos;
8. projetam ponto com `ST_LineInterpolatePoint`;
9. resolvem próxima ocorrência ordenada e distância restante por comprimentos de substrings;
10. ranqueiam candidatos com distância, bearing, continuidade/hints e retornam o escolhido.

Para circularidade, a distância até ocorrência anterior à fração atual soma restante até 1 e início até a ocorrência. Geometria degenerada, SRID incorreto ou LineString inválida pode tornar funções inválidas/enganosas; validação da importação é a primeira barreira.

## Funções PostGIS principais

| Função | Papel |
|---|---|
| `ST_SetSRID(ST_MakePoint)` | materializa GPS 4326 |
| `ST_DWithin` | filtro métrico de proximidade |
| `ST_Distance` | erro lateral/até parada em metros |
| `ST_LineLocatePoint` | fração longitudinal `[0,1]` |
| `ST_LineInterpolatePoint` | coordenada/bearing na rota |
| `ST_LineSubstring` | janela causal e distância parcial |
| `ST_Length(...::geography)` | comprimento em metros |
| `ST_Azimuth`/`degrees` | direção local |
| `ST_AsGeoJSON`, `ST_X`, `ST_Y` | saída para API |

## Índices relevantes

- GiST em `PadroesVersoes.Geometria`, `Paradas.Localizacao` e `Pois.Localizacao` favorece operadores/filtros espaciais compatíveis. Casts/substrings podem limitar aproveitamento direto; plano não foi medido.
- B-tree/unique em padrão atual, identidade externa, versão+ordem/fração reduzem candidatos antes do cálculo espacial.
- históricos: compostos por linha/versão/timestamp, veículo/timestamp, parada/timestamp e chaves de passagem.
- ETA: chaves de fechamento/sampling, timestamp, linha e status/modelo.
- rail: versão/hash/ativa, calendário/sentido/partida e sequences.

Índice presente não prova que toda query o usa. Não há afirmação de ganho percentual.

## SQL set-based e batching

CTEs carregam lotes de entradas e avaliam candidatos em uma ida ao banco, reduzindo round-trips e trabalho repetido. `NpgsqlBatch` agrupa comandos ETA/histórico; CTEs `INSERT ... ON CONFLICT`, `UPDATE ... RETURNING` implementam idempotência/contadores. Ordenação determinística antes de locks reduz risco de deadlock, mas não o elimina universalmente.

## Limites transacionais

| Operação | Unidade PostgreSQL | Concorrência/idempotência |
|---|---|---|
| importação estrutural | plano, identidades, versões, ocorrências/publicação | unique/hash e transação |
| schedule import/ativação | versão e filhos; troca de ativa | unique hash/ativa e transação |
| viagem durável/outbox | estado+eventos | advisory lock por veículo, versão/CAS lógico |
| histórico | journal+passagem+fechamento ETA | transaction, uniques e payload check |
| ETA batch | lote ordenado | advisory locks veículo/target e janela sampling |

Redis não participa dessas transações. Falha após commit e antes de projeção/ACK produz reprocessamento eventual, não rollback distribuído.

## EF Core e SQL direto

EF define schema, relações e consultas CRUD estruturais. SQL direto é usado onde PostGIS, CAS, batch, advisory locks, `SKIP LOCKED` e CTEs expressam melhor atomicidade/desempenho. Isso aumenta responsabilidade por compatibilidade de nomes/schema; migrations e testes de integração são essenciais.

## Crescimento, limitações e testes

Telemetria, histórico, eventos e previsões podem dominar I/O/índices. Não há particionamento identificado. Cast geography e substrings por candidato podem ficar caros com cardinalidade maior. Etapa 2.4B deve tratar planos, autovacuum, memória e capacidade — sem inferir gargalo atual. Evidências: repositories GPS/PostGIS, DbContext/migrations e testes matching/PostGIS. Não executados.

