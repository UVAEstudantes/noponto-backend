# 13 — Relatório final da Etapa 1

**Data:** 2026-10-06 · **Escopo:** auditoria e inventário, sem produção da documentação acadêmica final

## A. Inventário técnico

Foram examinados backend .NET 9, app Expo/React Native, serviço Python de ETA/ML, painel administrativo e ferramentas Python de coleta ferroviária. A arquitetura combina API/workers, PostgreSQL/PostGIS, Redis, SignalR, providers externos e app MapLibre/WebView. Foram reconstruídos os fluxos rodoviário, ferroviário, ETA e importação; inventariados modelo EF/migrations, testes, CI/CD e infraestrutura.

Produção confirmada: API, PostgreSQL/PostGIS e Redis. A API expõe 18 caminhos Swagger de leitura. O banco contém migrations atuais, grande histórico operacional e schedule ferroviário importado. O ML está parado.

## B. Estado do sistema

- **Implementado:** aquisição GPS multifuente, validação/correção/matching, cache/snapshots, SignalR, histórico, estrutura V2, mapa/app, schedule e estimativa ferroviária, telemetria ML.
- **Validado:** há ampla cobertura de testes em arquivos, mas nenhum foi executado nesta auditoria; validação ponta a ponta permanece pendente.
- **Em produção:** API/Redis/PostGIS e workers dentro da API; schedule ferroviário presente.
- **Experimental/parcial:** ETA V2 shadow/canary, scanner ferroviário condicionado a flags, extrator ferroviário e painel admin.
- **Não operacional:** serviço ML em produção na fotografia realizada.
- **Planejado:** roteamento multimodal, rotinas, transferências, notificações, tarifas compostas e evolução espaço-temporal/GNN.

## C. Lacunas e riscos

As maiores lacunas são tabelas estruturais rodoviárias aparentemente vazias, ML parado, flags runtime não confirmadas, ausência observável de autenticação, banco/Redis publicados, Docker socket montado na API, retenção/backlog e backup/rollback não comprovados. O painel admin tem alterações locais significativas e o extrator não está versionado como Git.

## D. Documentação gerada

Foram criados `docs/README.md` e os treze documentos numerados de `docs/00-auditoria/01-escopo-e-fontes.md` a `13-relatorio-final.md`.

## E. Prontidão

**READY_FOR_DOCUMENTATION**

Justificativa: código, contratos, modelo, fluxos, frontend e implantação fornecem evidência suficiente para redigir a Etapa 2 com honestidade técnica. As lacunas afetam resultados quantitativos e alegações de disponibilidade, mas estão explicitamente isoladas e não impedem a documentação da arquitetura e do estado atual.

## F. Próximos passos propostos

1. Visão geral, problema, justificativa e objetivos.
2. Arquitetura e fundamentação tecnológica.
3. Realtime rodoviário e tratamento de falhas.
4. Realtime ferroviário schedule-first e confiança.
5. Modelo conceitual/lógico/físico e persistência.
6. Frontend, mapa e experiência do passageiro.
7. ETA/ML: baseline atual, avaliação e evolução.
8. Infraestrutura, deploy, segurança e confiabilidade.
9. Requisitos de rotas/rotinas, claramente futuros.
10. Diagramas editáveis, plano de testes e material acadêmico.

Antes de números finais, investigar as três primeiras perguntas de [12-lacunas-e-perguntas.md](12-lacunas-e-perguntas.md). A Etapa 2 não foi iniciada e depende de revisão/autorização.

## Referências

Todo o conjunto desta pasta; baseline e comandos em [11-fontes-e-evidencias.md](11-fontes-e-evidencias.md).
