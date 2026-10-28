# Release 153 — comparação beta do híbrido desacoplado, 27/10/2026

## Decisão

**Desacoplamento APROVADO em produção; permanência Hybrid NO-GO por frescor.** GTFS foi entregue enquanto ITS estava pendente, inclusive antes do primeiro snapshot ITS completo. A cobertura REST aumentou, mas o número absoluto de posições físicas até60s caiu. A coorte já presente no baseline também envelheceu; a queda não é apenas diluição percentual por ônibus adicionais antigos.

Operação encerrada em **GTFSRT_BUS / fallback vazio / FullFallback / trace=false**, BRT GTFSRT_BRT, mesma imagem153, uma API, lock livre, cron removido, nenhuma captura, supervisor ou sonda temporária restante. Não houve segunda janela, nova imagem ou mudança de C#, GPS, TTL, cadência, matching, banco ou BRT nesta finalização.

## Revisão e operação

- Merge/PR86: `484a0e841370304fd91ab01d40fbf11064e10230`, árvore idêntica ao commit testado50d057c. Workflow153/run40586253845 aprovado; publicação/deploy já concluídos antes da janela.
- Imagem: `ghcr.io/guilhermedesales/noponto-api@sha256:5bea032825319c0cd8e243806f8a49afd26741fc643882aa04d958b5663e463b`.
- Supervisor/captura/sonda executados: commit operacional local `c14dfc3`. Guardas de imagem mantidas; captura única foi adotada preservando PID/nascimento/sessão/inode. Prova nova de cron independente/lock/watchdog realizada antes da ativação.
- Diretório privado: `/home/guilherme/.noponto-deploy/beta153-20261027T213054Z`; cópia local em `artifacts/beta153-20261027T213054Z/`. Arquivos e overrides referenciados pelos labels preservados; laboratórios intactos.

Todos os horários abaixo são UTC (Brasília=UTC−3). O fechamento foi retomado horas depois da janela, sem extensão da ITS.

Baseline: oito amostras, 129.30s observados. Solicitação de ativação: **2026-10-27T21:39:12.930538+00:00**. Deadline600s: **2026-10-27T21:49:12.930538+00:00**. Motivo: `ten_minutes_completed`. A primeira leitura externa de `phase=rollback` foi21:49:21,425 (T608,49); o registro não guarda o instante exato de entrada nessa fase, portanto não há precisão milissegundo para o início efetivo do comando. O deadline disparou a interrupção; a evidência externa delimita o início entre T600 e T608,49. **GTFS saudável confirmado: 2026-10-27T21:49:55.079595+00:00, T642.15s**. A restauração consumiu aproximadamente34–42s após o início; não chamar os642,15s de uma janela concluída integralmente em600s. O processo Hybrid ainda estava sendo encerrado após o deadline, até a troca de container. O limite controla a solicitação de parada, não garante restauração instantânea.

A sondaHTTP2s delimitou indisponibilidade inicial em34–38s e de rollback em32–36s. GTFS foi visto em `phase=complete` pela monitoração emT649,77; isso é detecção posterior à confirmação interna. Nenhum tempo foi ampliado para salvar logs. Captura fechada21:50:16,474; encerramento íntegro preservado durante as duas recriações. O cron ficou instalado, porém inerte emcomplete, durante a interrupção da sessão; foi removido na retomada em28/10 00:31:02UTC. Estado final novamente confirmado00:32:32UTC.

## REST: cobertura e frescor publicados

São8 amostras antes,35 durante e3 imediatamente depois. Contagens médias por amostra podem ser fracionárias. Percentuais ponderam todas as observações repetidas; idades são medianas das medianas/p90 dos snapshots. O limiar medido é **≤60s**, não estritamente<60s. REST demonstra posições visíveis, incluindo retenção legítima; não prova a fonte individual de cada posição. Após rollback há posições híbridas retidas, logo as três amostras finais não representam um GTFS-only estabilizado.

