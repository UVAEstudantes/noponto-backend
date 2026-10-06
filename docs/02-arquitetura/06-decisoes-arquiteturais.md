# NoPonto — decisões arquiteturais

**Finalidade:** relacionar problemas, soluções, evidências, benefícios e compromissos sem inventar motivações históricas.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Verificação:** decisões inferidas da implementação, salvo quando documentos históricos são citados.

## 1. PostgreSQL/PostGIS como base durável e geoespacial

- **Problema:** relacionar posições, linhas, geometrias e paradas com integridade e histórico.
- **Solução:** PostgreSQL para entidades/transações e PostGIS para Point/LineString, localização longitudinal e índices GIST.
- **Evidência:** `DbContext`, migrations e `GpsItinerarioRepository.*`.
- **Vantagens:** consultas set-based próximas aos dados; FKs/versionamento; uma base para estrutura e histórico.
- **Compromissos:** SQL/PostGIS especializado reduz portabilidade; planos/índices e crescimento precisam operação.
- **Alternativa fundamentada:** cálculo geoespacial em memória seria possível, mas exigiria carregar/sincronizar geometria; não há benchmark comparativo.

## 2. Redis para estado operacional

- **Problema:** snapshots frequentes, expiração e rejeição atômica de timestamp não combinam com leitura relacional repetida.
- **Solução:** chaves ativo/recente/linha, CAS, estado causal e streams no Redis.
- **Vantagens:** TTL e acesso de baixa latência; isolamento do estado efêmero.
- **Compromissos:** Redis vira dependência crítica do realtime; configuração sem persistência e `noeviction` exige capacidade; fonte durável continua no PostgreSQL.
- **Evolução:** explicitar recuperação, limites e métricas na Etapa 2.4.

## 3. Versionamento estrutural

- **Problema:** percursos mudam enquanto veículos podem estar viajando; parada física pode ocorrer mais de uma vez.
- **Solução:** `PadraoOperacional` estável, `PadraoVersao` imutável, ocorrências ordenadas e versão pinada na viagem.
- **Vantagens:** reprodutibilidade, circularidade e histórico coerente.
- **Compromissos:** mais entidades/joins e necessidade de publicação/reconciliação controlada.
- **História:** roadmap/contexto documentam a migração; código exclui o modelo mutável anterior.

## 4. Fontes GPS abstratas com primário e shadow

- **Problema:** providers têm contratos/qualidade diferentes e precisam ser comparados sem afetar operação.
- **Solução:** `IGpsSource`, resolver por modal, adapters normalizados e shadows.
- **Vantagens:** substituição/comparação isolada; hints preservados.
- **Compromissos:** shadow consome rede/processamento; não é fallback automático; promoção precisa política.

## 5. Polling de providers

- **Problema:** fontes externas oferecem consulta HTTP, não push controlado pelo NoPonto.
- **Solução:** loops com janela, overlap, catch-up, cadence, timeout e budget.
- **Vantagens:** controle de atualização e tolerância a atraso.
- **Compromissos:** carga periódica, duplicidade e compromisso frequência/custo. Valores são configuráveis.

## 6. SignalR por grupos de linha

- **Problema:** evitar polling mobile contínuo de todas as posições rodoviárias.
- **Solução:** inscrição/cancelamento e broadcast somente a grupos com assinantes.
- **Vantagens:** atualização direcionada e suporte a reconexão.
- **Compromissos:** contrato sem versão; reinscrição e escala multi-instância exigiriam coordenação/backplane não demonstrado.

## 7. Schedule-first ferroviário

- **Problema:** ausência de GPS contínuo não deve ser disfarçada nem impedir representação.
- **Solução:** schedules produzem runs esperadas; evidências ancoram tracker; estimador projeta posição e confiança; schedule-only pode existir.
- **Vantagens:** representação explícita de disponibilidade parcial.
- **Compromissos:** posição é inferência, dependente de grade/mapeamento; exige staleness e comunicação de confiança.

## 8. Processamento assíncrono e batch

- **Problema:** persistência/telemetria não devem alongar o hot path GPS indefinidamente.
- **Solução:** hosted services, channels bounded, Redis streams, batches, backpressure e retention.
- **Vantagens:** amortiza I/O e isola falhas não críticas.
- **Compromissos:** filas/backlogs/duplicidade precisam observabilidade; tudo compartilha o processo API.

## 9. ETA fail-open e migração shadow

- **Problema:** predição antiga pode ficar indisponível e seu target é semanticamente inadequado.
- **Solução:** timeout/cooldown legado sem bloquear GPS; V2 produz baseline/contexto/ground truth em pipeline separado, inicialmente shadow/canary.
- **Vantagens:** continuidade e avaliação antes de promoção.
- **Compromissos:** coexistência aumenta complexidade; hoje não há ETA V2 operacional.

## 10. MapLibre dentro de WebView

- **Problema:** renderizar mapa vetorial, camadas e animações com controle uniforme.
- **Solução:** HTML/JS MapLibre hospedado em WebView; ponte incremental.
- **Vantagens observáveis:** acesso ao ecossistema MapLibre GL e controle fino; separação `updateMap/updateRealtime/updateUser` reduz payload estrutural repetido.
- **Compromissos:** dois runtimes, serialização, CDN, debugging e lifecycle; `react-native-maps` instalado não participa.
- **Motivação histórica:** não documentada como comparação formal; adequação é inferência técnica.

## 11. Backend modular monolítico

- **Problema:** integrar domínios relacionados sem sobrecusto de deploy distribuído.
- **Solução:** um projeto/processo API com módulos e workers internos; dados externos em containers próprios.
- **Vantagens:** transações/composição simples e menor complexidade operacional.
- **Compromissos:** acoplamento de release e recursos; falha do processo afeta múltiplos workers. Não chamar de microsserviços.

## 12. Compatibilidade temporária

- **Problema:** backend V2 e frontend não migraram simultaneamente todos os contratos.
- **Solução:** fachada de mapa/detalhe e alias SignalR construídos sobre V2.
- **Vantagens:** migração incremental.
- **Compromissos:** dívida técnica e semântica ambígua de IDs; remoção deve ser coordenada.

## Arquitetura futura

Rotinas, rotas, notificações, metrô e ML supervisionado não possuem decisão arquitetural final. Podem reutilizar estrutura/eventos, mas grafo, critérios, contas, privacidade e serviços não devem ser antecipados.

## Limitações, referências e pendências

Não foram realizados benchmarks ou ADRs formais nesta etapa. Fontes: código, auditorias 14/15, roadmap e contexto. Pendente: converter decisões estáveis em ADRs, medir trade-offs e versionar contratos.
