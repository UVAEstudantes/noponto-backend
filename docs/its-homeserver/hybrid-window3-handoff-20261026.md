# Terceira janela híbrida — tentativa de 26/10/2026 após correção do handoff

**INCONCLUSIVO para o ganho de cobertura/frescor do híbrido; NO-GO para outra tentativa até revisar localmente a proteção durante a inicialização.** O handoff funcionou e o rollback obrigatório concluiu em GTFS-RT. A primeira aquisição ITS sofreu timeout de headers; nenhuma composição com contribuição ITS foi confirmada. O supervisor encerrou a tentativa na verificação inicial de Hybrid saudável, antes de produzir amostras `its_sample`. A 898 passou pelo servidor com novos timestamps, mas a prova conjunta exigida não apareceu até 90 s.

Base local `310c29dc88095b08ce23e5270be586d4243ca1a8`. Segunda-feira, 26/10; horários abaixo em UTC, três horas à frente de Brasília. Não comparar as contagens com o sábado 24/10 como se a operação fosse equivalente. Esta tarefa não alterou código, imagem, TTLs, cadências, limites, BRT ou banco; não houve push/merge/deploy de imagem nem segunda tentativa automática.

## Preflight, cron e handoff

Release 151 e digest `sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8` confirmados antes/depois. BUS iniciou e terminou em `GTFSRT_BUS`, fallback vazio, `FullFallback`, trace `false`; BRT sempre `GTFSRT_BRT`. Uma API escritora, lock livre no preflight, oito containers em execução e sem OOM. HTTP, Redis PONG, PostgreSQL aceitando conexões e SignalR saudáveis. Antes da instalação, nenhum processo/cron de janela estava ativo. RAM disponível 2.317.580 KiB, swap livre 4.887.032 KiB, disco livre 19.599.273.984 bytes, quatro CPUs/load de 1,94.

