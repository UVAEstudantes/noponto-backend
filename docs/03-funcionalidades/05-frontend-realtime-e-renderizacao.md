# 05 — Frontend realtime e renderização

**Finalidade:** reconstruir seleção, contratos, reconciliação e ponte React Native ↔ MapLibre/WebView.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Verificação:** código e testes locais do frontend; versão efetivamente distribuída não foi inspecionada.

## 1. Da seleção à assinatura

O usuário seleciona uma linha na interface; `app/index.tsx` mantém `LinhaSelecionadaInfo` com UUID, código, modal, cor, estado, filtro de sentido e visibilidade de paradas. `useMobilidadeRio.garantirItinerario` então:

1. chama `listarSentidosV2(codigoLinha)`;
2. em paralelo, chama `listarPadroesV2(sentido.id)`;
3. mantém padrões com `versaoAtualId`;
4. chama `obterItinerarioPadraoVersaoV2` para cada versão;
5. aceita apenas geometria `LineString`, converte `[lon,lat]` para `[lat,lon]`, ordena ocorrências e monta `padroesV2`;
6. armazena segmentos, paradas por versão, rótulos de sentido e mapa versão→segmento;
7. grava no estado por `linhaId` e só então executa `inscreverLinha(linhaCodigo)`.

`CacheEstruturalPorVersao` guarda `Promise` por `PadraoVersaoId`, deduplicando solicitações inclusive durante carregamento. Rejeição convertida em `null` permanece cacheada até limpeza explícita de testes; não há expiração/retry automática do cache estrutural.

Se o realtime traz versão ainda ausente, outro effect coleta IDs e tenta hidratar `/padroes-versoes/{id}/itinerario`. Enquanto isso, o veículo pode permanecer fora do mapa: `getVeiculosPorCodigo` filtra por versões visíveis e exige `padraoVersaoId` conhecido.

## 2. Conexão e contrato

`gpsHub.ts` cria singleton `HubConnection`, registra `PosicaoAtualizada`, usa reconexão automática e distribui a vários subscribers locais. `adaptarPosicaoRodoviariaV2` converte o alias SignalR para `VeiculoMapaRodoviario`:

- identidade visual `road:{ordem}`;
- linha trim/uppercase;
- IDs linha/sentido/padrão/versão;
- coordenadas, fração/comprimento, velocidade e próxima ocorrência;
- timestamp ISO → epoch JS;
- status numérico default `0`;
- bearing do backend ou cálculo frontend entre posição anterior/atual.

Não há validação runtime de finitude/range do payload TypeScript; interfaces não existem em runtime. Timestamp inválido vira `NaN`. A confiança está no backend.

## 3. Reconciliação dos snapshots

`reconciliarSnapshotsRodoviarios(atuais, recebidos)` agrupa recebidos por linha. Remove dos atuais todos os veículos das linhas presentes e concatena o novo grupo completo. Linhas não mencionadas permanecem intactas.

Consequências:

- posições são substituídas, não mescladas por veículo;
- veículos ausentes de um snapshot não vazio da mesma linha são removidos;
- array vazio retorna cópia do estado atual, pois não carrega identidade da linha;
- não compara timestamps nem rejeita snapshot regressivo;
- mudança de linha depende de um snapshot posterior da linha antiga para remover o registro antigo;
- perda de conexão mantém o último estado em memória; não há stale timer local nem REST fallback.

O backend marca `SemSinal`, mas o payload visual apenas transporta `status`; não foi encontrada diferenciação clara de ícone/opacidade no trecho de renderização rodoviária. `Inativo` não é enviado: itens expirados são omitidos.

## 4. Filtro e montagem para o mapa

`app/index.tsx` deriva padrões visíveis do `modoSentido`: primeiro sentido para `ida`, segundo para `volta`, todos para `ambos`. Essa posição ordinal é uma convenção de UI, não um identificador semântico. Veículos são filtrados por código e por `PadraoVersaoId` visível.

Versões carregadas por realtime podem acrescentar geometria à linha. O mapa recebe:

- `structureKey` com linha, cor, sentido, paradas e IDs de versões;
- segmentos e `itinerarioSegmentoMap`;
- paradas e nomes por versão;
- posições com identidade estrutural, coordenadas, progresso, heading, status e próxima parada.

Veículo sem versão ou de versão desconhecida não passa pelo filtro enquanto existe um conjunto visível. Isso evita desenhá-lo na geometria errada, mas o torna invisível ao passageiro durante falha de hidratação.

## 5. Ponte WebView

