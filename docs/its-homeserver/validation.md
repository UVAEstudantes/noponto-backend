# Validação final ITS + GTFS-RT e prontidão da janela

**Pronta funcionalmente para uma ativação controlada autorizada.** Os dois feeds reais passaram pelo pipeline descartável; o homeserver foi inspecionado somente para leitura e o override foi renderizado sem aplicação. Falta autorizar a publicação/implantação do binário e a janela supervisionada. Não é aprovação para operação permanente sem observação, nem exige outro benchmark de 459 linhas.

Procedimento e rollback: [activation.md](activation.md). Evidência agregada com hashes: [validation.summary.json](validation.summary.json). Branch de origem `feat/its-current-sppo`, baseline `e186cbe`; nenhuma lógica operacional da aplicação precisou de correção nesta etapa.

Conectividade adicional do homeserver: um único GET ITS no host, `limit=1`, minuto UTC anterior, timeout10s e corpo limitado64KiB, retornou HTTP200 em7,023s, com CONECTA/ZIRIX `ok`. Nenhuma página seguinte foi solicitada e nenhum dado foi publicado. Isso prova DNS/TLS/HTTP do host; o namespace/trust store do container novo será confirmado na janela, sem instalar ferramentas no servidor.

## Pipeline com feeds reais

Ensaio final em5min40s, prazo máximo8min, PostgreSQL16/PostGIS e Redis7.4 descartáveis em portas aleatórias loopback/tmpfs. Quatro containers locais preexistentes preservados; containers/rede próprios removidos ao final. GTFS estrutural público já baixado nesta sessão lógica, SHA256 `a99f925460e7628b6eeecb9952430542c06b3e2800afa8ba7f9765fd2e6f26f1`:459 linhas BUS,873 trips reais representativas,53214 ocorrências. Importação/publicação em schema guardado, sem GPS/traçados fabricados. Não replica todas as variantes ou o estado produtivo.

Aquisição ITS de toda a rede, sem filtro de linha; três assinaturas reais no WebSocket `/hub/gps`:838,898,006. Matching PostGIS, CAS/Lua Redis, histórico/predecessor, viagens/Outbox PostgreSQL e SignalR reais. Outros modais e ML externo desligados somente no host isolado. TTL40/180 e polling20s do appsettings local; matching individual somente das linhas assinadas. **Produção usa polling15s, batch set-based e enriquecimento de todas as linhas**; o ensaio funcional não mede essa carga nem pede sua alteração. O risco é acompanhado pelos limites da janela.

| Etapa | Quantidade/evidência |
| --- | --- |
| Quatro aquisições ITS completas, incluindo sondagens | 138151 registros;17028 rejeitados pela fonte;12796 observações normalizadas somadas, não veículos únicos |
| Veículos por aquisição ITS | 3212/3217/3206/3161; três janelas,9/11/10/6 páginas |
| Entrada no polling | 19275 observações válidas, incluindo leituras GTFS reutilizadas |
| Antigas/iguais recusadas antes do CAS | 6673; descartes legítimos, não falha de cobertura |
| CAS/Lua | 12602 tentativas/aceites;3361 veículos únicos;zero rejeições de concorrência/falhas de infraestrutura;nenhum `(veículo,GPS)` aceito duas vezes |
| Matching | 123 solicitações/120 enriquecidas;8776 históricos/117 próximas paradas entre aceites |
| PostgreSQL local | 35 estados operacionais;112 eventos/Outbox;78 linhas de histórico,77 novas sobre o seed de controle;materialização sem falhas |
| SignalR real | 315 itens em27 snapshots não vazios,36 veículos únicos;zero regressões/futuros;192 repetições de GPS em snapshots completos, sem eventos aceitos duplicados |
| Handoff | Oito gerações consumidas com ACK;ambas as fontes falhando não geraram novo lote |

Os rejeitados da fonte são registros brutos; o adaptador também colapsa várias posições do mesmo veículo. As populações acima não devem ser somadas como se fossem uma única contagem. A primeira sondagem ITS saudável é completa, mas não vira handoff ITS até a segunda confirmação.

| Injeção local | Fonte efetiva | Posições | Novos aceites | Tempo do ciclo completo |
| --- | --- | ---: | ---: | ---: |
| ITS indisponível | GTFSRT_BUS | 1946 | 1315 | 2,595s |
| ITS parcial | GTFSRT_BUS/cache | 1946 | 0 | 0,126s |
| ITS timeout | GTFSRT_BUS/cache | 1946 | 0 | 10,105s |
| Ambas indisponíveis | Falha | 0 | 0 | 0,013s |
| Entrada em recuperação | GTFSRT_BUS/cache | 1946 | 0 | 0,091s |
| Primeira sondagem saudável, após61s | GTFSRT_BUS | 1901 | 1901 | 19,205s |
| Segunda sondagem saudável, após outros61s | ITS_CURRENT | 3161 | 3152 | 12,268s |

Falhas são injetadas no processo local; nenhuma falha no provedor ou servidor foi provocada. O suporte mantém o mesmo cliente/cache entre cenários. Houve somente dois GETs GTFS reais, separados por90,266s; reutilização não renova GPS/recepção. Ambas falhando é injetado acima do cache, para testar indisponibilidade sem contornar o intervalo HTTP. O primeiro ensaio também passou1/1, mas recriava o cliente a cada cenário; a instrumentação foi corrigida e o ensaio final repetido. Isso não exigiu alteração no backend operacional.