Diretório exclusivo novo `/home/guilherme/.noponto-deploy/hybrid-window3-handoff-20261026T202900Z` (0700). Cinco arquivos canônicos Git UTF-8/LF, modo 0600, conferidos no servidor:

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `75bfe9a999858fbf964e79b2a6281e6b8a4fc3c3c6d228845515d5f799862286` |
| `its_log_capture.py` | `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

`start_capture()` foi chamado uma vez em `proof`. Contrato completo: PID 968010/nascimento `11748121`, sessão própria, hash, container e device/inode do arquivo `2049/3670611`. Cron real executou o **supervisor canônico** em `--tick` às 20:31:01, com ancestralidade `python3 → sh → cron → cron → systemd`, após desconexão SSH. Lock exclusivo rejeitou outro controlador; a captura não herdou lock/SSH e seguiu por ID imutável do container. `validate()` renderizou candidata e rollback somente em memória. A entrega independente do cron e os guards do watchdog foram conferidos; não se provocou falha artificial do controlador para testar seu ramo destrutivo.

Às 20:31:50, mudou-se somente `phase` para `scheduled`, preservando integralmente `state.capture`. O cron iniciou o supervisor real em `baseline`; os monitores confirmaram o mesmo PID/nascimento/sessão/inode, heartbeat recente e uma API, sem segunda captura, gaps ou falha. Não foi usado o harness temporário do preflight anterior para substituir a supervisão.

## Duração e interrupção

| Evento | UTC |
|---|---|
| Primeira amostra formal de baseline concluída | 20:32:03,900 |
| Oitava/última amostra formal concluída | 20:33:57,748 |
| Solicitação ITS; início do prazo máximo de 600 s | 20:34:13,480 |
| Captura passa a seguir o container híbrido | 20:34:46,108 |
| Deadline estrito da 898 | 20:35:43,480 |
| Erro da verificação inicial; início do rollback | 20:35:46,232 |
| Container híbrido deixa de ser seguido, EOF sem erro | 20:36:16,938 |
| Nova API GTFS iniciada, conforme inspect final | 20:36:17,237 |
| GTFS saudável confirmado; `phase=complete` | 20:36:27,758 |
| Arquivo fechado/verificado, fora do lock | 20:36:28,485 |

Foram **129,58 s entre a primeira amostra formal concluída e a solicitação ITS**, portanto o mínimo de 120 s foi cumprido. Oito amostras registradas, 838 com 19–20 veículos e 898 com 12 em todas; 006 legitimamente vazia.

A tentativa consumiu **134,28 s da solicitação ITS até GTFS saudável**; rollback começou aos **92,75 s** e levou **41,53 s**. O leitor observou o container híbrido por aproximadamente **90,83 s**; isso delimita a observação do container e não substitui um `StartedAt` exato da API removida. Os eventos históricos de Docker consultados depois já não retornaram esse intervalo. Não houve uma janela híbrida saudável de 600 s.

O motivo persistido foi `exception_RuntimeError`, com `reason=unclassified_failure`. O fluxo aprovado chama `healthy(ITS_CURRENT)` antes do laço de amostras; ele exige `BUS hybrid` com ITS e GTFS em sucesso e contribuição positiva de ambas. Os quatro resumos retidos mostram ITS em falha e contribuição zero; essa verificação não se confirmou. O log não preserva a mensagem específica do RuntimeError, portanto o contexto sustenta a falha da verificação inicial, sem atribuir uma exceção interna que não foi registrada.

**Pendência de segurança comprovada:** a espera de até 60 s em `healthy()` ocorre depois da troca da API e antes de avaliar o gate da 898. Nesta tentativa, o rollback começou **2,75 s após o deadline de 90 s**, sem prova conjunta. O prazo não foi aprovado retroativamente nem alterado. Antes de outra autorização, revisar/testar localmente a avaliação do deadline, a parada durante a inicialização e a observação REST/SignalR nessa espera. Não foi feita correção funcional nesta tarefa.

## Cobertura e frescor por linha

Valores REST: **veículos / quantidades até 40/60/180 s / idade mediana/p90 em segundos**. Baseline usa a última amostra formal, não uma média de dias diferentes. Pós-rollback foi lido na verificação final de saúde. `N/D` durante significa ausência de amostra comparável, não ausência de ônibus: a inicialização não chegou ao laço `its_sample`, e não houve leitura REST por linha arquivada nessa fase. Não é possível concluir ganho ou perda híbrida por linha.

| Linha | Último baseline GTFS | Durante Hybrid saudável | Após rollback GTFS |
|---|---:|---:|---:|
| BUS 838 | 20 / 0/12/20 / 57,5/71,5 | N/D | 21 / 0/3/20 / 67,3/78,3 |
| BUS 898 | 12 / 0/7/12 / 56,1/70,6 | N/D | 13 / 0/1/13 / 71,3/123,3 |
| BUS 006 | 0 / 0/0/0 / — | N/D | 0 / 0/0/0 / — |
| BUS 457 | 24 / 0/9/24 / 63,1/69,6 | N/D | 25 / 0/2/25 / 69,3/82,3 |
| BUS 483 | 30 / 0/15/30 / 60,1/73,6 | N/D | 32 / 0/5/32 / 70,3/82,3 |
| BUS 355 | 5 / 0/2/5 / 60,6/66,6 | N/D | 5 / 0/0/5 / 71,3/72,3 |
| BUS 805 | 1 / 0/1/1 / 49,6/49,6 | N/D | 1 / 0/0/1 / 150,3/150,3 |
| BRT 35 | 11 / 4/10/10 / 41,6/49,6 | N/D | 10 / 10/10/10 / 22,3/28,3 |
| BRT 60 | 24 / 8/22/24 / 41,6/49,6 | N/D | 23 / 22/22/23 / 27,4/32,4 |
| BRT 67 | 13 / 3/11/12 / 42,6/140,6 | N/D | 11 / 9/10/11 / 27,4/48,4 |

## ITS: decomposição efetivamente retida

Aquisição A: janela UTC 20:34, sem cursor, **timeout aguardando headers**. `ITS timing`/resumo registram falha `request_timeout`, duração 10.009,91 ms, zero páginas concluídas/veículos. A tentativa HTTP registrou headers 10.021,32 ms, sem status/body; a pequena diferença é entre medições/logs distintos e não deve ser somada. Gate 0,117 ms, espera por intervalo/pending e normalização zero nessa aquisição. Isso comprova onde o tempo dessa falha foi gasto; não identifica se a causa externa foi provedor, rede ou outro intermediário.

A aquisição B começou posteriormente e ainda estava em andamento durante o rollback. Os registros abaixo sobreviveram à remoção do container:

| Janela UTC / tentativa | HTTP/status | Headers ms | Body ms | Parse ms | Bytes | Normalização |
|---|---:|---:|---:|---:|---:|---|
| 20:36 / pending 1 | 200 | 446,12 | 0,04 | 25,43 | 125 | provider pending, 0 linhas descartadas |
| 20:36 / pending 2 | 200 | 148,20 | 0,03 | 0,14 | 125 | provider pending, 0 linhas descartadas |
| 20:36 / página 3 | 200 | 242,83 | 2.000,48 | 31,87 | 893.213 | 1.454 registros, 31,93 ms |
| 20:35 / página 4 | 200 | 393,77 | 296,13 | 21,60 | 958.737 | 1.540 registros, 20,34 ms |

Todos os pedidos retidos tinham `cursor_present=false`; esses números de página incluem as tentativas pending, não demonstram quatro páginas de cursor. Os dois eventos pending e a diferença entre timestamps/tempos HTTP são compatíveis com aproximadamente 2 s de espera entre tentativas; não há `ITS timing` final de B para totalizar todas as esperas. A terceira janela de B não foi retida. Não houve conclusão completa/snapshot ITS publicado nessa aquisição; não considerar os **2.994 registros brutos parciais** como frota normalizada.

Nas duas páginas normalizadas: ZIRIX 1.726 e CONECTA 1.268 registros brutos; rejeições conhecidas **service=59, physical_stale=40**, total 99. Não são veículos únicos nem contribuição final por provedor. Idades físicas no início/fim e descartes finais de B ficaram indisponíveis porque o ciclo foi interrompido. Não reapareceu uma aquisição completa de 48,6 s; os dados não resolvem a causa daquele caso histórico. Houve nesta tentativa timeout de headers, provider pending e uma leitura de body de 2 s, com parse/normalização muito menores nas páginas disponíveis.

## Composição, 898 e publicação

| UTC | Selecionados ITS / GTFS | GTFS até 40/60/180 s | Situação |
|---|---:|---:|---|
| 20:35:02 | 0 / 2.532 | 1.909 / 2.515 / 2.532 | ITS Falha, suporte Sucesso |
| 20:35:22 | 0 / 2.532 | 16 / 1.821 / 2.532 | reutilização sem renovar GPS físico |
| 20:35:33 | 0 / 2.538 | 1.752 / 2.524 / 2.537 | suporte atualizado |
| 20:35:53 | 0 / 2.538 | 1 / 1.752 / 2.537 | reutilização, já durante rollback |

Quatro composições, todas com **100% GTFS e 0% ITS**, motivo `hybrid_primary_unavailable`, sem conflitos de coordenadas/linha/sentido ou observações inválidas nos resumos. Não somar esses snapshots como veículos únicos. O frescor diminuiu nas reutilizações e recuperou com o feed atualizado; os timestamps físicos não foram artificialmente renovados. Essas contagens provam seleção, não publicação de toda a frota.

A captura reteve oito estágios da 898 para cada uma de duas gerações: selected → polling → age_valid → timestamp_new → matched → cas_accepted → signalr_completed → ack. Geração 1: 12 observações, nenhuma física posterior ao início da sessão de trace; ACK aos 61,78 s da solicitação ITS. Geração 2: 13 observações, **11 físicas novas**, com a sequência:

| Estágio, geração 2 | Segundos desde a solicitação ITS |
|---|---:|
| selected | 79,887 |
| polling | 80,665 |
| timestamp_new | 80,774 |
| matched | 89,575 |
| cas_accepted | 92,465 |
| signalr_completed | 92,741 |
| ack | 92,748 |

Há continuidade GTFS e aceitação/publicação **no servidor**, sem prova de perda da 898. Entretanto, `linked_complete_new=0` em todos os estágios; faltou leitura REST vinculada e confirmação arquivada do cliente durante essa inicialização. O envio completo do servidor não é ACK do cliente. Além disso, CAS/SignalR/ACK da geração nova só ocorreram **depois de 90 s**. **Gate 898 não satisfeito**, mesmo sem considerar a lacuna REST/cliente. Não usar esta geração tardia para aprovar a janela.

## Polling, recursos e saúde

Nos nove ciclos completos observados dentro do baseline, máximo 13.176 ms, 8.602 commits CAS aceitos, zero rejeitados/infra; são observações/ciclos, não veículos únicos. No pós-rollback, seis ciclos retidos, máximo 13.820 ms, zero falha CAS de infraestrutura. O trace da 898 comprova 12 e 13 aceitações por geração e ACK; não há métricas globais completas do polling híbrido, pois o supervisor falhou antes de `its_sample`. Seleção→ACK da geração 2 levou 12,86 s, incluindo matching e commit; não atribuir esse tempo ao HTTP ITS.

API no baseline: amostras CPU 0,60–84,20% de um núcleo, memória em torno de 276–283 MiB/768 MiB. **Não foram arquivadas amostras próprias de CPU/RAM durante Hybrid**; os monitores exibiam a última amostra de baseline, e esses valores não são medidas híbridas. Após retorno, uma amostra de boot chegou a 102,72%/139 MiB; pico isolado não prova saturação sustentada. No final, RAM disponível 2.292.492 KiB, swap livre 4.909.560 KiB e disco livre 19.629.912.064 bytes. Nenhum OOM/restart inesperado ou mudança nos sete containers dependentes. Não atribuir diferenças normais de recursos exclusivamente à prova.

Sondas SignalR de 35 s antes/depois: **45 → 27 mensagens**, zero erros/duplicatas/regressões/GPS inválido; baseline formal terminou com 90 mensagens e zero desses erros. A conexão receptora existia durante a inicialização, mas sua confirmação pós-ativação não foi arquivada em amostra do supervisor. Auth manteve o mesmo container; GETs sem autenticação em `/` e `/health` na porta 8081 retornaram 403 antes/depois, sem degradação de latência observada. Isso não comprova um login autenticado saudável. BRT permaneceu na mesma fonte e voltou a entregar posições recentes nos três controles; faltam amostras comparáveis durante a inicialização híbrida.

## Retenção, limpeza e decisão

Captura única por **361,47 s**, 1.451.838 bytes de entrada/64 MiB, arquivo **19.297 bytes e 38 registros** frente a 8 MiB/12.000 linhas. Zero overflow, gaps, duplicatas, truncamento ou falhas terminais. Três IDs seguidos (GTFS original → Hybrid → GTFS rollback), duas trocas normais; sem loop de reconexão. As famílias HTTP page, normalized page, timing, provider wait, acquisition, hybrid e flow estão presentes. Não há URL/cursor/token/identidade de veículo/coordenada/payload individual nos agregados preservados.

Rollback precedeu o encerramento da captura. `--verify` foi executado pelo supervisor uma vez após o retorno seguro e fora do lock: **integrity_valid=true, archive_complete=true, evidence_complete=false**. SHA256 do arquivo `9d282358219807acdbc64f6f3ee55e988067188b5156a38a07bf25b25ffd4df8`. `acquisitions_without_timing=2`: B foi realmente interrompida sem resumo final; A tem timing/resumo arquivados **antes** do HTTP page tardio, e o rastreador reabre o conjunto pendente quando recebe esse evento depois do resumo. O contador conservador não significa dois timings ausentes nem gap de transporte. Essa ordenação também merece teste local antes da próxima tentativa; o arquivo não foi reclassificado como evidência completa.

Às 20:37:24, somente o cron marcado desta janela foi removido após confirmar `phase=complete`, fonte GTFS, HTTP/Redis/PostgreSQL e captura encerrada. Crontab voltou a ausente. Verificação final às 20:38:12: release151/digest correto, uma API, lock livre, nenhum processo temporário, sete containers dependentes com IDs/configuração/inícios/reinícios idênticos. Nova API somente pelas duas trocas autorizadas, `RestartCount=0`, sem OOM; overrides privados usados pelos labels foram preservados. Evidências também foram copiadas para `artifacts/hybrid-window3-handoff-20261026T202900Z/`, não rastreadas no Git.

**Resultado híbrido INCONCLUSIVO; inicialização operacional não aprovada.** O suporte GTFS sustentou a 898 e o handoff/retenção sobre duas recriações funcionou. Falta uma aquisição ITS completa e uma observação híbrida comparável; a proteção de 90 s durante boot precisa revisão local antes de nova janela. Não há aprovação tardia nem autorização permanente. Produção final exclusivamente GTFS-RT. Próximo passo permitido por este relatório é investigação/correção local mediante escopo próprio, sem repetir automaticamente o ensaio.

Dados: [resumo estruturado](hybrid-window3-handoff-20261026.summary.json). Validados JSON, hashes canônicos, sintaxe dos scripts efêmeros, integridade da cópia do arquivo e diff documental. Testes Python/C# e CI não repetidos: nenhum código operacional foi alterado nesta tarefa; preservados os testes existentes e os laboratórios.
