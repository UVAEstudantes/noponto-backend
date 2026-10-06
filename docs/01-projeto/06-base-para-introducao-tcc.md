# NoPonto — base para a introdução do TCC

**Finalidade:** fornecer estrutura argumentativa e um texto-base para futura redação acadêmica.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** **RASCUNHO**; requer revisão bibliográfica, orientação acadêmica e definição do escopo da defesa.

> Este documento não é a introdução final. Não contém citações fictícias. Marcadores entre colchetes devem ser substituídos por fontes verificáveis ou decisões formais.

## 1. Estrutura argumentativa recomendada

1. Apresentar a mobilidade urbana e a informação ao passageiro como contexto.
2. Delimitar o transporte público e a coexistência de modais/fontes no Rio de Janeiro.
3. Explicar a fragmentação e as diferenças entre dados estruturais, GPS e estimativas.
4. Formular o problema de integração e apresentação.
5. Introduzir o NoPonto e sua proposta, sem antecipar detalhes de implementação.
6. Justificar relevância funcional e técnico-acadêmica.
7. Declarar objetivo geral e objetivos específicos.
8. Delimitar o que é entregue, o que está em evolução e o que fica como trabalho futuro.
9. Apresentar brevemente a organização dos capítulos.

## 2. Proposta inicial de texto acadêmico — RASCUNHO

### Contextualização

O transporte público participa dos deslocamentos cotidianos de trabalho, estudo e acesso a serviços. Para planejar e acompanhar uma viagem, o passageiro pode precisar identificar linhas, sentidos, locais de embarque, horários e condições operacionais. Em uma rede composta por diferentes modais e operadores, essas informações podem ser disponibilizadas por fontes distintas, com formatos, identificadores e frequências de atualização diferentes. `[REFERÊNCIA OFICIAL/ACADÊMICA PENDENTE: caracterização do uso de transporte público e da necessidade de informação ao passageiro]`

Dados de localização também não possuem a mesma natureza em todos os modais. Ônibus e BRT podem oferecer observações GPS, ainda sujeitas a atraso, duplicidade, ruído e associação incorreta ao percurso. Em sistemas ferroviários, a fonte disponível pode fornecer horários e evidências temporais sem uma posição GPS contínua, tornando necessário representar estimativas e seu nível de confiança. `[REFERÊNCIA ACADÊMICA PENDENTE: sistemas de informação ao passageiro e qualidade/heterogeneidade de dados em tempo real]`

Essa heterogeneidade dificulta a construção de uma visão integrada. Uma coordenada isolada não determina necessariamente o sentido, a variante do percurso ou a próxima parada; um horário programado, por sua vez, não comprova a posição exata de um veículo. Assim, uma plataforma multimodal precisa organizar a estrutura do transporte, interpretar observações operacionais e informar ao usuário a origem e as limitações do estado apresentado.

### Proposta

Neste contexto, este trabalho propõe o desenvolvimento do NoPonto, uma plataforma de mobilidade urbana inicialmente direcionada ao Rio de Janeiro. O sistema integra informações estruturais e operacionais de transporte público e as disponibiliza por uma API e um aplicativo móvel. A solução organiza modais, linhas, sentidos, versões de percurso e paradas ou estações; processa posições rodoviárias; representa a operação ferroviária por horários e evidências; e apresenta os resultados em um mapa interativo.

No transporte rodoviário, o backend adquire posições de fontes configuráveis, valida a ordem temporal das observações e utiliza processamento geoespacial para associar cada veículo ao percurso e à próxima parada. Na ferrovia, combina viagens programadas e evidências externas para produzir uma posição estimada, diferenciada de uma observação GPS. Os estados operacionais são distribuídos ao aplicativo por mecanismos adequados a cada fluxo, como atualização em tempo real para ônibus/BRT e consulta periódica de snapshots ferroviários.

### Justificativa

