# GPS 4C.6 — localização da perda e limites da evidência

2026-10-13. **Perda localizada antes da seleção de ocorrências; causa de dados/matching ainda não demonstrada. Nenhuma correção funcional aplicada.** A janela4C.5 e o retorno são aceitos como confirmados pelo operador; não foram repetidos nem houve acesso ao homeserver/produção. Agregados recebidos são a fonte real desta análise, não uma leitura independente do diretório remoto.

## Denominadores

60 ciclos completos, sem duplicação/incompletude/malformação. Resultado e predecessor reconciliam exatamente: **34.560 avaliações** = 31.856 BUS + 2.704 BRT = 27.260 ItineraryChanged + 5.289 Updated + 2.011 NuloSemMatching. São chamadas/observações aceitas neste funil, não veículos ou viagens distintos, nem todos os itens recebidos do GTFS.

- ItineraryChanged:78,877%; Updated:15,304%; NuloSemMatching:5,819% dessas avaliações. NuloSemMatching é retorno sem versão/fração atual no serviço, não necessariamente falha de infraestrutura.
- BUS predecessor válido:28.008/31.856; ausente/inválido:3.848/31.856 (12,079%). Plausível:26.611/31.856; não é um denominador filtrado apenas por predecessores válidos.
- BRT predecessor válido:2/2.704; ausente/inválido:2.702/2.704 (99,926%); plausível:2/2.704. Ausente/inválido inclui campos ausentes, coordenadas inválidas ou timestamp não crescente. O contador não separa miss, expiração, JSON inválido e esses casos.
- ProjecaoMaior:5.126/5.289 comparações normais; significa fração final maior que a anterior, não distância suficiente para alcançar uma ocorrência. ProjecaoMenor159 e igual4 completam o denominador. Não mede movimento físico em metros nem avanço de cursor.
- ComAlvo:5.289 é presença da próxima ocorrência retornada pela consulta de transição; ZeroCruzamentos:5.289 é lista Ultrapassadas vazia. Selecao/commit de PassagemParada são estágios posteriores distintos.
- ProjecaoPinadaAdotada119 é adoção da projeção operacional no repositório, não todas as solicitações/projeções encontradas. Retry0 significa nenhum evento contado nesse contexto dos ciclos; não prova inexistência de pendências fora dele.

Definições: `GpsCicloPerformance.Operacional.cs` métodos RegistrarResultadoOperacional(57), RegistrarPredecessor(78), RegistrarDecisaoRegra(116), RegistrarRamo e RegistrarEventos. Serviço registra resultados em `ViagemObservadaService.AtualizarInternoAsync`(45–78); predecessor é contado depois do aceite GPS em `GpsPollingService.AtualizarCacheEViagemAsync`(1111–1130). Chamadas rejeitadas pelo timestamp/CAS não entram nesse denominador.

## ItineraryChanged: rejeição persistente, não reancoragem demonstrada

`ViagemOperacionalRepository.TentarAtualizarInternoAsync`(103–180) compara matching atual com a versão da viagem por identidade física `gps.Ordem`. Os três caminhos são:

1. Flag de mudança habilitada, candidato existente e evidência rejeitada/insuficiente:linha155. **Não explica a janela com flagfalse confirmada.** Não executar/ativar esse fluxo por hipótese.
2. Viagem Ativa/PossivelFim confiável, versão divergente e projeção operacional solicitada: infraestrutura falha retorna InfrastructureFailure; resultado não encontrado, versão diferente, fração inválida/regressiva ou comprimento inválido retorna Divergencia/ItineraryChanged(linhas159–171).
3. Viagem Ativa confiável ainda em versão divergente após o tratamento de projeção:linha180, inclusive projeção NaoSolicitada.

`Divergencia`(495) devolve a observação operacional anterior, sem escrever novo estado/cursor/timestamp e sem chamar Decidir. Repetir observações divergentes pode repetir ItineraryChanged indefinidamente; não implica27.260 trocas efetivas ou execuções novas. Contadores de baseline/mudança/adoção/proteção recuperada estão zerados na evidência fornecida: não há demonstração de reancoragem repetida nesta janela.

