# NoPonto — Auditoria complementar de SignalR, WebSocket e ngrok

Data: 13/11/2026. **Somente investigação. Nenhuma otimização implementada.** Complementa [AUDITORIA_NGROK_NOPONTO.md](AUDITORIA_NGROK_NOPONTO.md), lido integralmente. O único arquivo criado nesta etapa é este relatório; `HISTORICO.md` não foi alterado.

Referências: **B/** = `NoPonto/`; **F/** = `../noponto-frontend/NoPonto/`. Backend local `d491e15`; frontend `3062d55`. As referências de linha são destes checkouts. A API remota continua na imagem `cbddab780f8f`, iniciada em **12/11/2026 23:57:55,856 UTC**, sem reinícios; não há revisão de fonte comprovada na imagem. O histórico do agente abrange também versões anteriores.

Classificações: **Confirmado**, por código ou medição identificada; **Provável**, por indícios; **Hipótese**, cenário a validar; **Não mensurável com a instrumentação atual**, quando não existe observação suficiente. Código confirmado não equivale a execução comprovada em determinado APK.

Unidades: MB = 1.000.000 bytes; GB = 1.000 MB; KiB = 1.024 bytes; MiB = 1.048.576 bytes. As duas leituras manuais do painel têm arredondamento de interface.

## 1. Resumo executivo

1. **Confirmado: agora existem duas medições de saída de conexões associadas ao hub.** O desenvolvedor leu no painel duas conexões TLS, cada uma com exatamente um request vinculado a `GET /hub/gps`: **3,02 MiB em 250,008 s** e **717,42 KiB em 62,065 s**. São aproximadamente **45,60 e 42,61 MB/h de conexão**, respectivamente. Os horários coincidem com upgrades 101 na captura local. Isso comprova transferência relevante no canal persistente, sem atribuí-la a mensagens/eventos específicos. Duas sessões curtas de desenvolvimento não estimam a média de todos os usuários.
2. **Confirmado: os gigantescos contadores internos não são entrega ao ngrok.** Entre **21:59:00,824 e 22:10:00,819 UTC**, houve **26.095 publicações unificadas e 468.920.435 bytes de argumentos JSON estimados**, além de presença física. O agente permaneceu em 7.072 requests, sem conexões abertas nas leituras. O acumulado unificado chegou a **35,86 GB**, não a 35,86 GB transferidos. Não há evidência de vazamento de rede nesse contador.
3. **Confirmado no código: o mesmo ônibus pode chegar pelos contratos operacional e unificado.** O unificado já contém um objeto `operational`, além de posição física, associação de linha e validade; o outro stream entrega um DTO operacional de 28 campos. Há sobreposição real, mas os contratos têm funções distintas e fallback necessário. Com duas linhas de payloads semelhantes aos de 838/855 e cadência compatível com os logs, a soma é da ordem de **42–47 MB/h**, próxima das duas conexões medidas. **Atribuição causal a esses grupos permanece hipótese.**
4. **Confirmado no hop agente–origem: não houve compressão WebSocket negociada.** Todos os **262** upgrades capturados pedem `permessage-deflate`; nenhum 101 devolve essa extensão. A configuração atual usa JSON SignalR sobre uma conexão compartilhada, sem MessagePack. Gzip HTTP não resolve esses frames. Compressão WS merece experimento futuro compatível com .NET 9 e Android, não promessa de economia imediata.
5. **Prioridade para o beta: demanda visível e orçamento de uso, antes de protocolos complexos.** O dono unificado de mapas não recebe foco; abas montadas podem manter grupos de linhas ocultas. Donos da mesma linha são unidos, portanto dois componentes não significam duas entregas do mesmo grupo. Preservar essa proteção. **Três a cinco participantes com sessões curtas podem caber**, dependendo da mistura ônibus/trem, do uso do desenvolvedor e dos bytes reais por sessão. Dez participantes não significam dez simultâneos, mas uso frequente de trem ou sessões com 45 MB/h tornam a franquia restritiva.

**O que ainda não fechou:** não há total TCP do endpoint na mesma janela dos 48,41 MB HTTP, nem atualização do total mensal de 183,83 MB informado na primeira auditoria. Assim, continua impossível reconciliar a conta inteira ou ordenar todos os eventos por bytes efetivamente entregues. O avanço é uma prova real de transferência em duas conexões, ausência de compressão no hop inspecionado e demonstração de publicação interna sem trânsito público.

**Recomendação:** continuar com ngrok, iniciar o beta em grupo pequeno e sessões limitadas, medir conexões encerradas e corrigir futuramente a demanda invisível. Não migrar infraestrutura nem implementar deltas/MessagePack como requisito inicial do TCC.

## 2. Dados efetivamente obtidos do ngrok

### 2.1 Acesso disponível nesta execução

**Confirmado:** nenhuma ferramenta contendo ngrok foi exposta no inventário desta sessão. Descoberta MCP retorna `unknown MCP server 'ngrok'`. O comando de leitura `codex mcp get ngrok --json`, com saída restrita a campos seguros, confirma integração habilitada, transporte `streamable_http`, URL oficial `https://mcp.ngrok.com/mcp`. Isso não concede ao executor ferramentas ausentes, nem comprova autenticação utilizável por ele.

O acesso ao navegador também não está disponível: inventário sem browsers e erro do host nativo. Não houve reconfiguração, instalação, nova autenticação, extração de OAuth ou chamada manual com credencial. A alternativa foi SSH existente e GETs em **127.0.0.1:4040**, exclusivamente de observabilidade. O usuário complementou a investigação com leitura manual do painel.

### 2.2 Leituras do agente

| Evidência | Resultado | Janela / origem |
|---|---:|---|
| Agente | 3.39.1, online | API local, 13/11 22:07 UTC |
| Endpoint deste agente | 1 HTTPS, nome `api`, upstream `http://localhost:8080` | `/api/endpoints`; não inventaria outros agentes da conta |
| Requests acumulados | **7.072**, sem incremento | 18:26 -03 da primeira auditoria; 22:07:51, 22:10:15 e 22:12:46 UTC desta etapa |
| Conexões acumuladas / abertas | **1.278 / 0** | `/api/tunnels`, mesmas leituras; não são usuários nem mensagens |
| Capturas retidas | **6.673**, inalteradas | 07/11 15:10:41 a 13/11 16:18:20, horário -03 |
| Negociações / upgrades | **276 / 262** | Histórico retido inteiro; POST 200 / GET 101 |
| Campos da captura | `id`, `start`, `duration`, request/response e metadados de túnel | Não há Connection ID TCP, fechamento ou frames WS |
| Transferência mensal atual | **Não mensurável com a instrumentação atual** | 183,83 MB continua sendo referência anterior do usuário |

A raiz `/api/` anuncia túneis, requests HTTP, status e atualização; a documentação da API local não oferece o histórico TCP do painel. Não foram tentados endpoints privados da conta ou APIs de escrita. A interface local e a observabilidade de nuvem têm capacidades diferentes. [API oficial do agente](https://ngrok.com/docs/gateway/agent/api), [inspector local](https://ngrok.com/docs/gateway/agent/web-inspection-interface).

### 2.3 Traffic Inspector TCP: caminho correto para reduzir a lacuna

**Confirmado na documentação atual:** a aba TCP inclui conexões HTTP/HTTPS; oferece Connection ID, requests associados, duração, bytes recebidos/enviados, resultado e gráfico cumulativo. HTTP/HTTPS aparecem após o fechamento; a lista aberta é limitada a TCP/TLS. Free retém 24 h. Uma conexão pode carregar vários requests: **não somar seu total aos bytes HTTP já incluídos nela**. [Traffic Inspector TCP Connections](https://ngrok.com/docs/obs/traffic-inspection/tcp-connections).

As duas conexões fornecidas pelo usuário utilizam exatamente essa correlação. Não foi obtida a lista completa da janela, com Bytes Out de todas as conexões. A API local não permite recriar essa lista. A retenção de seis dias do inspector local não significa seis dias disponíveis na nuvem.

### 2.4 Conexões lidas manualmente pelo usuário

| Identificador deste relatório | Início → encerramento no painel | Duração | Requests vinculados | Bytes In | Bytes Out | Saída equivalente |
|---|---|---:|---:|---:|---:|---:|
| C1 | 13/11 15:43:33 → 15:47:43 | 250,008 s | 1: `GET /hub/gps` | 4,11 KiB | **3,02 MiB ≈ 3,167 MB** | **43,49 MiB/h ≈ 45,60 MB/h** |
| C2 | 13/11 16:17:08 → 16:18:10 | 62,065 s | 1: `GET /hub/gps` | 2,30 KiB | **717,42 KiB ≈ 0,735 MB** | **40,64 MiB/h ≈ 42,61 MB/h** |

**Confirmado, fonte manual declarada:** valores de Connection Details e Requests comunicados pelo usuário nesta investigação; não foi possível reler esses bytes pela ferramenta. **Confirmado independentemente:** há exatamente um GET 101 no agente em cada horário inicial indicado. O painel não veio com fuso explícito; a correspondência exata com timestamps `-03:00` do agente sustenta o alinhamento, classificado como **Provável**. São 18:43/19:17 UTC sob essa premissa.

Fórmula: `MB/h = BytesOut × 3.600 / duração_em_segundos / 1.000.000`. A soma aproximada das duas saídas é **3,90 MB** em conexões diferentes. Ela não é o consumo mensal nem o total de argumentos SignalR. Inclui overhead observado pelo ngrok para essas conexões. Um único request vinculado torna forte a atribuição ao canal WS da conexão, sem tráfego REST adicional nela; ainda há handshake, controle, framing e possíveis efeitos do transporte. Não se extrai daí um custo por evento.

## 3. Análise das conexões WebSocket

### 3.1 Negociação, protocolo e contagem

**Confirmado no histórico retido:** os 538 requests do hub são 276 negociações 200 e 262 upgrades 101. Todas as negociações anunciam WebSockets, SSE e LongPolling; todos os GETs de transporte capturados efetivamente fizeram upgrade. Anunciar fallback não significa usá-lo. Não foi encontrada sequência de long polling ou SSE nessa amostra.

Comparando `connectionToken` da resposta negotiate com `id` do GET apenas em memória, sem registrar tokens, foram pareados **261 upgrades**. A demora negotiate → upgrade foi **0–2 s, mediana 1 s**, limitada à resolução de segundos de `start`. Restam **15 negociações sem upgrade correspondente retido** e um upgrade sem negociação retida. Esse upgrade é justamente o primeiro registro da retenção, **07/11 15:10:41 -03**, compatível com negociação anterior ao recorte. Logo, `276−262=14` não é uma taxa de falha demonstrada.

Depois do upgrade, eventos, invocações de grupo, respostas às invocações e pings trafegam na mesma conexão; não são novos HTTP requests. O fluxo atual normalmente custa negotiate + GET de abertura, aproximadamente **2 requests por estabelecimento**, sujeitos a falhas/novas tentativas. A contagem faturada específica da conta não foi consultada. Não aplicar a cota TCP/TLS de forma automática às mensagens de um endpoint HTTPS. [Suporte WebSocket do ngrok](https://ngrok.com/docs/gateway/endpoints/http).

### 3.2 Reconexões: o que os números permitem dizer

| Dia (-03), recorte retido | Negociações | Upgrades |
|---|---:|---:|
| 07/11, parcial | 21 | 18 |
| 08/11 | 54 | 45 |
| 09/11 | 35 | 35 |
| 10/11 | 70 | 69 |
| 11/11 | 6 | 6 |
| 12/11 | 41 | 41 |
| 13/11, até 16:18 | 49 | 48 |
| **Total** | **276** | **262** |

Há **14 aberturas excedentes no mesmo segundo**, 52 intervalos entre aberturas adjacentes de até 5 s e 82 de até 30 s, incluindo os simultâneos. Mediana entre aberturas adjacentes: 99 s; pico: 12 upgrades em 09/11 às 17h. **Esses são intervalos entre aberturas de todas as origens, não durações de sessões.** Não se pode converter 99 s em vida média de conexão. Somente 68 upgrades retidos são posteriores ao início do processo atual da API; o restante não pode ser comparado diretamente aos seus contadores acumulados.

O campo HTTP `duration` tem mediana **1,404 ms** nos upgrades: mede a resposta 101 observada, não os minutos de C1/C2. As durações reais comprovadas nesta etapa são as duas fornecidas pelo painel. As métricas `conns` do agente também têm percentis agregados, mas misturam conexões REST e WS; não oferecem encerramento individual nem causa e não foram usadas para estimar vida de sessão SignalR.

**Provável:** testes do desenvolvedor, bundle recarregado, abertura/fechamento e background/foreground contribuem para a quantidade. **Hipótese:** reconexões inesperadas por rede ou duplicação de runtimes. Não existe identificação de versão/aparelho nem motivo de fechamento que permita dividir as 262 aberturas entre essas causas. Os logs recentes não contêm mensagens de abertura, fechamento, reset de socket ou close code; a rotação já não cobre as duas sessões manuais.

O SDK instalado é `@microsoft/signalr` **10.0.0**. `gpsHub.ts:158–161` ativa reconnect padrão; `node_modules/@microsoft/signalr/src/DefaultReconnectPolicy.ts:7` define tentativas em 0, 2, 10 e 30 s, depois termina. Falha inicial de `start()` não recebe automaticamente esse mesmo ciclo (`gpsHub.ts:224–229`). Não há prova de laço infinito de reconnect no código atual.

### 3.3 Cruzamento das sessões C1/C2

Comparação passiva com os requests retidos da mesma origem de rede, mantendo IP somente em memória. Essa origem pode conter mais de um runtime/aparelho. Intervalos usados para contar REST são **[início, encerramento)**, sem incluir nova abertura no instante final.

| Sessão | Evidência de contexto | O que ela não comprova |
|---|---|---|
| C1 | 60 requests da mesma origem no intervalo; 11 eventos de três URLs de parada. Estrutura da linha 10 às 15:43:28 e de 838/855 às 15:43:44–45. Outro upgrade ocorreu 5 s antes de C1 | Não comprova coexistência dos dois sockets: falta encerramento do anterior. Estrutura não prova inscrição no grupo |
| C2 | Nove requests da mesma origem no intervalo; quatro eventos de uma parada. Estrutura de 838/855 nos dois minutos anteriores; upgrades anteriores às 16:15:51 e 16:16:02 | Não identifica troca de tela, Fast Refresh ou motivo da nova conexão |

O GET às **16:18:10**, no mesmo segundo do encerramento informado de C2, foi excluído do recorte de segundos e é compatível com substituição da conexão. A resolução disponível não determina sobreposição subsegundo. O header `X-SignalR-User-Agent` dessas duas aberturas identifica SDK 10.0.0; seu texto “Browser/Unknown OS” é do cliente JS e **não comprova navegador, release ou Android específico**.

As consultas REST desse contexto usam outras conexões: C1/C2 têm um único request no painel. Seus corpos REST não devem ser subtraídos dos Bytes Out dessas duas conexões WS, nem acrescentados novamente a um total TCP que já englobe tudo.

## 4. Ranking dos eventos SignalR por frequência

### 4.1 O que está sendo medido

`B/1-API/Hubs/SignalRTraffic.cs:46–52,80–135` observa `SendGroupAsync` e `SendConnectionAsync`. Serializa **somente o argumento de payload** com as opções JSON do hub, chama o manager real e registra sucesso da chamada. Não observa bytes no socket. `SuccessfulEstimatedPayloadBytes` significa que o método concluiu sem exceção, mesmo com grupo vazio ou conexão já ausente.

O reporter (`:186–195`) emite acumulados desde o processo e `WindowPublications` desde o resumo anterior. Não somar acumulados de minutos sucessivos. O limite padrão de 128 chaves produz bucket `overflow` por evento/escopo; não são 128 linhas máximas do serviço. Não há bytes máximos por mensagem no contrato de métricas.

### 4.2 Acumulado válido até 13/11 22:10:00,819 UTC

Origem: 12 resumos existentes do Docker, janela 21:59–22:10 UTC; acumulado desde o processo iniciado em 12/11 23:57:55,856 UTC, cerca de 22h12. Os timestamps de `docker logs --timestamps` apareciam também entre fragmentos da mesma linha longa. Foram removidos apenas esses prefixos na análise em memória; todos os 12 JSONs finais passaram validação de chaves/eventos. Resultados preliminares com fragmentos corrompidos foram descartados.

| Ordem | Evento / escopo | Publicações acumuladas | Publicações na janela de 11 min | Taxa nessa janela |
|---|---|---:|---:|---:|
| 1 | `LocalizacaoUnificadaAtualizada` / group | **2.370.079** | **26.095** | ~2.372/min, soma de linhas |
| 2 | `PresencaFisicaAtualizada` / group | **33.423** | **363** | ~33/min, soma de instalações |
| 3 | `PosicaoAtualizada` / group | **552** | 0 | 0/min nessa janela ociosa |
| 4 | `LocalizacaoUnificadaAtualizada` / caller | **191** | 0 | Por inscrição inicial; sem nova inscrição nessa janela |
| 5 | `LinhaEsvaziada` / group | **10** | 0 | Por geração/linha vazia; não timer próprio |

Não apareceu registro caller de presença física nesse acumulado. Isso não demonstra ausência em toda a semana. Todos esses contadores tinham falhas de envio/medição iguais a zero, sem prova de entrega ou recepção pelo app.

### 4.3 Produtores, frequência e grupos

| Evento | Produtor e destino | Gatilho / natureza |
|---|---|---|
| `PosicaoAtualizada` | `GpsSignalRPublisher.PublicarAsync`, `:24,53–89`; grupo `linha:{CODIGO}` (`GpsHub.cs:95`) | Ciclo GPS configurado em 15 s, condicionado a assinaturas. Lista completa válida da linha, incluindo posições recentes; não delta e não uma mensagem por veículo |
| `LinhaEsvaziada` | Mesmo publisher, `:73–97`, mesmo grupo | Vazio uma vez por geração de inscrição; nova geração permite nova invalidação. Falha conserva possibilidade de retry |
| `LocalizacaoUnificadaAtualizada` | `CanonicalPhysicalLocation.PublishAsync:178–194`; grupo `gps:physical:v1:{codigo}` | Após commits que afetam linhas e expiração; snapshot completo por linha, sem verificar assinantes |
| Mesmo evento / caller | `GpsHub.InscreverLocalizacaoUnificada:20–24` | Adiciona ao grupo e envia snapshot inicial à conexão chamadora; reinscrição pode repetir bootstrap |
| `PresencaFisicaAtualizada` | `PhysicalPresenceMvp:234–310`; `presenca-fisica:{stopId}` | Após lote de aquisição/projeção, instalações configuradas e alternativa de linha; snapshot completo, inclusive vazio |
| Mesmo evento / caller | `GpsHub.InscreverPresencaFisica:99–106` | Snapshot inicial; se instalação indisponível, resposta vazia. Nem toda inscrição pedida entra em grupo válido |

**Confirmado no ambiente em 22:11 UTC:** PhysicalPresence enabled/publish, estado/publicação canônicos, unificado e publicação global habilitados; cadence 30 s; 460 entradas de coorte e três instalações em variáveis. Isso não significa 460 grupos com clientes. O pipeline agrega fontes/lotes e mudanças; 30 s de aquisição não é um teto de duas mensagens por minuto por linha.

Em outra janela, **22:08:00,822–22:12:00,818 UTC**, as 128 chaves de grupo unificado individualizadas tiveram taxa mediana **6,875/min**, mínima zero e máxima **9/min**. É amostra do subconjunto antes de `overflow`, não distribuição de todas as 460 linhas e não taxa de entrega por cliente.

O código legado `UnifiedLocation.DispatchAsync:354–358` também publica snapshots, mas `ObserveAsync:192–204` desvia para canônico quando a publicação canônica está habilitada. **Não somar automaticamente esses dois caminhos como broadcasts concorrentes.** `TremSimulacaoWorker` contém outro send legado, porém está excluído do projeto por `NoPonto.csproj:66`, sem registro ativo em Program; não é prova de stream ferroviário utilizado hoje.

## 5. Ranking dos eventos por volume estimado

### 5.1 Bytes lógicos, mesma referência de 22:10 UTC

| Ordem por bytes | Evento / escopo | Bytes estimados acumulados | Média por publicação | Veículos médios / máximo | Bytes por ocorrência de veículo* |
|---|---|---:|---:|---:|---:|
| 1 | Unificado / group | **35.856.510.769** | **15.128,83** | 9,964 / 45 | ~1.518 |
| 2 | Presença física / group | **19.680.645** | **588,84** | 1,090 / 11 | ~540 |
| 3 | Operacional / group | **6.044.776** | **10.950,68** | 9,895 / 20 | ~1.107 |
| 4 | Unificado / caller | **4.487.965** | **23.497,20** | 15,188 / 23 | ~1.547 |
| 5 | Linha vazia / group | **214** | **21,40** | 0 / 0 | Não aplicável |

\* `soma de bytes / soma de veículos nas mensagens`, incluindo overhead dos snapshots. Um veículo repetido mil vezes conta mil ocorrências; isso não é tamanho isolado de DTO nem veículos únicos.

**Não mensurável com a instrumentação atual:** tamanho máximo de publicação ou frame. Há `VehiclesMax`, mas não `PayloadBytesMax`. Entre os 128 grupos individualizados, a média cumulativa por grupo variou de **1,86 kB a 63,06 kB**, mediana **15,93 kB** no resumo de 22:10. O extremo de média não é máximo de mensagem. `overflow` concentra 1.397.484 das 2.370.079 publicações unificadas (~59%); os três outros eventos de grupo e unificado caller também estavam agregados nele. Não é possível identificar o código das linhas por esses pseudônimos HMAC.

Na janela comum de 11 min, os argumentos unificados somaram **468,920 MB**, presença física **0,253 MB** e os demais zero. Esses são números de processamento/publicação, não ranking de banda pública. O ranking por entrega pode ser diferente: uma linha muito assinada pesa mais que centenas de grupos vazios.

### 5.2 Framing e codificação

**Confirmado no cliente atual:** `HubConnectionBuilder.ts:255` usa `JsonHubProtocol` por padrão; `JsonHubProtocol.ts:13–23,89` declara formato textual e serializa JSON; `TextMessageFormat.ts:7–11` acrescenta separador **0x1E**. O backend registra `AddSignalR()` sem protocolo binário (`Program.cs:579–580`). O framing esperado é:

```text
{"type":1,"target":"LocalizacaoUnificadaAtualizada","arguments":[<snapshot>]} + 0x1E
```

Isso é codificação estabelecida pelo código/SDK, não frame capturado. Handshake de protocolo, pings, completions de `invoke`, eventual fragmentação e fechamento não estão nas métricas do argumento. Para uma invocation simples compacta com um argumento, o envelope acima adiciona **68 bytes** ao payload unificado; os outros nomes adicionam 62/55/52 bytes para presença/posição/vazio. Opções, headers de mensagem e versões podem mudar esse custo. [Especificação oficial SignalR](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md?plain=1).

WebSocket acrescenta seu próprio framing e TLS/TCP têm overhead separado. Um send SignalR não equivale obrigatoriamente a um frame TCP/WS único. Não foi reconstruída essa camada a partir do contador JSON. [RFC 6455](https://datatracker.ietf.org/doc/html/rfc6455).

### 5.3 Compressão e peso dos campos

**Confirmado no hop inspecionado:** 262 pedidos de `permessage-deflate`, zero aceites nos 101; zero `Sec-WebSocket-Protocol` de subprotocolo, o que é compatível com handshake SignalR no conteúdo. Não há prova independente de transformação específica da borda até o cliente. O transporte .NET 9 de referência aceita WebSocket sem habilitar compressão nesse caminho; não basta instalar middleware gzip. [Transporte SignalR do ASP.NET Core 9](https://raw.githubusercontent.com/dotnet/aspnetcore/v9.0.0/src/SignalR/common/Http.Connections/src/Internal/Transports/WebSocketsServerTransport.cs).

Não foi executado novo GET de snapshot. Reutilizando as **duas medições internas da auditoria anterior**, de outro instante:

| Linha / veículos | JSON HTTP | Subobjetos `operational` aproximados | gzip offline do snapshot |
|---|---:|---:|---:|
| 838 / 20 | 36.714 bytes | 20.133 bytes (~55%) | 5.053 bytes |
| 855 / 23 | 40.323 bytes | 21.233 bytes (~53%) | 5.601 bytes |

Esses tamanhos não são frames de C1/C2. Opções JSON HTTP/SignalR podem diferir. Gzip offline sugere alta repetição, mas **não mede ganho de permessage-deflate**: algoritmo, janela, contexto, framing e CPU diferem. O objeto operacional é o único bloco cujo peso foi isolado anteriormente; não há medição em bytes por campo individual.

Campos repetidos incluem GUIDs de linha/sentido/padrão, nomes de parada, códigos, timestamps, chaves JSON e partes de contexto operacional. `FrontendLegacyDtos.cs:34–54` duplica `PadraoVersaoId` em `ItinerarioId`. Coordenadas, velocidade, status e evidência temporal variam; geometria de itinerário não está sendo enviada nesses snapshots a cada tick. `PosicaoApiDto.cs:159–180` e outros atributos `JsonIgnore` já excluem metadados internos: não tratá-los como peso público.

### 5.4 Que combinação explica 42,6–45,6 MB/h?

**Hipótese quantitativa, não atribuição:** suponha que uma conexão receba unificados das duas linhas de tamanho semelhante às amostras 838/855, mais posição operacional de ambas. Com 8 publicações unificadas/min e operacional a cada 15 s:

```text
Unificado: (36.714 + 40.323) × 8 × 60 = 36,978 MB/h
Operacional: 2 × 10.950,68 × 4 × 60 = 5,256 MB/h
Soma de argumentos: 42,234 MB/h, antes de framing/controle
Com 9 unificados/min: 46,856 MB/h de argumentos na mesma hipótese
```

A ordem de grandeza é compatível com C1/C2. Há consultas de estrutura 838/855 nesses contextos e cadências individuais de até nove/min nos logs posteriores. **Faltam grupos e mensagens realmente recebidos por essas duas conexões** para confirmar a hipótese; médias de outro horário/linha não são prova. Outras combinações, linha com muitos veículos, presença física habilitada ou mudança de seleção também podem explicar o volume.

Não foi demonstrado que a taxa resulte de dois sockets duplicados: os bytes de C1 e C2 pertencem a conexões individuais. Vários streams dentro de um único socket já podem explicar volume alto.

## 6. Quantidade de destinatários, quando mensurável

| Camada | O que sabemos | O que permanece desconhecido |
|---|---|---|
| Publicações produzidas | Contagem e argumento JSON por evento/escopo | Quantas chegaram ao cliente |
| Grupo operacional | Só é processado quando consta em `GpsHub.AssinaturasAtivas`; manager rastreia linhas/conexões | Número histórico de membros em cada send; corrida com desconexão |
| Grupo unificado/físico | Publicador não verifica membros; sucesso não exige membro | Fração histórica de sends a grupos vazios e fan-out real |
| Caller | Intenção de enviar a uma conexão, snapshot inicial | Se ainda existia, se concluiu entrega e se era pública/interna |
| TCP C1/C2 | Duas conexões diferentes, um request vinculado cada, Bytes Out informados | Eventos/grupos e bytes por evento dentro delas |
| Billing | Franquias públicas conhecidas | Transferência mensal atual por endpoint/canal e ajustes de contabilização |

**Evidência passiva de ausência de trânsito público:** o mesmo agente permaneceu com 7.072 requests e gauge zero desde a leitura da primeira auditoria às 21:26 UTC até as leituras posteriores a 22:10. Serviço/PID preservados. Sem conexão inicial aberta e sem nova abertura HTTP, as publicações da janela 21:59–22:10 não representam novas entregas WSS por esse endpoint, conforme os contadores do agente. Isso não prova que não existia consumidor interno nem constitui aferição independente de faturamento.

O manager padrão retorna tarefa concluída para grupo inexistente e não escreve em socket; adicionar a mesma conexão duas vezes ao mesmo grupo não duplica a membresia. A instrumentação do NoPonto executa a serialização de contagem antes dessa verificação do manager. Logo, pode medir bytes que nem seriam serializados pelo transporte sem ouvintes. [DefaultHubLifetimeManager do ASP.NET Core 9](https://raw.githubusercontent.com/dotnet/aspnetcore/v9.0.0/src/SignalR/server/Core/src/DefaultHubLifetimeManager.cs).

Modelo correto: `bytes lógicos enviados aos clientes ≈ Σ(payload + envelope) × destinatários efetivos de cada publicação`. Mesmo esse produto não prova recebimento Android nem Bytes Out faturados. É necessário separar conexões públicas/internas, framing, compressão e escrita real. Nunca multiplicar 35,86 GB pelo número de participantes do beta.

**Menor instrumentação futura útil, sem implementação:** manter membresia por conexão/grupo no manager e contar publicação sem destinatário, destinatários e bytes do protocolo despachados, por evento; usar identificador efêmero de sessão/build sem token ou localização. Registrar abertura/fechamento, causa e transporte. Contagem no manager ainda é estimativa de envio; a medição de escrita por conexão e o TCP/billing da borda fecham os outros níveis. Não é necessário payload integral, trace por veículo ou aumentar `MaxGroups` indiscriminadamente.

## 7. Possíveis duplicações e desperdícios

| Achado | Classificação | Consequência real / limite |
|---|---|---|
| Operacional + unificado para a mesma linha | **Confirmado no código** | Parte da informação trafega em dois contratos. Não foi medida a parcela duplicada de C1/C2 |
| `operational` completo dentro do unificado | **Confirmado** | ~53–55% dos dois snapshots HTTP anteriores; vários campos são necessários, não economia integral disponível |
| Dois mapas/consumidores pedindo a mesma linha | **Confirmado: há união de donos** | Não gera uma cópia de grupo por dono no mesmo socket. Pode duplicar processamento JS, que não é banda ngrok |
| Mapa oculto conserva linhas/grupos unificados | **Confirmado no código; provável impacto em Android** | Pode manter demanda sem apresentação. Mesma linha ainda usada por outro dono não permite poupar toda sua banda |
| Presença física global na Home | **Condicional a flag do APK** | Inscreve quatro IDs de instalação/alternativa, não apenas parada próxima; pode existir demanda sem seleção de linha |
| Snapshot inicial e próximo broadcast muito próximos | **Hipótese de sobreposição útil** | Estado inicial garante mapa imediato; não eliminar sem garantir versão/ordem e retomada |
| Publicação unificada/física sem inscritos | **Confirmado que o caminho permite; observado sem trânsito público** | Custo de CPU/serialização/Redis, economia de ngrok aproximadamente zero enquanto não há entrega |
| `ConsecutiveIdentical=0` | **Confirmado como contador, insuficiente semanticamente** | `ComposedAt`, geração, freshness e contexto mudam; `overflow` nem compara hash. Não prova novidade útil |
| Reconexões vazando | **Hipótese** | Sem close code, runtime e duração histórica; nenhum vazamento confirmado |

O unificado responde por posição física válida mesmo quando matching operacional não é confiável. O operacional oferece rota/estado/ETA e fallback em linhas sem unificado, incluindo BRT. Remover o antigo globalmente pode perder veículos ou manter contexto incorreto. A interface já arbitra identidade e timestamps para evitar dois marcadores; **deduplicar desenho não elimina bytes já recebidos** (`F/app/index.tsx:140–149`, `unifiedLocationContract.ts:96` em diante).

## 8. Análise de comportamento do frontend

### 8.1 Conexão, donos e limpeza

`F/src/services/gpsHub.ts:16–30` contém uma conexão por runtime e mapas de donos operacionais/físicos. `atualizarInscricoesGps:102` substitui o conjunto de cada dono; reconciliação serial em `:107–135` remove linhas não desejadas antes de adicionar novas. O caminho físico/unificado (`:72–97`) também une conjuntos, selecionando o método por prefixo `unified:`. A criação de um array/objeto por render não cria por si só nova conexão: `connection` é reutilizada, `selected` vira string ordenada no hook e os sets evitam `invoke` repetido.

Os quatro eventos são registrados uma vez na construção da conexão (`:163–192`). Há listeners por consumidor, mas não quatro sockets. Cleanup de `useMobilidadeRio:158–186` remove listener e dono; `observePhysicalPresence:63–68` remove dono e sincroniza. `liberarSeOciosa:41–44` para a conexão após as filas quando não resta demanda.

Android background: `gpsHub.ts:47–54` chama `connection.stop`, invalida leitura e limpa inscrições confirmadas. Foreground reconcilia os donos restantes. Reconnect (`:195–197`) incrementa geração, limpa sets confirmados e reinscreve a união; grupos do socket antigo são removidos pelo manager. O backend remove seu índice operacional em `GpsHub.OnDisconnectedAsync:86–90`. **Confirmado no código, não validado fisicamente em Android nesta etapa.** Não há tratamento equivalente para todo estado intermediário `inactive`; o código trata transições Android `background/active` explicitamente.

### 8.2 Cenários solicitados

| Cenário | Comportamento encontrado | Confiança / ressalva |
|---|---|---|
| Aberto sem linha selecionada | Sem donos legítimos, o hub não inicia. Mas Home pode usar presença física com flag e Radar terminal de 838/855 quando uma parada inicial é elegível | **Confirmado condicional**; “sem seleção” não significa necessariamente zero demanda |
| Uma linha ônibus | Grupo operacional `linha:codigo`; se suportada, grupo unificado. Pode compartilhar demanda terminal/instalações | **Confirmado no código**; não contar uma conexão por stream |
| Várias linhas | União de códigos; uma conexão. Bytes crescem com snapshots dos grupos distintos e número de veículos, não necessariamente linearmente com itens selecionados | **Confirmado**; linhas repetidas entre donos não multiplicam grupo |
| Trocar Home ↔ Linhas | `useMobilidadeRio` respeita `enabled`/foco. Consumidor unificado dentro do mapa não recebe foco | **Provável retenção indevida de demanda** em aba montada; sem prova de vazamento operacional |
| Mapa oculto em outra aba | Pode continuar dono de `unified:codigo` enquanto props conservam linhas | **Confirmado caminho**, duração/bytes evitáveis não medidos |
| Background Android | Hub interrompido; timer de capabilities é separado e não tem AppState | **Confirmado código**; SO pode suspender timers. Não afirmar polling JS contínuo garantido |
| Voltar ao foreground | Nova abertura com negociação, reinscrição e snapshot inicial; serialização/geração evitam reinscrição antiga pendente | **Confirmado código**; não medida multiplicidade de runtimes |
| Radar aberto | Eventos HTTP da parada a cada ~30 s; terminal pode solicitar unificado para até duas linhas elegíveis. Mapa/presença podem continuar com demanda legítima | **Confirmado**, não um polling por ônibus monitorado |
| Radar remoto em background | Backend calcula internamente e envia Expo/FCM; Android não faz polling contínuo de chegadas. Inscrições do hub suspensas | **Confirmado código**; push/áudio em aparelho não executados |
| BRT | Hook operacional inclui `brt`; mapa unificado filtra `modal === 'onibus'` | **Confirmado no cliente**, preservar fallback BRT |
| Trem | `useRailRealtime` usa snapshot HTTP a cada 5 s após conclusão, com foco e AppState; geometria separada/cache | **Confirmado**, sem stream ferroviário atual no caminho desse hook |

Referências de demanda: `useMobilidadeRio.ts:170–186`; `mapOSM.tsx:65`; `app/index.tsx:137–138,349–357,571–573,782–783,790–812,1003–1012`; `app/linhas.tsx:487–490,812,996`; `app/_layout.tsx:70`. O hook unificado (`unifiedLocation.ts:18–55`) não aceita `enabled`. O cache de capabilities é global, TTL 30 s e single-flight (`unifiedNetwork.ts:20–28`), portanto não presumir um GET adicional por cada dono/linha.

### 8.3 Demanda legítima sem seleção manual e contratos necessários

`physicalPresence.ts:6–10,27–47` só ativa com `EXPO_PUBLIC_PHYSICAL_PRESENCE_ENABLED=true`; quando ativa, pede os quatro IDs fixos da Home. O backend remoto tem três instalações configuradas e pode também gerar alternativa `line:838`; uma inscrição inválida recebe bootstrap vazio sem grupo. A flag não apareceu explicitamente na `.env`/perfil EAS local lidos; o default do código é desligado. **Não foi comprovado o valor compilado nos APKs históricos.**

`useTerminalRadarPresence.ts` não é um arquivo separado: a função está em `useTerminalRadar.ts:55–60`. Ela calcula `terminalRadarScope`, pede somente linhas de parada elegível e é desligada por `enabled`. `terminalRadarContext.ts:10–22` limita a 838/855 e no máximo duas linhas, com corredor/parada inicial até três paradas/1.500 m. Não é inscrição indiscriminada em todas as linhas do catálogo. Se o mesmo mapa já pediu 838, o dono terminal reutiliza a inscrição.

`hybridContext.ts:68–92` usa `operational.status`, timestamp GPS, linha/código, padrão, sentido e coordenadas para validar contexto e bloquear conflito. `unifiedLocationContract.ts:24–94` também utiliza velocidade, rota, próxima parada e estado em fallbacks. Uma proposta de DTO mínimo precisa mapear esses usos e o adaptador `veiculosMapa.ts:79–118`, que conserva `raw`; não basta procurar apenas um acesso em uma tela.

### 8.4 Desenvolvimento, release e APK antigo

O singleton é por runtime JS, não por conta/aparelho. Bundle recarregado ou runtimes diferentes podem ter sockets independentes. `__DEV__` muda diagnósticos/logs, mas não cria a lacuna de foco nem troca automaticamente o protocolo. Nenhum `StrictMode` explícito foi encontrado no layout já auditado; não explicar as aberturas por ele sem prova. O SDK/versionamento observado em C1/C2 não identifica commit do frontend, tipo de build ou flags EAS. Não estender conclusões do checkout atual a todo APK antigo.

## 9. Tráfego necessário versus evitável

| Preservar | Motivo | Possível economia sem perder função |
|---|---|---|
| Dados frescos dos grupos visíveis | Movimento, mapa e confirmação de contexto | Compactar contrato/transportar uma vez após prova de equivalência |
| Bootstrap/reinscrição na retomada | Evita esperar próximo ciclo ou manter estado antigo | Evitar somente reabertura indevida comprovada; não remover bootstrap às cegas |
| Linha vazia, expiração e tombstones | Removem veículos/estados vencidos | Volume de linha vazia é 214 bytes lógicos no processo: ganho irrelevante ao cortar |
| Timestamps de evidência e validade | Coordenada idêntica pode ter idade/status diferentes | Não renovar evidência antiga nem esconder mudança de estado ao deduplicar |
| Fallback operacional/BRT e cobertura incompleta | Unificado não é universo completo da rede | Versão negociada de contrato pode reduzir duplicidade com fallback explícito |
| Radar remoto, áudio e push | Função central do beta | Aquisição/backend/FCM não atravessam o túnel para cada aviso |

**Evitável provável:** snapshots de grupos mantidos exclusivamente por mapas invisíveis; dados operacionais redundantes que tenham cobertura/semântica equivalentes; reconexão desnecessária, se vier a ser demonstrada. **Não é desperdício ngrok demonstrado:** preparar snapshot de grupo vazio. Isso pode justificar revisão de CPU separadamente, mas não é o principal problema de franquia desta etapa.

### Timestamps, mudanças e snapshots completos

O canônico compara assinatura de estado/linha/validade antes de criar patches (`CanonicalPhysicalLocation.cs:110–118`), publica linhas afetadas após commit (`:124–149`) e linhas expiradas em `MaintainAsync:196–200`. O snapshot recompõe contexto operacional e `ComposedAt=now` (`:153–175`). Portanto já existe supressão de parte das mudanças idênticas no estado; não recomendar implementá-la como se nada fosse filtrado.

Outra fonte ou observação pode afetar a linha e provocar snapshot completo, repetindo veículos que não mudaram. Freshness, validade, transferência de linha e expiração podem mudar sem movimento. `ConsecutiveIdentical` só vê igualdade byte a byte e é desativada no overflow. Deltas exigiriam sequência/revisão, remoções, ressincronização e garantia de validade; para beta pequeno, compactar e corrigir demanda vem primeiro.

## 10. Estimativas para beta de 3, 5 e 10 pessoas

### 10.1 O que é medição e o que é hipótese

**Medições utilizáveis:** Bytes Out/duração somente de C1/C2; médias de HTTP da auditoria anterior; tamanhos lógicos/médias de publicação desta etapa. **Não existe medição de MB por hora de um usuário beta típico.** Os cálculos abaixo são cenários de planejamento, não capacidade homologada.

Modelo de sessão: `requests = 22 + horas × taxa_dinâmica`; `MB = 0,25 + horas × taxa_MB`. Os 20 requests/0,25 MB de bootstrap mais duas aberturas HTTP são premissas herdadas, não média comprovada de APK release. Trocas de tela, cache frio, metadados, reconexões e comandos Radar adicionais podem aumentar ambos. Bootstrap pode ocorrer sem WSS no trem; manter 22 é margem comum do cenário.

| Perfil em primeiro plano | Requests dinâmicos/h | MB/h utilizados no planejamento | Origem / confiança |
|---|---:|---:|---|
| Ônibus + uma parada Radar | Até ~240: capabilities + eventos | **12 hipotéticos** | ~6,24 MB unificado + 2,63 operacional + 2,09 HTTP; arredondamento com margem. Combina médias globais e cadências de outros instantes, não uma sessão medida |
| Sessão rodoviária semelhante a C1/C2 | Mesmos 240 somente se mantiver uma parada estável | **45–48 hipotéticos totais** | WSS medido 42,61–45,60 MB/h + ~2,09 HTTP/h e pequena margem. Para tabelas, usar 48; não chamar de média universal |
| Trem sem Radar | Até ~720 | **~13,46 de corpos HTTP** | 417 snapshots anteriores, média 18.695 bytes; ciclo saudável de 5 s; overhead extra não aferido |
| Trem + Radar visível | Até ~840 | **~15,16 de corpos HTTP** | Soma do snapshot + eventos; sem aquisição externa por usuário |
| Background com Radar remoto | Sem polling persistente esperado | Próximo de zero no túnel após comandos/transição | Push direto; capabilities fora de foco é ressalva. Não há taxa Android medida |

No modelo de 12 MB/h, usa-se média unificada de 15.129 bytes × 6,875/min, operacional 10.951 bytes × 4/min e HTTP histórico. A cadência de 6,875/min é mediana de 128 grupos numa janela sem clientes, não parâmetro obrigatório. Linhas com mais veículos e múltiplos grupos pesam muito mais. O cenário 48 MB/h evita ignorar as medições reais, mas não estabelece teto.

### 10.2 Por sessão e por participante

| Uso | Ônibus: requests / MB (modelo leve → cenário semelhante às amostras) | Trem + Radar: requests / MB de corpos |
|---|---:|---:|
| Sessão de 10 min | ~62 / **2,25–8,25** | ~162 / 2,78 |
| Sessão de 20 min | ~102 / **4,25–16,25** | ~302 / 5,30 |
| Sessão de 30 min | ~142 / **6,25–24,25** | ~442 / 7,83 |
| 10 min, dez dias/mês | ~620 / **22,5–82,5** | ~1.620 / 27,8 |
| 20 min, dez dias/mês | ~1.020 / **42,5–162,5** | ~3.020 / 53,0 |
| 30 min, vinte dias/mês | ~2.840 / **125–485** | ~8.840 / 156,6 |

A faixa de ônibus une duas hipóteses distintas de uso, não intervalo estatístico. A taxa das amostras é extrapolada apenas para demonstrar risco se aquele padrão durar. No trem, as estimativas de bytes são corpos e precisam de margem para transporte. Requests dependem de latência, porque o timer aguarda a conclusão.

### 10.3 Grupo cadastrado, atividade e simultaneidade

Tabela para **uma sessão de 20 min em dez dias por pessoa/mês**, excluindo desenvolvedor e outros consumidores. `N` é participantes ativos nesse perfil, não N conectados o mês inteiro. Média diária ao longo de 30 dias = `N/3` pessoas; simultâneos podem variar de um a N. O trabalho externo de coleta continua compartilhado; as entregas são individuais.

| Participantes | Cliente-horas/mês | Todos ônibus: requests / MB | Todos trem + Radar: requests / MB |
|---|---:|---:|---:|
| **3** | 10 | **3.060 / 127,5–487,5** | **9.060 / ~159** |
| **5** | 16,67 | **5.100 / 212,5–812,5** | **15.100 / ~265** |
| **10** | 33,33 | **10.200 / 425–1.625** | **30.200 / ~530** |

Mistura hipotética de **2/3 das horas ônibus e 1/3 trem**, mantendo o mesmo calendário: três participantes ~**5.060 requests / 138–378 MB**; cinco ~**8.433 / 230–630 MB**; dez ~**16.867 / 460–1.260 MB**. Frações representam composição de horas do grupo, não contagem literal de um terço de aparelho.

Dez ônibus simultâneos por **uma única sessão de 20 min** custam aproximadamente 1.020 requests e 42,5–162,5 MB nesse modelo; não confundir esse pico com seu mês inteiro. Dez cadastrados, com somente três seguindo o perfil de uso, devem ser calculados pela atividade dos três.

### 10.4 Desenvolvedor, saldo e qual limite vem primeiro

O desenvolvedor compartilha a mesma conta. Seu histórico inclui alternância de telas, configurações Radar e reinícios; não se deve tratar o consumo anterior como dez usuários reais. Exemplo futuro, **Hipótese**: 20 sessões de uma hora/mês, 2/3 ônibus e 1/3 trem + Radar, gera ~**9.240 requests / 266–746 MB**, antes de outras ações de teste. Não é medição do tempo de uso passado. Somado a três betas mistos do perfil anterior: ~**14.300 requests / 404–1.124 MB**. A transferência pode limitar mesmo quando requests cabem.

Orçamento proposto para planejamento: **16.000 requests e 800 MB/mês**, reservando 20% das franquias. Isso não altera limites da conta. Na referência anterior, se 7.072 e 183,83 MB forem exatamente do ciclo/endpoint em questão, restariam **8.928 requests e 616,17 MB até essa reserva**. O contador de requests continua igual nesta etapa; o total de bytes não foi atualizado. Não prometer esse saldo como billing confirmado.

- Só ônibus no perfil de 20 min/dez dias: teto aritmético exclusivo de requests permitiria 15 participantes na reserva; bytes permitiriam cerca de 18 no modelo leve, mas **apenas quatro** se persistisse o cenário de 48 MB/h. **Transferência primeiro no cenário alto; requests primeiro no leve.** Sem margem adicional, limites brutos seriam aproximadamente 19 e seis no cenário alto de bytes.
- Só trem + Radar no mesmo calendário: reserva de requests comportaria **cinco**, quase sem espaço para desenvolvimento; requests limitam antes dos bytes. Dez excedem até o limite bruto de 20.000.
- Misto: três a cinco participantes são um ponto de partida administrável se houver orçamento separado para testes do desenvolvedor. Dez precisam de medição e atividade menor, sobretudo se as sessões se parecerem com C1/C2.

Esses números não medem limite de conexões simultâneas ou hardware. No estado atual, recomendar **começar com três participantes ativos em sessões curtas e ampliar para cinco após medir**; dez cadastrados podem participar em rodízio. Manter ngrok e rever uso semanalmente, sem exigir migração.

## 11. Oportunidades de otimização priorizadas

Somente estudo para sprint futura. Percentuais abaixo não são economia comprovada de billing; cada estimativa informa seu denominador. Não somar ganhos sobre os mesmos bytes.

| Prioridade / proposta | Evidência e ganho em requests | Ganho em transferência | Esforço / dependência / risco | Viabilidade para beta |
|---|---|---|---|---|
| **1 — Ligar dono unificado a foco/foreground/contexto** | Lacuna em `mapOSM.tsx:65`, `unifiedLocation.ts:18`; pode evitar até ~60–120 capabilities/h ociosa por runtime, respeitando cache compartilhado | Remove integralmente os eventos dos grupos cujo último dono era o mapa oculto; valor real desconhecido. Ex.: metade do tempo de uma demanda exclusiva evita metade dos bytes dessa demanda | Pequeno/médio, frontend; risco baixo/médio na retomada. Preservar dono terminal/Radar legítimo | **Alta**, primeira correção funcional candidata |
| **2 — Instrumentação mínima de destinatários e reconexões** | Não poupa requests diretamente; impede diagnosticar falsos loops | Não poupa bytes diretamente; revela quais grupos estão em C1/C2 e quanto realmente recebem | Médio, backend e identificação efêmera no cliente; sem payload/credencial | **Alta**, pequena e orientada à decisão; somente após autorização de implementação |
| **3 — Enxugar DTO versionado sem retirar provas de validade** | Zero requests WS poupados | Cada 20% removido de um fluxo de 42,6–45,6 MB/h pouparia ~8,5–9,1 MB/h **se a redução se aplicar àquele tráfego**; proporção de campos removíveis ainda não medida | Médio, frontend/backend e clientes antigos; risco médio por uso de contexto/ETA | **Média/alta** após matriz de campos; começar por alias/duplicação demonstrável, não objeto inteiro |
| **4 — Consolidar operacional/unificado somente com cobertura comprovada** | Em geral zero; não abrir segunda conexão | Com duas linhas no exemplo da seção 5.4, eliminar duas entregas operacionais equivalentes pouparia ~5,26 MB/h. Não é economia validada de C1/C2 | Médio/alto, ambos; alto risco se retirar fallback BRT/linha sem cobertura ou contextos/veículos ausentes do unificado | **Média**, sem bloqueio do beta; nunca desligamento global sem negociação |
| **5 — Restringir presença física ao contexto, se APK a usa** | Não muda HTTP significativamente; pode evitar manter WSS exclusivamente por instalações distantes | Só volume dos grupos que deixam de ter dono. Evento físico médio de 589 bytes é pequeno; não usar seus 19,68 MB internos como entrega | Pequeno/médio, frontend; risco de perder descoberta de terminal/avisos | **Média/baixa** até confirmar flag/uso real; não cortar funcionalidade de descoberta |
| **6 — Experimento de compressão WebSocket** | Zero requests | Potencial alto pela repetição de JSON; gzip anterior reduziu snapshots ~86%, mas isso **não prediz permessage-deflate** nem garante ganho semelhante | Médio/alto; backend .NET 9, cliente Android, CPU/memória e análise de risco de compressão com dados sensíveis. Sem configuração pronta presumida | **Média**, spike curto após medir DTO; não requisito inicial |
| **7 — Ferrovia por grupos do hub existente, HTTP bootstrap/recovery** | Até ~720 GETs/h substituídos por eventos; em perfil beta trem 3,33 h/mês, ~2.400 snapshots evitáveis por pessoa antes de bootstrap | Snapshot com mesma frequência não reduz JSON por si só. Pode remover HTTP overhead; real economia de bytes depende de envio por mudança | Médio/alto, ambos; preservar Live/Estimated/Scheduled, expiração, filtros e reconexão | **Condicional**: priorizar se beta usar muito trem e requests forem gargalo |
| **8 — Suprimir apenas publicações semanticamente sem mudança** | Zero HTTP | Só economiza ngrok quando havia destinatário. Canonical já filtra parte do estado; preserva expiração e alterações operacionais | Médio/alto, backend; risco de dado vencido, mudança de linha ou ETA não propagada | **Baixa/média**, medir antes; não deduplicar por coordenada |
| **9 — Deltas por veículo + ressincronização** | Não reduz requests atuais WS; recuperação pode acrescentá-los | `ganho ≈ 1 − (bytes de mudanças + remoções + metadados)/snapshot`; sem série de frames não há percentual mensurado | Alto, ambos; sequência, gap, removidos, cliente antigo e fallback | **Baixa para sprint inicial** do pequeno beta |
| **10 — Reduzir reconexões indevidas** | Cerca de 2 requests por abertura efetivamente evitada; teto histórico de todas as aberturas não é economia disponível | Evita handshake/bootstrap e eventual sobreposição; não evita atualização necessária | Primeiro medir causas; correção depende de diagnóstico | **Condicional**, nenhum vazamento comprovado para corrigir agora |
| **11 — Não preparar broadcasts de grupos vazios** | Zero | Aproximadamente **zero ngrok** quando não há membro; benefício é CPU/Redis/serialização | Médio, backend; inicial/recovery e estado compartilhado precisam continuar | **Baixa como otimização de franquia**; avaliar processamento em tarefa distinta |

Compressão HTTP/cache versionado continuam oportunidades válidas do relatório anterior, mas não foram reavaliadas nem contabilizadas como solução para esses frames. MessagePack e mudanças de provedor não são prioridades desta etapa.

## 12. Pontos que não devem ser alterados

- **Conexão compartilhada, união por dono e fila de reconciliação.** Já evitam sockets/inscrições por componente e protegem respostas assíncronas antigas.
- **Suspensão Android e retomada com snapshot válido.** Manter comportamento; medir, sem conservar socket em background apenas para poupar duas requisições.
- **Freshness e validade independentes da coordenada.** `physicalAt`, `validUntil`, revisão, associação de linha e status operacional não são timestamps decorativos.
- **Linha vazia e remoções.** Ganho desprezível em bytes; são essenciais para não exibir veículos inexistentes.
- **Fallback operacional e BRT, e o caráter parcial da cobertura unificada.** Ausência no feed físico não é prova de ausência de veículo operacional.
- **Radar remoto, notificações e áudio.** Não substituir push por polling; seus workers/FCM não são um GET ngrok por usuário/ciclo.
- **Aquisição externa compartilhada e filtros de inscrições GPS já existentes.** Não reduzir o GPS de todos para economizar tráfego que nem atravessa o túnel.
- **HTTP de bootstrap e recuperação da ferrovia.** Uma futura distribuição por hub ainda precisa ressincronização, versão e expiração.

## 13. Lacunas de observabilidade ainda existentes

| Lacuna | Precisamente o que falta | Menor caminho para resolver |
|---|---|---|
| Diferença 183,83 MB versus 48,41 MB HTTP | Mesma janela, endpoint, unidade e transferências atuais; 399 requests não retidos | Leitura de Usage e total de conexões correspondentes; não subtrair janelas diferentes |
| Distribuição global REST/WS | Bytes Out de conexões ligadas a upgrades e REST, sem duplicar conexão HTTP multiplexada | Connection ID → requests, total TCP uma única vez; separar handshake e limites de atribuição |
| Conteúdo de C1/C2 por evento | Grupos inscritos, contagens/tamanhos por evento e destinatários | Contadores agregados por sessão/evento, sem payload; não usar amostras de outro horário como prova |
| Grupos vazios versus ocupados no processo | Membresia no momento de send e destino público/interno | Manager com contagem de membros/entregas estimadas, sem IDs sensíveis |
| Máximo de payload e ganho de compactação | Bytes máximos e distribuição de campos/alterações | Métrica bounded por evento; amostras sintéticas aprovadas em ambiente local ou medição mínima sem conteúdo |
| Causas de reconnect | Close code/erro, gatilho de lifecycle, build e runtime efêmero | Logs mínimos correlacionáveis, com retenção curta, sem IP/token |
| Android release e flags | APK usado em cada conexão, foco real, demanda sem seleção | Experimento manual com build conhecido; não confiar só no texto SDK |
| Compressão ponta a ponta | Extensão aceita no lado cliente/borda e bytes antes/depois | Inspeção de handshake e contador TCP da conexão em ensaio autorizado |
| Billing do upgrade/reset | Regra efetiva da conta, saldo/reset/fuso e outros endpoints | MCP utilizável ou leitura manual de Usage/limites; não extrair credenciais |

Os **135,42 MB nominais** de diferença anterior continuam **não atribuídos**. C1/C2 provam tráfego WS relevante, não que toda a diferença seja WS. Também não é válido usar 35,86 GB internos menos 183,83 MB como “bytes desperdiçados”: são métricas de camadas e janelas diferentes.

`docker stats` mostrava aproximadamente 252 GB recebidos / 85,4 GB enviados no container, acumulados e incluindo Redis/SQL/fontes externas. Esses números foram descartados como medição de ngrok. Não houve captura de pacotes, instalação, leitura de tokens ou monitor deixado em execução.

## 14. Experimentos mínimos pendentes

**Nenhum executado nesta etapa. Exigem autorização antes de iniciar.** Como agora há duas amostras TCP, o ensaio deve explicar demanda/ciclo de vida, não repetir medição genérica nem fazer carga.

Preparação proposta: um Android com build/flags anotados e cache aquecido; desenvolvedor sem outras sessões do NoPonto durante o ensaio; marcar horários/fuso; ler requests e saldo antes/depois; encerrar conexões pelo uso normal do app. Usar somente metadados das conexões encerradas e requests vinculados. Não ativar full capture, replay, exportação paga ou log de conteúdo. Se houver outros usuários, não presumir delta da conta exclusivo do ensaio.

| Caso | Duração proposta | Passos / evidência desejada | Orçamento preliminar por caso* |
|---|---:|---|---|
| Uma linha rodoviária | 2 min | Selecionar uma linha conhecida, uma parada; anotar grupos esperados e encerrar sessão. Medir Bytes Out/tempo e requests | ~50 requests / até 2 MB planejados |
| Duas linhas | 2 min | Mesmas condições, segunda linha distinta. Comparar incremento de bytes, conexão ainda única | ~30 requests / até 3 MB |
| Mapa oculto | 2 min | Com linhas carregadas, abrir aba sem mapa; identificar se socket continua e taxa residual, sem outro dono legítimo | ~8 requests / até 2 MB |
| Background | 2 min | Enviar app ao background; verificar fechamento e ausência de nova abertura até voltar. Retomar uma vez e medir bootstrap | ~8 requests / até 0,5 MB, incluindo transição |
| Radar | 2 min | Parada elegível; distinguir HTTP de eventos, unificado terminal e comandos remotos. Background deve usar push | ~20 requests / até 2 MB |
| Trem | 2 min | Uma linha, sem mudança de seleção; contar GETs e bytes. Sem acionar refresh manual | ~70 requests / até 2 MB |

\* São **estimativas de orçamento**, não tetos garantidos do app. Bootstrap frio desconhecido pode ultrapassar; pressupõem dados pré-carregados e separam a taxa alta das duas amostras. Reservar **até 250 requests e 12 MB** para a série inteira, incluindo transições. Conferir saldo antes; interromper se alcançar o orçamento, houver requisições inesperadas ou demanda persistente após cleanup. Sem laço de teste e sem processos de monitoramento permanentes.

Coleta mínima manual após cada conexão: início/fim/duração, Bytes Out, número de requests ligados ao socket, flag do APK, linhas/telas do roteiro e contadores globais. Não precisa IP, token, coordenadas ou corpo de mensagem. HTTP/HTTPS ainda aberto pode só aparecer após fechar; isso evita interpretar ausência imediata na lista como zero tráfego. Não executar seis sessões simultâneas.

Uma linha versus duas esclarece crescimento; mapa oculto/background esclarecem demanda residual; Radar confirma o que precisa de stream; trem valida a pressão de requests. Identificar eventos dentro do socket exigirá a instrumentação futura da seção 6 ou métricas já disponíveis no build de diagnóstico — sem criá-las automaticamente neste ensaio.

## 15. Recomendações para o planejamento da sprint

| Etapa futura | Entrega / decisão | Critérios para avançar |
|---|---|---|
| **0 — Baseline verificável** | Guardar metadados de sessões curtas conhecidas; ler Usage do ciclo e conexões correspondentes; separar orçamento do desenvolvedor | Mesma janela/fuso, MB/MiB corretos; sem somar HTTP e TCP sobrepostos; nenhuma captura sensível |
| **1 — Foco e demanda** | Habilitação explícita do consumidor unificado por foco/foreground e contexto; manter conexão compartilhada | Mapa oculto sem outro dono deixa grupo; mesma linha no Radar continua; volta imediata sem estado vencido; background fecha WSS |
| **2 — Visibilidade de entrega** | Menor instrumentação de membros/evento/sessão/build e motivo de fechamento | Separar público/interno, zero destinatários, argumento estimado e bytes de conexão; overhead de telemetria limitado |
| **3 — DTO antes de protocolo novo** | Matriz de campos usados, contrato compacto versionado e fallback; avaliar consolidar streams só onde houver equivalência | Mesmos veículos, rota, ETA, expiração, transferência de linha, BRT, estado vazio e cliente antigo corretos; bytes menores demonstrados no Android |
| **4 — Beta inicial** | Três participantes ativos, sessões curtas; ampliar para cinco conforme consumo; demais em rodízio | Manter reserva 20%; requests e bytes por sessão conhecidos; desenvolvimento não consome o orçamento sem registro |
| **5 — Decisão condicional** | Ferrovia por SignalR se requests ferroviários forem o gargalo; compressão WS se bytes dominarem | Ganho medido suficiente para o beta; ressincronização/compatibilidade preservadas. Deltas/MessagePack podem ficar fora da primeira sprint |

O teste decisivo não é “o contador de publicação caiu”. É **a sessão com a mesma função, seleção, duração e validade entregar menos Bytes Out ou menos requests**, sem perder atualização, Radar, áudio ou remoções. A comparação deve separar build release de desenvolvimento.

**Validação desta investigação:** leitura de código/SDK, metadados do agente e logs existentes, pareamento em memória de negociações, cálculos de unidades e cenários; nenhuma solicitação ao hostname público, conexão WS sintética, GET novo de dados da API, experimento Android, build, teste automatizado funcional, alteração de código/configuração/infra, commit ou deploy. Leituras de observabilidade por SSH/loopback não abriram sessões de uso do aplicativo. Não foram persistidos tokens, IPs, capturas brutas ou payloads sensíveis.

**Resultado para decidir:** o beta pequeno continua possível com ngrok, mas a banda por sessão pode ser várias vezes maior que a hipótese leve anterior. As melhores primeiras frentes são demanda visível, medição de destinatários e compactação compatível dos contratos. A evidência ainda não autoriza chamar as reconexões de vazamento, retirar o stream operacional globalmente ou afirmar que a diferença mensal inteira é SignalR.
