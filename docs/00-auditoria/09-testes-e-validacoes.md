# 09 — Testes e validações

**Data:** 2026-10-06 · **Repositórios:** backend, frontend, ML e extrator · **Estado:** inventário estático; testes não executados

## Inventário

O backend incorpora xUnit no próprio projeto e possui dezenas de classes em `5-Testes`. Cobertura temática identificada:

- providers GPS, cadência, validação, matching batch/set-based/diferencial e benchmarks;
- correção temporal, estado causal Redis, snapshots, TTL e retenção;
- viagens observadas/duráveis, outbox e telemetria ML;
- estrutura V2, GTFS, ArcGIS, reconciliação e PostGIS;
- ETA V2 foundation/hardening/integração;
- schedule, expected-run binding, tracker, scanner, canary e vertical slice ferroviário;
- compatibilidade do frontend legado e eventos de parada.

O frontend contém testes TypeScript junto a services/utils/hooks (`*.test.ts`): geometria ferroviária, contratos estruturais, apresentação de confiança, histórico, eventos, busca, mapa e veículos. O `package.json` não declara script `test`, portanto o runner efetivo precisa ser esclarecido.

O ML possui diretório `tests/` e scripts de avaliação/validação temporal. O extrator possui `test_rail_plan_collector.py` e `test_rail_schedule_builder.py`.

## Estado de validação

Nenhum teste foi executado nesta auditoria para respeitar a restrição de não escrever fora de `docs/` e evitar conexões acidentais a banco/Redis existentes. A presença de um teste é registrada como **testes existentes, mas não executados**, nunca como aprovação.

Há sinais de validação experimental em artefatos `lab-output`, relatórios ferroviários e arquivos de benchmark, mas resultados não foram promovidos a evidência de release porque proveniência, comando, ambiente e commit não estão uniformemente vinculados.

## Gaps

- Separar projeto de testes do artefato web e marcar explicitamente testes que exigem infraestrutura.
- Criar fixtures efêmeras/containers isolados e um comando CI seguro.
- Adicionar testes E2E app → API → Redis/PostGIS e contrato Swagger.
- Publicar métricas de cobertura e resultados por commit.
- Validar carga/latência em ambiente próprio; não usar produção para benchmark invasivo.
- Adicionar healthcheck e teste de degradação quando ML/provider está indisponível.

## Referências

`NoPonto/5-Testes`; `NoPonto.csproj`; frontend `**/*.test.ts`; `ml/tests`; testes em `extrair-trem/`; workflows CI. Resultado verificável nesta auditoria: **não executado**.