|BUS|Média/máximo antes → Hybrid|Média≤40s antes → Hybrid|Média≤60s antes → Hybrid|%≤60s antes → Hybrid|Mediana idade s antes → Hybrid|p90 s antes → Hybrid|
|---|---|---|---|---|---|---|
|838|19.4/20 → 22.2/24|2.6 → 1.0|11.6 → 7.6|60.0 → 34.4|55.9 → 66.8|65.5 → 93.8|
|898|14.0/14 → 13.7/14|4.1 → 1.1|10.5 → 6.2|75.0 → 45.1|47.9 → 60.8|60.2 → 74.4|
|006|0.0/0 → 1.7/2|0.0 → 0.0|0.0 → 0.1|— → 6.7|— → 91.0|— → 93.9|
|457|23.5/24 → 26.2/28|3.5 → 1.5|14.8 → 10.9|62.8 → 41.7|53.0 → 60.8|67.5 → 81.8|
|483|31.0/33 → 34.3/36|4.9 → 1.9|19.8 → 14.0|63.7 → 40.7|52.2 → 62.3|63.5 → 87.4|
|355|7.0/7 → 8.7/10|1.1 → 0.3|5.1 → 3.3|73.2 → 37.8|53.2 → 62.3|66.7 → 107.0|
|805|0.0/0 → 1.7/2|0.0 → 0.0|0.0 → 0.1|— → 5.0|— → 97.7|— → 99.7|

- GTFS baseline: média94.88, máximo simultâneo97; média≤40s16.25, ≤60s61.75 (65.1%).
- Hybrid: média108.54, máximo simultâneo112; média≤40s5.86, ≤60s42.17 (38.9%).
- Após rollback, ainda com retenção: média111.00, máximo simultâneo111; média≤40s29.00, ≤60s48.00 (43.2%).

Ganho líquido médio: **13,67 ônibus (+14,4%)** nas sete linhas. Não é contagem de exclusivos ITS. O número médio≤60s caiu **19,58**. Excluindo o início, nas29 amostras a partirT120,42, média110,69 ônibus/35,34≤60s; portanto a perda não se restringe ao downtime inicial. 006/805 estavam legitimamente vazias no baseline e chegaram a dois veículos cada. Não existe prova de cobertura completa da rede.

## Coorte GTFS: renovação versus idade dos adicionais

Identidades do baseline foram mantidas somente em memória. “Adicional” significa ausente do baseline, **não fonte ITS comprovada**: novas entradas GTFS também cabem nessa categoria. As médias das coortes somam exatamente o REST. Intervalos são observados pelo cliente REST; amostragem pode perder posições intermediárias. Históricos limitados a512 intervalos/grupo;483 atingiu esse limite no Hybrid. Nenhuma identidade ou coordenada é persistida. A coorte originalmente GTFS pode receber observações ITS dos veículos compartilhados durante Hybrid; pertencer à coorte não identifica a fonte de cada atualização.

|BUS|Coorte baseline: %≤60 antes → Hybrid|Intervalo físico mediana/p90 s antes → Hybrid|Intervalo observado de chegada mediana/p90 s antes → Hybrid|Adicionais: média / mediana idade s / %≤60|
|---|---|---|---|---|
|838|60.0 → 39.4|30.0/60.0 → 30.0/31.0|32.6/32.6 → 32.6/34.5|3.43 / 96.9 / 6.7|
|898|75.0 → 45.8|32.0/63.0 → 30.0/46.0|24.4/32.6 → 32.6/39.7|0.66 / 66.4 / 30.4|
|006|— → —|—/— → —/—|—/— → —/—|1.71 / 91.0 / 6.7|
|457|62.8 → 45.0|30.0/60.0 → 30.0/33.0|32.5/32.6 → 32.6/34.5|2.34 / 100.2 / 7.3|
|483|63.7 → 43.3|31.0/61.0 → 30.0/39.0|24.4/32.6 → 32.6/34.5|2.57 / 84.6 / 7.8|
|355|73.2 → 46.8|30.0/61.0 → 30.0/31.0|24.4/32.6 → 32.6/34.5|2.03 / 97.9 / 8.5|
|805|— → —|—/— → —/—|—/— → —/—|1.71 / 97.7 / 5.0|

