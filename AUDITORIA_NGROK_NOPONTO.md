# Auditoria completa de consumo do ngrok — NoPonto

Data: 13/11/2026. Investigação somente de leitura; nenhuma otimização implementada.

**Escopo e evidências.** Frontend `noponto-frontend`, HEAD `3062d55`; backend deste checkout, HEAD `d491e15`; API em execução no servidor, imagem `cbddab780f8f`, iniciada em 12/11/2026 às 23:57:55 UTC, sem reinícios do container. A imagem não tem label de revisão: não foi comprovada igualdade binária com o checkout. Contratos publicados foram conferidos pelo Swagger interno.

Referências de código: **F/** = `../noponto-frontend/NoPonto/`; **B/** = `NoPonto/`. Linhas referem-se aos arquivos inspecionados, não a todas as versões que produziram o histórico de tráfego.

Graus de confiança: **Confirmado** = código inequívoco ou medição; **Provável** = indícios fortes sem atribuição completa; **Hipótese** = exige validação adicional. Uma frequência confirmada no código não significa tempo de uso medido no aparelho.

## 1. Resumo executivo

1. **Confirmado: o maior problema de requisições é a repetição de consultas pequenas e de estrutura.** Em 6.673 registros retidos pelo agente, `/sentidos/{id}/padroes` soma 874 chamadas, eventos de parada 859, `/linhas` 636 e detalhes de parada 607. Foram encontrados 99 GETs excedentes de `/modais`, com mesma URL, segundo e conteúdo de resposta; mais 40 GETs de reconciliação Radar repetidos com a mesma credencial e resposta. Há caminhos duplicados correspondentes no frontend.
2. **Confirmado: itinerários são o maior consumidor HTTP capturado em bytes, não em quantidade.** São 15,50 MB em 551 downloads. Todos correspondem a apenas 16 versões e 16 conteúdos distintos: 535 downloads repetem exatamente os mesmos bytes, totalizando 15,11 MB. Existe cache em memória por versão; falta reaproveitamento entre reinicializações do aplicativo. Não se trata de ausência total de cache.
3. **Confirmado: há grande potencial de compressão HTTP, ainda não aplicado na origem.** Eventos de parada, itinerários e snapshots ferroviários somam 35,46 MB. A compressão gzip offline dos próprios corpos capturados os reduz a 7,78 MB. Uma consulta interna com `Accept-Encoding: gzip` confirmou itinerário sem `Content-Encoding`: 18.986 bytes, contra 6.186 bytes na simulação. Compressão HTTP não comprime automaticamente mensagens WebSocket.
4. **Provável: SignalR explica parcela importante da transferência que falta atribuir.** Os registros HTTP têm 48,41 MB de corpos e 49,58 MB de respostas brutas; o painel informado pelo usuário tinha 183,83 MB. O inspector não contém frames WebSocket. A telemetria existente do servidor mede publicações, inclusive para grupos vazios, e desconhece destinatários. Seus 34,1 GB de payloads preparados/publicados **não são 34,1 GB enviados pelo ngrok**. Não é possível fechar o ranking global por bytes com a observabilidade acessível.
5. **Confirmado no código: existe uma lacuna de visibilidade no consumidor de localização unificada.** O mapa chama `useUnifiedLocation` sem informar foco da tela; o hook consulta capabilities periodicamente sem verificar `AppState`. A conexão compartilhada suspende no background Android, mas esse timer HTTP é separado. Em abas mantidas montadas, inscrições unificadas também podem manter demanda enquanto outro mapa está oculto. O volume efetivamente desperdiçado ainda não foi medido em aparelho.

**Problema mais crítico para a decisão:** a franquia de requisições é pequena para polling por cliente, especialmente o ferroviário de 5 s. Mesmo eliminando desperdício estrutural, um usuário ferroviário de 1 h/dia produz aproximadamente 21.600 GETs de snapshot/mês, antes da inicialização e do Radar. Preservar a atualização exige mudar distribuição/protocolo ou orçamento de hospedagem, não apenas reduzir a frequência indiscriminadamente.

**Avaliação geral:** ngrok gratuito serve ao desenvolvimento controlado e a uso ocasional pequeno. Não deve ser assumido como capacidade de produção para 5–20 usuários frequentes. Priorizar cache estrutural, unificação dos carregamentos e correção de visibilidade; em paralelo, planejar distribuição ferroviária por conexão persistente e uma opção de exposição sustentável.

## 2. Arquitetura encontrada

```text
Android / Expo / APK configurado com o domínio atual
  ├─ REST HTTPS ────────────────────────────────┐
  └─ SignalR: negotiate HTTP + upgrade WSS ─────┤
                                               v
               endpoint público ngrok HTTPS, nome local "api"
               ununionized-suzanne-nonliterary.ngrok-free.dev
                                               |
                       agente ngrok 3.39.1 no servidor Linux
                       systemd: ngrok.service
                       inspector: 127.0.0.1:4040
                                               |
                       http://localhost:8080
                                               |
                       publicação Docker 8080 -> 8080
                                               |
                       noponto_api / ASP.NET Core + workers
                         ├─ PostgreSQL/PostGIS: rede noponto_default
                         ├─ Redis: rede noponto_default
                         ├─ HTTP direto: Mobilidade Rio / GTFS-RT / ITS / TrensRJ
                         └─ HTTP direto: Expo Push -> FCM -> Android

Admin / ferramentas autorizadas pela rede Tailscale
  ├─ servidor:8080 -> API NoPonto, sem ngrok
  └─ servidor:8081 -> auth_api, sem ngrok

WebView Android -> Carto / Google tiles / unpkg, diretamente, sem ngrok
```

**Confirmado no ambiente:** um endpoint ativo neste agente, encaminhando para `http://localhost:8080`, inspeção já habilitada antes da auditoria. Serviço ativo desde **07/11/2026 13:50:44 -03**. Na leitura inicial havia 7.072 requisições, 1.278 conexões acumuladas e gauge de conexões igual a zero. Conexão de transporte não equivale a usuário ou sessão do aplicativo.

Não foi encontrado proxy Nginx/Caddy ativo nem listener nas portas 80/443 na inspeção feita. O caminho confirmado é agente -> porta Docker -> API. Isso não comprova ausência de qualquer componente fora desse host ou política na borda da conta.

**Caminhos fora da franquia desse túnel:** aquisição de GPS/grade, SQL, Redis, push e downloads de tiles/bibliotecas. `Program.cs:234–335` registra os clientes externos diretamente; `F/src/components/mapOSM/webview/mapHtml.tsx:20` carrega bibliotecas de unpkg; `mapScript.ts:880,1195` usa Google/Carto. O polling GPS de 15 s confirmado na configuração do servidor é coleta do backend; não é uma requisição ngrok a cada 15 s.

**Aplicações e ambientes:** `.env` e perfil EAS Radar do frontend ativo usam o domínio atual (`F/eas.json:26`). O worktree `noponto-frontend-principal` ainda referencia outro domínio ngrok, `duplicity-wisplike-friend.ngrok-free.dev`; não foi encontrado endpoint correspondente neste agente. Não foi testado esse domínio. APKs já instalados podem conter configurações antigas; não há identificação de versão/aparelho nos registros para enumerá-los. O Admin tem bases padrão Tailscale, conforme `../noponto-adm/src/service/api.ts:33`.

Os containers `noponto_api` e `auth_api` não apresentaram URL ngrok nos valores das variáveis inspecionadas, e não houve referência a ngrok no crontab do usuário nem nos diretórios cron de sistema pesquisados. Não há evidência de monitor periódico público na amostra. Isso não exclui tarefas em outros usuários, hosts ou contas. `expo start --tunnel` existe nos scripts de desenvolvimento, mas não se pode atribuir o túnel Metro à mesma conta/franquia sem telemetria da conta.

**Superfície pública confirmada:** Swagger interno lista 40 padrões de caminhos REST: catálogos de linhas/sentidos/padrões/paradas; itinerários; eventos; instalação/sessão/push Radar; snapshot/refresh de trens; localização unificada; veículos; tarifas/formas de pagamento; adaptadores legados e raiz `/`. O hub `/hub/gps` e arquivos Swagger são adicionais. A auditoria leu o documento de rotas; não executou os métodos de escrita listados nele.

Controllers legados de linhas, paradas, POIs e Admin estão excluídos da compilação em `B/NoPonto.csproj:39–44`. Rotas atuais vêm dos controllers `Estrutura*` e dos adaptadores explicitamente preservados. Arquivo existente não foi tratado automaticamente como endpoint ativo.

## 3. Baseline de consumo

### 3.1 Acesso à observabilidade e período

O MCP oficial foi priorizado: configuração local encontrada em `https://mcp.ngrok.com/mcp`. Entretanto, nenhuma ferramenta ngrok foi disponibilizada ao executor; descoberta de recursos e templates retornou `unknown MCP server 'ngrok'`. A busca de plugin também não disponibilizou alternativa. O host de acesso ao navegador estava indisponível. **Não foi alterada a integração, feita nova autenticação ou extraída credencial.**

O fallback foi a **API oficial local do agente**, por SSH já autorizado: `GET /api/tunnels`, `GET /api/endpoints`, `GET /api/status` e `GET /api/requests/http?limit=10000`, todos em loopback. As leituras não passam pelo domínio público. A API retorna apenas capturas ainda em memória; a documentação descreve descarte por `inspect_db_size`. Portanto a amostra é um histórico retido, não um extrato completo de faturamento. [API oficial do agente](https://ngrok.com/docs/gateway/agent/api).

| Indicador | Valor | Origem e confiança |
|---|---:|---|
| Requisições informadas inicialmente | 7.070 | Painel observado pelo usuário em 13/11; não relido |
| Requisições do agente na auditoria | **7.072** | Confirmado por `/api/tunnels`, 13/11 às 18:26 -03 |
| Transferência informada | **183,83 MB** | Referência do usuário; contador atual da conta inacessível |
| Horas informadas | 68 h | Referência do usuário; sem definição da métrica acessível |
| Serviço ngrok ativo desde | 07/11 13:50:44 -03 | Confirmado por systemd |
| Tempo até leitura inicial | 6,191 dias / 148,6 h | Intervalo de relógio, não horas de uso do app |
| Registros HTTP retidos | **6.673** | 07/11 15:10:41 a 13/11 16:18:20 -03 |
| Cobertura do contador do agente | **94,36%** | 6.673 / 7.072; 399 requisições sem registro retido |
| Corpos de resposta HTTP retidos | **48.414.702 bytes** | Base64 decodificado e chunking removido |
| Respostas HTTP brutas retidas | **49.578.779 bytes** | Incluem headers/framing da resposta no agente |
| Endpoint ativo neste agente | 1 | Não é inventário completo da conta |
| Limites públicos Free | 20.000 HTTP/mês; 1 GB saída/mês | Documentação oficial, não consulta da assinatura |

A referência de “quatro dias” não corresponde à janela encontrada. Também não é válido dividir o consumo por 30 dias só porque o painel selecionou “últimos 30 dias”, nem dividir por 68 h sem saber o que esse contador representa.

O ngrok documenta renovação no primeiro dia do mês; a página Free também descreve reset por ciclo mensal. **O timestamp/fuso do próximo reset e a assinatura efetiva da conta não foram confirmados.** Para os exemplos de capacidade usa-se GB decimal = 1.000 MB. [Limites e renovação](https://ngrok.com/docs/pricing-limits), [plano gratuito](https://ngrok.com/docs/pricing-limits/free-plan-limits).

### 3.2 Distribuição temporal e erros

| Dia (-03) | HTTP retidos |
|---|---:|
| 07/11, parcial | 497 |
| 08/11 | 891 |
| 09/11 | 945 |
| 10/11 | 1.367 |
| 11/11 | 659 |
| 12/11 | 1.269 |
| 13/11, até 16:18 | 1.045 |
| **Total** | **6.673** |

Pico horário retido: **347 chamadas em 09/11, 17h**. Pico por minuto: **86 em 12/11 às 11:25**; outros picos foram 72, 70 e 68/min. A média sobre toda a janela retida é aproximadamente 46/h ou 0,77/min, escondendo longos intervalos ociosos. O limite instantâneo público de 4.000/min não é o gargalo observado; o orçamento mensal é.

Métodos: GET 6.194; POST 354; PUT 94; DELETE 31; **OPTIONS zero retido**. Android nativo não deve ser equiparado a navegador sujeito a CORS/preflight. Não há base para recomendar “eliminar OPTIONS” como economia atual.

Status: 200 = 6.269; 101 = 262; 204 = 71; 201 = 48; 202 = 8; 503 = 8; 429 = 3; 400 = 2; 404 = 1; 401 = 1. São **15 erros 4xx/5xx, 0,22%** da amostra, não a origem dominante do consumo. Sete 503 e um 429 ocorreram na solicitação de confirmação push; o refresh ferroviário teve dois 429, um 503 e dois 400. Nenhum desses POSTs foi reproduzido pela auditoria.

### 3.3 Origem e tráfego externo

User-Agent: 6.601 registros okhttp, 67 navegador e cinco outros; três endereços de origem distintos, sem divulgação de IPs. Um IP não identifica uma pessoa e NAT pode agregar vários aparelhos. Também não se pode inferir usuários únicos pela contagem de conexões.

As rotas e o padrão temporal são compatíveis com aplicativo e inspeção Swagger. Não apareceram varreduras típicas de WordPress, `.env`, PHP ou sequências de 404, nem cadência de healthcheck público nesta amostra. **Não há evidência de bots relevantes no material acessível**, o que não comprova inexistência no restante da conta ou nas 399 requisições não retidas. A intenção dos acessos Swagger e a identidade dos clientes permanecem indeterminadas.

## 4. Ranking dos maiores consumidores

Os números abaixo são **medições HTTP do agente**, não ranking global de transferência faturada. URLs foram agrupadas por GUID/código mantendo a análise de recursos individuais separada. “Bytes” são corpos das respostas, sem frames WebSocket nem headers; médias consideram também respostas sem corpo da família.

### 4.1 Ranking por requisições

| Método e rota | Chamadas | Participação HTTP | Bytes médios | MB de corpos |
|---|---:|---:|---:|---:|
| GET `/sentidos/:guid/padroes` | 874 | 13,1% | 274 | 0,239 |
| GET `/paradas/:guid/eventos` | 859 | 12,9% | 14.159 | 12,163 |
| GET `/linhas` | 636 | 9,5% | 996 | 0,634 |
| GET `/paradas/:guid` | 607 | 9,1% | 217 | 0,132 |
| GET `/padroes-versoes/:guid/itinerario` | 551 | 8,3% | 28.129 | 15,499 |
| GET `/radar/sessao` | 451 | 6,8% | 1.025 | 0,462 |
| GET `/linhas/:codigo/sentidos` | 439 | 6,6% | 246 | 0,108 |
| GET `/rail/vehicles/snapshot` | 417 | 6,2% | 18.695 | 7,796 |
| GET `/veiculos/localizacao-unificada/v1/capabilities` | 306 | 4,6% | 3.272 | 1,001 |
| GET `/radar/instalacao` | 281 | 4,2% | 131 | 0,037 |
| POST `/hub/gps/negotiate` | 276 | 4,1% | 316 | 0,087 |
| GET `/hub/gps` — upgrade 101 | 262 | 3,9% | 0* | 0,000* |
| GET `/modais` | 208 | 3,1% | 183 | 0,038 |
| GET `/radar/instalacao/push` | 200 | 3,0% | 59 | 0,012 |
| PUT `/radar/sessao` | 94 | 1,4% | 1.143 | 0,107 |
| POST `/radar/sessao` | 43 | 0,6% | 1.064 | 0,046 |
| GET `/veiculos/padrao-versao/:guid/geometria` | 43 | 0,6% | 12.058 | 0,518 |
| DELETE `/radar/sessao/:guid` | 31 | 0,5% | 0 | 0,000 |
| POST `/radar/instalacao/push/confirmacoes` | 16 | 0,2% | 66 | 0,001 |
| GET `/swagger/v1/swagger.json` | 11 | 0,2% | 122.590 | 1,348 |

\* Zero corpo HTTP capturado após upgrade **não significa zero transferência WebSocket**.

### 4.2 Ranking por bytes HTTP

| Rota | Chamadas | MB de corpos | Participação nos 48,41 MB |
|---|---:|---:|---:|
| `/padroes-versoes/:guid/itinerario` | 551 | 15,499 | 32,0% |
| `/paradas/:guid/eventos` | 859 | 12,163 | 25,1% |
| `/rail/vehicles/snapshot` | 417 | 7,796 | 16,1% |
| `/swagger/swagger-ui-bundle.js` | 4 | 5,704 | 11,8% |
| `/swagger/v1/swagger.json` | 11 | 1,348 | 2,8% |
| `/paradas` | 10 | 1,121 | 2,3% |
| `/veiculos/localizacao-unificada/v1/capabilities` | 306 | 1,001 | 2,1% |
| `/swagger/swagger-ui-standalone-preset.js` | 3 | 0,690 | 1,4% |
| `/linhas` | 636 | 0,634 | 1,3% |
| `/swagger/swagger-ui.css` | 4 | 0,608 | 1,3% |
| `/veiculos/padrao-versao/:guid/geometria` | 43 | 0,518 | 1,1% |
| `/radar/sessao` GET | 451 | 0,462 | 1,0% |
| `/sentidos/:guid/padroes` | 874 | 0,239 | 0,5% |
| `/paradas/:guid` | 607 | 0,132 | 0,3% |
| `/linhas/:codigo/sentidos` | 439 | 0,108 | 0,2% |

Swagger completo soma aproximadamente **8,38 MB**, embora apenas quatro downloads do bundle principal tenham ocorrido. Acesso manual interno ao Swagger evita esse custo; remover documentação funcional não é necessário.

### 4.3 Recursos individuais e canais

Sentidos: linhas 838 = 171 GETs; 855 = 166; 884 = 36; SV866 = 28; TREM-SANTA-CRUZ = 20; 10 = 11; 12 = sete. São sete códigos. Padrões correspondem a 14 sentidos distintos; detalhes de parada a 34 paradas; itinerários a 16 versões.

Duas paradas concentram 290 e 287 consultas de eventos — 577/859, **67,2%**. Para identificá-las sem expor seleção/localização do usuário, os pseudônimos de rota são `df288b6b` e `8c66c537` (oito primeiros caracteres de SHA-256 da URI). Medianas dos intervalos: 31 s e 29 s, compatíveis com o Radar de 30 s somado a mudanças de contexto/retornos. Não demonstram loop ilimitado.

| Canal | Medida disponível | Limitação |
|---|---|---|
| HTTP REST/negociação/upgrade | Tabelas acima | 399 registros não retidos; bytes da borda não acessíveis |
| SignalR unificado, grupos | 2.274.538 publicações; 34.118.200.829 bytes JSON estimados | Inclui grupos sem ouvintes; não multiplicar por clientes presumidos |
| SignalR unificado, caller inicial | 191 publicações; 4.487.965 bytes JSON | Destino único no processo, mas sem prova de caminho público/interno |
| SignalR posição operacional | 552 publicações; 6.044.776 bytes JSON | Grupos; número de destinatários desconhecido |
| SignalR presença física | 32.061 publicações; 18.811.822 bytes JSON | Grupos, inclusive vazios |
| SignalR linha esvaziada | 10 publicações; 214 bytes JSON | Tráfego essencial de invalidação, desprezível em bytes |

Telemetria SignalR acima: resumo de 13/11 21:28 UTC, acumulado desde o processo da API. Não é a mesma janela do agente iniciado em 07/11, nem os contadores devem ser somados entre resumos cumulativos.

## 5. Auditoria do frontend

### 5.1 Inventário dos mecanismos de comunicação

| Arquivo/função | Disparo e endpoints | Frequência / início / parada | Cache, duplicação e necessidade |
|---|---|---|---|
| `F/src/services/api.ts:55` — cliente `api` | GET/POST/PUT/DELETE solicitados pelos serviços | Por chamada; GET aceita AbortSignal | Sem retry/cache global. `REQUEST_TIMEOUT_MS` não é aplicado por este cliente; alguns consumidores têm timeout próprio |
| `F/app/index.tsx:176` + `mobilidadeRio.ts:41,141` — modais | Mount da Home e primeira busca/sugestão chamam `/modais` | Uma leitura por caminho/runtime | Cache existe só dentro de `criarFiltroModalBusca`; não atende a leitura direta da Home. **99 respostas idênticas excedentes no mesmo segundo** |
| `F/app/index.tsx:320`, `app/linhas.tsx:571` — busca | Digitação -> `/linhas?nome=...` e catálogo de modais no primeiro filtro | Debounce 600/400 ms; aborta ao mudar termo/desmontar | Já tem debounce e cancelamento; não foi encontrada chamada por tecla sem espera. Sugestões são carregadas por categoria, não por tecla |
| `F/src/services/mobilidadeRio.ts:143` — sugestões | Entrada/alteração da categoria -> `/linhas` | Mount/mudança de categoria | Sem cache de sugestões entre telas. Separar dessas chamadas a busca digitada e a busca de metadados |
| `F/src/hooks/useMobilidadeRio.ts:221` — itinerário | Linha ativa/restaurada/selecionada -> terminais, sentidos, padrões, itinerários | Por linha não carregada; retry de erro a cada 30 s somente ativo/foreground | Cache e pending por instância do hook; itinerário por versão é compartilhado no serviço. Não há polling HTTP contínuo de GPS nesse hook |
| `F/src/services/estruturaV2.ts:7,60,68,74` | `/linhas/{codigo}/sentidos`, `/sentidos/{id}/padroes`, `/padroes-versoes/{id}/itinerario` | Demandas de mapa, detalhe e catálogo terminal | Itinerários e terminais têm cache de Promise em memória. Sentidos/padrões não têm cache comum. **Não recomendar outro cache igual ao existente** |
| `F/src/hooks/useTerminalRadar.ts:11,37` | GPS/local disponível e Home ônibus/favoritos -> catálogo das linhas de terminal | Sucesso guardado em `pending`; falha tenta após 30 s enquanto enabled; limpeza ao desabilitar | Pode concorrer com `garantirItinerario` para sentidos/padrões. São 18 GETs de padrões excedentes de URL idêntica no mesmo segundo na amostra; causa individual não é rastreável |
| `F/src/services/gpsHub.ts:16,100,158` | Consumidor ativo solicita união das inscrições; negotiate + `/hub/gps` | Uma conexão por runtime; reconnect automático; remove donos; suspende background Android | Proteções já existem. Não foi confirmada criação de uma conexão por componente/linha |
| `F/src/services/unifiedLocation.ts:18` | Qualquer mapa/consumidor com códigos rodoviários -> capabilities + inscrição unificada | Timer 30 s; cache de sucesso 30 s/falha 15 s; para no cleanup ou sem linhas | Single-flight global já existe (`unifiedNetwork.ts:20`). Sem guarda AppState/foco própria. Mediana observada de GETs foi 60 s, portanto não assumir 120 chamadas/h efetivas |
| `F/src/components/mapOSM/mapOSM.tsx:65` | Códigos de ônibus passados ao mapa -> `useUnifiedLocation` | Enquanto mapa montado com linhas | Não recebe `enabled` de foco. Mapas em `app/index.tsx:1003` e `app/linhas.tsx:996` não são renderizados condicionalmente pelo foco |
| `F/src/hooks/useRailRealtime.ts:116` | Linhas ferroviárias visíveis -> `/rail/vehicles/snapshot` | Imediato, depois 5 s após conclusão; só tela habilitada e AppState active; para no cleanup | Uma chamada reúne os filtros; geometria em cache separado. Não existe multiplicação automática de GET por cada trem |
| `F/src/hooks/useRadarParada.ts:37` + `useConsultaEventosParada.ts:33` | Parada automática/manual/detalhe -> `/paradas/{id}/eventos` | Imediato e 30 s após término; cancela timer em blur/background/contexto inválido | Uma consulta compartilhada para compacto/detalhe na Home; single-flight por parada no serviço. Resposta em voo é ignorada quando antiga, mas não abortada pelo cleanup |
| `F/src/hooks/useRadarParada.ts:41,50` | Metadados da parada escolhida ou seletor de até 12 candidatos | Por demanda; duas leituras simultâneas no seletor | `radarParadaMetadata.ts:4` tem cache por ID, até 100 entradas; timeout 12 s. Não é uma requisição por card/veículo |
| `F/src/hooks/useRadarEspera.ts:119,136` e `app/_layout.tsx:43` | Mount/foreground -> reconciliação remota | Por evento de ciclo de vida, sem polling de chegadas | Fila serializa comandos, mas não agrupa reconciliações já pendentes. Layout e hook solicitam reconciliação no início |
| `F/src/services/radarRemotoCore.ts:109,147` | Reconciliação/configuração/ativação/encerramento | GET instalação + sessão; status push se ativo; GET+PUT/POST ao configurar; recuperação 409 limitada | Manter revisão/credencial/posse/expiração. Não eliminar GET de controle de concorrência sem preservar contrato |
| `F/app/linhas.tsx:749`, `mobilidadeRio.ts:319,331` | Seleção de percurso/POI -> `/pois/por-itinerario/{id}`, `/pois/por-parada/{id}` | Por interação, sem loop | Zero hits na amostra; ausência dessas rotas no Swagger atual. É pendência de compatibilidade, não grande fonte de consumo demonstrada |
| `F/src/hooks/useFavoritos.ts:5` e serviços de preferências | Favoritos/tema/modal/voz -> AsyncStorage/SecureStore | Local | Nenhum backend para sincronizar favoritos/configuração observado |

TanStack React Query está instalado, mas não há `QueryClient`, `useQuery`, políticas de staleTime ou retries dessa biblioteca no fluxo inspecionado. Não atribuir retries à dependência só por constar no package.json. O fetch genérico também não faz retry automático explícito.

### 5.2 Achados de ciclo de vida e duplicação

**F1 — Modais duplicados, confirmado.** Home lê diretamente `buscarModais`; sugestões passam pelo cache privado do filtro, cujo loader chama a mesma função. Compartilhar o carregamento válido na função comum evita dois GETs iniciais sem mudar sugestões, modais ou frequência crítica. A economia demonstrada é 99 requisições, apenas ~18 kB de corpos; seu valor está na cota de chamadas.

**F2 — Reconciliação Radar duplicada, confirmado quanto às chamadas; ganho condicionado à operação.** Foram 21 GETs de sessão e 19 de instalação repetindo URL, segundo, credencial e hash do corpo. Layout, hook montado e eventos de foreground solicitam reconciliação; a fila em `radarRemotoCore.ts:67` executa todos. Agrupar somente reconciliações simultâneas equivalentes é candidato; não agrupar ativação, edição ou encerramento, nem usar uma resposta anterior a uma mutação. Economia observada potencial: 40 GETs, 0,6% da amostra.

**F3 — Capabilities e inscrições de mapa sem foco explícito, confirmado no código; impacto provável.** `useUnifiedLocation` mantém timer enquanto `hasRoadLines` e habilitação global forem verdadeiros. Home continua passando suas linhas em `app/index.tsx:790` mesmo sem foco; `app/linhas.tsx:812` também não esvazia seus dados por blur. As Tabs não pedem desmontagem em blur (`app/_layout.tsx:70`). Em runtime que mantenha a tela montada, sair do mapa deixa esse consumidor ativo. A suspensão global Android protege o hub no background, mas não o timer HTTP; timers JS podem ser suspensos pelo SO, por isso não há estimativa de tráfego contínuo garantido em background. Correção futura deve passar visibilidade ao consumidor e preservar a retomada imediata.

**F4 — Cache entre runtimes, confirmado.** O cache por versão já evita concorrência e downloads repetidos dentro do processo. A captura mostra 535 repetições idênticas entre usos, mas não prova que todos ocorreram dentro do mesmo runtime. Cache persistente por `padraoVersaoId`, com integridade, limite de armazenamento e política para mudanças de versão, ataca exatamente essa repetição. Sentidos e padrões precisam de revalidação porque a versão atual pode mudar.

**F5 — Respostas abandonadas, confirmado; contribuição não medida.** O hook de trens e a consulta Radar descartam resposta antiga, mas não encaminham abort do cleanup à requisição em andamento. Troca rápida de contexto pode concluir um download sem uso. É desperdício limitado por requisição em voo, não um loop confirmado. Abort tardio também não garante recuperação de bytes já transmitidos.

**Desenvolvimento versus release.** Fast Refresh, reinício do bundle e reinstalação eliminam caches em memória e abrem novas conexões. Não foi encontrado `StrictMode` explicitamente ativado no layout inspecionado; não se atribuiu duplicação a ele. Os acessos okhttp não identificam release, Dev Client ou versão do APK. Parte do padrão de desenvolvimento pode diminuir em release; as duplicidades de loaders e a falta de foco não dependem de `__DEV__`.

### 5.3 Radar, áudio e notificações

O polling de eventos existe para a apresentação aberta, a cada 30 s, com um relógio local de 1 s. O relógio **não** chama HTTP. O Radar remoto usa comandos de configuração e push; não realiza polling de chegadas no Android em segundo plano. O antigo risco de reconciliação por token repetido já tem comparação em memória em `radarRemotoAndroid.ts:276`; não propor como nova correção algo já presente.

O timer de 2 s em `radarAvisosPorVoz.tsx:169` consulta a rota de áudio nativa, não a API. O módulo Kotlin usa Firebase Messaging, notificação, temporização local e TTS; a busca por clientes HTTP/URLs não encontrou consulta ngrok nesses caminhos. Áudio/notificação local não deve ser contado como uma chamada por anúncio. Token Expo/FCM e entrega push usam serviços externos diretamente; só enrollment/configuração/recuperação de sessão atravessam a API pública.

Não foi comprovado vazamento do polling de eventos ao fechar a tela. Múltiplas linhas e veículos na mesma parada não criam um polling por item: o endpoint entrega a coleção e o frontend filtra.

## 6. Auditoria do backend

### 6.1 Endpoints e contratos

| Área | Evidência | Avaliação |
|---|---|---|
| Catálogo de linhas | `B/1-API/Controllers/EstruturaLinhasController.cs:21`; repository `EstruturaLeituraV2Repository.cs:139` | Paginação já limitada a 100, filtros no servidor e projeções. Não recomendar paginação que já existe |
| Sentidos/padrões | `EstruturaLinhasController.cs:45`, `EstruturaSentidosController.cs:10` | Respostas pequenas; custo é repetição de GET, não campos enormes. Falta política de cache HTTP nesses métodos |
| Itinerários | `PadroesVersoesController.cs:10`; repository `:230–267` | Por versão; geometria e ocorrências juntas. Frontend usa ambos. Não retransmitir em cada sessão se já tem a versão localmente |
| Detalhe de parada | `EstruturaParadasController.cs:35` | Média 217 bytes; 607 GETs para 34 IDs. Cache cliente já existe, repetição entre runtimes ou caminhos antigos precisa ser distinguida |
| Relações compactas | `EstruturaLinhasController.cs:12`, `EstruturaParadasController.cs:14` | Já existem e estão publicadas: um hit de cada na amostra. Não substituem a geometria do itinerário; não justificar migração total por benchmark sintético antigo |
| Eventos | `EventosParadaController.cs:13`; `EventosParadaService.cs:18` | Retorna todos os contextos atendidos pela parada; consulta Postgres/Redis/snapshot ferroviário. Sem aquisição HTTP externa neste fluxo. Filtrar só o contexto solicitado pode reduzir bytes, mas precisa preservar seletor/linhas prioritárias |
| Ferrovia | `RailVehiclesController.cs:20` | Snapshot compartilhado em memória, filtros `linhaId` e `sentidoId`, geometria separada. Não consulta TrensRJ por cliente em cada GET |
| Geometria ferroviária | `F/src/services/railRealtime.ts:55,77`; controller de veículos | Cache Promise por versão no frontend, separado do cache de itinerários. Pode haver geometria igual por dois contratos, mas 0,52 MB na amostra: prioridade menor |
| Capabilities | `UnifiedLocationController.cs:14` | **Já tem** `Cache-Control: public, max-age=30`; cliente tem TTL/single-flight. Persistência da consulta fora de contexto é o ponto a corrigir primeiro |
| Radar remoto | `Middlewares/RadarHttpFilter.cs:15`, `RadarPushController.cs:36` | `no-store` intencional por credencial/sessão. Não aplicar cache público às respostas de instalação/sessão |

**B1 — Compressão ausente na origem, confirmado.** `Program.cs` não registra `AddResponseCompression`/`UseResponseCompression`; as 6.673 respostas capturadas não tinham `Content-Encoding`. Os clientes de dados públicos enviaram Accept-Encoding compatível (4.938 registros gzip e demais navegadores também aceitando gzip). A medição interna confirmou ausência no itinerário. Não foi possível verificar eventual transformação da resposta na borda da conta; o relatório mede a origem/agente. Proposta futura: compressão seletiva de JSON público e arquivos Swagger, verificando o recebimento no cliente. Não incluir indiscriminadamente credenciais Radar em cache/compressão compartilhada.

**B2 — Falta filtro multisseleção ferroviária, confirmado como incompatibilidade de contrato, sem consumo extra observado nessa amostra.** O frontend envia `linhaIds` em `railRealtime.ts:67`; o backend aceita apenas `linhaId`/`sentidoId`. Com múltiplas linhas, sem `linhaId` singular, o backend retorna a rede e o frontend filtra depois. Porém **todos os 417 requests capturados tinham ambos `linhaId` e `linhaIds`**, e os corpos continham uma única linha com todos os veículos dentro da seleção. Não atribuir os 7,80 MB a esse defeito. Implementar o filtro múltiplo futuramente preserva uma chamada por ciclo e reduz bytes somente no cenário multisseleção.

**B3 — DTO operacional no stream unificado, oportunidade coordenada.** `UnifiedVehicleDto` inclui `PosicaoVeiculoDto? Operational` (`UnifiedLocation.cs:60`), enquanto o stream operacional envia `FrontendLegacyPosicaoSignalRDto`, com **28 propriedades**, inclusive `ItinerarioId` repetindo `PadraoVersaoId` (`FrontendLegacyDtos.cs:34`). Há redundância estrutural. Entretanto `hybridContext.ts:68–92` usa estado, timestamps, linha, padrão, sentido e coordenadas operacionais para validar projeção. Não remover o objeto inteiro ou o stream antigo sem tabela de campos consumidos e fallback para BRT/linhas sem unificado.

**B4 — HTTP cache e Redis resolvem problemas diferentes.** Redis guarda posições/estado e evita novas aquisições por cliente; não impede que o JSON seja retransmitido ao Android. Endpoints de estrutura não apresentam ETag/Last-Modified/Cache-Control específico. Dezessete respostas de arquivos estáticos Swagger apresentaram validadores, mas não houve 304 retido. Para versão imutável, cache local válido evita tanto GET quanto bytes; ETag reduz corpo quando precisa revalidar, mas a requisição 304 continua consumindo a cota HTTP.

### 6.2 GPS, workers, trens e push

`GpsSignalRPublisher.cs:24,53–89` retorna quando não há assinaturas operacionais e só envia grupos das linhas assinadas. Recompõe a lista válida completa da linha, inclusive itens recentes, e emite vazio uma vez por geração. `GpsHub.cs:53–90` remove conexões e recalcula linhas ativas. **Não foi confirmado broadcast operacional para todos os usuários/rede.** Não foram observadas chamadas HTTP de `/veiculos/linha/{codigo}` na amostra, e a função frontend correspondente não tinha callers produtivos encontrados além de sua declaração. Portanto não há prova de polling HTTP GPS duplicado com SignalR.

O stream unificado publica snapshots completos por linha em `CanonicalPhysicalLocation.cs:178` e `UnifiedLocation.cs:354`. O snapshot inclui `ComposedAt` e dados de validade/associação, que podem mudar sem deslocamento. Hash diferente não demonstra informação útil nova para todos os consumidores. Pular publicações sem mudança exigiria comparar estado semântico, preservar expiração/remoção e não renovar evidência antiga; não é um quick win trivial.

Radar backend: `RadarWorker.cs:20` chama `IRadarEngine`, que organiza processamento por parada. `RadarStopProcessor.cs:17–34` lê sessões e consulta eventos **uma vez para a parada**, reutilizando para os destinatários; usa o serviço interno diretamente, não faz GET no ngrok. Envio/retry/recibos Expo (`ExpoPushGateway.cs:51,113`) são HTTP direto para o gateway externo. Multiplicar todos os ciclos Radar/GPS pelo número de usuários para calcular franquia seria incorreto.

Trens: aquisição automática e refresh manual usam serviços/budgets internos; o GET do snapshot não dispara aquisição a cada cliente. O POST manual realmente atravessa ngrok quando iniciado pelo app, mas teve somente seis hits. Cooldown, quotas e reuso já existem; não propor repetição automática desse refresh para obter economia. BRT participa do pipeline rodoviário e do hub operacional; não foi identificado canal Metro dedicado com consumo relevante no período.

### 6.3 Logs e divergência de janelas

Foram lidas amostras limitadas de `docker logs --timestamps --tail 25000 noponto_api`. A janela de uma leitura cobriu aproximadamente 21:20–21:28 UTC de 13/11. Não havia eventos ASP.NET `Request starting/finished`; logo esses logs não produzem ranking HTTP completo nem atribuem IP/cliente. Os maiores emissores eram fontes GTFS-RT, EF, GPS e serviços ferroviários: quantidade de linhas de log não equivale a quantidade de requisições ngrok.

Os resumos `SignalRTraffic` estão ativos (`Enabled=true`, `SummarySeconds=60`), assim como ambiente Production, PhysicalPresence canônico, Radar e runtime ferroviário. Configuração GPS de 15 s e presença física de 30 s foram lidas sem divulgar outras variáveis. Os logs Docker usam rotação `20m × 3`. Journal do serviço ngrok não estava acessível ao usuário SSH; tentativa de leitura com `sudo -n` também não foi autorizada. Não houve elevação interativa, reinício ou mudança de logging.

## 7. SignalR e tráfego persistente

### 7.1 Requisição, mensagem, conexão e bytes

O fluxo observado usa **276 POSTs negotiate e 262 GETs com status 101**. Todos os GETs do hub retidos terminaram em upgrade; não há assinatura de long polling/SSE gerando sucessivos GETs/POSTs de transporte na amostra. As 14 negociações excedentes não provam falha de 14 conexões: algumas etapas podem estar fora da retenção.

Cada negociação e abertura/reabertura HTTP é uma requisição. Depois do upgrade, mensagens SignalR são frames sobre a conexão, não novos requests HTTP. Para o orçamento usa-se aproximadamente dois requests por abertura no fluxo atual, mais novas aberturas/recuperações. A documentação confirma suporte nativo a WebSockets; a regra comercial específica da conta e o detalhamento de cobrança de upgrades não puderam ser conciliados com o billing. **Não foi contado um request por mensagem.** [WebSockets no ngrok](https://ngrok.com/docs/gateway/endpoints/http).

Os bytes das mensagens de saída devem entrar no orçamento de transferência, mesmo sem novos GETs. Não se deve aplicar a cota de 5.000 conexões TCP/TLS diretamente às mensagens WSS deste endpoint HTTPS. O número de conexões de transporte acumuladas do agente também não substitui a métrica HTTP faturada.

**Atenção à duração:** o campo `duration` de uma captura 101 reflete o processamento da resposta de upgrade observado, não fornece a duração comprovada da sessão WebSocket. Os valores de milissegundos não foram interpretados como reconexões a cada milissegundo.

### 7.2 Conexões, grupos e possível duplicação

O singleton `gpsHub.ts:16` compartilha uma conexão por runtime; mapas e presença usam donos, união de conjuntos e reconciliação serial. Android em background interrompe a conexão e limpa inscrições confirmadas. Ao reconectar, os grupos são reinscritos. Não foi encontrada evidência inequívoca de vazamento de conexão ou loop infinito no cliente atual.

Há upgrades próximos e repetidos no histórico, inclusive no mesmo segundo. Sem versão do APK, identidade de runtime, motivo de fechamento e duração real, podem ser múltiplos runtimes, reinícios de desenvolvimento, foreground/background ou reconexões. **Reconexão excessiva é hipótese; não diagnóstico fechado.**

A mesma linha de ônibus pode entrar no grupo operacional e no unificado: `useMobilidadeRio` solicita códigos rodoviários e `MapaOSM` também solicita `unified:<codigo>`. A duplicação de transporte de parte da informação é **confirmada pelo código**, mas os dois contratos têm papéis distintos e nem todas as linhas/modalidades têm cobertura unificada. Consolidar demanda e payload exige homologar matching, remoção, veículo trocando de linha, dado físico sem rota e BRT. Não cortar o fallback apenas porque existe outro stream.

### 7.3 Tamanho de snapshots e lacuna dos destinatários

Medições internas pontuais, sem atravessar ngrok:

| Linha unificada | Veículos naquele instante | JSON HTTP | gzip offline | Subobjetos `operational`, serialização compacta aproximada |
|---|---:|---:|---:|---:|
| 838 | 20 | 36.714 bytes | 5.053 bytes | 20.133 bytes |
| 855 | 23 | 40.323 bytes | 5.601 bytes | 21.233 bytes |

São amostras atuais de `/veiculos/localizacao-unificada/v1/{code}`, não frames entregues durante os seis dias. Opções JSON HTTP e SignalR/framing podem diferir. A fração operacional próxima de metade sugere onde auditar campos, sem provar que possam ser descartados.

Na telemetria de 21:28 UTC, uma janela de 60 s registrou 1.563 publicações unificadas de grupo distribuídas pela rede, embora não houvesse cliente público ativo na primeira leitura do agente. Grupos individuais exibiam exemplos de cinco a nove publicações por minuto e médias entre poucos kB e mais de 60 kB. O contador é anterior ao fan-out efetivo, e `base.SendGroupAsync` pode concluir com zero membros.

`SignalRTraffic.cs:46–52,101–127` serializa o argumento de payload para estimar bytes; não observa entrega por socket nem conta membros do grupo. `MaxGroups=128` por padrão agrega excedentes em `overflow`, perdendo atribuição por linha e comparação de duplicatas nesses grupos. `ConsecutiveIdentical=0` não prova ausência de repetição semântica, especialmente com timestamps de composição diferentes.

**Instrumentação futura mínima, dependente de aprovação para implementar:** bytes por conexão efetivamente escrita/transportada, destinatários por grupo, abertura/fechamento por motivo, versão do cliente e separação público/interno; somente contagens/pseudônimos, sem payload/token. Antes disso, conciliar o billing/Traffic Inspector da conta pelo MCP em sessão com as ferramentas anexadas. Não ativar captura de corpos nem simplesmente aumentar logs.

## 8. Consumo necessário versus desnecessário

| Classificação | Operação | Motivo e confiança |
|---|---|---|
| Necessário | GPS dinâmico das linhas visíveis, expiração e linha vazia | Preserva atualização, remoção e confiança; confirmado |
| Necessário | Eventos de parada visíveis e atualização ferroviária | Usados por previsão, estado operacional e apresentação; frequência pode mudar de protocolo sem perder atualização |
| Necessário | Enrollment/posse, revisão de sessão, renovação e recuperação | Segurança e consistência do Radar; não remover indiscriminadamente |
| Necessário | Aquisição externa compartilhada / Redis / Postgres / Expo Push | Mantêm funcionalidade e não consomem este túnel diretamente |
| Desperdício demonstrado em conteúdo | 535 respostas de itinerário iguais a uma das 16 já transferidas | 15,11 MB repetidos; parte é recarregamento esperado sem persistência, não bug de loop |
| Desperdício demonstrado no mesmo contexto de leitura | 99 modais + 40 leituras Radar idênticas no mesmo segundo | 139 GETs candidatos à coalescência; preservação de barreiras de mutação obrigatória |
| Provável desperdício | Capabilities/telas ocultas mantendo demanda unificada | Lacuna de foco confirmada; duração/bytes evitáveis não medidos |
| Oportunidade, não desperdício funcional | JSON sem compressão | Dados necessários podem usar menos bytes sem alterar conteúdo |
| Hipótese | Reconexão excessiva / APK antigo / vários runtimes | Sem identificação de versão e motivos de fechamento |
| Não demonstrado | Bots, polling GPS HTTP duplicado, vazamento do polling Radar em background | Amostra/código não sustentam essas acusações |
| Baixo retorno | Eliminar status vazio, mexer em TTS/localização local, reduzir coleta GPS para poupar ngrok | Ganho nulo/pequeno ou prejudica funções sem atacar o túnel |

## 9. Propostas de otimização

Propostas somente para sprint futura. Categorias: **A** quick win; **B** ajuste intermediário; **C** estrutural; **D** preservar/baixo ganho. Reduções se referem à amostra ou cenário indicado, não a promessa percentual da fatura.

| Ordem / categoria | Problema e causa / localização | Proposta e benefício | Requisições / transferência potencial | Esforço, risco e UX | Dependências / confiança |
|---|---|---|---|---|---|
| 1 — A | Dois loaders de modais; F1 | Compartilhar Promise/cache válido em `buscarModais`, reusar no filtro/Home | 99 GETs demonstrados (~1,5%); ~18 kB | Pequeno, baixo; mesma UX | Teste de concorrência/falha; alta |
| 2 — A | Reconciliação Radar enfileirada repetida; F2 | Single-flight só para reconciliação equivalente, respeitando mutações | Até 40 GETs demonstrados; dezenas de kB | Pequeno/médio, baixo se barreiras preservadas; mesma recuperação | Testar mount+foreground+mutação; alta para duplicidade, média para economia |
| 3 — A | Demanda unificada sem visibilidade; F3 | Informar enabled/foco/foreground a mapa/hook, limpar dono e retomar imediatamente | Até um GET a cada expiração do TTL por runtime ocioso; bytes dos grupos ocultos não medidos | Pequeno/médio; risco de retomada/fallback; nenhum atraso deliberado | Validar duas abas e background em Android; alta código, baixa volume |
| 4 — B | JSON público sem compressão; B1 | Compressão seletiva na origem, limiar de tamanho e validação no cliente | Zero GETs; três rotas principais: 27,68 MB de redução offline | Pequeno/médio; CPU a verificar; conteúdo/frequência intactos | Aceitação gzip já observada; HTTPS/caching correto; alta compressibilidade |
| 5 — B | Versões baixadas em cada runtime; F4 | Cache persistente por versão, limite/integridade, descobrir versão atual separadamente | Teto histórico 535 GETs e 15,11 MB; ganho real depende de reaproveitamento | Médio; risco de estrutura antiga se invalidação errada; melhora abertura | Contrato de imutabilidade/versionamento; alta repetição, média execução |
| 6 — B | Metadados sem cache comum entre loaders | Compartilhar sentidos/padrões/paradas válidos; revalidar ponteiro de versão | Teto histórico 860 padrões + 432 sentidos + 573 paradas; corpos pequenos | Médio; evitar congelar versão atual; UX mais rápida | Política de validade e testes troca versão; média |
| 7 — A/D | Swagger pesado usado pelo endereço público | Usar acesso interno autorizado na manutenção; preservar documentação | ~8,38 MB observados se todos esses acessos forem internos no futuro; poucas chamadas | Operacional pequeno; sem efeito no app | Acesso Tailscale; alta bytes, intenção dos acessos desconhecida |
| 8 — B | Resposta antiga continua em voo; F5 | Encaminhar AbortSignal na troca/saída | Até uma chamada em voo por troca; bytes restantes variáveis | Pequeno; sem penalizar consumo compartilhado de outro hook | Testar troca rápida/mesma parada; média |
| 9 — B | `linhaIds` ignorado; B2 | Aplicar união de IDs no servidor, manter filtros singulares compatíveis | Zero economia demonstrada nos 417 hits; evita bytes de linhas não escolhidas no cenário múltiplo | Pequeno/médio, baixo; melhora precisão do filtro | Teste real de duas linhas e seleção vazia; alta contrato |
| 10 — B/C | Duas projeções da mesma linha em SignalR; B3 | Contrato/inscrição que entregue o necessário uma vez, com fallback explícito | HTTP quase igual; bytes potencialmente relevantes, sem porcentagem comprovada | Médio/alto; risco para GPS físico/operacional e BRT | Inventário de campos, cobertura, métricas de entrega; média |
| 11 — C | Snapshot ferroviário via GET de 5 s por cliente | Publicar snapshot/alterações por grupos no hub existente; bootstrap/recovery HTTP | Até ~720 GETs/h por cliente substituídos; bytes continuam existindo | Médio/alto; precisa retomada, expiração, compatibilidade | Versão de contrato/cliente; alta aritmética, implementação não validada |
| 12 — C | Snapshots completos repetem metadados | Compactar DTO e/ou diffs com ressincronização | Zero promessa HTTP; economia de bytes a medir por campo/mensagem | Alto; remoções/ordenação/perda/reconexão aumentam complexidade | Medir antes; baixa economia global |
| 13 — C | Franquia de desenvolvimento usada como produção | Avaliar domínio/proxy, túnel nomeado alternativo ou plano pago | Remove/transfere o gargalo comercial; não reduz desperdício por si só | Médio/alto; operação e segurança | Estudo da seção 13; decisão posterior |
| 14 — D | Workers externos, push, relógios locais | Manter cadências e proteções atuais | Nenhuma economia ngrok relevante ao reduzi-los | Cortar prejudicaria atualização/áudio sem benefício | Confirmado caminho fora do túnel |

## 10. Quick wins

Pacote inicial recomendado: compartilhar `/modais`; coalescer reconciliação Radar sem atravessar mutações; ligar consumidores unificados à visibilidade; orientar manutenção a usar Swagger interno.

São mudanças independentes e pequenas. Critérios de aceite: um GET de modais por cache válido; uma reconciliação por evento concorrente equivalente; nenhum GET de capabilities nem grupo de mapa invisível quando não houver outro consumidor legítimo; retomada imediata ao voltar; Radar remoto em background continua recebendo push e áudio.

Não aumentar TTL de capabilities às cegas: há cache de 30 s já implementado. Primeiro remover a demanda sem tela e medir o custo restante. Não reduzir frequência GPS/chegadas para obter esses ganhos.

## 11. Melhorias de maior complexidade

**Persistência estrutural e contratos compactos:** começar pelo itinerário versionado, cuja repetição foi demonstrada, antes de trocar todas as relações pelo endpoint novo. Sentidos/padrões precisam descobrir publicação nova. Aproveitar AsyncStorage já instalado; não é necessário introduzir biblioteca de cache apenas para esse caso. Definir descarte por versão e recuperação de cache corrompido.

**SignalR ferroviário:** é a alteração com maior economia estrutural de requests para usuário frequente de trem. Reusar o hub ou canal existente com grupos de linhas, versão/geração do snapshot e bootstrap; manter expiração e estado Scheduled/Estimated/Live. O backend já tem snapshot compartilhado em memória; não criar coleta externa por inscrito. Não exige enviar uma mensagem por trem se um lote por grupo resolver.

**Unificar streams rodoviários:** só vale avançar após medir entrega real e demonstrar equivalência de dados para consumidores. O payload `operational` tem uso concreto na arbitragem; compactar campos sem estudo pode remover informação necessária à confiabilidade. Diffs só depois de um snapshot compacto completo: precisam tombstones, sequência e ressincronização para não manter veículo removido.

**Compressão WebSocket:** investigar negociação/configuração específica de WebSocket e protocolo antes de recomendar; middleware de resposta HTTP não resolve frames após upgrade. Considerar custo de CPU e compatibilidade. MessagePack ou DTO binário é mudança de protocolo, não quick win presumido.

## 12. Estimativa de economia

### 12.1 Potencial medido por conteúdo HTTP

Compressão gzip padrão aplicada offline a cada corpo, sem mudar servidor:

| Família | Original | gzip offline | Redução de corpo |
|---|---:|---:|---:|
| Itinerários | 15,499 MB | 5,173 MB | 10,326 MB / 66,6% |
| Eventos de parada | 12,163 MB | 1,411 MB | 10,752 MB / 88,4% |
| Snapshot ferroviário | 7,796 MB | 1,193 MB | 6,603 MB / 84,7% |
| **Três famílias** | **35,458 MB** | **7,777 MB** | **27,680 MB / 78,1%** |

São 57,2% dos corpos HTTP da amostra economizáveis na simulação dessas três famílias. Gzip de todos os GETs públicos fora de `/radar`/`/hub` — incluindo Swagger — passou de **47,660 MB para 11,064 MB**: 36,596 MB de diferença. Comprimir pequenos corpos pode não compensar e arquivos já compactados podem crescer; a tabela ampla é limite de simulação, não configuração recomendada sem limiar.

Cache persistente ideal das 16 versões evitaria 15,113 MB originais. Não somar esse número integralmente à compressão: as repetições comprimidas equivalem a 5,041 MB. Aplicando ambos no conjunto amplo: **36,596 + 5,041 = 41,636 MB** de redução simulada, e não 51,709 MB.

### 12.2 Cenários de sprint, com premissas

| Cenário | Requisições na amostra de 6.673 | Corpos HTTP na amostra de 48,41 MB | Premissas / confiança |
|---|---|---|---|
| Conservador | 99–139 a menos, 1,5–2,1% | Duplicidades poupam pouco; se metade do ganho gzip das três rotas se concretizar: ~13,8 MB | Coalescência correta e compressão parcial; média. Sem crédito por background não medido |
| Provável, após cache/compressão | Meta de 10–20% a menos (~670–1.330) | Meta de 25–32 MB a menos (~52–66%) | Reaproveitamento parcial de versões/metadados e boa aceitação gzip; cenário, não medição pós-correção |
| Otimista, teto histórico | Até ~2.539 a menos, 38,0% | Até ~41,6 MB (~86%) na simulação ampla | Cache ideal de padrões/sentidos/paradas/versões e compressão dos GETs públicos; exige invalidação correta; baixa como previsão real |

Teto de requests de cache: `(874−14) + (439−7) + (607−34) + (551−16) + 99 + 40 = 2.539`. É um contrafactual de reaproveitar recursos durante toda a janela, que não garante validade para ponteiros mutáveis e múltiplos dispositivos. Não inclui supressão de polling crítico nem elimina inicialização do primeiro cliente.

**Não converter essas porcentagens em economia da conta inteira.** Faltam frames entregues, 399 requests e billing atual. A diferença nominal `183,83 − 48,41 = 135,42 MB` não é uma medição de WebSocket: períodos, headers, borda e retenção diferem. É apenas uma lacuna de reconciliação. Se WebSocket dominar, otimizar só HTTP não resolverá transferência.

## 13. Sustentabilidade do ngrok gratuito

### 13.1 Projeção do padrão encontrado

Para requests, a aproximação `7.072 / 6,191 = 1.142/dia` usa o tempo desde o serviço iniciar. É uma média do agente, não do ciclo completo da conta. Para bytes, usar `183,83 / 6,191 = 29,7 MB/dia` **é condicional**: pressupõe que o valor informado pelo usuário corresponde ao mesmo endpoint/período e continua atual. Isso não pôde ser confirmado.

| Orçamento / projeção | Resultado condicional |
|---|---|
| Utilização de requests | 35,36% se o contador do agente representar o ciclo da conta |
| Utilização de bytes | 18,38%, usando os 183,83 MB informados e GB decimal |
| Saldo nominal | 12.928 requests; 816,17 MB |
| Até esgotar requests, no ritmo acima | **11,3 dias adicionais**; aproximadamente 25/11, a partir da leitura de 13/11 |
| Até esgotar bytes, no ritmo acima | **27,5 dias adicionais**, somente sob a premissa de período comum |
| Duração de uma franquia vazia nesse ritmo | 17,5 dias por requests; 33,7 dias por bytes |
| Ritmo equivalente de 30 dias | ~34.300 requests; ~891 MB condicionais |
| Reserva operacional proposta | 20%: trabalhar com 16.000 requests e 800 MB/mês |
| Saldo até atingir a reserva, partindo da referência | 8.928 requests e 616,17 MB; ~7,8 e ~20,8 dias, respectivamente |

**Requests tendem a acabar primeiro no padrão observado.** Se o reset for 01/12 e não houver outro consumo, continuar a média pós-início até lá levaria a aproximadamente 26–27 mil requests no ciclo, antes de completar novembro. Dividir pelos dias de novembro desde 01/11 daria média menor, mas pressupõe ausência de tráfego anterior e dilui os dias sem endpoint; não é previsão adequada de manutenção do ritmo atual.

### 13.2 Custo adicional por cenário de uso

São estimativas por cliente, com uma conexão, excluindo bootstrap, reconexões e outras abas. Timers aguardam conclusão, logo as frequências abaixo são aproximações superiores em rede saudável.

| Cenário | Requests dinâmicos/h | Corpos HTTP/h | Transferência persistente adicional |
|---|---:|---:|---|
| App sem linhas nem parada elegível | Perto de zero após inicialização | Próximo de zero | Não deve haver grupos sem demanda |
| Mapa ônibus com uma linha, sem evento elegível | Até ~120 capabilities; observado historicamente com mediana 60 s | Até 0,393 MB | Depende dos dois streams e dos veículos da linha |
| Ônibus + Radar visível em uma parada | Até ~240 = 120 capabilities + 120 eventos | ~2,09 MB, usando médias capturadas | Streams GPS; não um request por veículo |
| BRT + Radar | ~120 eventos; capabilities do mapa filtra somente ônibus | ~1,70 MB | Stream operacional da linha |
| Trem visível | ~720 snapshots | ~13,46 MB | Não há stream ferroviário específico usado pelo hook |
| Trem + Radar visível | ~840 = 720 + 120 | ~15,16 MB | Geometrias carregadas separadamente e cacheadas |
| Várias linhas ônibus | Capabilities compartilhado, eventos por parada, não por linha | Cresce conforme resposta | Soma dos payloads dos grupos inscritos; conexão continua compartilhada |
| Radar remoto em background, fluxo correto | Comandos ocasionais; sem polling de chegadas | Configuração/recuperação apenas | Push direto Expo/FCM, fora do túnel |
| Retorno de inatividade | Reconciliação Radar, reabertura do hub e eventos iniciais | Depende do cache/runtime | Duas requisições HTTP por abertura como aproximação; mensagens iniciais adicionais |

O custo de aquisição externa e processamento compartilhado não deve ser multiplicado por cliente no orçamento ngrok. Já **cada entrega** de snapshot ao cliente é transferência adicional, mesmo quando o cálculo do servidor foi compartilhado.

### 13.3 Um usuário ocasional, frequente e 5/10/20 simultâneos

Modelo hipotético explícito para ônibus + Radar, sem otimizações: bootstrap de **20 requests e 0,25 MB por abertura**; uma abertura/conexão por sessão; 240 GETs/h dinâmicos; stream unificado **20 kB a cada 8 s = 9 MB/h**; operacional **10 kB a cada 15 s = 2,4 MB/h**; eventos `120 × 14.159 bytes = 1,70 MB/h`; capabilities `120 × 3.272 = 0,393 MB/h`. Total aproximado **13,49 MB/h**, mais bootstrap. Os tamanhos/cadências de streams são premissas representativas da ordem de grandeza vista nos logs, não medição de entrega de uma linha específica.

Fórmulas: `R = aberturas × (20 + 2) + horas × 240`; `MB = aberturas × 0,25 + horas × 13,49`. Sem multiplicação da coleta externa do servidor. Não inclui headers, keepalives, reconexões, múltiplas linhas ou manutenção; daí a reserva.

| Perfil | Horas/mês | Aberturas/mês | Requests/mês | MB/mês aproximados |
|---|---:|---:|---:|---:|
| Um ocasional: 20 min, dez dias/mês | 3,33 | 10 | ~1.020 | ~47 |
| Um frequente: 1 h/dia, 30 dias | 30 | 30 | ~7.860 | ~412 |
| Cinco frequentes no mesmo horário | 150 cliente-horas | 150 | ~39.300 | ~2.061 |
| Dez frequentes no mesmo horário | 300 cliente-horas | 300 | ~78.600 | ~4.122 |
| Vinte frequentes no mesmo horário | 600 cliente-horas | 600 | ~157.200 | ~8.244 |

Um pico isolado de uma hora com 5/10/20 clientes nesse modelo custa aproximadamente **1.310 / 2.620 / 5.240 requests** e **69 / 137 / 275 MB**. Isso não é limite de conexões simultâneas; é consumo acumulado. Se as sessões forem curtas ou só remotas em background, o resultado muda muito.

Com orçamento de 16.000/800 MB, o modelo exclusivo comporta aproximadamente **15 ocasionais** ou **um frequente** antes de esgotar uma das reservas; para ocasionais requests limitam primeiro, para frequente ônibus a transferência assumida limita primeiro. Isso **não é capacidade medida do NoPonto**, nem inclui outros consumidores da conta. Para trem frequente, apenas snapshot de 5 s por 30 h já ultrapassa os 20.000 requests.

Após quick wins e cache estrutural, a componente dinâmica permanece. Não há evidência para prometer 5–20 frequentes no gratuito. O limite também muda com número de veículos/linhas e tamanho real dos frames. Recalcular com destinatários/bytes efetivos antes de anunciar capacidade.

### 13.4 Alternativas arquiteturais, sem migração

| Alternativa | Complexidade / custo | Segurança e confiabilidade | SignalR / limites / indicação |
|---|---|---|---|
| Manter Free com correções | Baixa; sem novo custo de plano | Mantém dependência de franquia e aviso interstitial; adequado a desenvolvimento | WSS já funciona; 20k/1GB continuam restritivos. Bom para piloto ocasional com medição |
| Plano ngrok superior | Baixa mudança de arquitetura; preços/condições a confirmar no contrato | Operação atual preservada; não elimina payload repetido | Mais orçamento, sem presumir ilimitado. Comparar custo real com hospedagem |
| Domínio próprio + proxy reverso HTTPS | Média/alta; domínio, host/VPS, banda e manutenção | TLS, firewall, autenticação, atualização, rate limit e observabilidade passam a ser responsabilidade operacional | WebSocket precisa upgrade/timeouts corretos. No servidor residencial é necessário verificar IP público/CGNAT e disponibilidade; não abrir portas agora |
| Cloudflare Tunnel nomeado | Média; domínio e condições do plano; possível evitar exposição de porta de origem | Conector de saída, políticas e dependência de outro provedor; não substitui autenticação da API | Cloudflare documenta WebSockets/SignalR; reinícios da borda podem encerrar conexões. Conferir limites/termos atuais; não foi validado nesta conta |
| Híbrido: produção em domínio estável; ngrok só diagnóstico | Média; dois ambientes/URLs administrados | Isola manutenção e franquia de desenvolvimento; exige evitar APK com destino errado | Separar por ambiente é mais simples que espalhar aleatoriamente REST/WSS entre provedores |

Cloudflare suporta WebSockets em todos os planos e exige recuperação após fechamentos. Publicação por Tunnel tem limites de configuração e termos específicos, inclusive restrições para grandes arquivos/vídeos; não foi afirmado tráfego gratuito ilimitado. [WebSockets/SignalR na Cloudflare](https://developers.cloudflare.com/network/websockets/), [roteamento e limites de aplicações](https://developers.cloudflare.com/tunnel/concepts/routing/).

**Recomendação:** corrigir desperdício agora em sprint própria; manter ngrok no desenvolvimento; avaliar domínio com túnel nomeado/proxy ou orçamento pago antes de ampliar uso frequente. Primeiro comparar com a distribuição real HTTP/WSS da conta. Uma migração não substitui cache e contratos econômicos.

## 14. Roadmap sugerido

| Etapa futura | Entrega | Dependências | Critério de validação |
|---|---|---|---|
| 0 — Fechar baseline | MCP anexado à sessão; consumo do ciclo e bytes por endpoint/canal; metadados sem corpos | Acesso de leitura à conta | Reset/fuso/plano, outros agentes e saldo documentados; conciliar 7.072 e registros retidos; não somar janela cumulativa |
| 1 — Duplicidade e foco | Modais compartilhados, reconciliação agrupada, enabled unificado | Testes focados de frontend | Uma leitura equivalente concorrente; zero demanda de mapa invisível sem outro dono; retorno imediato; nenhuma regressão de configuração Radar |
| 2 — Transporte HTTP | Compressão seletiva e cache persistente de itinerário versionado | Etapa 1 e contrato de versão | Comparar bytes originais/recebidos em Android; segunda abertura sem download da mesma versão; atualização da versão e cache corrompido funcionando |
| 3 — Metadados e filtros | Cache comum com revalidação; suporte `linhaIds`; abort seguro | Mapa de contratos/callers | Duas linhas retornam só seleção; seleção vazia não pede rede; troca de versão/contexto não conserva resposta antiga |
| 4 — Medir SignalR entregue | Métricas por socket/destinatários e causas de reconnect, sem payload sensível | Revisão de instrumentação e aprovação separada | Separar zero destinatários, interno e ngrok; conhecer bytes/cliente-hora e número real de conexões |
| 5 — Decisão estrutural | Ferrovia por hub; avaliar DTO/streams; escolher exposição produtiva | Etapas 0–4 | Preservar freshness, remoções e Radar; demonstrar economia por cenário; comparar custo/limites de hospedagem |

Cenários de homologação da sprint: abertura fria e quente; mapa sem linha; uma/várias linhas; alternar Home/Linhas/Favoritos; parada compacta/detalhada; Radar remoto e local; background por dez minutos; retorno; rede intermitente; duas linhas ferroviárias; 5/10/20 clientes em ambiente de teste autorizado. Não executar carga em produção como validação padrão.

Critérios transversais: mesma atualização crítica, validade e remoção dos veículos; notificações/áudio em background íntegros; nenhuma coleta externa por cliente; nenhum segredo em logs; contagem e bytes antes/depois separados; comparar release com desenvolvimento sem misturar reinícios do bundle na mesma série.

## 15. Limitações e informações pendentes

1. **Billing/MCP:** integração configurada, mas ferramentas ausentes nesta sessão. Não foi possível confirmar autenticação efetiva do executor, plano contratado, créditos, próximo reset/fuso, outros endpoints/agentes da conta, filtros do painel ou transferência atual. O fallback leu o agente oficial existente; não reconfigurou nada.
2. **Cobertura histórica:** 6.673 capturas contra 7.072 do agente; 399 sem detalhes. Retenção é em memória; não há extrato de todas as chamadas do ciclo. A diferença pode envolver retenção e condições de captura; causa exata não foi comprovada.
3. **Bytes:** 48,41 MB de corpos HTTP/49,58 MB brutos são do hop inspecionado. Não incluem frames WSS e não comprovam bytes contabilizados na borda. Os 183,83 MB permanecem referência informada. Não há ranking global HTTP+WSS fechado nem divisão exata necessário/desperdício da fatura.
4. **Identidade e versões:** três origens e User-Agent não identificam usuários/APKs. Não foi enumerado parque instalado nem origem de cada sequência de inicialização. O histórico atravessa deploys e versões anteriores do frontend.
5. **Logs:** amostra Docker limitada, sem access logs HTTP no nível observado; journal ngrok inacessível por permissão. Nenhuma configuração de logging foi alterada. Logs completos antigos de containers substituídos não foram coletados.
6. **Medições práticas executadas:** leitura SSH de containers/portas/systemd/flags permitidas; APIs GET do agente; análise dos corpos previamente capturados em memória com hashes/tamanhos; gzip offline; **cinco GETs internos controlados** — itinerário, capabilities, Swagger e snapshots unificados 838/855. Nenhuma chamada desta auditoria foi feita ao hostname público do NoPonto.
7. **Não executados:** testes automatizados de frontend/.NET, builds, emulador/APK, ADB, testes de carga, sessões WSS sintéticas, replay de capturas, envio de push/áudio real, consultas/migrations de banco, restart/deploy/commit/merge. A fase é de análise; não foi necessária suíte para alterar comportamento porque comportamento não foi alterado.
8. **Simulações:** compressão foi calculada sobre corpos reais, mas não homologada no Android após uma mudança. Cenários de usuários e economias futuras são explicitamente hipotéticos. Não foram conduzidos testes de capacidade do hardware.
9. **Arquitetura alternativa:** não foi verificada elegibilidade, preço final, conta Cloudflare, CGNAT ou disponibilidade de domínio. Nenhum recurso/credencial foi criado.
10. **Escopo preservado:** nenhuma otimização ou alteração de infraestrutura/código funcional. Apenas este relatório e registro documental no histórico. Artefatos não rastreados preexistentes preservados. Não foram persistidas capturas brutas, credenciais, IPs de clientes ou seleções pessoais no relatório.

### Roteiro de reprodução de leitura

No servidor, com o acesso SSH autorizado existente, consultar somente os GETs locais `http://127.0.0.1:4040/api/tunnels`, `/api/endpoints`, `/api/status` e `/api/requests/http?limit=10000`. Não usar replay/DELETE/POST da API do agente. Para agregar: normalizar GUIDs e códigos de linha em padrões, contar método/status/horário, decodificar `response.raw`, separar headers e remover chunking antes de medir corpo; nunca imprimir Authorization, cookies, provas push, tokens ou corpos Radar.

Hash de URI nesta análise serve apenas para agrupar recursos; não identifica uma pessoa. Para reconciliação, a comparação de credencial ocorreu apenas em memória por hash e não foi gravada. Na futura medição de clientes, usar dados agregados e política de retenção mínima.

As referências oficiais foram consultadas em 13/11/2026; limites e preços devem ser reconferidos na decisão comercial. O próximo passo é aprovar uma sprint concreta com os critérios da seção 14, após fechar o billing pendente; este documento não autoriza implantação das propostas.
