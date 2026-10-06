# 02 — Inventário de repositórios

**Data:** 2026-10-06 · **Ambientes:** workspace local e produção · **Estado:** verificado

| Componente | Branch/commit examinado | Tecnologias confirmadas | Estado do worktree | Implantação |
|---|---|---|---|---|
| `noponto-backend` | `main` / `53567bd` | .NET 9, ASP.NET Core, EF Core 9, Npgsql/PostGIS, Redis, SignalR, xUnit | limpo antes da documentação | API em produção |
| `noponto-frontend` | `main` / `c69a92e` | Expo 55, React 19.2, RN 0.83.6, TypeScript 5.9, Expo Router, React Query, SignalR, WebView | limpo | não verificado |
| `ml` | `develop` / `08c493b` | Python, Flask, pandas, scikit-learn/joblib, PostgreSQL | `lab-output/` não versionado | container parado/unhealthy |
| `noponto-adm` | `develop` / `422a97d` | React/Vite/TypeScript/Tailwind | muitas alterações locais e `.env`; não modificadas | não verificado |
| `extrair-trem/` | sem repositório Git identificado | Python, requests, JSON/JSONL/CSV, pytest | N/A | ferramenta local; importação posterior confirmada no banco |

## Backend

Projeto único `NoPonto/NoPonto.csproj`, organizado em `1-API`, `2-Application`, `3-Domain`, `4-Data` e `5-Testes`. O `.csproj` é uma evidência arquitetural essencial: controllers e serviços legados de linhas, itinerários, paradas, POIs e administração são removidos da compilação. As superfícies ativas usam principalmente `Estrutura*Controller`, compatibilidade de mapa e serviços V2.

O ponto de composição `Program.cs` registra providers GPS, enriquecimento, cache, telemetria, ETA V2, schedule ferroviário, scanner/canary, SignalR, repositórios e hosted services. O Swagger de produção confirmou 18 caminhos públicos de consulta.

## Frontend

Telas roteadas: mapa (`app/index.tsx`), linhas (`app/linhas.tsx`), favoritos e configuração. `src/services` concentra API, hub GPS, estrutura V2, realtime ferroviário, eventos de parada, histórico e persistência. O mapa é uma implementação OSM/MapLibre em WebView (`src/components/mapOSM/webview/*`), apesar de `react-native-maps` também constar como dependência.

## ML e extrator ferroviário

O ML contém servidor de inferência, geração de dataset, treino, avaliação temporal e modelos Joblib versionados no workspace. O extrator contém coletor com checkpoint, dados normalizados, erros e builder de schedules; há saída para dias úteis, sábado e domingo.

## CI/CD

Backend possui `.github/workflows/deploy.yml`; frontend possui `.github/workflows/build.yaml`. O deploy real usa imagens GHCR `latest` e Compose em `/opt/stacks/noponto`. Rollback formal não foi identificado.

## Referências e pendências

`NoPonto/NoPonto.csproj:1-100`; `NoPonto/Program.cs`; `noponto-frontend/NoPonto/package.json`; `ml/requirements.txt`; `extrair-trem/rail_plan_collector.py`; arquivos de workflow. Confirmar se o painel admin deve ser versionado a partir do worktree atual.