Matching atual: GTFS-RT normaliza veículo, crosswalk entrega CodigoLinha e hints de RouteId/TripId/DirectionId (`GtfsRealtimeGpsSource`95–123). BUS mantém ID normalizado; BRT usa prefixo BRT-, sem usar trip_id como chave física. `GpsObservationMapper.ToPosition`(150) coloca hints em ObservacaoEstrutural, não escolhe SentidoId/PadraoVersaoId. O enriquecedor escolhe versão por posição/bearing/código, continuidade/histerese e validação temporal; a query global considera versões atuais dos padrões dessa linha. Hint de direction_id não substitui esse matching.

Projeção operacional: contexto Ativa/PossivelFim confiável (`ContextoOperacional.PodeProjetar`); orçamento a partir do timestamp/progresso do estado. A query lê a versão pinada, inclusive versões não atuais, e exige padrão/sentido/linha/código compatíveis, comprimento positivo, segmento alcançável e proximidade máxima. Query individual: `GpsItinerarRepository.BuscarMatchingCombinadoAsync`, SQL227–265; set-based equivalente em `GpsItinerarioRepository.CombinedSetBased.cs`101–126. A elegibilidade final não admite regressão de fração na versão pinada. Mudança real de código/linha pode tornar a projeção inelegível; geometrias/sentidos divergentes também. Agregados não mostram qual condição ocorreu.

Memória `_padraoAtual` do enriquecedor e estado durável da viagem são referências diferentes. Reiniciar a API limpa a primeira, preserva a segunda. A coleta ocorreu após recriação diagnóstica; não assumir que o matching em memória tinha o mesmo aquecimento da API anterior.

## Por que movimento ainda pode resultar em zero cruzamentos

`PosicaoInicialTransicao`(ViagemOperacionalRepository356) usa PosicaoNaRotaConfirmada da mesma versão, salvo baseline/nova execução/proteção. Nas5.289 decisões fornecidas: Ativa, Normal, comparação estrutural válida, mesmo número de volta e sem wrap; não há sinal de baseline/adocao/candidato nos contadores. O ramo normal faz a consulta sobre a versão operacional, inclusive quando ela foi pinada.

`OcorrenciaParadaRepository.Sql`(32–38), ramo sem baseline/wrap, exige simultaneamente:

```
ocorrência pertence à versão consultada
posição atual >= posição anterior
ordem da ocorrência > última ordem do cursor
posição da ocorrência > posição anterior
posição da ocorrência <= posição atual
```

Próximo alvo é escolhido por ordem após o cursor(56–59), não é prova de que sua fração foi alcançada. Fração pode avançar pouco dentro do mesmo intervalo. Também é possível, como hipótese de dados, que um alvo após o cursor esteja espacialmente atrás da posição anterior ou nunca seja alcançado; os agregados não contêm frações/ordens para decidir.

`ViagemOperacionalRegra.DecidirInterno`(151–170) atualiza a fração e emite PassagemParada apenas para Ultrapassadas, salvo adoção. Sem essa lista, cursor permanece e nenhum evento chega ao CommitAsync/outbox. Nesta janela a falta de seleção já explica os commits zerados: não é evidência de falha de materialização. Os contadores não demonstram que o repositório deixou de selecionar uma ocorrência que satisfazia todos os predicados.

**Predecessor físico não é um gate universal dessa decisão normal:** teste novo produz PassagemParada com matching atual e transição válida mesmo sem TemHistorico/MatchingOperacionalPlausivel. Esses campos influenciam bearing/matching/provas de mudanças/circularidade, mas não justificam atribuir todos os zeros BRT diretamente à falta de predecessor.

## BRT: cache e cadências separados

`GpsPollingService`286–309 lê exclusivamente `veiculo:{ordem}:ativo`; MontarComHistorico(1202) copia coordenadas/timestamp apenas desse payload. GpsTimestampDeduplication.ParseActive/ReadAsync rejeita payload inválido e lê `veiculo:{ordem}:ts` quando falta ativo. Watermark é um número, não uma posição. Recente não entra como predecessor.

`PosicaoVeiculoPayloadWriter` Lua92–96 aplica TTL físico: ativo/recentes e watermark. GET/consultas e duplicatas não renovam TTL. A deduplicação ocorre antes do commit; `PosicaoVeiculoCacheRepository`142 usa max(TTLrecente,TTLativo) para watermark. Chaves são físicas, independentes de trip_id; prefixoBRT impede a colisão simples com BUS.

