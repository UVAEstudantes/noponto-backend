# Terceira janela — tentativa interrompida no preflight,26/10/2026

**INCONCLUSIVO para o híbrido; NO-GO no preflight. ITS não foi ativada.** A autorização de até600s não foi exercida: uma proteção falhou antes de scheduled/baseline/its. O checkpoint funcional foi `da52893`, com os cinco arquivos operacionais aprovados de `3fcbce8`; nenhum foi modificado.

## Estado inicial e final

Inspeções em15:25:35–15:29:51UTC (12:25:35–12:29:51 Brasília), segunda-feira26/10. Release151/active.run/active.env consistentes; imagem `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`. BUSGTFSRT_BUS/fallback vazio/FullFallback/tracefalse, BRTGTFSRT_BRT antes/depois. Uma API escritora, ID/início/configuração iguais. Os oito containers preservaram IDs, imagens, fingerprints de configuração, início, reinícios e saúde. ZeroOOM/reinício novo. Não houve deploy, stop/up/recriação de API, alteração de fonte, banco, TTL ou cadência.

RedisPONG/PostgreSQL aceitando conexões/containershealthy, HTTPAPI saudável e lock livre antes/depois. Duas sondas de35s receberam40 e36listasSignalR, uma conexão cada, zero erros/regressões/duplicatas/coordenadas inválidas. Os containers de autenticação permaneceram em execução e com configuração/início preservados; não se fez benchmark HTTP específico de autenticação após a interrupção.

## Preparação e motivo da interrupção

Novo diretório privado0700 `/home/guilherme/.noponto-deploy/hybrid-window3-20261026T152446Z`; cinco arquivos0600 exportados por `git show` em UTF-8/LF, com SHA256 iguais ao manifesto aprovado. Sem state/PID/prova reaproveitados. Render de candidata/rollback validado em memória, não aplicado. Segundo flock não bloqueante rejeitado com lock ocupado e aceito após liberação, enquanto captura rodava; captura sem herança do lock/SSH, sessão própria.

Cron temporário marcado `noponto-hybrid-window3-20261026T152446Z`, exclusivamente em phase=proof. Prova nova de execução independente com ancestrais python3→sh→cron→cron→systemd; nunca entrou em scheduled/baseline/its. Ramo destrutivo do watchdog não foi exercitado. A sonda de validação da captura não aprovou saúde; inspeção subsequente confirmou a falha e nenhuma ativação foi armada.

Captura começou15:26:47.708UTC. Primeiro gap às15:27:17.441, **29,73s após início**: `input_queue_overflow`. O arquivo registra32ocorrências desse motivo,32aberturas/32encerramentos do stream, depois `stream_budget` e `capture_duration_or_budget`. O coletor encerrou por proteção às15:27:55.466UTC, após67,76s. Leu1137754bytes; arquivou15988bytes/100linhas, sem atingir64MiB de entrada ou8MiB/12000linhas de saída.

A causa imediata está comprovada pelo código e motivo registrado: `DockerStream._read` insere **todas as linhas brutas** numa fila256 usando put_nowait e trata queue.Full como overflow; sanitização ocorre depois, no consumidor. Logo a fila pode saturar mesmo com linhas que seriam descartadas por não pertencer às famílias ITS/flow. Nesta provaGTFS-only não foi arquivado nenhum agregado dessas famílias. Não se conhece o tamanho exato do burst nem quais mensagens o compuseram: linhas brutas não são persistidas, por privacidade. Não atribuir a falha à API ITS, à rede, ao parsing ITS ou à aquisição histórica48,6s: nenhuma aquisição ITS ocorreu.

A falha permaneceu explícita/degradada durante reconexões; não foi mascarada como arquivo completo. Não aumentamos fila, budgets, número de reconexões ou qualquer limite. O ensaio de sábado à noite não comprovava tolerância ao fluxo diurno: esta execução demonstra uma limitação operacional real da captura.

## Arquivo e limpeza

`--verify` no Debian confirmou **integrity_valid=true**, mas **archive_complete=false/evidence_complete=false**,34gaps e truncated=false. SHA256 do arquivo: `7a7666b9d7a5dbdf535bde84fe3d9f4876ed59688fafc6f970152336019ea26a`. Integridade do prefixo não significa completude da evidência. Cópia idêntica retida em artifacts/hybrid-window3-20261026T152446Z/capture-aggregates.jsonl, além dos arquivos privados do servidor.

Removido somente o job marcado; crontab voltou a ausente, igual ao inicial. Nenhum processo desse diretório permanece ativo; lock livre e state final precondition_failed/capture_input_queue_overflow. O coletor já havia terminado autonomamente; não foi necessário sinalizar API ou aplicar rollback. Arquivos privados e overrides preservados, com permissões0600. A API continua usando seus Compose originais.

## Frota observada — somente GTFS antes/depois do preflight

