# Migrations, integridade e evolução

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Produção:** estado herdado da auditoria read-only de 06/10; não reinspecionado nesta etapa.

## Evolução do schema

| Período/migration | Evolução |
|---|---|
| `InitialMigration` e abril/maio | modelo antigo: linhas, sentidos, itinerários, paradas, POI, histórico, tarifas |
| `OutboxViagens` (entidade `ViagemOperacionalOutbox`) | eventos/outbox para viagem |
| `TelemetriaMlContinua` | telemetria contínua, stream→PostgreSQL |
| `PositionCorrectionShadowOrigins` | auditoria de correção temporal |
| `GtfsParadaItinerarioMvp` | transição estrutural intermediária |
| `EstruturaTransporteV21` | fontes/importações/identidades/padrões/versões/ocorrências |
| `ViagemDuravelPostgres` | PostgreSQL como autoridade da viagem |
| `EstruturaFinalEtapas1e2` e `RuntimeEstruturalEtapa3` | integridade/publicação/runtime V2 |
| `RemoverLegadoEstruturalBusBrt` | retirada física/compilada de partes antigas |
| `EtaV2Foundation` | previsões, constraints e índices |
| `RemoverIndiceAtualizadoEmUtcViagensOperacionais` | ajuste de índice |
| `RailScheduleFoundation` | grade ferroviária versionada |

Migrations são sequência histórica; não devem ser reescritas por limpeza casual. A presença local não prova aplicação. A auditoria anterior confirmou 21 migrations aplicadas até `20261003211227_RailScheduleFoundation` em produção. Essa é a baseline citada, sem nova consulta.

## Integridade por camada

- **Aplicação:** valida inputs, transições e payloads antes de persistir.
- **EF/migration:** FKs, nulabilidade, max lengths, cascade/restrict.
- **Checks:** enums/status, intervalos `[0,1]`, ordens/distâncias/offsets, coerência ETA.
- **Unique/partial:** identidades externas, versões/hash, uma schedule ativa, passagem por ocorrência/volta, observação ML.
- **Transação/locks:** importações/publicação, viagem/outbox, histórico/ETA, advisory locks.
- **Redis Lua:** CAS temporal e preflight de tipos/ACL/estado.

Nenhuma camada oferece atomicidade global PostgreSQL+Redis+SignalR. O desenho usa autoridade durável, projeções reconstruíveis, idempotência e consistência eventual limitada.

## Estado arquitetural

| Estrutura | Classificação |
|---|---|
| V2: fontes, identidades, padrões, versões, ocorrências | atual |
| viagem durável, eventos, passagens, telemetria | atual |
| rail schedules | atual e ativo em produção para Santa Cruz |
| ETA V2 | em migração, tabelas presentes, runtime OFF |
| `GpsEtaClient`/serviço Python | legado ativo contratual; serviço intencionalmente OFF |
| `Itinerario`/`ParadaItinerario` | legado removido/substituído para arquitetura principal; migrations históricas permanecem |
| fachadas DTO/endpoints antigos | legado ativo de compatibilidade onde há call site |
| tracker/expected runs ferroviários | runtime em memória, não schema |

Não há recomendação de `DROP TABLE` nesta etapa. Dependências externas, backups e migrations aplicadas precisam ser verificados antes de limpeza.

## Desempenho e crescimento

Estrutura é relativamente estável; telemetria, histórico, eventos, outbox e previsões crescem continuamente. Unique e índices melhoram leitura/idempotência, mas aumentam custo de insert e armazenamento. Não foi identificado particionamento. Retention existe para streams/outbox; retenção durável das grandes tabelas requer política explícita. Saturação Redis, bloat/vacuum e I/O são riscos potenciais a estudar na 2.4B, não problemas comprovados aqui.

## Preparação para diagramas

Produzir posteriormente:

1. DER conceitual geral: modal→linha→sentido→padrão→versão→ocorrência→parada;
2. DER lógico estrutural com fontes/identidades/importações;
3. DER ferroviário version→pattern→run→stop e mapeamento V2;
4. DER operacional: viagem durável/outbox→evento→passagem→ETA;
5. modelo telemetria/shadow separado;
6. sequência CAS GPS Redis;
7. sequência checkpoint/outbox e reprojeção;
8. sequência passagem fechando ETA;
9. estados viagem e previsão.

Cada relação deve indicar PK/FK, optionalidade e delete behavior. Redis deve aparecer em diagrama de componentes/estado, não no DER relacional.

## Divergências e pendências

- corrigir futuramente, em etapa autorizada, a documentação 2.3C que cita timeout ETA de 5 s; código vigente usa 3 s;
- catalogar schema produtivo automaticamente e comparar ao model snapshot;
- registrar qualidade interpolada/observada da passagem;
- definir retenção/particionamento para históricos volumosos;
- testar restore de viagem após perda Redis e concorrência multi-instância;
- revisar entidades auxiliares/legadas antes de limpeza.

## Testes e referências

Fontes: todas as migrations não-Designer, `TransporteDbContextModelSnapshot`, `DbContext`, entities, repositories e documentos 14/15 de auditoria. Testes estruturais, migration/PostGIS, viagem/outbox, telemetria, ETA e rail foram inventariados; nenhum foi executado.

## Veredito

O modelo vigente, suas garantias e suas limitações estão suficientemente identificados para aprofundar infraestrutura/operação sem inventar relações físicas.

**`READY_FOR_STAGE_2_4B`**