A proposta procura reduzir a necessidade de interpretar informações isoladas de diferentes fontes e oferecer uma representação comum para o acompanhamento do deslocamento. Seu potencial inclui facilitar a consulta de linhas e percursos e apoiar decisões sobre espera e conexão. A dimensão desse benefício deverá ser sustentada por bibliografia e, se fizer parte da metodologia, por avaliação com usuários. `[REFERÊNCIA ACADÊMICA PENDENTE: impacto de informação integrada/em tempo real sobre experiência ou decisão do passageiro]`

Do ponto de vista da Ciência da Computação, o trabalho permite investigar integração de sistemas, modelagem versionada, processamento geoespacial, consistência de streams, cache distribuído, comunicação em tempo real e desenvolvimento móvel. A solução também estrutura uma evolução de estimativas de chegada baseada em eventos observáveis e avaliação, evitando tratar um modelo de aprendizado de máquina como componente isolado da semântica dos dados.

### Delimitação

O núcleo do trabalho abrange a modelagem estrutural, o processamento de veículos rodoviários, a representação ferroviária estimada, a API e o aplicativo de visualização. A integração de metrô, a promoção do ETA V2 e a validação completa em dispositivo dependem da definição final do escopo da defesa. Planejamento multimodal, Rotinas, notificações e modelos avançados permanecem como evolução posterior e não são apresentados como funcionalidades concluídas.

## 3. Formulação do problema

### Questão principal proposta

Como desenvolver uma plataforma móvel capaz de integrar e apresentar informações estruturais e operacionais de diferentes modais de transporte público, preservando a relação entre veículo, percurso e parada ou estação e distinguindo dados observados de posições estimadas?

### Questões técnicas auxiliares

- Como representar variantes e mudanças de percurso sem perder a referência da viagem em andamento?
- Como tratar leituras GPS atrasadas ou inconsistentes antes de publicá-las?
- Como associar uma posição à rota e à próxima ocorrência de parada?
- Como representar veículos ferroviários quando a fonte não fornece GPS contínuo?
- Como disponibilizar atualizações ao aplicativo sem confundir estrutura durável e estado efêmero?
- Como evoluir ETA de forma observável e comparável a um baseline?

Essas questões auxiliares podem orientar capítulos técnicos, mas não precisam ser apresentadas como hipóteses científicas independentes.

## 4. Justificativa resumida proposta

O NoPonto é justificado funcionalmente pelo potencial de reunir informações de transporte que possuem fontes e naturezas distintas. Tecnicamente, é justificado pela necessidade de modelar, reconciliar, processar e distribuir esses dados com semântica consistente. Academicamente, constitui um objeto concreto para aplicar e avaliar decisões de engenharia de software, bancos geoespaciais, sistemas distribuídos e interfaces móveis.

## 5. Objetivo geral

Desenvolver e avaliar uma plataforma móvel de mobilidade urbana capaz de integrar dados estruturais e operacionais de transporte público, organizar linhas, sentidos, percursos e paradas ou estações, e apresentar ao passageiro a localização observada ou estimada de veículos e informações úteis ao acompanhamento do deslocamento.

## 6. Objetivos específicos

1. Modelar de forma versionada os elementos estruturais do transporte público.
2. Integrar fontes externas e preservar identidades e proveniência.
3. Adquirir, normalizar e validar posições de ônibus e BRT.
4. Associar veículos rodoviários à rota, ao sentido, ao progresso e à próxima parada.
5. Disponibilizar snapshots e atualizações em tempo real para o aplicativo.
6. Representar a operação ferroviária por horários e evidências, explicitando o caráter estimado da posição.
7. Desenvolver aplicativo móvel para consulta e visualização dos dados integrados.
8. Empregar mecanismos de persistência e cache coerentes com estrutura, histórico e estado operacional.
9. Estruturar uma abordagem avaliável para estimativas de chegada.
10. Verificar as funcionalidades por testes e cenários controlados.
11. Documentar decisões, limitações e evolução arquitetural.

Os itens 9 e 10 devem ser ajustados ao escopo/tempo aprovado para a defesa.

## 7. Delimitação proposta

### Incluído