Configuração anteriormente confirmada no relatório16: pollingBUS15/BRT20, aquisiçãoGTFS30, ativo40/recente180. Isso não significa timestampGPSnovo a cada20/30s: `GpsBrtPollingGate` também pode reutilizar a fonte e GtfsRealtimeGpsClient limita aquisição. Feed com observação repetida aos30s e timestamp novo aos60s perde o ativo40 sem deixar de passar na deduplicação por watermark. A fixture reproduz isso usando MemoryDistributedCache e relógio controlado, sem Redis real.

**É mecanismo reproduzido, não causa comprovada dos2.702 casos.** Faltam gaps efetivos de timestamps/aceites e motivo de ausência do ativo. Não aumentado TTL nem usado recente como predecessor: isso mudaria política/semântica sem prova.

## Reprodução/validação local

Novo `NoPonto/5-Testes/GpsPassagens4C6Tests.cs`:14 casos, todos aprovados,780ms. Oito casos BUS/BRT exercitam repositório+serviço reais antes de qualquer conexão (dependênciasRedis/DBnulas) com divergência repetida/projeção ausente, inelegível, versão errada ou regressiva; comprovam estado antigo intacto e resultadoItineraryChanged sem decisão/commit. Outros casos demonstram movimento≠cruzamento, passagem normal sem predecessor físico, expiração de cache/dedup e identidade física separada dos hintsGTFS. Fixtures sintéticas explicitamente identificadas; não certificam trajetórias reais.

Regressões offline selecionadas:375 aprovadas, zero falhas/ignorados,26s; mais14novos = **389 casos distintos aprovados**. Seleção explícita: enriquecimento, deduplicação, GTFS-RT, contadores/logs, cursor/candidato, regra/codec de viagem, retry, serviço e pollingfontes/cadência. Nenhuma classe conectada selecionada. Compilação Release de testes --no-restore, cinco avisos preexistentes. Primeiro erro foi uso indevido de using em MemoryDistributedCache na fixture; corrigido antes da execução final. Nenhum teste de regressão funcional foi demonstrado falhando no código produtivo; estes são testes de caracterização para não confundir correlação e causa.

TRX/inventário de preservação: `D:/repositorio_github/NoPonto/gps-diagnostics-4c6-evidence-20261013`. Build de imagem/transferência/janela4C.5 não repetidos. Nenhuma API real iniciada ou banco operacional/produção acessado.

## Lacuna mínima para um patch fundamentado

Não solicitar outra janela4C.5 nem inventário amplo. Se já existirem artefatos locais/exports autorizados, bastam exemplos correlacionados pelo mesmo veículo/observação:

1. **Um casoItineraryChanged:** identidade linha/sentido/padrão/versão da viagem antiga e matching atual; posição/timestamp anteriores; ProjecaoOperacional.Status e, quando encontrada, versão/fração/distância/comprimento; código da linha atual versus código pinado. Isso separa troca real, escolha de sentido/versão, inelegibilidade geométrica e regressão.
2. **Um casoNormal com avanço sem cruzamento:** parâmetros reais da consulta (versão,baseline,anterior,atual,últimaID/ordem,topologia/volta), alvoID/ordem/fração e sequência ordenada daquela versão. Dois/ três pontos consecutivos bastam. Se existir ocorrência satisfazendo os predicados e seleção vazia, reproduzir essa fixture; se não existir, não corrigir a SQL por hipótese. Matching.ProximaOcorrencia não deve ser confundida com o alvo operacional da consulta.
3. **Um parBRT aceito consecutivo:** identidade física normalizada igual, timestampsGPS/aceite, origem do predecessor, campos disponíveis/validade e eventual rejeição do payloadativo. Isso demonstra ou exclui intervalo>TTL/cadência/parse/identidade. Não inferir expiração a partir do contador agregado.

Os seis logs exportados não incluem esses IDs/frações; somas não permitem reconstruí-los. Não associar por proximidade temporal nem usar labels não auditados. Caso esses artefatos não existam, a próxima ação depende de autorização separada para obtenção pontual dessa evidência; não implementada nova instrumentação nesta rodada.

**Entrega:** localização causal do estágio, ramos e reproduções do mecanismo concluídas; correção funcional ainda bloqueada pela lacuna de evidência acima. Modificados somente este relatório, um arquivo de testes novo e acréscimo no histórico. Instrumentação anterior, demais trabalhos/worktrees, ETA/ML, regras/gates/timestamps/dados preservados. Sem commit/push/merge/deploy/migration ou relaxamento de proteções.
