# NoPonto — rotas multimodais e algoritmos candidatos

**Finalidade:** formular o planejamento multimodal, avaliar algoritmos e delimitar um MVP sem afirmar implementação.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** estrutura V2, schedules, ETA/realtime, PostGIS, requisitos RF-025/026 e literatura algorítmica consolidada. **Estado:** planejado; nenhuma busca de rota existe no produto.

## Problema do passageiro e entradas

Uma consulta procura sequências temporalmente possíveis entre origem e destino, não apenas o menor traçado. Localização atual é capturável mediante permissão; coordenadas digitadas/geocodificação, horário de partida/chegada, limites de caminhada, modais e preferências exigem UI/contratos novos. Acessibilidade, segurança e conveniência só podem influenciar a busca quando houver dados confiáveis, versionados e com cobertura conhecida.

| Entrada | Situação | Decisão necessária |
|---|---|---|
| origem/destino coordenados | derivável da localização ou seleção no mapa | geocodificação e privacidade |
| partida “agora” | disponível no dispositivo/servidor | timezone e relógio autoritativo |
| chegada desejada | nova implementação | busca reversa/algoritmo temporal |
| modais | catálogo existe | quais entram no MVP |
| caminhada máxima | nova preferência | rede pedonal e acessibilidade |
| custo/transferências/confiabilidade | parcialmente derivável | critérios e dados oficiais |

## Dados existentes e lacunas

A estrutura V2 fornece linhas, sentidos, padrões, versões, geometrias, ocorrências ordenadas e coordenadas de paradas. Schedules ferroviários fornecem runs/stops dependentes de calendário; realtime fornece observações rodoviárias e inferências ferroviárias; tarifas são parciais. PostGIS pode gerar candidatos próximos.

Ainda faltam uma rede explícita de transferências validadas, grafo pedonal, duração de caminhada, tempos de permanência/transferência, calendários de feriado completos, headways rodoviários confiáveis, regras tarifárias integradas, acessibilidade das conexões e metrô. Proximidade geográfica é apenas candidato: vias, barreiras, entrada da estação e segurança podem invalidá-la.

## Modelo de rede

Uma rede **espacial** representa conectividade física. Uma rede **temporal** associa uma aresta a partidas/chegadas ou a uma função de custo dependente do horário. Nós candidatos: ocorrências de parada, plataformas/estações e pontos de origem/destino. Arestas:

- viagem: ocorrência `i`→`i+1`, preservando padrão/versão/sentido;
- embarque/espera: disponibilidade do serviço no instante;
- desembarque e transferência: tempo mínimo e regras;
- caminhada: caminho pedonal validado, não linha reta cega;
- ligação origem/destino: acesso inicial/final.

O estado de busca pode incluir nó, tempo, número de embarques, custo e restrições. Em rede time-dependent FIFO, chegar mais tarde por uma mesma aresta não deve produzir chegada anterior de modo inconsistente; ETAs incertos exigem distribuição/faixa ou penalidade explícita. Não se deve somar ETA rodoviário, schedule e inferência ferroviária como valores de mesma confiança.

Tempo total = acesso a pé + espera + deslocamento + dwell + transferências/esperas + acesso final. Cada parcela deve indicar fonte, timestamp e incerteza. Realtime ajusta a expectativa; a topologia e o calendário continuam restringindo a possibilidade.

## Algoritmos candidatos

| Abordagem | Dados/horários | Transferências/multicritério | Custo e adequação ao NoPonto |
|---|---|---|---|
| Dijkstra | grafo ponderado; suporta expansão time-dependent | exige estado adicional; variantes multi-label | simples para MVP estático; perde eficiência/semântica se materializar todos os eventos |
| A* | igual ao Dijkstra + heurística admissível | mesmas extensões | reduz busca espacial; heurística temporal multimodal precisa não superestimar |
| grafo expandido/dependente do tempo | eventos ou funções por aresta | representa espera e disponibilidade | correto conceitualmente; expansão por evento pode consumir memória |
| CSA | conexões ordenadas por partida e footpaths | transferências por caminhos; extensões multicritério | eficiente em timetable; excelente para ferrovia/GTFS, mas ônibus sem grade exige modelo adicional |
| RAPTOR | rounds por número de transferências sobre rotas/stops | transferências são naturais; McRAPTOR cobre Pareto | apropriado a transporte público programado; requer timetable consistente e adaptação realtime |
| multi-label/Pareto | mantém rótulos não dominados | duração, custo, caminhada, transfers | explica alternativas; frontier pode crescer e requer limites |
| motor existente (OTP/Valhalla etc.) | normalmente GTFS + OSM e realtime padronizado | recursos maduros variam | reduz implementação, mas adiciona serviço/recursos e adaptação ao V2; avaliar licença/operação |

