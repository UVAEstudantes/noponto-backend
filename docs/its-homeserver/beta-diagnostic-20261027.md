# Janela beta diagnóstica ITS + GTFS — 27/10/2026

**APROVADO como experimento diagnóstico; não aprovado para permanência híbrida.** A ITS concluiu oito snapshots completos e o híbrido publicou cobertura adicional. A fração de GPS físico até 60 segundos caiu nas sete linhas observadas. A produção retornou a GTFS-RT, sem segunda tentativa.

Supervisor executado: `6f0d17a7fd686b083eb763762ab6c856d9f55e12`, após `cf672fe`. Release 151 preservada: `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`. Não houve push, merge, nova imagem, migrations, limpeza, alteração de TTLs, cadências, regras GPS ou BRT. Somente as duas recriações autorizadas da API para ativar e retornar.

## Execução e proteção

Preflight rápido aprovado: fontes esperadas, HTTP e SignalR saudáveis, Redis PONG, PostgreSQL aceitando conexões, uma API escritora, lock livre e ausência de janela concorrente. RAM disponível: 2.279.120 KiB; disco livre: 18.472.706.048 bytes. Os sete testes beta direcionados passaram, incluindo recuperação HTTP entre sondas e rollback por indisponibilidade persistente. Não repetidas suítes .NET, CI ou provas POSIX; não houve alteração de código nesta tarefa.

Diretório novo 0700, cinco arquivos canônicos Git UTF-8/LF 0600 e hashes conferidos. Captura única: PID 1612454, nascimento 19548006, sessão `71c62336c10e87f858ceb09e59d7e4fd`, inode 3670655. A prova do cron real ocorreu às 18:11:01 UTC, com ancestrais cron/systemd e SSH desconectado. Exclusão pelo lock e ausência de herança pelo capturador confirmadas. O supervisor adotou a mesma captura no baseline; nenhum segundo leitor. Watchdog independente disponível; seu ramo destrutivo não foi provocado.

Baseline: oito amostras e 130,44 segundos da primeira observação completa à ativação, com 12 veículos na 838 e 9 na 898. Ativação solicitada às **18:14:13,966 UTC** (15:14:13,966 Brasília); deadline às **18:24:13,966 UTC**. Rollback por `ten_minutes_completed`, com GTFS saudável confirmado às **18:24:27,180 UTC**: cerca de 13,21 segundos para concluir o retorno após o limite de 600 segundos. O motivo, o código executado e a sonda de dois segundos delimitam o início do rollback; seu timestamp exato não foi arquivado. O stream do container híbrido foi observado por cerca de 570,86 segundos, o que não é uma medida exata de tempo do escritor.

A sonda HTTP independente, a cada dois segundos, delimitou a indisponibilidade inicial em **34–38 segundos**, e a do rollback em **6–10 segundos**. São limites por sondagem, incluindo latência de leitura. O supervisor observou a primeira falha em T=16,79 s e repetiu a cada dois segundos até T=34,80 s. A recuperação ocorreu antes dos 30 segundos desde sua primeira falha observada. **Esse relógio difere da duração física total:** a amostragem inicial normal de 15 segundos deixa atraso de detecção. O limite de 30 segundos não foi alterado. O HTTP recuperou por volta de T=38,24 s, evitando o aborto anterior entre sondas.

SignalR registrou oito erros de conexão em cada troca, reconectou e não registrou regressões, duplicações ou coordenadas inválidas.

## Aquisição ITS completa

**11 aquisições: oito sucessos completos, duas parciais e uma falha.** Todos os sucessos concluíram as três janelas UTC. As três leituras não completas terminaram por `request_timeout`, sem promover amostras truncadas.

Cada snapshot completo produziu **3.327–3.385 veículos únicos válidos**, em **6,31–24,14 segundos**, com média de 13,50 segundos. Não somar esses veículos entre snapshots. Houve 68 requisições HTTP registradas, 65 páginas retornadas e 62 normalizadas; três respostas com provider pending foram descartadas antes da normalização.

| Fim UTC | Estado | Páginas/janelas | Registros brutos | Rejeitados | Veículos válidos | Total s | Headers s | Body s | Parse s | Normalização s | Pending/intervalo s | GPS p50/p90 s |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 18:15:09 | Parcial | 2/1 | 9612 | 497 | 0 | 18.81 | 8.60 | 9.80 | 0.308 | 0.110 | 0.00/0.00 | —/— |
| 18:16:21 | Sucesso | 6/3 | 19756 | 1265 | 3327 | 10.11 | 5.10 | 4.57 | 0.249 | 0.188 | 0.00/0.00 | 85.9/101.9 |
| 18:17:42 | Sucesso | 10/3 | 38183 | 2714 | 3380 | 18.92 | 6.54 | 10.78 | 0.407 | 0.209 | 0.00/0.97 | 39.0/53.0 |
| 18:18:10 | Sucesso | 6/3 | 17094 | 1128 | 3338 | 6.31 | 1.50 | 3.62 | 0.159 | 0.080 | 0.00/0.94 | 84.3/98.3 |
| 18:18:39 | Sucesso | 9/3 | 35771 | 2473 | 3385 | 19.31 | 7.25 | 10.97 | 0.397 | 0.227 | 0.00/0.46 | 45.7/59.7 |
| 18:19:08 | Sucesso | 7/3 | 17574 | 1307 | 3346 | 7.83 | 1.66 | 2.76 | 0.243 | 0.106 | 2.00/1.06 | 82.4/95.4 |
| 18:19:42 | Sucesso | 9/3 | 33676 | 2660 | 3379 | 24.14 | 7.56 | 15.77 | 0.430 | 0.217 | 0.00/0.16 | 56.6/71.6 |
| 18:20:16 | Sucesso | 8/3 | 18753 | 1362 | 3344 | 12.74 | 2.02 | 5.59 | 0.211 | 0.091 | 4.00/0.82 | 82.2/99.2 |
| 18:20:52 | Parcial | 2/1 | 9390 | 473 | 0 | 15.59 | 7.10 | 8.37 | 0.076 | 0.035 | 0.00/0.00 | —/— |
| 18:22:04 | Falha | 0/0 | 0 | 0 | 0 | 10.00 | 5.38 | 4.62 | 0.000 | 0.000 | 0.00/0.00 | —/— |
| 18:23:15 | Sucesso | 6/3 | 19368 | 1467 | 3352 | 8.66 | 3.05 | 5.30 | 0.154 | 0.145 | 0.00/0.00 | 84.0/98.0 |

O [resumo JSON](beta-diagnostic-20261027.summary.json) contém cada página, minuto UTC, aquisição, status HTTP, headers/body/parse/bytes, normalização, provedores e rejeições. Não contém URL, cursor, token, identidade de veículo, coordenada ou payload individual. Headers representa o tempo até os headers; não separa DNS, TCP ou TLS.

Ao todo: 219.177 registros brutos, 144.083.000 bytes HTTP observados e 15.346 rejeições: serviço ausente 9.953, GPS antigo 5.390 e GPS futuro 3. Esses registros se repetem entre páginas, minutos e ciclos; não representam frota única.

Os tempos acumulados foram 55,768 s até headers, 82,148 s de body, 2,633 s de parsing e 1,408 s de normalização. Esperas explícitas: intervalo 4,414 s e provider pending 6,000 s. O gate interno consumiu tempo próximo de zero. Na aquisição completa mais lenta, de 24,14 s, headers consumiram 7,56 s e body 15,77 s; parsing e normalização somaram cerca de 0,65 s. Os três timeouts receberam HTTP 200/headers e terminaram com body incompleto: o limite da requisição cobre headers mais body. Isso não é evidência de falha de parsing nem de indisponibilidade HTTP do backend.

Não reapareceu uma aquisição de 48,6 s. Comprova-se nesta execução o predomínio de transferência/leitura, além de esperas por pending de 2 e 4 segundos em dois ciclos. Isso não determina retrospectivamente a causa da aquisição histórica. ZIRIX e CONECTA são provedores internos à ITS; não houve uso de ZIRIX_DIRECT. Sua participação muda conforme o GPS físico selecionado.

## Composição, CAS e publicação

Após o timeout inicial, o snapshot completo em T=127,90 s foi apenas a primeira sondagem saudável de recuperação. A primeira composição com as duas fontes só ocorreu em **T=210,06 s**, após a segunda sondagem. Outra falha reiniciou essa espera mais tarde. Houve **seis composições conjuntas e 23 com suporte GTFS apenas**, todas com suporte saudável.

Nas seis composições conjuntas:

- ITS: 3.338–3.385 veículos; GTFS: 2.084–2.105; compartilhados: 2.015–2.036.
- Exclusivos ITS: **1.309–1.354**; exclusivos GTFS: **67–74** por composição. Como `invalid=0`, é possível subtrair compartilhados dos inputs.
- Selecionados: 3.359–3.413, sendo **1.354–2.154 ITS** e **1.259–2.053 GTFS**.
- Soma de observações selecionadas nas seis gerações: ITS 10.220 (50,42%) e GTFS 10.050 (49,58%). Não são veículos distintos no período.

A diferença entre a união e a seleção corresponde aos empates conflitantes rejeitados. Contadores de conflito de coordenada e linha podem se sobrepor; não somá-los como veículos distintos. Nenhuma proteção foi relaxada para obter cobertura.

A imagem 151 rastreia exatamente **apenas a linha 898**, toda recebida pelo GTFS nesta execução. Foram 143 observações selecionadas/consumidas/validadas por idade/ACK, das quais 122 novas por timestamp, associadas, aceitas pelo CAS, concluídas no SignalR e retornadas pelo REST. São contagens por geração, não 122 veículos.

Na geração 2: seleção T=87,658 s; matching 94,791 s; CAS 96,918 s; SignalR 97,196 s; ACK 97,212 s; REST 104,209 s. O backend vinculou oito observações físicas novas na mesma geração. O supervisor observou a prova conjunta com renovação no cliente em **T=105,393 s**. Portanto o gate estrito de 90 segundos **não foi cumprido**, sem aprovação retroativa; no beta isso permaneceu diagnóstico. O backend mantém o vínculo exato da coorte em memória. O resumo do cliente confirma renovação física agregada, sem exportar uma chave individual para correlação externa.

**Não há nesta imagem contagem exata por fonte de CAS/REST/SignalR para toda a frota.** Não converter `selected_its` em publicados ITS. A prova funcional de cobertura publicada está nas amostras REST e no cliente abaixo. Nos 47 ciclos de polling observados com início dentro da ativação: 36.765 CAS aceitos, zero rejeitados/infra e 5.657 veículos enviados; 16 snapshots SPPO consumidos, sem pendência registrada. Esses totais incluem BRT e observações repetidas. Não atribuí-los integralmente à ITS ou a veículos únicos.

Duração dos ciclos: mediana 1,192 s e máximo 15,838 s; matching máximo 9,010 s; etapa SignalR máxima 0,083 s. O tempo de aquisição do coletor não é o mesmo que a duração do polling.

## Cobertura e frescor publicados

Percentuais são a soma das observações até 40/60/180 s dividida pela soma de veículos das amostras, contando possíveis repetições. Idades são a **mediana das medianas/p90 de cada snapshot**, não percentis globais. Antes: oito amostras; durante: 36; depois: três, ainda incluindo recuperação.

A comparação é temporal, sem um feed GTFS paralelo como contrafactual. Mudanças de frota/hora e retenção afetam as contagens; não se prova causalidade isolada nem cobertura completa da rede.

| BUS | Veículos antes → durante → depois (mín–máx) | GPS≤40% antes/durante/depois | GPS≤60% | GPS≤180% | Mediana física s | p90 por snapshot s |
|---|---|---|---|---|---|---|
| 838 | 12–12 → 12–16 → 13–13 | 2.1/7.1/12.8 | 57.3/29.6/25.6 | 100.0/96.4/100.0 | 55.7/67.5/84.3 | 68.0/95.0/104.3 |
| 898 | 9–9 → 9–10 → 9–10 | 1.4/4.5/35.7 | 52.8/26.7/64.3 | 100.0/99.1/96.4 | 57.9/67.8/42.2 | 76.3/89.9/57.2 |
| 006 | 1–1 → 0–2 → 0–0 | 0.0/2.0/— | 37.5/11.8/— | 100.0/80.4/— | 63.5/89.7/— | 63.5/98.1/— |
| 457 | 21–22 → 22–25 → 23–23 | 0.0/7.2/31.9 | 48.0/35.2/52.2 | 100.0/98.1/100.0 | 60.3/65.4/41.2 | 69.3/85.4/103.1 |
| 483 | 24–24 → 21–28 → 20–21 | 2.1/7.3/27.4 | 40.1/31.7/62.9 | 100.0/97.2/95.2 | 61.5/70.5/45.2 | 73.8/100.4/57.2 |
| 355 | 6–6 → 6–7 → 6–6 | 2.1/6.4/16.7 | 45.8/30.3/50.0 | 100.0/95.3/100.0 | 63.0/72.9/66.7 | 77.0/102.5/104.3 |
| 805 | 2–2 → 2–2 → 2–2 | 6.2/15.3/16.7 | 68.8/50.0/33.3 | 100.0/100.0/100.0 | 55.3/59.9/83.3 | 61.8/67.7/89.3 |