- backend e aplicativo móvel;
- estrutura V2;
- ônibus/BRT em GPS processado;
- ferrovia schedule-first/estimada;
- contratos HTTP/SignalR, PostGIS e Redis;
- documentação e validação técnica do núcleo.

### Condicionado à decisão de escopo

- ETA V2 shadow e avaliação;
- metrô;
- movimento contínuo consolidado;
- POIs;
- estudo de usabilidade.

### Não incluído como entrega obrigatória

- painel administrativo;
- Rotinas funcionais;
- roteamento multimodal e recomendação de alternativas;
- notificações inteligentes;
- ML supervisionado V2/GNN/DCRNN;
- cobertura nacional ou integração com todos os operadores;
- garantia de precisão, disponibilidade ou melhor rota.

## 8. Referências bibliográficas necessárias

| Tema | Evidência necessária | Fonte preferencial |
|---|---|---|
| Mobilidade no Rio de Janeiro | participação/volume/cobertura dos modais | órgãos públicos e operadores |
| Informação ao passageiro | efeito de realtime/informação integrada | artigos revisados por pares |
| Sistemas multimodais | conceitos de integração e planejamento | literatura de ITS/mobilidade |
| GPS e map matching | problemas e métodos de associação à rota | artigos/livros técnicos |
| Dados ferroviários sem GPS | schedule, AVL/ETA e inferência | pesquisa e documentação técnica |
| Bancos geoespaciais | fundamentos de consultas/índices espaciais | livros/artigos/documentação acadêmica |
| Sistemas em tempo real/distribuídos | consistência, cache e streaming | literatura de sistemas |
| ETA e avaliação temporal | baselines, leakage, métricas e validação | artigos de previsão de transporte |
| UX/acessibilidade mobile | critérios de avaliação | normas e pesquisa HCI |
| Privacidade | dados de localização, rotina e LGPD | legislação e literatura jurídica/técnica |

Não selecionar referências apenas por confirmarem a solução adotada. A revisão deve incluir limitações e abordagens alternativas.

## 9. Pontos que dependem de confirmação

1. Qual é a data e o escopo mínimo da defesa?
2. Metrô é entrega obrigatória ou evolução posterior?
3. ETA V2 precisa estar apenas em shadow ou deve aparecer ao passageiro?
4. Haverá avaliação com usuários ou somente validação técnica?
5. Quais plataformas/dispositivos serão usados na demonstração?
6. POIs e tarifas permanecem no escopo funcional?
7. O objetivo deve mencionar explicitamente os quatro modais ou apenas os modais efetivamente demonstrados?
8. Qual conjunto de métricas será usado para avaliar GPS, ferrovia e ETA?
9. O trabalho será caracterizado metodologicamente como desenvolvimento tecnológico, estudo de caso, pesquisa aplicada ou combinação aprovada pela instituição?

## 10. Limitações do rascunho

O texto ainda não possui revisão bibliográfica, citações, metodologia aprovada ou resultados experimentais consolidados. A linguagem deverá ser adaptada às normas da instituição e ao estilo do orientador. Afirmações de impacto devem permanecer condicionais até receberem evidência.

## Evidências e ligações

Baseado em [visão geral](01-visao-geral.md), [problema e justificativa](02-problema-e-justificativa.md), [objetivos](03-objetivos-e-escopo.md), [requisitos](04-requisitos-e-funcionalidades.md) e [roadmap](05-estado-atual-e-roadmap.md), com prevalência das auditorias [14](../00-auditoria/14-evolucao-arquitetural-e-legado.md) e [15](../00-auditoria/15-reconciliacao-producao-e-contexto.md).

## Pendências

Substituir todos os marcadores de referência por fontes verificadas; aprovar metodologia, escopo da defesa e objetivos com o orientador; revisar o texto conforme as normas institucionais.

## Prontidão

**READY_FOR_STAGE_2_2** — a visão, o problema, os objetivos, o catálogo de requisitos e a delimitação estão suficientemente estruturados para fundamentar os próximos documentos técnicos, sem transformar roadmap em entrega concluída.
