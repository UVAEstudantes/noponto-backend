# NoPonto Backend

Backend da plataforma acadêmica NoPonto, voltada à consulta de transporte público e ao acompanhamento de ônibus, BRT e trem. Reúne APIs, aquisição de dados, processamento geoespacial, estimativas e comunicação em tempo real para o aplicativo móvel.

## Funcionalidades e arquitetura

Monólito modular em ASP.NET Core/.NET 9, com Entity Framework Core 9, Npgsql, NetTopologySuite, PostgreSQL/PostGIS, Redis e SignalR. Controllers e workers compartilham o processo da API.

- Estrutura V2 versionada: modais, linhas, sentidos, percursos, paradas e ocorrências; consultas compactas e importação estrutural.
- Ônibus/BRT: aquisição configurável (SPPO, BRT, ITS e GTFS-RT conforme configuração), normalização, deduplicação, matching PostGIS, viagem operacional e publicação de posições.
- Trem: grades versionadas, viagens esperadas, consultas limitadas a provedores, refresh e posições inferidas sobre o percurso. Posição ferroviária estimada não equivale a GPS medido.
- Eventos de parada, ETA, tarifas/formas de pagamento, telemetria e histórico. ETA avançado e modelos ML dependem de flags, dados e serviços próprios; sua presença no código não comprova ativação em produção.
- Radar de Espera: sessões, monitoramento e integração push Android/FCM, condicionados à configuração e às permissões do cliente.
- Saúde: `/health/live`, `/health/ready` e `/health/fontes`; disponibilidade da API e saúde das fontes são sinais distintos.

## Organização

| Caminho | Responsabilidade |
| --- | --- |
| `NoPonto/1-API` | Controllers, hub, middleware e configuração HTTP |
| `NoPonto/2-Application` | Serviços, workers, GPS, ferrovia, Radar e ETA |
| `NoPonto/3-Domain` | Entidades de domínio |
| `NoPonto/4-Data` | Persistência, repositórios, Redis e SQL/PostGIS |
| `NoPonto/5-Testes` | Testes xUnit, incluindo integração |
| `NoPonto/Migrations` | Evolução do schema |
| `tools/`, `.ci/`, `.deploy/` | Ferramentas, validação e operação |

Alguns controllers e serviços legados estão excluídos no `NoPonto.csproj`; consulte os contratos compilados e o Swagger, não apenas os nomes de arquivos.

## Desenvolvimento local

Requer SDK .NET 9, PostgreSQL com PostGIS e Redis. Use instâncias locais isoladas. Docker/Compose é opcional; os arquivos existentes devem ser revisados antes de qualquer execução.

Configure o ambiente a partir de `NoPonto/.env.local.example`, sem copiar valores reais para o Git. A aplicação carrega `.env` no diretório de execução sem sobrescrever variáveis já definidas; `.env.local` não deve ser presumido como carregado automaticamente.

Variáveis essenciais: `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `REDIS_HOST` e `REDIS_PORT`. Configure também `ASPNETCORE_ENVIRONMENT`, `CORS__ORIGINS__*`, provedores `GPS__*`/`GpsSources__*`, opções ferroviárias e Radar/FCM conforme os módulos utilizados. A configuração de isolamento valida os destinos de infraestrutura. Nunca use banco, Redis ou credenciais de produção no desenvolvimento.

```sh
cd NoPonto
dotnet restore NoPonto.csproj
dotnet run --project NoPonto.csproj
```

**Atenção ao startup:** em `Development`, o código aplica migrations automaticamente. Nos demais ambientes, verifica pendências e bloqueia o startup se necessário; depois executa o bootstrap Redis. Revise os destinos antes de iniciar. Workers podem consultar fontes externas conforme a configuração.

A URL local depende de `Properties/launchSettings.json` e do ambiente. O Swagger fica em `/swagger`, com documentos público e administrativo. A especificação gerada é a referência para as rotas disponíveis.

## APIs e integrações

Consultas estruturais V2, veículos, eventos de parada, tarifas, snapshots ferroviários e Radar são expostos via HTTP. O hub GPS usa `/hub/gps` por padrão (`GPS__HUB__ROUTE` configurável), com inscrições por linha e contratos de presença. Redis mantém estado operacional efêmero; PostgreSQL/PostGIS mantém dados duráveis.

As integrações incluem fontes municipais de ônibus/BRT, ITS/GTFS-RT, TrensRJ e FCM para Radar remoto, conforme os módulos habilitados. Proteja endpoints mutáveis e credenciais ao expor a API; o projeto não deve ser presumido como uma superfície pública inteiramente autenticada.

## Testes e validação

Há testes xUnit no projeto e verificações Python/shell nas ferramentas. Integrações podem exigir PostgreSQL/PostGIS ou Redis descartáveis. A seleção offline utilizada pelo CI está em `.ci/offline-tests.json`; o workflow mostra o isolamento e a verificação dos resultados. Para execução manual, escolha o conjunto apropriado ao ambiente antes de iniciar testes.

A limpeza documental foi validada somente por inspeções estáticas, hashes, referências, YAML e `git diff --check`; não foram executados builds ou suítes .NET.

## CI/CD e deploy

`.github/workflows/deploy.yml` executa validação em pull requests e pushes para `main`. Em push para `main`, após a validação, publica imagem `linux/amd64` no GHCR com tags `latest`, temporal/SHA e `build-N`.

O job de deploy exige push para `main` e `vars.NOPONTO_DEPLOY_ENABLED == 'true'`. Usa o digest publicado, ambiente `production`, Tailscale/SSH e scripts versionados em `.deploy/`, com verificações e rollback previstos nesses scripts. Não há `workflow_dispatch` nesse workflow. Um futuro push pode publicar imagem e realizar deploy; confirme a variável antes de enviar alterações. O workflow backend foi preservado nesta limpeza.

## Limites e documentação

A qualidade de posições e estimativas depende das fontes, idade dos dados e configuração. Rotinas demonstrativas do aplicativo não representam um motor de rotas multimodais concluído. Estado Redis não substitui persistência durável; testes offline não comprovam comportamento em produção ou em aparelhos físicos.

Documentação técnica, auditorias, evidências e históricos estão no [noponto-docs](https://github.com/UVAEstudantes/noponto-docs), com [índice da organização documental](https://github.com/UVAEstudantes/noponto-docs/blob/main/INDICE.md). O arquivo `NoPonto/docs/ETA_ML_TREINO_3B.md` permanece como exceção técnica: `tools/eta_ml/test_audit_sql.py` extrai e verifica um gerador nele contido. Scripts/SQL anteriormente documentais foram preservados em `tools/auditoria/`; YAMLs auxiliares, em `.deploy/arquivo-documental/`. Resultados locais em `/artifacts/` são ignorados.

[Aplicativo](https://github.com/UVAEstudantes/noponto-frontend) · [Releases](https://github.com/UVAEstudantes/noponto-backend/releases)
