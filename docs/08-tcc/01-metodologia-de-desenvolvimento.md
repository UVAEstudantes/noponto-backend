# Metodologia de desenvolvimento

**Finalidade:** caracterizar o trabalho e o processo comprovável. **Data/baseline:** 2026-10-06; backend `53567bd`, frontend `c69a92e`, ML `08c493b`.

## Caracterização

O NoPonto é desenvolvimento de artefato computacional aplicado à mobilidade urbana, envolvendo integração de sistemas, processamento geoespacial, estado distribuído e visualização mobile. Pode sustentar pesquisa aplicada quando uma pergunta, protocolo, evidência e análise forem explicitados. Design Science Research é uma possibilidade se forem demonstrados problema relevante, construção do artefato, avaliação iterativa e comunicação dos resultados; os documentos atuais comprovam construção/evolução, mas não bastam para afirmar adesão retrospectiva integral a DSR.

## Processo efetivamente evidenciado

```text
problema e fontes → implementação incremental → modelo V2/versionamento
→ GPS/matching/realtime → schedules/inferência ferroviária
→ telemetria/ETA experimental → auditorias/reconciliação
→ documentação, testes existentes e deploy por imagem
```

Commits, migrations, testes, roadmaps e produção demonstram iteração e correção. Não há evidência para declarar Scrum, sprints, papéis ou cerimônias formais. A metodologia da monografia deve relatar desenvolvimento incremental guiado por problemas técnicos e validação por testes/inspeção, distinguindo o que foi executado do plano futuro.

## Tecnologias e instrumentos

- produto: C#/.NET 9, ASP.NET Core, EF Core/Npgsql, PostgreSQL/PostGIS, Redis, SignalR, TypeScript/Expo/React Native, WebView/MapLibre;
- desenvolvimento/operação: Git, GitHub Actions, Docker/Compose, GHCR e EAS;
- verificação: xUnit incorporado ao projeto, testes PostGIS/Redis, logs/métricas internas e inspeções read-only;
- documentação: Markdown, Mermaid e matrizes de rastreabilidade.

Tecnologia não é método de avaliação. O fato de um teste existir não prova que passou na baseline; execução deve registrar commit, ambiente e resultado.

## Decisões e limitações metodológicas

Versão estrutural, ocorrência específica, estado causal e schedule-first respondem a problemas de reprodutibilidade/semântica. Redis efêmero e monólito modular são trade-offs operacionais. ETA V2 OFF impede apresentar resultado preditivo atual. Produção é fotografia, não experimento controlado.

**Pendências:** escolher questão de pesquisa e opção A/B/C; alinhar método institucional; executar protocolo; registrar ameaças à validade. Fontes: [objetivos](../01-projeto/03-objetivos-e-escopo.md), [decisões](../02-arquitetura/06-decisoes-arquiteturais.md) e [diagramas](../07-diagramas/00-catalogo-convencoes-e-rastreabilidade.md).
