# NoPonto — objetivos e escopo do TCC

**Finalidade:** definir objetivos observáveis, entregas avaliáveis e fronteiras entre estado atual, possível entrega para a defesa e evolução posterior.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** proposta pronta para revisão do desenvolvedor/orientador; não fixa unilateralmente a entrega final.

## Objetivo geral proposto

Desenvolver e avaliar uma plataforma móvel de mobilidade urbana capaz de integrar dados estruturais e operacionais de transporte público, organizar linhas, sentidos, percursos e paradas ou estações, e apresentar ao passageiro a localização observada ou estimada de veículos e informações úteis ao acompanhamento do deslocamento.

Esse objetivo é compatível com o núcleo já implementado e não condiciona o sucesso do TCC à conclusão de roteamento multimodal ou de um modelo avançado de aprendizado de máquina.

## Objetivos específicos propostos

| ID | Objetivo específico | Evidência de alcance esperada | Estado na baseline |
|---|---|---|---|
| OE-01 | Levantar e modelar os conceitos de modal, linha, sentido, padrão operacional, versão de percurso e ocorrência de parada. | modelo V2, migrations e diagrama posterior | implementado |
| OE-02 | Integrar fontes externas estruturais e preservar identidades, proveniência e versionamento. | importações, identidades externas e dados publicados | implementado; execução deve ser documentada |
| OE-03 | Adquirir e normalizar posições de ônibus e BRT provenientes de providers configuráveis. | adapters, polling e métricas de ciclo | implementado e operacional |
| OE-04 | Validar temporal e geograficamente as leituras e associá-las à rota, ao sentido, à viagem e à próxima parada. | pipeline, consultas PostGIS e testes | implementado e operacional |
| OE-05 | Disponibilizar snapshots de veículos por consultas e atualização em tempo real. | REST, Redis e SignalR | implementado e operacional |
| OE-06 | Representar a operação ferroviária a partir de horários programados e evidências externas, distinguindo posição estimada de localização GPS. | schedules, tracker, estimator e contrato de confiança | implementado e operacional; qualidade a validar |
| OE-07 | Desenvolver aplicativo móvel para pesquisar e selecionar linhas, visualizar percursos, paradas e veículos em mapa interativo. | telas, services, hooks e demonstração em dispositivo | implementado no código; demonstração pendente |
| OE-08 | Implementar mecanismos de persistência, cache, expiração e atualização apropriados ao tipo de dado. | PostgreSQL/PostGIS, Redis, TTL e versionamento | implementado |
| OE-09 | Estruturar uma abordagem de ETA avaliável, com contexto de viagem, versão, ocorrência, baseline e ground truth. | pipeline ETA V2, eventos e métricas | em migração; desligado em produção |
| OE-10 | Verificar as funcionalidades por testes automatizados e cenários integrados controlados. | relatório reproduzível por commit | testes existem; execução consolidada pendente |
| OE-11 | Documentar arquitetura, decisões, limitações e evolução de modo verificável. | conjunto documental e diagramas das etapas seguintes | em andamento |

## Escopo implementado e demonstrável

Integra o núcleo atual:

- modelo estrutural V2 e dados de produção populados;
- consultas de linhas, sentidos, padrões, percursos e paradas;
- aquisição GPS de ônibus/BRT, validação, correção temporal e matching;
- viagem operacional, progresso longitudinal e próxima ocorrência;
- snapshots Redis, REST e SignalR;
- schedules, viagens esperadas e posição ferroviária estimada;
- mapa, busca, filtros, seleção, paradas, veículos e preferências no frontend;
- histórico/telemetria e infraestrutura para ETA V2;
- execução conteinerizada da API, PostgreSQL/PostGIS e Redis.

“Demonstrável” exige preparar um roteiro e validar o app em dispositivo. O código frontend existe, mas sua distribuição atual não foi confirmada.

## Escopo pretendido para a defesa — decisão pendente

Os itens abaixo podem ser candidatos, mas não devem ser assumidos como compromisso até aprovação:

1. habilitar ETA V2 em shadow e apresentar avaliação do baseline longitudinal;
2. consolidar movimento visual contínuo de veículos rodoviários;
3. integrar metrô em um nível mínimo coerente com a representação multimodal;
4. resolver ou retirar temporariamente a UI de POIs sem backend ativo;
5. executar suíte isolada e um roteiro de validação ponta a ponta;
6. preparar build mobile demonstrável e registrar versão/configuração;
7. estabelecer critérios de confiança e avaliação ferroviária.

### Pergunta de decisão

Qual subconjunto desses itens será critério de entrega do TCC? A resposta deve ser registrada com orientador/desenvolvedor e convertida em critérios de aceite.

## Evolução posterior

Não é necessária para validar o núcleo atual:

- Rotinas recorrentes ou pontuais;
- cálculo de rotas multimodais e alternativas;
- comparação dinâmica de tempo, caminhada, baldeações, custo e confiabilidade;
- acompanhamento automático de conexões e chegada ao destino;
- notificações contextuais;
- histórico de horários e atrasos como produto ao passageiro;
- ML supervisionado V2, GNN/DCRNN ou outros modelos avançados;
- expansão para outras regiões e operadores;
- painel administrativo.

## Entregas verificáveis propostas

| Entrega | Critério mínimo |
|---|---|
| Catálogo e modelo de domínio | diagrama e correspondência com schema V2 |
| Backend integrado | endpoints e workers documentados; execução controlada |
| Realtime rodoviário | veículo percorre aquisição → matching → snapshot → app |
| Ferrovia | schedule/evidência gera posição explicitamente estimada no app |
| Aplicativo | busca, seleção, estrutura e veículos demonstrados em dispositivo |
| Persistência | papéis de PostGIS e Redis demonstrados |
| ETA | estado real e, se incluído, protocolo de shadow/avaliação reproduzível |
| Testes | resultados datados e vinculados a commit/ambiente |
| Documentação | arquitetura, fluxos, modelo, limitações e evidências atualizados |

## Fora do escopo atual

- painel administrativo;
- gestão comercial ou operacional por operadores;
- emissão/venda de bilhetes;
- garantia de disponibilidade dos providers;
- substituição dos sistemas oficiais;
- recomendação perfeita ou garantia de tempo de chegada;
- monitoramento ofensivo ou intervenção em infraestrutura de terceiros.

## Critérios para avaliação posterior

Cada objetivo deverá ser avaliado como alcançado, parcialmente alcançado ou não alcançado. A evidência deve combinar código compilado, teste executado, configuração e demonstração. A existência de uma classe ou tela não basta. Metas quantitativas só podem ser usadas depois de definidas e medidas.

## Limitações

Fontes externas, diferenças entre GPS e inferência ferroviária, cobertura parcial, ETA em migração e ausência de validação mobile consolidada limitam conclusões. Ver [estado e roadmap](05-estado-atual-e-roadmap.md).

## Evidências

Fontes: auditorias [14](../00-auditoria/14-evolucao-arquitetural-e-legado.md) e [15](../00-auditoria/15-reconciliacao-producao-e-contexto.md), roadmap e código atual.

## Pendências

Aprovar formalmente o “escopo pretendido para a defesa” e converter os itens escolhidos em critérios de aceite.