Os intervalos físicos centrais permaneceram próximos de30s, sem paralisação de renovações. Porém, a idade publicada piorou na própria coorte:83860→39,4%≤60s e89875→45,8%. A898 passou de24,4 para32,6s de intervalo mediano observado;838 ficou em32,6s, com cauda32,6→34,5s. Logo, não é correto dizer que o frescor GTFS permaneceu igual. Também não se prova que cada degradação foi causada pela ITS: horários, idade do feed, carga compartilhada e fase de amostragem variaram. Adicionais da838 tiveram mediana96,9s e só6,7%≤60s; há diluição **e** envelhecimento da coorte anterior.

## ITS: aquisição completa e custo

De12 aquisições: **10 completas**, uma parcial (`request_timeout`,3 páginas/uma janela) e uma falha (`request_timeout`,zero páginas). Parciais não foram publicadas como completas. Completos produziram3.490–3.522 veículos válidos únicos,9–11 páginas/três minutosUTC. Duração10,62–44,31s, média23,41s. A primeira voltou a aproximar o atraso histórico de48,6s, sem ser a mesma ocorrência.

|Aquisição|T final s|Resultado|Páginas/janelas|Brutos/rejeitados|Únicos válidos|Total s|Headers/body/parse/normalização s|Idade física p50 início → fim s|
|---|---|---|---|---|---|---|---|
|A01|81.53|Sucesso|11/3|48779/13428|3522|44.31|22.85/19.77/1.19/0.48|13.2 → 57.4|
|A02|116.32|Parcial|3/1|12494/449|0|25.99|18.62/7.10/0.15/0.11|— → —|
|A03|209.60|Sucesso|9/3|38161/1966|3513|26.21|13.74/11.96/0.35/0.15|19.3 → 45.5|
|A04|305.30|Sucesso|11/3|49450/8910|3504|27.15|17.07/9.04/0.49/0.22|14.1 → 41.2|
|A05|320.17|Sucesso|9/3|39643/1739|3505|10.62|4.02/5.29/0.32/0.17|19.5 → 30.1|
|A06|354.50|Sucesso|10/3|43716/4960|3501|24.90|8.38/15.95/0.39/0.17|26.5 → 51.4|
|A07|382.37|Sucesso|9/3|36376/1528|3499|21.46|14.01/6.57/0.36/0.15|21.8 → 43.3|
|A08|420.23|Sucesso|11/3|44327/6538|3495|28.26|13.93/13.62/0.51/0.19|23.9 → 52.1|
|A09|441.01|Sucesso|9/3|36565/1483|3499|18.00|10.55/6.84/0.38/0.14|22.9 → 40.9|
|A10|460.52|Sucesso|10/3|44100/2335|3504|16.45|10.00/5.44/0.42/0.21|17.0 → 33.4|
|A11|474.13|Falha|0/0|0/0|0|10.00|7.66/2.35/0.00/0.00|— → —|
|A12|564.68|Sucesso|9/3|38074/1697|3490|16.76|9.63/6.23/0.40/0.23|21.8 → 38.6|

431,685 registros brutos (observações repetidas), 45,033 rejeitados: `service`=7245, `physical_stale`=37758, `physical_future`=30. Não são veículos únicos rejeitados. Na primeira aquisição, a janela mais antiga concentrou GPS vencido durante a leitura; o p50 físico passou13,15→57,45s. Headers22,85s/body19,77s explicam a maior parte dos44,31s; parse1,19s/normalização0,48s. Não há evidência de que normalização ou serialização tenha causado esse atraso.

