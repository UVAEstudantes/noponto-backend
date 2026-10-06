# 11 — Fontes e evidências

**Data:** 2026-10-06 · **Estado:** catálogo verificável

## Baseline Git

| Repositório | Commit | Data/descrição |
|---|---|---|
| backend | `53567bdfe3dad87bf767c77b9c3f69cf67a7a73d` | 2026-10-05; correção de busca por modal/tipo |
| frontend | `c69a92ea161e749b77842d35f82e14a86cbfc66e` | 2026-10-05; Node do CI |
| ML | `08c493baddb83db37c935dc459ca4d7a93aeab2d` | 2026-09-20; holdout temporal |
| admin | `422a97d5a16f491dcf8dd3de5e77baca38fce828` | 2026-05-14; início mockado |

## Evidências por tema

| Tema | Fontes primárias |
|---|---|
| Build/versões | `NoPonto/NoPonto.csproj`; frontend `package.json`; ML `requirements.txt`; Dockerfiles |
| Composição | `NoPonto/Program.cs:83-653` |
| API ativa | controllers não excluídos; Swagger de produção em 2026-10-06 |
| GPS | `2-Application/Services/GPS`; `4-Data/Repositories/GpsItinerarioRepository.*` |
| Ferrovia | `Services/TremRealtime`, `Services/TremSchedule`, `RailVehiclesController.cs` |
| Persistência | `4-Data/Context/DbContext.cs`; migrations; entidades; catálogo PostgreSQL |
| Frontend | `app/*.tsx`; `src/services`; `src/components/mapOSM` |
| ML | `server.py`, `dataset_v1.py`, `treinar.py`, scripts de validação |
| Coleta schedule | `extrair-trem/rail_plan_collector.py`, builder e saídas normalizadas |
| Infraestrutura | Compose/Dockerfile/workflows; estado Docker do host |
| Testes | `5-Testes`, frontend `*.test.ts`, `ml/tests`, testes do extrator |

## Observações read-only de produção

Executados no ambiente autorizado: data/SO/kernel/CPU/memória/discos; versões Docker/Compose; lista, inspect e stats sem stream dos containers; nomes de variáveis (sem valores); chamada HTTP local da raiz e Swagger; catálogo de extensões/tabelas, histórico de migrations e `pg_stat_user_tables` via `SELECT`.

Não foram executados: `sudo`, escrita em banco, migrations, restart, deploy, alteração de arquivos, leitura de valores secretos, benchmark, varredura de portas ou teste ofensivo.

## Regras de citação para Etapa 2

Cada afirmação definitiva deve apontar para commit + arquivo/símbolo; dados de produção devem incluir data. Números de `pg_stat_user_tables` devem ser rotulados como estimativas. Comportamento protegido por feature flag deve ser descrito como condicional até que o valor runtime seja verificado.

## Pendências

Gerar automaticamente uma matriz endpoint → controller → serviço → repositório; preservar relatórios de CI por commit; versionar o extrator; adicionar ADRs para decisões arquiteturais.