Havia representatividade preliminar:838com18veículos e898com9, portanto não foi um NO-GO por baixa frota. **Não foi executado o baseline formal120s**, pois a captura falhou antes. As tabelas são duas amostras de saúdeSignalR, não comparação ITS×GTFS nem comparação com sábado. Colunas: veículos; quantidades até40/60/180s físicos; mediana/p90 em segundos. DuranteHybrid: não aplicável em todas as linhas.

| Modal/linha | Antes veículos | Até40/60/180 | Mediana/p90 | Depois veículos | Até40/60/180 | Mediana/p90 |
|---|---:|---|---|---:|---|---|
| BUS 838 | 18 | 12/17/18 | 36.2/49.7 | 18 | 0/15/18 | 50.6/65.1 |
| BUS 898 | 9 | 8/9/9 | 30.7/49.7 | 9 | 0/8/9 | 49.1/66.1 |
| BUS 006 | 1 | 1/1/1 | 32.7/32.7 | 1 | 0/1/1 | 42.1/42.1 |
| BUS 457 | 23 | 14/20/21 | 38.7/78.7 | 22 | 0/17/22 | 54.6/63.1 |
| BUS 483 | 23 | 17/21/23 | 35.7/47.7 | 23 | 1/20/23 | 49.1/63.1 |
| BUS 355 | 5 | 3/5/5 | 39.7/52.7 | 5 | 0/4/5 | 55.1/68.1 |
| BUS 805 | 1 | 0/0/0 | 223.7/223.7 | 0 | 0/0/0 | —/— |
| BRT 35 | 10 | 8/8/9 | 30.7/84.7 | 9 | 2/7/9 | 46.1/108.1 |
| BRT 60 | 16 | 14/14/15 | 24.7/90.7 | 14 | 7/13/14 | 39.6/48.1 |
| BRT 67 | 12 | 11/11/12 | 25.2/33.7 | 11 | 1/10/11 | 44.1/48.1 |

## Objetivos que não puderam ser medidos

| Entrega prevista | Resultado desta tentativa |
|---|---|
| Duração híbrida/rollback |0s; sem troca de fonte, rollback não necessário/não executado |
| ITS por aquisição/página/janela,HTTP/body/parse/normalização/esperas |Não adquirido; nenhuma conclusão sobre48,6s |
| Rejeições ITS, cobertura híbrida e contribuição por fonte |Não observado; não preencher com zero nem extrapolar GTFS |
|898 mesma observação seleção→geração→CAS→REST→SignalR→ACK |Não testada; traceOFF/gate90s não iniciado |
|CAS/gerações/ACK do híbrido |Não disponíveis; runtimeGTFS continuou, sem publicar escritor diagnóstico |
|Captura íntegra e completa |Íntegra, **incompleta**, impedindo ativação |

## Recursos

API CPU amostrada0,93%→1,16%, RAM236,7→267,7MiB/768MiB; não atribuir essa oscilação à captura sem evidência causal. Host RAM disponível2352268→2315168KiB (~2,21GiB final); SwapFree4907768→4908024KiB, sem aumento do uso; disco livre~18,67GiB final. Load1min0,82→0,90 em quatro CPUs. Serviços permaneceram saudáveis.

A amostra inicial do coletor foi21496KiB RSS. CPU/I/O de toda sua vida não foram medidos: ele já havia terminado autonomamente quando a limpeza conferiu o PID. Não transportar números de sábado nem tratá-los como desempenho desta tentativa. O consumo e ritmo total de logs da futura janela continuam sem validação.

## Decisão e pendência

**INCONCLUSIVO** sobre ITS+GTFS, porque a janela não ocorreu. **NO-GO** para nova execução enquanto a captura não tolerar o fluxo normal do backend com evidência preservada. A proteção impediu uma ativação sem observabilidade confiável.

Próxima correção deve ser local e limitada à ingestão/backpressure da captura, com testes determinísticos de bursts e reconexões, privacidade/budgets/fail-closed preservados. Avaliar filtragem segura antes da fila e/ou drenagem concorrente limitada; não escolher uma solução nem aumentar limites sem testar. Depois conferir novos hashes e repetir somente a provaGTFS no Debian com carga representativa. Só após esse gate será apropriado solicitar nova autorização da janela.

Nenhum código funcional ou arquitetura foi alterado nesta tarefa; somente relatório, resumo, links e histórico locais. Não repetidas suítes/CI: a evidência nova é a falha operacional real. Não houve push, merge, imagem nova, migrations, limpeza Redis/PostgreSQL, alteraçãoBRT ou permanênciaITS.

## Hashes operacionais conferidos

| Arquivo | SHA256 canônico UTF-8/LF |
|---|---|
| `its_window.py` | `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed` |
| `its_log_capture.py` | `d1ce61448534750fb005c1952b81149062bd9bd3217836d47b75fd61a5f1ca31` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |
