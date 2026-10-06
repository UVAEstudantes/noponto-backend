# NoPonto — auditoria técnica (Etapa 1)

**Data:** 2026-10-06 · **Estado:** Etapas 1, 1.5, 2.1, 2.2, 2.3A–C e 2.4A concluídas · **Escopo atual:** projeto, arquitetura, funcionalidades e modelagem de dados

Esta pasta reúne a documentação técnica e de produto do NoPonto: auditoria, projeto, arquitetura, funcionalidades, dados, infraestrutura e evolução planejada. Código, configuração, migrations, testes e observação read-only de produção foram tratados como fontes primárias. A existência de um arquivo não foi confundida com compilação, execução ou uso em produção.

Os documentos 01–13 de `00-auditoria` são a fotografia histórica da Etapa 1. A Etapa 1.5 reconciliou suas divergências nos documentos 14 e 15; essas correções prevalecem: a estrutura V2 de produção foi confirmada populada e o serviço ML antigo está desligado intencionalmente durante a migração para ETA/ML V2.

## Índice

1. [Escopo e fontes](00-auditoria/01-escopo-e-fontes.md)
2. [Inventário de repositórios](00-auditoria/02-inventario-repositorios.md)
3. [Inventário de funcionalidades](00-auditoria/03-inventario-funcionalidades.md)
4. [Mapa de arquitetura](00-auditoria/04-mapa-arquitetura.md)
5. [Fluxos identificados](00-auditoria/05-fluxos-identificados.md)
6. [Banco e persistência](00-auditoria/06-banco-e-persistencia.md)
7. [Infraestrutura de produção](00-auditoria/07-infraestrutura-producao.md)
8. [ETA/ML e evolução](00-auditoria/08-eta-ml-evolucao.md)
9. [Testes e validações](00-auditoria/09-testes-e-validacoes.md)
10. [Segurança e limitações](00-auditoria/10-seguranca-e-limitacoes.md)
11. [Fontes e evidências](00-auditoria/11-fontes-e-evidencias.md)
12. [Lacunas e perguntas](00-auditoria/12-lacunas-e-perguntas.md)
13. [Relatório final](00-auditoria/13-relatorio-final.md)
14. [Evolução arquitetural e legado](00-auditoria/14-evolucao-arquitetural-e-legado.md)
15. [Reconciliação de produção e contexto](00-auditoria/15-reconciliacao-producao-e-contexto.md)

## Etapa 2.1 — Projeto e escopo do TCC

1. [Visão geral](01-projeto/01-visao-geral.md)
2. [Problema e justificativa](01-projeto/02-problema-e-justificativa.md)
3. [Objetivos e escopo](01-projeto/03-objetivos-e-escopo.md)
4. [Requisitos e funcionalidades](01-projeto/04-requisitos-e-funcionalidades.md)
5. [Estado atual e roadmap](01-projeto/05-estado-atual-e-roadmap.md)
6. [Base para a introdução do TCC — RASCUNHO](01-projeto/06-base-para-introducao-tcc.md)

## Etapa 2.2 — Arquitetura, tecnologias e integrações

1. [Arquitetura geral](02-arquitetura/01-arquitetura-geral.md)
2. [Stacks e tecnologias](02-arquitetura/02-stacks-e-tecnologias.md)
3. [Componentes backend e frontend](02-arquitetura/03-componentes-backend-frontend.md)
4. [Integrações externas](02-arquitetura/04-integracoes-externas.md)
5. [Contratos e comunicação](02-arquitetura/05-contratos-e-comunicacao.md)
6. [Decisões arquiteturais](02-arquitetura/06-decisoes-arquiteturais.md)
7. [Arquitetura de implantação](02-arquitetura/07-arquitetura-de-implantacao.md)

## Etapa 2.3A — Realtime de ônibus e BRT

1. [Fluxo completo](03-funcionalidades/01-realtime-onibus-brt-fluxo-completo.md)
2. [Aquisição, validação e estado causal](03-funcionalidades/02-aquisicao-validacao-e-estado-causal.md)
3. [Matching V2 e viagem operacional](03-funcionalidades/03-matching-v2-e-viagem-operacional.md)
4. [Redis, SignalR e publicação](03-funcionalidades/04-redis-signalr-e-publicacao.md)
5. [Frontend realtime e renderização](03-funcionalidades/05-frontend-realtime-e-renderizacao.md)
6. [Falhas, casos-limite e validação](03-funcionalidades/06-falhas-casos-limite-e-validacao.md)