Excluindo o início até T=240,06 s, 30 segundos após a primeira composição conjunta, as 22 amostras restantes ainda incluem fallback posterior. A 838 teve média de 15,23 veículos e 26,9% até 60 s, contra 12 e 57,3% no baseline. A 898 teve 9,27 e 25,5%, contra 9 e 52,8%. Logo, a perda de frescor não se explica apenas pela troca inicial.

A cobertura adicional chegou ao REST, mas nem toda posição adicional era recente. Na 006 o segundo veículo envelheceu e ambos desapareceram ao final, sem mudanças de TTL. A conclusão é **mais cobertura, sem ganho consistente de frescor; nesta amostra, a fração até 60 s piorou**.

| BRT controle, GTFSRT_BRT | Veículos antes/durante/depois | GPS≤60% antes/durante/depois | Mediana física s |
|---|---|---|---|
| 35 | 9–10 / 8–10 / 10–10 | 88.3/78.7/90.0 | 41.4/40.8/21.2 |
| 60 | 17–19 / 17–19 / 19–19 | 97.2/85.6/89.5 | 40.4/39.8/24.2 |
| 67 | 8–10 / 9–10 / 11–11 | 90.3/77.5/90.9 | 43.0/45.0/25.2 |

BRT permaneceu GTFSRT_BRT. Houve oscilações e reconexão durante a recriação da API compartilhada; não declarar impacto zero. As medianas BRT ficaram próximas das anteriores, sem erro GPS observado. Os containers de autenticação/dependências foram preservados; não foi testado login de usuário externo.

## Evidência e recursos

Captura de 863,33 s: 3.496.899 bytes de entrada de logs, arquivo de 185.092 bytes/325 registros, dentro dos limites de 64 MiB/8 MiB/12.000 linhas/1.000 s. Três IDs de container, duas trocas normais, sem reconexões inesperadas, overflow, gaps, truncamento ou duplicatas. `integrity_valid=true`, `archive_complete=true`, `evidence_complete=true`; 11 timings e 11 resumos reconciliados. Isso comprova retenção dos eventos, não sucesso de todas as aquisições.

SHA256 do arquivo: `a601ec796a91535a88276d1ad208ebc9db5f0ed13a7a431cdf92f8d07239811f`; eventos: `10b6ba15caf0c0fcfdad68127d3d9d7985a0aeca6a11fb08a237f4dd95e0c8cf`. Evidências privadas preservadas em `/home/guilherme/.noponto-deploy/beta-diagnostic-20261027T180912Z` e em artifacts local. Overrides referenciados pelos labels preservados. Não persistidas identidades/coordenadas/payloads GPS.

Amostras da API: CPU 0,65–78,58% de um núcleo, memória de container 117,1–282,2 MiB/768 MiB, incluindo a primeira amostra do processo anterior. Não são custo integrado de CPU nem pico absoluto. PostgreSQL atingiu cerca de 100% de um núcleo também no baseline; não atribuir todo seu custo à ITS.

RAM final disponível: 2.332.500 KiB. Swap livre final: 4.823.544 KiB, contra 4.854.264 KiB inicialmente, cerca de 30 MiB adicionais ocupados, sem exaustão ou pressão sustentada observada. Nenhum OOM ou restart inesperado. Os sete containers dependentes mantiveram ID, configuração, início e reinícios idênticos.

## Estado final

Às **18:26:06 UTC**: release 151 e mesmo digest, **GTFSRT_BUS/fallback vazio/FullFallback/trace false**, BRT GTFSRT_BRT, uma API, lock livre. Cron próprio removido às 18:25:16 UTC. Nenhum capturador, controlador ou sonda HTTP temporária ativo. HTTP, Redis e PostgreSQL saudáveis; cliente SignalR recebeu 27 mensagens em 35 segundos, sem erros, regressões, duplicações ou coordenadas inválidas.