Complexidades indicativas: Dijkstra com heap `O((V+E) log V)`; A* tem o mesmo pior caso; CSA varre conexões relevantes aproximadamente linearmente; RAPTOR escala por rounds, rotas e paradas alcançadas. Esses limites não substituem benchmark no dataset real.

## Arquitetura candidata e MVP

MVP tecnicamente proporcional: pré-processar offline um snapshot versionado da rede; construir footpaths candidatos e validá-los; executar busca no backend sob demanda; começar com partida “agora”, caminhada + um conjunto pequeno de modais programados/estruturais, otimização principal por chegada e limite de transferências. Um Dijkstra time-dependent ou RAPTOR/CSA sobre timetable são experimentos razoáveis; a escolha depende de transformar ônibus em timetable/headway e da cobertura dos dados.

Separar: `GraphBuilder` offline, snapshot versionado, serviço de consulta, adaptador de realtime e explicador de itinerário. Cache deve incluir versão do grafo, bucket temporal e preferências; invalidar quando calendário/estrutura mudar. O frontend envia intenção e apresenta etapas; cálculo local aumentaria bundle, sincronização e risco de dados stale.

## Alternativas e multicritério

“Mais rápida”, “menos caminhada”, “menos transferências”, “menor custo” e “mais confiável” podem gerar conjunto de Pareto. Pontuação ponderada é mais simples, mas oculta trade-offs e pesos; ordenação lexicográfica é explicável; Pareto evita declarar um ótimo único, porém exige limitar alternativas semelhantes. Não há pesos aprovados.

Tarifa total precisa motor de regras com vigência, integração, benefício e janela; soma nominal é incorreta em muitos casos. Confiabilidade pode usar idade/fonte/cobertura, nunca fingir probabilidade sem calibração.

## Requisitos candidatos e critérios

- `REQUISITO_CANDIDATO ROTA-REDE`: gerar snapshot reproduzível e versionado. Aceite: mesmas fontes/versões produzem hash e conectividade equivalentes.
- `ROTA-TEMPO`: rejeitar conexão temporalmente impossível e respeitar sentido/calendário.
- `ROTA-TRANSFERENCIA`: usar apenas transferências validadas e informar caminhada/tempo mínimo.
- `ROTA-INCERTEZA`: marcar trechos programados, observados e estimados; não ocultar stale.
- `ROTA-ALTERNATIVAS`: retornar opções não dominadas ou regra de ordenação documentada.
- `ROTA-FALHA`: distinguir “sem rota” de “dados/provider indisponíveis”.

Validação futura: casos dourados, loops/circularidade, meia-noite, feriados, atraso, conexão perdida, geometria incompatível, comparação com cálculo manual e motor de referência; medir tempo de busca, memória, cobertura e correção, sem meta inventada.

## Limitações, riscos e decisões

Riscos: grafo incorreto, transferência impossível, explosão de rótulos, schedule incompleto, ETA enviesado, custo tarifário falso e carga no host limitado. Pré-processamento deve ser offline/periódico; consulta é por demanda; replanejamento só para viagens ativas, não para todas as rotinas continuamente.

Decisões pendentes: modais do MVP; timetable de ônibus; fonte pedonal; backend versus motor externo; critério primário; tratamento de tarifa e acessibilidade. Recomendação provisória: protótipo pequeno e avaliável antes de quatro modais, sem compromisso de defesa.

Relacionados: [estrutura V2](../04-dados/03-estrutura-v2-identidades-e-versionamento.md), [schedules](../03-funcionalidades/08-schedules-coleta-builder-e-importacao.md), [ETA](../03-funcionalidades/13-eta-visao-geral-e-fluxo-operacional.md) e [arquitetura futura](06-dependencias-tecnicas-e-arquitetura-futura.md).