O fluxo rodoviário está documentado da aquisição ao mapa.

## Etapa 2.3B — Realtime ferroviário

1. [Fluxo completo](03-funcionalidades/07-realtime-ferroviario-fluxo-completo.md)
2. [Schedules: coleta, builder e importação](03-funcionalidades/08-schedules-coleta-builder-e-importacao.md)
3. [Expected runs, scanner e evidências](03-funcionalidades/09-expected-runs-scanner-e-evidencias.md)
4. [Tracker, predição temporal e posição](03-funcionalidades/10-tracker-predicao-temporal-e-posicao.md)
5. [Publicação ferroviária e frontend](03-funcionalidades/11-publicacao-ferroviaria-e-frontend.md)
6. [Falhas, limites e validação](03-funcionalidades/12-ferrovia-falhas-limites-e-validacao.md)

O fluxo ferroviário está documentado da coleta offline e grade ativa até o mapa. Diferentemente do rodoviário, cuja posição nasce de coordenadas GPS, o ferroviário combina horários, ETA por pares de estações, identidade do trem, topologia e geometria para **inferir** uma posição longitudinal. “Ao vivo” significa evidência recente do provedor, não GPS; a precisão depende da grade, do binding, da atualidade da evidência e do padrão estrutural.

## Etapa 2.3C — ETA, telemetria e Machine Learning

1. [ETA: visão geral e fluxo operacional](03-funcionalidades/13-eta-visao-geral-e-fluxo-operacional.md)
2. [ETA legado e migração para V2](03-funcionalidades/14-eta-legado-e-migracao-v2.md)
3. [Telemetria, histórico e ground truth](03-funcionalidades/15-telemetria-historico-e-ground-truth.md)
4. [ETA V2: baseline, shadow e canary](03-funcionalidades/16-eta-v2-baseline-shadow-e-canary.md)
5. [Datasets, treinamento e validação](03-funcionalidades/17-datasets-treinamento-e-validacao.md)
6. [ETA avançado e modelos espaço-temporais](03-funcionalidades/18-eta-avancado-e-modelos-espacotemporais.md)
7. [Limitações, testes e roadmap](03-funcionalidades/19-eta-limitacoes-testes-e-roadmap.md)

O cliente ETA antigo permanece no fluxo GPS em modo fail-open, mas o serviço Python está desligado intencionalmente. A telemetria contínua e a fundação ETA V2 existem; baseline, shadow e canary V2 permanecem desabilitados em produção. Um modelo supervisionado V2 semanticamente correto ainda depende de dataset reproduzível, treinamento e comparação com o baseline. A modelagem e a persistência são aprofundadas na Etapa 2.4A abaixo; a Etapa 2.4B permanece reservada.

## Etapa 2.4A — Modelagem de dados e persistência

1. [Modelagem conceitual e lógica](04-dados/01-modelagem-conceitual-e-logica.md)
2. [Modelagem física e dicionário de dados](04-dados/02-modelagem-fisica-e-dicionario-de-dados.md)
3. [Estrutura V2, identidades e versionamento](04-dados/03-estrutura-v2-identidades-e-versionamento.md)
4. [Modelagem ferroviária e schedules](04-dados/04-modelagem-ferroviaria-e-schedules.md)
5. [Viagens, histórico, telemetria e ETA](04-dados/05-viagens-historico-telemetria-e-eta.md)
6. [PostGIS, consultas, índices e transações](04-dados/06-postgis-consultas-indices-e-transacoes.md)
7. [Redis, estado operacional e consistência](04-dados/07-redis-estado-operacional-e-consistencia.md)
8. [Migrations, integridade e evolução](04-dados/08-migrations-integridade-e-evolucao.md)

A modelagem conceitual, lógica e física usa a estrutura V2 como referência vigente. PostgreSQL/PostGIS é a persistência durável; Redis mantém estado operacional efêmero, projeções reconstruíveis e streams.

## Etapa 2.4B — Infraestrutura, operação e resiliência

1. [Servidor, ambientes e topologia](05-infraestrutura/01-servidor-ambientes-e-topologia.md)
2. [Containers, redes, volumes e dependências](05-infraestrutura/02-containers-redes-volumes-e-dependencias.md)
3. [Build, CI/CD, deploy e rollback](05-infraestrutura/03-build-ci-cd-deploy-e-rollback.md)
4. [Desempenho, otimizações e capacidade](05-infraestrutura/04-desempenho-otimizacoes-e-capacidade.md)
5. [Logs, métricas e observabilidade](05-infraestrutura/05-logs-metricas-e-observabilidade.md)
6. [Segurança e superfície de exposição](05-infraestrutura/06-seguranca-e-superficie-de-exposicao.md)
7. [Resiliência, backup e recuperação](05-infraestrutura/07-resiliencia-backup-e-recuperacao.md)
8. [Operação, riscos e plano de melhorias](05-infraestrutura/08-operacao-riscos-e-plano-de-melhorias.md)

A infraestrutura produtiva foi documentada distinguindo configuração de execução e fotografia pontual de tendência. Segurança e observabilidade foram analisadas; limitações de desempenho, capacidade, backup e recuperação foram registradas sem alterar o ambiente. A evolução funcional correspondente é consolidada na Etapa 2.5 abaixo.

## Etapa 2.5 — Evolução funcional e consolidação do roadmap

1. [Experiência atual e jornadas do passageiro](06-evolucao/01-experiencia-atual-e-jornadas-do-passageiro.md)
2. [Rotas multimodais e algoritmos candidatos](06-evolucao/02-rotas-multimodais-e-algoritmos-candidatos.md)
3. [Rotinas, favoritos e personalização](06-evolucao/03-rotinas-favoritos-e-personalizacao.md)
4. [Expansão modal, POIs e tarifas](06-evolucao/04-expansao-modal-pois-e-tarifas.md)
5. [Notificações, UX e acessibilidade](06-evolucao/05-notificacoes-ux-e-acessibilidade.md)
6. [Dependências técnicas e arquitetura futura](06-evolucao/06-dependencias-tecnicas-e-arquitetura-futura.md)
7. [Roadmap, priorização e escopo da defesa](06-evolucao/07-roadmap-priorizacao-e-escopo-da-defesa.md)
8. [Critérios de aceite, avaliação e riscos](06-evolucao/08-criterios-de-aceite-avaliacao-e-riscos.md)

A evolução funcional foi documentada sem prometer capacidades futuras. Rotas multimodais e Rotinas permanecem planejadas; metrô não possui integração operacional comprovada; POIs estão incompletos; ETA V2 continua em migração. O escopo da defesa depende de decisão do desenvolvedor e do orientador. A consolidação acadêmica e os diagramas correspondentes são apresentados na Etapa 3 abaixo.

## Etapa 3 — Diagramas e consolidação acadêmica

### Diagramas técnicos

1. [Catálogo, convenções e rastreabilidade](07-diagramas/00-catalogo-convencoes-e-rastreabilidade.md)
2. [C4 — contexto](07-diagramas/01-c4-contexto.md)
3. [C4 — containers](07-diagramas/02-c4-containers.md)
4. [Componentes backend](07-diagramas/03-componentes-backend.md)
5. [Componentes frontend](07-diagramas/04-componentes-frontend.md)
6. [Implantação e comunicação](07-diagramas/05-implantacao-e-comunicacao.md)
7. [DER — estrutura V2](07-diagramas/06-der-estrutura-v2.md)
8. [DER — ferrovia](07-diagramas/07-der-ferrovia.md)
9. [DER — viagens, histórico, telemetria e ETA](07-diagramas/08-der-viagens-historico-telemetria-eta.md)
10. [GPS — sequências e atividades](07-diagramas/09-gps-sequencias-e-atividades.md)
11. [Matching, estado causal e viagem](07-diagramas/10-matching-estado-causal-e-viagem.md)
12. [Redis, SignalR e consistência](07-diagramas/11-redis-signalr-e-consistencia.md)
13. [Ferrovia — coleta, scanner e binding](07-diagramas/12-ferrovia-coleta-scanner-e-binding.md)
14. [Ferrovia — predição, estados e publicação](07-diagramas/13-ferrovia-predicao-estados-e-publicacao.md)
15. [ETA, telemetria, ground truth e ML](07-diagramas/14-eta-telemetria-ground-truth-e-ml.md)
16. [Frontend, WebView e realtime](07-diagramas/15-frontend-webview-e-realtime.md)
17. [CI/CD, armazenamento e recuperação](07-diagramas/16-ci-cd-armazenamento-e-recuperacao.md)

