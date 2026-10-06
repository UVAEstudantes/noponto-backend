# NoPonto — experiência atual e jornadas do passageiro

**Finalidade:** reconstruir o que o aplicativo realmente oferece e separar experiência implementada de visão futura.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** telas, componentes, hooks, services e testes do frontend; contratos backend; auditorias 14/15 e documentos 01–05. **Estado:** código confirmado; build efetivamente distribuído não verificado.

## Estado atual

O app Expo Router possui quatro abas: mapa inicial, Rotinas (arquivo chamado `favoritos`), Linhas e Configurações. A navegação customizada oculta a tab bar padrão. O mapa MapLibre roda em WebView; estrutura é obtida por HTTP, ônibus/BRT recebem snapshots e SignalR, e ferrovia usa polling HTTP. TanStack Query está instalado, porém não há `QueryClient`, `useQuery` ou `useMutation`: documentos que lhe atribuam cache nos fluxos atuais estão desatualizados. Cache, deduplicação e lifecycle são implementados por hooks/estado próprios.

| Capacidade | Implementação | Disponibilidade | Prioridade |
|---|---|---|---|
| busca/seleção de linha, sentido, padrão, percurso e paradas | implementada, não integralmente validada em dispositivo | código; produção do app não verificada | núcleo/TCC |
| ônibus/BRT no mapa | operacional no backend; UI implementada | backend confirmado; build mobile não verificado | núcleo/TCC |
| trens inferidos | operacional no backend; UI implementada | backend confirmado; build mobile não verificado | núcleo/TCC |
| eventos/chegadas de parada | implementada sobre `/paradas/{id}/eventos` | contrato backend confirmado | núcleo/TCC |
| histórico de busca e seleção de linhas | local, implementado | código; sem sincronização | núcleo auxiliar |
| Rotinas | placeholder textual | não implantada funcionalmente | evolução/decisão |
| POIs/tarifas | UI/contratos parciais | fluxo completo não confirmado | decisão |

## Jornada A — acompanhar ônibus ou BRT

**Objetivo:** localizar uma linha e acompanhar seus veículos. O passageiro abre o mapa, escolhe modal, busca por código/nome, seleciona uma linha e pode alternar sentido, cor e visibilidade das paradas. O hook hidrata padrões/geometria, lê snapshot e inscreve a linha no hub; a WebView recebe atualizações incrementais. Ao tocar em parada, o sheet combina linhas visíveis e eventos disponíveis.

Quando faltam estrutura ou veículos, o mapa pode permanecer sem marcadores; erros de busca são registrados no console e frequentemente convertidos em lista vazia. Isso nem sempre diferencia “sem serviço”, “sem dados” e “falha”. A seleção e o modal são preservados em AsyncStorage.

## Jornada B — visualizar ferrovia

**Objetivo:** selecionar ramal/linha ferroviária e ver trens. A busca estrutural entrega linha, sentidos e geometria. `useRailRealtime` consulta snapshots, filtra por linha e a apresentação distingue estados Live/Estimated/Scheduled conforme campos de origem/confiança. A posição é inferida, não GPS.

Sem snapshot ou geometria compatível, o veículo não aparece. O passageiro não recebe hoje uma explicação uniforme para provider indisponível, grade sem run ou dado stale. Cobertura e precisão ainda precisam de teste em dispositivo e amostra temporal.

## Jornada C — consultar uma parada

**Objetivo:** entender quais linhas atendem a parada e eventos próximos. O usuário habilita paradas da linha, toca no marcador e abre `ParadaSheet`; o app consulta eventos, contextualiza os veículos pelas linhas selecionadas e permite focar um veículo. A lista depende da estrutura já hidratada e do endpoint de eventos. Ausência/erro resulta em lista vazia, podendo ser ambígua.

## Telas e estados

`linhas.tsx` oferece busca detalhada, escolha de sentido/padrão, mapa, paradas, componentes de tarifa/chegada e painéis POI. As chamadas POI degradam para vazio porque os endpoints atuais não estão compilados. Configurações persistem tema, estilo do mapa e lateralidade; há alguns labels/roles de acessibilidade. “Rotinas” descreve capacidades futuras, mas não possui CRUD, rota, conta ou acompanhamento.

Há indicadores de carregamento e mensagens locais, porém o modelo de estados não é uniforme. Critério futuro: representar explicitamente `loading`, `empty`, `error`, `stale`, `sem cobertura`, `provider indisponível` e `geometria incompatível`, sempre com ação possível e timestamp/origem quando pertinente.

## Requisitos, riscos e validação

Reutiliza RF-001–019 e RNF-013–015. `REQUISITO_CANDIDATO UX-ESTADO`: toda ausência operacional deve distinguir serviço inexistente, dado indisponível e erro de comunicação. Aceite: cenários simulados apresentam mensagens diferentes e não conservam veículo stale como realtime.

Riscos: build não validado, contratos de compatibilidade, ausência de cache declarativo central, CDN do mapa, perda de reinscrição e estados vazios silenciosos. Validar com roteiro em dispositivo: busca, seleção, reinício, reconexão, mudança de sentido, parada, ferrovia, offline e acessibilidade básica. Não há avaliação com usuários comprovada.

**Pendências:** confirmar versão distribuída; decidir POIs/tarifas; padronizar erros; avaliar leitor de tela/contraste/movimento; testar reinscrição. Relacionados: [frontend realtime](../03-funcionalidades/05-frontend-realtime-e-renderizacao.md), [ferrovia](../03-funcionalidades/11-publicacao-ferroviaria-e-frontend.md) e [UX futura](05-notificacoes-ux-e-acessibilidade.md).
