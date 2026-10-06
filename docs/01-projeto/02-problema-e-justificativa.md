# NoPonto — problema e justificativa

**Finalidade:** formular o problema de desenvolvimento e fundamentar a relevância social, funcional, técnica e acadêmica do projeto.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** motivação e solução sustentadas pelo projeto; afirmações populacionais permanecem pendentes de bibliografia.

## Contexto do problema

O uso cotidiano do transporte público pode exigir que o passageiro combine informações de linhas, sentidos, paradas, horários e posição operacional. Quando essas informações se encontram separadas por modal, operador ou fonte, cabe ao próprio passageiro reconstruir a situação de sua viagem e interpretar dados que nem sempre possuem a mesma atualização ou semântica.

No caso de veículos rodoviários, uma coordenada GPS isolada não informa necessariamente em qual variante da rota o veículo está, qual seu sentido, quanto já percorreu ou qual parada vem a seguir. No transporte ferroviário, a ausência de uma coordenada GPS contínua exige distinguir posição observada de posição inferida. Em deslocamentos que combinam modais, essas diferenças tornam ainda mais importante uma representação integrada e transparente.

Essas constatações são a motivação funcional do NoPonto. Elas não constituem, sozinhas, prova estatística de frequência, impacto ou percepção entre a população.

## Formulação do problema de desenvolvimento

Como integrar dados estruturais e operacionais heterogêneos de transporte público, associá-los de forma consistente a linhas, sentidos, percursos e paradas ou estações, e apresentá-los em uma aplicação móvel que diferencie posições observadas de estimativas e apoie o acompanhamento do deslocamento pelo passageiro?

Uma formulação mais curta para a introdução é:

> Como desenvolver uma plataforma móvel capaz de organizar e apresentar, de forma integrada, informações estruturais e operacionais de diferentes modais de transporte público, considerando diferenças de fonte, atualização e disponibilidade de localização?

## Desdobramento técnico

O problema geral contém desafios verificáveis:

- reconciliar identificadores e modelos provenientes de fontes distintas;
- representar variantes de uma linha e versionar percursos sem perder a viagem em execução;
- validar leituras GPS atrasadas, duplicadas ou implausíveis;
- associar uma posição à rota, ao sentido e à próxima ocorrência de parada;
- distribuir atualizações sem transformar o banco relacional em cache de baixa latência;
- representar veículos ferroviários a partir de horários e evidências, sem afirmar GPS inexistente;
- manter contratos utilizáveis pelo aplicativo durante uma migração arquitetural;
- evoluir ETA de um modelo legado semanticamente incompatível para um pipeline observável e avaliável.

## Justificativa funcional e social

Uma interface que concentre linha, sentido, percurso, paradas e estado operacional pode reduzir o esforço necessário para consultar fontes distintas e interpretar um deslocamento. O potencial é particularmente relevante em viagens recorrentes, nas quais incertezas sobre espera, conexões ou alterações operacionais afetam decisões de saída e escolha de alternativa.

Para sustentar impacto social na monografia, ainda são necessárias fontes externas:

- `[REFERÊNCIA ACADÊMICA PENDENTE: evidência sobre o papel da informação em tempo real na experiência, confiança ou decisão do usuário de transporte público]`
- `[REFERÊNCIA OFICIAL PENDENTE: dados sobre volume de passageiros, extensão ou relevância dos modais no Rio de Janeiro]`
- `[REFERÊNCIA ACADÊMICA PENDENTE: estudos sobre fragmentação de informação e integração multimodal em sistemas de mobilidade]`
- `[REFERÊNCIA OFICIAL PENDENTE: caracterização de deslocamentos recorrentes por trabalho/estudo na região analisada]`

Até que essas referências sejam levantadas, o texto deve usar “o projeto procura”, “pode apoiar” e “há potencial”, evitando generalizações como “a maioria dos passageiros sofre” ou números sem fonte.

## Justificativa técnica

O NoPonto não se limita a consumir uma API. Ele constrói uma camada de integração e interpretação:

- estrutura V2 com identidades externas, proveniência e versões imutáveis;
- consultas PostGIS e matching em lote para relacionar GPS a percursos;
- estado causal e compare-and-set temporal para evitar regressão operacional;
- Redis para snapshots efêmeros e PostgreSQL/PostGIS para estrutura e histórico;
- SignalR para atualização rodoviária e polling controlado para ferrovia;
- schedule-first e estados de confiança para localização ferroviária estimada;
- telemetria e ETA V2 fail-open para experimentação sem comprometer o fluxo principal;
- aplicativo multiplataforma com mapa interativo em MapLibre/WebView.

Esses elementos tornam o projeto adequado a um TCC de desenvolvimento de software porque permitem discutir requisitos, modelagem, arquitetura, algoritmos geoespaciais, integração, testes e limitações com evidência concreta.

## Relevância para Ciência da Computação

As contribuições acadêmico-técnicas potenciais são:

1. aplicar modelagem de domínio versionada a dados de transporte heterogêneos;
2. projetar um pipeline de processamento quase em tempo real com consistência temporal e degradação controlada;
3. combinar banco geoespacial, cache e comunicação realtime em uma solução distribuída;
4. representar explicitamente diferentes níveis de observabilidade — GPS, estimativa ancorada e schedule;
5. estruturar uma transição de ETA baseada em telemetria e ground truth, separando baseline, shadow e promoção;
6. integrar backend e aplicativo móvel por contratos que preservam contexto estrutural.

A monografia não deve alegar contribuição científica original em aprendizado de máquina enquanto o ML V2 não estiver treinado e avaliado. A contribuição verificável atual está sobretudo na engenharia, integração e representação operacional.

## Justificativa da abordagem incremental

O projeto separa estrutura, operação e inteligência preditiva. Essa divisão permite demonstrar valor mesmo sem concluir um modelo avançado: primeiro organiza-se o domínio; depois associa-se o veículo à estrutura; em seguida apresenta-se o estado ao passageiro; somente então previsões podem ser avaliadas sobre dados semanticamente consistentes. O ETA antigo evidenciou o risco de treinar e consumir grandezas com significados diferentes, justificando a migração para eventos de previsão versionados e observáveis.

## Limitações da justificativa atual

- não foi realizada pesquisa com passageiros;
- não há estudo de usabilidade ou impacto social concluído;
- não há comparação empírica documentada com aplicativos existentes;
- não há métrica validada de precisão ou disponibilidade do sistema;
- a cobertura geográfica e modal ainda é parcial;
- dados externos podem mudar de contrato ou ficar indisponíveis.

## Evidências

Arquitetura e operação: auditorias [14](../00-auditoria/14-evolucao-arquitetural-e-legado.md) e [15](../00-auditoria/15-reconciliacao-producao-e-contexto.md). Requisitos verificáveis: [04-requisitos-e-funcionalidades.md](04-requisitos-e-funcionalidades.md). Formulação acadêmica: [06-base-para-introducao-tcc.md](06-base-para-introducao-tcc.md).

## Pendências

Realizar revisão bibliográfica; selecionar dados oficiais da região; decidir se haverá avaliação com usuários; definir comparação com soluções existentes sem extrapolar o escopo.