Acumulado nas12: headers150,45s/body110,17s/parse4,96s/normalização2,21s. Esperas de intervalo registradas somaram2,235s; gate acumulado<1ms; `pending_wait_ms=0` e sem eventosprovider_wait. Isso não exclui espera interna no provedor embutida emheaders/body. O resumoJSON contém as103 respostasHTTP (incluindo falhas),101 páginas normalizadas, seus minutosUTC e tempos. Leitura de body/headers mede cliente+rede+servidor, não isola causalmente o provedor. Repetição de registros nas três janelas aumenta volume; otimizar consulta exige manter cobertura e contrato.

## Composição desacoplada e publicação

Foram35 composições,32 com as duas entradas; suporteGTFS comSucesso em todas. GTFS já foi selecionado emT39,32 enquanto a primeiraITS só concluiu emT81,53. A geração2/898 foi selecionada emT70,33, também antes desse fim. A primeira composição conjunta ocorreuT90,42; o primeiro snapshot válido foi admitido apesar da recuperação posterior continuar com duas sondagens de saúde.

- Duração total das composições2,19–2.031,95ms (mediana822,18ms); GTFS1,75–1.989,86ms (mediana418,49ms;34/35 extensões capturadas).
-21 composições observadas comITS pendente; nenhuma faz `await` daquela aquisição. `gtfs_wait_avoided_ms` atingiu42.283,10ms (mediana do gauge23.727,48ms, incluindozeros). É espera estimada de sincronização evitada por sequência, repetida nos logs; **não somar** para inventar economia total/percentual.
- Cache completo observado com3,06–113,39s desde conclusão (mediana30,67s), mantendo timestamps físicos. Falhas parciais/timeout mantiveram o último completo elegível; não observada publicação de cache vencido, nem prova real de expiração além dos testes anteriores. Idade do cache não substitui idade física das posições.
- Nas composições conjuntas: exclusivos válidosITS749–1.066 eGTFS78–610, compartilhados1.979–2.529; selecionadosITS749–3.288 eGTFS281–2.604. São contagens da aquisição/composição de rede, **não da publicação** nas sete linhas.
-483 conflitos de coordenadas e937 de linha acumulados em observações de composições (com repetição); sentido0/inválidos0. Não equivalem a veículos únicos descartados. A seleção aplica as regras existentes sem misturar campos.

A imagem não expõe fonte por veículo nos DTOsREST/SignalR e o trace está limitado à898. Portanto, o número exato de **exclusivosITS efetivamente publicados** nas demais linhas permanece não observável. O ganho REST é real, mas não recebe atribuição artificial de fonte. O JSON separa aquisição, seleção, CAS e visibilidade.

## Linha898, gerações e gargalo restante

Geração2,13 observações físicasGTFS novas: seleçãoT70,329; CAS85,754; envioSignalR86,214; ACK86,289; REST89,045. O supervisor constatou prova conjunta com cliente em**T90,230**, portanto `gate898_within90=false` — não aprovação retroativa. Era diagnóstico beta e não interrompeu a janela. O envio do servidor não foi usado sozinho como confirmação do cliente.

No trace total:467 observaçõesselected/polling/age_valid/ACK;247 comtimestampnovo/matched/CASaceito/SignalRconcluído;233 retornadasREST. São observações porgeração, não IDs únicos. Diferenças incluem repetição, filtros e requisiçõesREST que não consomem todas as gerações, sem prova de perda indevida. SignalR da janela:552 mensagens,zero regressões/duplicações/GPSinválido, nove erros de conexão no entorno do startup/reconexões.

P50 agregado da idade898 na seleção:40,87s; noCAS43,03s; noREST52,65s. Essas medianas usam conjuntos diferentes e **não podem ser subtraídas como uma latência causal**. Na mesma geração2, porém, seleção→CAS15,42s e seleção→REST18,72s são demonstrados. Mediana de `since_ready_ms`:CAS14,83s/REST24,53s. Pareando os estágios da mesma geração, as medianas seleção→polling foram0,192s; seleção→matched11,517s; seleção→CAS14,821s; seleção→SignalR15,123s; seleção→REST24,531s. Assim, a maior demora backend medida depois da seleção está no processamento/matching e commit, não na espera pelo polling nem no envio SignalR. A leitura periódica REST adiciona atraso de observação. Há também espera/cadência antes de novas gerações, mesmo com aquisição GTFS rápida.