API final: `5eb27cf40df334ba9ee06f9231e851858c05c96650c52eb41e1429303a01173e`, início 18:24:16,674 UTC, restart 0/OOM false. A tarefa encerra esta única tentativa; nenhuma nova janela armada.

## Proposta para o backend — não implementada

`ItsFallbackGpsSource.GetResultAsync` aguarda `primary.GetResultAsync` antes de `ReadFallbackAsync`. O suporte GTFS, mesmo atualizado pelo cache compartilhado, não chega ao compositor enquanto a ITS está pendente. Simplesmente iniciar as duas leituras e fazer `Task.WhenAll` **ainda espera a fonte mais lenta**; não elimina esse bloqueio.

O coletor existente aguarda o resultado, publica um snapshot, não sobrescreve geração sem ACK e depois espera o intervalo configurado (padrão 10 s). O polling tem alvo de 15 s. Aquisição, espera do handoff e matching adicionam idade ao GPS antes da publicação.

Proposta restrita ao modo Hybrid:

1. Manter um coordenador/compositor no adaptador e o único escritor atual. Permitir **uma aquisição ITS em voo e uma leitura GTFS**, respeitando os gates, budgets, caches e intervalos HTTP existentes. ITS usa token de vida do host e budget próprio; GTFS conserva seu deadline. Não cancelar a aquisição ITS porque uma rodada GTFS terminou, nem iniciar outra em cada poll.
2. Retornar um snapshot GTFS completo utilizável assim que disponível, sem aguardar ITS pendente. A tarefa ITS concluída só atualiza um cache imutável em memória do último snapshot completo; nunca escreve Redis ou SignalR. Na próxima chamada elegível, compor esse ITS com GTFS corrente usando `ItsHybridGpsComposition`. O produtor/handoff/ACK continua único, com backpressure.
3. Antes da composição, revalidar idade física, identidade, modal e coordenadas. Não renovar timestamps de caches, reutilizar dados vencidos ou inventar atualização pelo recebimento. Preservar deduplicação, matching, CAS e TTLs. Leituras parciais não substituem um snapshot completo.
4. Separar a declaração de recuperação saudável da admissão por veículo no Hybrid. Duas sondagens podem continuar evitando alternância de estado, mas bloquear um snapshot completo já validado custa tempo: primeiro completo em T=127,90 s, composição conjunta em T=210,06 s. Uma alteração nessa admissão precisa ser explícita e testada; não foi feita aqui.
5. Ao encerrar o host, cancelar tarefas próprias, observar exceções e descartar resultados tardios/de execução anterior. Descarte assíncrono delimitado, sem segurar lock de deploy nem esperar o provedor indefinidamente. Se GTFS falhar e houver ITS completo ainda válido, compor ITS; se ambas falharem, retornar estado explícito sem geração fictícia. Preservar FullFallback e GTFS isolado.
6. Medir espera por ITS pendente, idade física na origem/composição/handoff/publicação, tempo GTFS e contagens por fonte. Para atribuição exata de publicação fora da 898, planejar coortes agregadas limitadas, sem identidades persistidas ou aumento silencioso de logs.

Testes necessários antes de publicar essa mudança: ITS lenta, timeout ou IO que ignora cancelamento enquanto GTFS novo retorna sem bloqueio; uma tarefa por fonte; GTFS falho com ITS completo; cache ITS expirado; empate conflitante e GTFS mais novo; ACK pendente sem segundo escritor; cancelamento/restart sem tarefa órfã ou resultado tardio; CAS monotônico e SignalR sem duplicação. Usar relógio/fontes falsos e, depois, integração descartável autorizada.

Outra prioridade é reduzir o downtime de troca e identificar o que demora no encerramento da API. A assimetria de startup HTTP 34–38 s versus rollback 6–10 s não identifica qual tarefa bloqueou shutdown. Nos dados ITS, transferência/leitura domina: parsing e normalização não são o primeiro alvo. Uma futura mudança do contrato de consulta por minuto pode reduzir registros repetidos, mas exige validação de cobertura, sem truncamento.

Nenhuma alteração C# ou nova imagem nesta entrega. A próxima ação útil é revisar e implementar o coordenador limitado; não repetir automaticamente a janela nem manter Hybrid ativo.
