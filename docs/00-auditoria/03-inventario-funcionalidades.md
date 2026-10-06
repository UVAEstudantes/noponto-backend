# 03 — Inventário de funcionalidades

**Data:** 2026-10-06 · **Repositórios:** todos os componentes inventariados · **Estado:** misto, discriminado abaixo

| Domínio / função | Implementação | Validação | Implantação | Evidência e limite principal |
|---|---|---|---|---|
| Polling GPS ônibus/BRT (Zirix, BRT, Data.Rio) | implementado | testes existentes, não executados | produção confirmada | `GpsPollingService`, `GpsSppoCollectorService`, source adapters; dependência externa |
| Validação, deduplicação e correção temporal | implementado | suíte extensa não executada | produção confirmada | `GpsLeituraValidator`, `EstadoCausalPosicao*`, `CorrecaoTemporalPosicao*` |
| Matching rota/sentido/progresso/próxima parada | implementado | testes PostGIS/diferenciais existentes | produção confirmada, dados estruturais atuais vazios | `GpsEnriquecimentoService`, `GpsItinerarioRepository.*` |
| Snapshots Redis e histórico PostgreSQL | implementado | testes existentes | produção confirmada | `PosicaoVeiculoCacheRepository`, `HistoricoPassagens`, `EventosViagem` |
| SignalR GPS | implementado | não validado ponta a ponta | endpoint em produção | `GpsHub`; `src/services/gpsHub.ts` |
| Consulta de linhas/sentidos/padrões/paradas | implementado V2 | testes existentes | endpoints em produção | controllers `Estrutura*`; tabelas estruturais estavam vazias |
| Mapa, filtros, seleção e persistência | implementado | testes unitários pontuais não executados | frontend não verificado | `app/index.tsx`, MapLibre/WebView, AsyncStorage |
| Busca e detalhe de linhas | implementado | testes pontuais | frontend não verificado | `app/linhas.tsx`, `mapStructure.ts` |
| Eventos/previsões em paradas | implementado/parcial | testes existentes | endpoint em produção | `/paradas/{id}/eventos`; qualidade depende do matching |
| Schedule ferroviário versionado | implementado | testes existentes | produção confirmada | 1 versão, 23 padrões, 246 runs e 7.433 stops estimados |
| Scanner/evidências ferroviárias | implementado com flags | extensa suíte existente | componentes na API; flags exatas não verificadas | `TremRealtime/*` |
| Posição ferroviária estimada sem GPS | implementado | testes existentes | endpoint snapshot em produção | `RailPositionEstimator`, confiança/staleness |
| ETA legado via serviço Python | implementado no código | experimental | ML não implantado/ativo | `GpsEtaClient`; container `noponto_ml` parado |
| ETA V2 shadow/batch | implementado parcialmente | testes existentes | workers presentes; efetividade não verificada | `EtaV2Shadow`, batch/maintenance, tabela vazia |
| Telemetria para ML | implementado | testes existentes | produção confirmada | ~19,3 mi linhas estimadas; retenção/workers |
| Importação GTFS/ArcGIS | implementado como comandos/serviços | testes existentes | execução atual não verificada | `Services/GTFS/*`; partes legadas excluídas |
| Tarifas | leitura implementada | não validado | endpoint em produção, tabela vazia | `TarifasController` |
| Administração | arquivos existentes, excluídos da compilação backend | não validado | não implantado nesta API | exclusões explícitas no `.csproj` |

## Aplicativo

O frontend integra mapa, busca, linhas, sentidos, itinerários, paradas, marcadores rodoviários e ferroviários, seleção de camadas, próximos veículos, histórico de pesquisa, tema e persistência local. Favoritos/configuração existem como rotas, mas seu grau de completude funcional deve ser validado em dispositivo. Não foi encontrada implementação completa de roteamento multimodal.

## Futuro, sem alegação de entrega

Planejamento multimodal, rotinas inteligentes, comparação de trajetos, tarifas compostas, previsão de transferências, notificações, ocorrências de trânsito, DCRNN/GNN e expansão geográfica permanecem roadmap/possibilidades. Há fundações reutilizáveis — grafo estrutural, histórico, ETA e telemetria — mas isso não equivale às funcionalidades finais.

## Referências e pendências

`Program.cs:175-653`; `NoPonto.csproj` (itens `Compile Remove`); controllers; `app/index.tsx`; `app/linhas.tsx`; serviços GPS/TremRealtime. Validar ponta a ponta após recomposição dos dados estruturais.