Ciclos híbridos observados:44, totalmediano3,69s/máximo18,54s; matching mediano1,13s/máximo11,43s, commit/viagem0,61s/máximo4,86s, leituraRedis0,39s/máximo1,80s. Baseline tem10 ciclos de logs, com e semsnapshot, totalmediano1,06s/máximo14,90s; comparar só medianas mistura classes de carga. Zero errosCASinfra. Nenhum segundo escritor, alteração de matching/CAS ou regressãoGPS observada.

O bloqueio explícitoITS→GTFS foi removido. O orçamento de60s continua sendo consumido por **idade física já presente no feed/cache e latência posterior do pipeline compartilhado**, com caudas de matching/commit e cadência de novas posições. O custoHTTPITS continua grande, mas agora não bloqueia o retornoGTFS. Não há contrafactual simultâneo para declarar um único componente causa exclusiva da perda no mapa.

Próxima correção concreta proposta, **não implementada**: otimizar o caminho em lote de matching e commit das novas observações, usando os testes de continuidade/CAS existentes para manter resultados e o escritor único. O alvo medido é reduzir os≈11,5s até matched e≈14,8s até CAS por geração, sem pular matching nem enfraquecer validações. O polling consumiu rapidamente o snapshot (mediana0,192s), portanto não é o primeiro alvo nesta amostra. Validar também idade/cache/cadência do feedGTFS, pois leituraHTTP de poucosms não significaGPSnovo. Não reduzirTTL/filtros nem refatorar novamente a aquisição sem essa distinção. ConsultaITS menos redundante é outro alvo medido, secundário à entrega de posições recentes.

## Controles BRT e recursos

|BRT, sempreGTFSRT_BRT|Média veículos antes → Hybrid|%≤60 antes → Hybrid|Mediana física s antes → Hybrid|
|---|---|---|---|
|35|11.00 → 11.51|89.8 → 63.0|42.0 → 50.0|
|60|21.00 → 20.97|98.2 → 72.3|36.5 → 48.2|
|67|12.38 → 12.74|94.9 → 62.8|41.2 → 52.2|

O BRT também envelheceu sem mudança de fonte; isso reforça investigar carga/pipeline compartilhado e variação temporal, e impede atribuir toda a perda à composiçãoITS. Não houve teste de login externo; autenticação/containers dependentes ficaram preservados, não declarar prova funcional de todos os fluxos de autenticação.

API:CPU0,08–104,20% de um núcleo, memória117,7–280,3MiB/768MiB. BaselineCPU chegou105,33%; PostgreSQL136,37% antes e133,27% durante. SemOOM/exaustão/restartsinesperados. São amostras, não perfis integrados nem picos absolutos. Os sete containers dependentes mantiveramID/configuração/início/reinícios idênticos. Snapshot próximo a T=504 s tinha1,87 GiB aproximados deRAMdisponível e swaplivre5.001.208KiB; fechamento horasdepois:RAMdisponível2.251.608KiB/swaplivre4.914.680KiB/discolivre17.778.761.728bytes. A diferença final não deve ser atribuída toda ao experimento, dado o intervalo de horas.

## Captura, fechamento e validações

Captura913,99s,4.141.341bytesentrada/282.091bytesarquivo/495registros, dentro64MiB/8MiB/12.000linhas/1.000s. Três streams porID imutável, duas trocas esperadas, sem overflow/gaps/truncamento/duplicatas ou reconexão inesperada.12 timings/resumos reconciliados, inclusiveParcial/Falha; `integrity_valid=true`, `archive_complete=true`, `evidence_complete=true`. Completude do arquivo não transforma aquisição falha em completa.

SHA256 arquivo: `adc6a0c733cf44c2e34e6f977bb1c3e3aa4a34bee231baf00d85331ef28f0578`. Eventos: `ae9902801b077f705c54d44d650a1b6c518b654e354c45c20ec65f3a1a86a521`.