Identidade:1868 IDs compartilhados na primeira transição,631 pares com mesmo GPS físico;619 também com coordenadas equivalentes e623 com velocidades equivalentes. Diferenças entre provedores/instantes/códigos não foram apagadas por uma fusão; não há complementação por veículo. O ensaio não afirma igualdade universal entre feeds.

Checagem adicional somente leitura das unidades, sem banco/cache/host:3189 ITS,2010 GTFS e1935 IDs compartilhados. **500 pares em movimento**, mesmo ID/GPS físico/coordenadas, coincidiram com a velocidade GTFS bruta em km/h;zero coincidiram após multiplicar por3,6. Isso confirma `KilometresPerHour` nesta amostra, sem usar veículos parados como prova. Nenhum dado individual foi registrado.

## Frescor e duração

ITS completo levou17,82/22,57/16,46/8,98s. O coletor continua sequencial, espera o término da aquisição e depois a pausa configurada; não abre outra coleta enquanto houver geração pendente. Aquisição acima15s não cria escritores concorrentes. Na falha inicial, o orçamento ITS de até90s ainda pode atrasar o primeiro fallback; não foi otimizado nesta etapa.

Na fase ITS saudável:6429 entradas e6234 aceites;aquisição até40/60s=2504/5942;no aceite=2003/5806;no socket=15/55 de69 itens. No ensaio inteiro, aceites até40/60/180s=3785/9559/12598;socket=40/217/314 de315. São populações diferentes: frota versus três linhas. Houve17 itens `SemSinal` e258 itens ativos com GPS acima40s:TTL continua contado da escrita, não da idade física. Um item no socket ultrapassou180s;retenção recente e idade de GPS não são a mesma coisa.

Uma observação GTFS futura apareceu nas aquisições, dentro da tolerância legada de120s do parser;ITS rejeitou futuros. Não houve futuro nas três linhas do socket observado. Não foi alterada a política GTFS já usada em produção. A janela inclui observação de futuros/regressões e não deve confundir sucesso HTTP com GPS físico recente.

Ciclos de polling com entrada:0,068–3,279s. Soma das etapas:matching5,251s,commit/viagem4,332s,leitura Redis0,955s,SignalR0,098s. Aquisição, ciclo de polling e ciclo completo de fallback são medições distintas. Testhost:122,22sCPU,pico592117760bytes, incluindo importação/observadores. Em25 amostras locais:PostGIS31,55%CPU/pico476,2MiB;Redis33,51%/14,59MiB. Amostragem pode perder picos; não extrapolar para capacidade produtiva.

## Testes e reprodução

- `GpsPipelineDisposableIntegrationTests.ItsEndToEnd_Real`:1/1 no ensaio final e1/1 no primeiro;asserts de ambos feeds, aceites GTFS, predecessor, matching, próxima parada, ACK, socket e ausência de aceites duplicados/regressões.
- Direcionados:464/464;offline oficial:1015/1015, zero falhas/ignorados. Verificador confirmou todas as classes;allowlist inalterada.
- Smoke de unidades:500 testemunhos em movimento, saída positiva. Compilação Release pelos testes/smoke aprovada, cinco avisos anteriores;build independente da solução/imagem Linux não executado nesta etapa.
- AST Python/JSON e render Compose remoto dos dois arquivos YAML propostos aprovados, confirmando mudança restrita às duas chaves BUS. Bloco de comandos futuros passou em `bash -n`, transmitido como bytes LF; somente sintaxe, comandos não executados. Nenhum teste usa PostgreSQL/Redis produtivos para escrita ou migrations.

```powershell
pwsh -NoProfile -File tools/gps/Invoke-ItsE2e.ps1 -GtfsZip D:/repositorio_github/NoPonto/noponto-backend/artifacts/its-e2e-20261023/gtfs-structural.zip -ResultsDirectory D:/repositorio_github/NoPonto/noponto-backend/artifacts/its-gtfs-homeserver/connected-run2 -SubscribedLines 3 -Run
# Para repetir, fornecer ResultsDirectory NOVO; o guard recusa diretório existente.
dotnet run --project tools/ItsCurrent.Smoke/ItsCurrent.Smoke.csproj -c Release --no-restore -- --read-only --verify-gtfs-units
# Direcionados: mesma seleção de oito classes documentada em ../its-gtfs-support.md.
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { throw 'Seleção offline inválida' }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=homeserver-offline.trx' --results-directory artifacts/its-gtfs-homeserver
if ($LASTEXITCODE -ne 0) { throw 'Falha offline' }
python .ci/offline_tests.py verify artifacts/its-gtfs-homeserver/homeserver-offline.trx
```

Evidências locais em `artifacts/its-gtfs-homeserver`, com hashes no resumo versionado. Scripts transmitidos por stdin ao SSH só leram configuração/metadados/logs/REST e executaram SELECTs READ ONLY limitados. O render proposto não aplica serviço. A inspeção confirmou correspondência das configurações de conexão com o container efetivo, banco/host PostgreSQL e schema, sem imprimir valores sensíveis.

Não há bloqueio novo de código para testar as três linhas. Restam autorização específica, artefato imutável correspondente, implantação inicial desse binário com GTFS e a janela supervisionada de10min com interrupções/rollback definidos. As150 identidades já não validadas e a carga de enriquecimento de toda a rede são limitações conhecidas a observar, sem exigir reforma estrutural/performance antes da janela. Nenhum push, merge, deploy ou alteração produtiva foi feito.
