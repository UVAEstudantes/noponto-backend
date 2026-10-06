# 01 — Escopo e fontes

**Data:** 2026-10-06  
**Repositórios:** backend `53567bd`, frontend `c69a92e`, ML `08c493b`, admin `422a97d`  
**Ambientes:** workspace Windows; produção Debian via SSH read-only  
**Estado:** verificado nos limites descritos

## Método

A auditoria confrontou árvore e histórico Git, manifests, pontos de entrada, DI, controllers compilados, serviços, entidades, migrations, testes, Docker/CI, código do aplicativo e estado de containers/banco em produção. README e documentos anteriores foram usados apenas como pistas. Não foram executados migrations, importadores, coletores, deploys nem testes ligados a infraestrutura.

Foram permitidas alterações apenas nesta nova árvore `docs/`. Alterações locais preexistentes no painel administrativo e artefatos de laboratório do ML foram preservados.

## Fontes examinadas

| Fonte | Uso | Confiança/limite |
|---|---|---|
| Backend ASP.NET Core | Contratos, workers, persistência, realtime | Alta para implementação compilada; conferir exclusões do `.csproj` |
| Frontend Expo | Telas, mapa, API, SignalR, armazenamento | Alta para código; execução em dispositivo não realizada |
| Serviço ML Python | Modelo, features, endpoints e treino | Alta para código local; produção inativa |
| `extrair-trem/` | Coleta e construção de schedules | Diretório sem Git; proveniência por commit indisponível |
| Painel admin | Superfície administrativa | Worktree muito divergente; implantação não verificada |
| Produção | SO, containers, Swagger, migrations e estatísticas | Observação pontual em 2026-10-06, sem benchmark |

## Restrições

- Testes existentes foram inventariados, não executados.
- As estatísticas `n_live_tup` do PostgreSQL são estimativas do coletor de estatísticas, não contagens exatas.
- Valores de variáveis de ambiente não foram copiados; somente nomes e estados não sensíveis foram considerados.
- Não houve inspeção ofensiva, varredura de rede ou leitura extensiva de logs.

## Pendências

Repetir a fotografia de produção próximo à entrega do TCC; obter evidência de builds mobile; esclarecer a origem/versionamento de `extrair-trem`; validar os fluxos completos em ambiente isolado.

## Referências

`NoPonto/NoPonto.csproj`; `NoPonto/Program.cs`; `.github/workflows/deploy.yml`; migrations; manifests dos quatro repositórios; comandos read-only relacionados em [11-fontes-e-evidencias.md](11-fontes-e-evidencias.md).