Cinco hashes operacionais canônicos executados:

- `its_window.py`: `304bca05641acb139dbcd4ec2e95f6e2aa82975a17a2bd1b776d62570ce5fd76`
- `its_log_capture.py`: `530a5d39d32f30bbd466f05ddf7fa0604673c28b3b989497ed882fd10551490c`
- `probe_http.py`: `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248`
- `its.primary.override.yml`: `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1`
- `gtfs.rollback.override.yml`: `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900`

Na retomada, somente consultas de saúde e remoção do cron próprio, idempotente conforme estado confirmado; nenhuma recriação da API. A primeira sonda final teve uma reconexãoSignalR,20mensagens/zeroerrosGPS; não foi ocultada. A confirmação seguinte35s recebeu24mensagens,zeroerros/repetições/regressões/inválidos. HTTP/RedisPONG/PostgreSQLaccepting saudáveis. Última leituraREST:8389veículos/3≤60s;8987/5≤60s, já em outro horário, não usada na comparação formal. Nenhum processo temporário, cronausente, locklivre e estadoactive.run153/active.env consistente comimagem.

Validação desta finalização: SHA256 dos arquivos/eventos, reconciliação de contagens de todas as amostras/coortes e comparação dos sete containers preservados. Suítes95supervisor+33captura já aprovadas antes da janela; não repetidas sem alteração de código.1111offlineRelease prévios eCI153 aprovados; sem repetiçãoC#/CI nesta entrega documental.

## Comparação histórica com release151

[Janela sequencial anterior](beta-diagnostic-20261027.md): oito completos,3.327–3.385veículos, duração6,31–24,14s/média13,50s; seis composições conjuntas e primeiraT210,06. Na153: dez completos,3.490–3.522veículos,10,62–44,31s/média23,41s;32 composições conjuntas, primeiraT90,42,GTFS já entregueT39,32 enquantoITSpendente. Completos entram sem bloquear pela política de duas sondagens. As aquisições ITS foram mais lentas nesta amostra, mas não seguraram GTFS até seu fim.

Ambas as janelas ocorreram27/10, em horários diferentes (151início18:14UTC;15321:39UTC), frotas e feed diferentes; não é comparação controlada de releases. Na151,838média12→até16 e≤60%57,3→29,6;89852,8→26,7. Na153,83860→34,4 e89875→45,1. Não inferir percentuais causais de melhora pela versão com baselines tão diferentes. A prova direta do desacoplamento vem da entregaGTFS durante aquisiçãoITS, não dessa comparação temporal.

## Respostas e decisão de uso

1. **Desacoplamento funcionou/GTFS parou de esperarITS: sim**, demonstrado por gerações e métricas; permanecem outras esperas de feed/handoff/pipeline.
2. **Mais ônibus publicados: sim**, +13,67 de média líquida nas sete linhas; atribuição exata de exclusivosITS publicados indisponível nesta imagem.
3. **CoorteGTFS continuou renovando: sim**, aproximadamente30s físicos centrais; **não manteve o mesmo frescor**, e a frequência observada piorou em parte das linhas.
4. **Quantidade absoluta recente melhorou: não**,61,75→42,17≤60s. Não é apenas perda percentual por adicionarGPSantigo.
5. **Padrão permanente: não recomendado agora.** Sucesso funcional de cobertura/desacoplamento não atende a meta de frescor. Produção permaneceGTFS-only.
6. **Gargalo restante:** idade do feed/cache somada à espera e custo atéCAS/REST, além doHTTPITS redundante/lento. Reduzir o custo de matching/commit e validar idade do feed/cache é a próxima correção focada; nenhuma nova refatoração ou janela está armada.

[ResumoJSON com tabelas completas, páginas e gerações](beta153-decoupled-20261027.summary.json). Evidências privadas preservadas; nenhuma nova publicação autorizada por este relatório.