`MapaOSM` gera HTML uma vez por âncora inicial. A WebView carrega MapLibre GL JS 3.6.1 e estilo Carto externos. Depois de `map.on('load')`, cria layers/sources, inicia animações e envia a string `map_ready` pelo bridge. Antes disso, effects não injetam updates; o script ainda suporta `window.pendingData` para chamadas diretas precoces.

Há três canais separados:

| Comando | Conteúdo | Frequência/efeito |
|---|---|---|
| `updateMap` | estrutura, tema, tráfego, estilo | pesado; muda quando `structureSignature` muda |
| `updateRealtime` | posições rodoviárias/ferroviárias | leve; a cada estado realtime |
| `updateUser` | localização, heading e acurácia do usuário | independente e frequente |

`structuralCacheRef` guarda cópias das linhas sem posições. `updateRealtime` combina posições novas com `lastMapData` e chama `updateMap` com `realtimeOnly:true`. Dentro de `updateMap`, `structuralUpdate=false` impede reconstrução de GeoJSON de linhas e cache de paradas. Essa é a garantia concreta de que GPS não remonta a estrutura.

## 6. Sources, layers e marcadores

Rotas são `Feature<LineString>` em uma `FeatureCollection`; sources/layers MapLibre persistentes recebem `setData` somente em update estrutural. Paradas usam `Marker` DOM com thinning conforme zoom, bounds e chave física. Veículos rodoviários usam `Marker` DOM e popups, não a source ferroviária.

Chave do marcador: `modal:nome-da-linha:id-do-veículo`. Para cada snapshot:

- cria marcador se inexistente;
- escolhe segmento pela versão;
- se há `posicaoNaRota`, interpola na LineString e usa heading local da rota;
- se existente, anima sincronização por 700 ms;
- sem fração, anima linearmente até GPS em 1.600 ms;
- remove todas as chaves não vistas naquele `updateMap` realtime;
- atualiza popup, cor e rotação.

Dead-reckoning (`startDR`) usa fração, comprimento e velocidade média entre updates. Próximo de parada (<300 m) aplica fator de desaceleração visual. Ao sincronizar, não recua normalmente; aceita rewind apenas se a nova fração estiver mais de `0,15` atrás, interpretado como wrap/mudança relevante. Essa heurística visual é distinta das regras operacionais do backend.

`map.on('rotate')` recalcula headings relativos ao bearing do mapa. Popups mostram linha/veículo/sentido, tempo, velocidade e próxima parada conforme dados disponíveis. Mensagens WebView→RN incluem `stop_click` e métricas realtime a cada 25 updates; mensagens não JSON são ignoradas.

## 7. Falhas e ciclo de vida

- WebView não pronta: RN aguarda `map_ready`; effects rodam novamente quando `mapReady` muda.
- CDN/estilo MapLibre indisponível: não há fallback cartográfico local demonstrado.
- estrutura falha: `null`, sem assinatura; Promise resolvida como `null` fica cacheada.
- conexão falha: log e estado anterior; não há fallback REST.
- reconnect: não reinscreve códigos.
- snapshot vazio: não remove estado React; falta identidade da linha.
- versão desconhecida: hidratação é tentada; veículo filtrado até haver geometria.
- timestamp antigo: frontend não compara; aceita a ordem de eventos recebida.

## 8. Testes e lacunas

`veiculosMapa.test.ts` cobre adaptação, filtros e reconciliação; `estruturaV2.test.ts` cobre helpers/cache. Não foi localizado teste automatizado completo do bridge `map_ready → updateMap → updateRealtime`, animação, perda/reconnect ou snapshot vazio. `railPresentation.test.ts` é ferroviário e não valida o realtime rodoviário.

Não foram executados testes nesta etapa. Devem ser adicionados testes com timers/fake WebView, reconexão/reinscrição, payload regressivo, versão desconhecida que falha e expiração do último veículo.

## 9. Diagrama futuro, referências e pendências

**Sequência frontend/WebView:** seleção → estrutura V2/cache → inscrição → SignalR → adapter → reconciliação → filtro → `dadosParaMapa` → `updateRealtime` → marcadores/animação.  
**Participantes:** `app/index.tsx`, `useMobilidadeRio`, `estruturaV2.ts`, `gpsHub.ts`, `veiculosMapa.ts`, `MapaOSM`, `mapScript.ts`.

Referências: frontend `src/hooks/useMobilidadeRio.ts`; `src/services/estruturaV2.ts`; `src/services/gpsHub.ts`; `src/services/veiculosMapa.ts`; `app/index.tsx:638-740`; `src/components/mapOSM/mapOSM.tsx:24-243`; `webview/mapScript.ts`.

Pendências: timeout/retry de estrutura, catálogo de inscrições, política local de stale, remoção por snapshot vazio e validação runtime dos contratos.