### Consolidação para o TCC

1. [Metodologia de desenvolvimento](08-tcc/01-metodologia-de-desenvolvimento.md)
2. [Rastreabilidade de objetivos e requisitos](08-tcc/02-rastreabilidade-de-objetivos-e-requisitos.md)
3. [Plano de testes e validação](08-tcc/03-plano-de-testes-e-validacao.md)
4. [Catálogo de evidências](08-tcc/04-catalogo-de-evidencias.md)
5. [Fundamentação teórica e referências pendentes](08-tcc/05-fundamentacao-teorica-e-referencias-pendentes.md)
6. [Estrutura proposta da monografia](08-tcc/06-estrutura-proposta-da-monografia.md)
7. [Base acadêmica de arquitetura e desenvolvimento](08-tcc/07-base-academica-de-arquitetura-e-desenvolvimento.md)
8. [Base acadêmica de resultados, limitações e evolução](08-tcc/08-base-academica-de-resultados-limitacoes-e-evolucao.md)
9. [Decisões pendentes e checklist da defesa](08-tcc/09-decisoes-pendentes-e-checklist-da-defesa.md)

`07-diagramas` reúne modelos editáveis e rastreáveis da arquitetura vigente. `08-tcc` organiza metodologia, requisitos, avaliação, evidências e redação acadêmica preliminar sem inventar resultados. A Etapa 3 completa a base documental; a redação/formatação final da monografia e a execução dos experimentos permanecem posteriores.

## Etapa 4 — Validação técnica e preparação de evidências

1. [Auditoria e validação dos diagramas](09-validacao/01-auditoria-e-validacao-dos-diagramas.md)
2. [Inventário de testes e execuções](09-validacao/02-inventario-de-testes-e-execucoes.md)
3. [Plano experimental GPS e matching](09-validacao/03-plano-experimental-gps-e-matching.md)
4. [Plano experimental ferroviário](09-validacao/04-plano-experimental-ferrovia.md)
5. [Plano experimental ETA V2](09-validacao/05-plano-experimental-eta-v2.md)
6. [Validação mobile e jornadas](09-validacao/06-validacao-mobile-e-jornadas.md)
7. [Catálogo operacional de evidências](09-validacao/07-catalogo-operacional-de-evidencias.md)
8. [Backlog de estabilização e prontidão](09-validacao/08-backlog-de-estabilizacao-e-criterios-de-prontidao.md)
9. [Relatório consolidado de validação](09-validacao/09-relatorio-consolidado-de-validacao.md)

A Etapa 4 conecta a documentação às evidências: registra execuções locais reais, separa falhas de bloqueios ambientais e prepara protocolos quantitativos sem acessar produção. A validação Mermaid permaneceu estática por ausência de renderizador; integrações PostgreSQL/Redis e jornadas Android dependem de ambientes controlados.

## Convenções

- **Implementação:** implementado, parcial, em desenvolvimento, planejado ou não identificado.
- **Validação:** aprovado nesta auditoria, testes existentes não executados, experimental, não validado ou indeterminado.
- **Implantação:** produção confirmada, apenas local, não implantado ou não verificado.
- Caminhos `NoPonto/...` referem-se ao backend; caminhos explicitamente prefixados por outro repositório referem-se ao componente correspondente.
- Nenhum segredo, valor de credencial ou string de conexão foi registrado.

## Veredito da auditoria

**NEEDS_TEST_ENVIRONMENT.** A base documental e os protocolos de validação estão completos, e um subconjunto seguro foi executado. A validação integrada requer PostgreSQL/PostGIS e Redis descartáveis, runner frontend, dispositivo Android e renderizador Mermaid controlado; falhas de fixture e instabilidade temporal foram registradas.
