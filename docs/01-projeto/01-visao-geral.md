# NoPonto — visão geral do projeto

**Finalidade:** apresentar o produto, seu público, sua proposta de valor e sua evolução sem pressupor conhecimento técnico prévio.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** visão funcional baseada em código e produção reconciliados; experiência mobile publicada não verificada.

## O que é o NoPonto

O NoPonto é uma plataforma de mobilidade urbana voltada à organização e apresentação de informações sobre transporte público. Seu contexto inicial é a cidade do Rio de Janeiro, onde o passageiro pode precisar consultar linhas, sentidos, itinerários, paradas ou estações e acompanhar veículos cuja posição é observada ou estimada.

A proposta nasce de um problema de integração: informações úteis ao deslocamento podem estar distribuídas entre operadores, fontes públicas, sistemas de localização e grades de horários. O NoPonto procura transformar esses dados heterogêneos em uma representação comum e apresentá-los em uma interface móvel orientada ao passageiro.

O sistema contempla ônibus, BRT e trem em diferentes níveis de maturidade. Metrô integra a visão de produto, mas não possui cobertura operacional equivalente confirmada. Portanto, o NoPonto deve ser apresentado como uma plataforma em evolução multimodal, e não como integração completa e homogênea dos quatro modais.

## Público e situações de uso

O público-alvo primário é o passageiro de transporte público, em especial quem realiza deslocamentos recorrentes para trabalho, estudo ou outros compromissos. O sistema também oferece uma base técnica para estudar integração de dados, comportamento operacional e estimativas de chegada.

Situações de uso atuais incluem:

- pesquisar linhas e seus destinos;
- selecionar sentido e variante operacional;
- visualizar geometria, paradas ou estações no mapa;
- acompanhar ônibus e BRT a partir de posições GPS processadas;
- visualizar trens por posição inferida a partir de horários e evidências externas;
- consultar eventos ou previsões associados a uma parada;
- filtrar o conteúdo exibido e preservar preferências locais.

Não há evidência de planejamento multimodal de viagem, recomendação automática da melhor alternativa ou Rotinas funcionais na implementação atual.

## Proposta de valor

O valor central não está somente em desenhar marcadores sobre um mapa. O sistema procura estabelecer continuidade entre quatro etapas:

1. adquirir e organizar dados provenientes de fontes diferentes;
2. relacionar observações operacionais à estrutura correta de linha, sentido e percurso;
3. representar posição, progresso e próxima parada ou estação com indicação adequada de origem e confiança;
4. disponibilizar essa informação ao passageiro por consultas HTTP, atualização em tempo real e interface cartográfica.

Para veículos rodoviários, a posição decorre de GPS real, submetido a validação, correção temporal e associação geoespacial à rota. Para a ferrovia, onde não existe GPS contínuo equivalente na fonte utilizada, o sistema combina horários programados e evidências temporais para estimar a posição. Essa diferença deve permanecer explícita na interface e na documentação.

## Capacidades confirmadas

### Estrutura do transporte

O backend mantém uma estrutura versionada de modais, linhas, sentidos, padrões operacionais, versões de percurso e ocorrências ordenadas de parada. Em produção foram confirmadas 502 linhas, 951 sentidos, 7.798 paradas e 980 padrões/versões. O aplicativo consulta essa estrutura para montar linhas e percursos.

### Operação rodoviária

O backend coleta posições de ônibus e BRT, utiliza fontes primárias e fontes shadow, rejeita leituras inconsistentes, executa matching geoespacial, mantém a versão do percurso associada à viagem, calcula progresso e próxima ocorrência e publica snapshots no Redis. Veículos de linhas selecionadas são entregues ao aplicativo pelo SignalR; consultas REST também existem.

### Operação ferroviária

O sistema possui grades versionadas, viagens esperadas, scanner de evidências, associação entre observação e viagem, controle de atualização e estimador temporal/espacial. O frontend obtém snapshots por polling e os projeta sobre a geometria ferroviária. A posição exibida é estimada, e não deve ser descrita como GPS do trem.

### Aplicativo

O aplicativo Expo/React Native oferece mapa MapLibre dentro de WebView, busca, seleção de linhas e sentidos, exibição de percursos e paradas, veículos rodoviários e ferroviários, histórico de pesquisa, temas e preferências visuais. Essas capacidades foram verificadas no código; a versão efetivamente distribuída em loja ou dispositivo não foi auditada.

## Capacidades em transição

O ETA é a principal migração arquitetural. O cliente do modelo antigo permanece no fluxo, mas seu serviço está desligado intencionalmente e falha sem interromper o GPS. O ETA V2 possui baseline longitudinal, seleção canary, fila, persistência e fechamento do erro observado, porém está desabilitado em produção e ainda não substitui o ETA apresentado.

Há também contratos de compatibilidade entre backend V2 e partes do frontend. Eles são necessários no estado atual, mas não representam a arquitetura-alvo de longo prazo.

## Visão de evolução

A evolução pretendida inclui Rotinas para deslocamentos recorrentes ou pontuais, rotas multimodais, comparação entre alternativas, acompanhamento de conexões, informações de tarifas e alertas contextuais. Uma Rotina poderá reunir origem, destino, dias, horários e combinações como ônibus+trem, BRT+trem ou BRT+metrô. Dados operacionais poderão, futuramente, apoiar a escolha entre alternativas.

Essa visão é roadmap. Algoritmos, critérios de recomendação e arquitetura de roteamento ainda não estão fechados e não devem ser apresentados como entrega do TCC sem confirmação posterior.

## Relação com Ciência da Computação

O projeto materializa problemas de integração de sistemas, modelagem de domínio, processamento geoespacial, tratamento de streams, consistência temporal, cache distribuído, comunicação em tempo real e desenvolvimento multiplataforma. Também cria uma base observável para estudar estimativas e aprendizado de máquina sem tornar um modelo preditivo específico condição para a utilidade do sistema.

## Limites atuais

- cobertura modal desigual; metrô ainda planejado;
- dependência de disponibilidade e qualidade de fontes externas;
- posição ferroviária inferida, sujeita à qualidade do schedule e das evidências;
- ETA V2 ainda desligado;
- chamadas de POI presentes no frontend sem endpoints ativos equivalentes;
- tela de Rotinas apenas informativa;
- publicação e comportamento do aplicativo em dispositivos não verificados nesta baseline.

## Evidências e ligações

Ver [problema e justificativa](02-problema-e-justificativa.md), [objetivos e escopo](03-objetivos-e-escopo.md), [requisitos](04-requisitos-e-funcionalidades.md), [estado e roadmap](05-estado-atual-e-roadmap.md) e as auditorias [14](../00-auditoria/14-evolucao-arquitetural-e-legado.md) e [15](../00-auditoria/15-reconciliacao-producao-e-contexto.md).

## Pendências

Confirmar a cobertura modal pretendida para a defesa, a versão mobile que será demonstrada, o papel de ETA na entrega e se POIs permanecerão visíveis.
