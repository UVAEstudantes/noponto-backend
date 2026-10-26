# Terceira janela, nova tentativa de 26/10/2026 — NO-GO no preflight

**INCONCLUSIVO para ITS + GTFS; NO-GO para ativação nesta tentativa.** A proteção falhou antes do baseline formal e da troca de fonte. ITS ficou ativa por **0 s**; não houve rollback de configuração porque a API nunca saiu de GTFS-RT. A falha foi detectada pelo supervisor às 16:14:07 UTC (13:14:07 Brasília) como `CaptureFailure`, `operational_reason=capture_evidence_failed`, `activated=false`.

## Estado antes e depois

Release 151, imagem imutável `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`. BUS `GTFSRT_BUS`, fallback vazio, `FullFallback`, trace `false`; BRT `GTFSRT_BRT`. Uma API escritora. Os oito containers e os arquivos Compose efetivos mantiveram IDs, imagens, hashes de configuração, horário de início e contadores de reinício; zero OOM. Deploy lock livre antes/depois. Redis PONG, PostgreSQL aceitando conexões, HTTP raiz e SignalR saudáveis; sondas de 35 s receberam 27→38 listas, sem erros, duplicatas, regressões nem coordenadas inválidas. Host: quatro CPUs, RAM disponível 2.297.360→2.265.132 KiB, swap livre 4.917.752→4.916.472 KiB, disco livre 19,986→19,981 GB. São amostras, sem atribuição causal à prova.

Diretório privado novo `/home/guilherme/.noponto-deploy/hybrid-window3-retry-20261026T161200Z` (0700), sem reaproveitar state/PID/arquivo. Cinco arquivos de `git show eff3a3a:path`, em UTF-8/LF, modo 0600, conferidos contra o manifesto de [correção](log-capture-burst-fix-20261026.md): `its_window.py` `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed`; `its_log_capture.py` `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f`; `probe_http.py` `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248`; override ITS `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1`; rollback GTFS `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900`. Candidato/rollback somente renderizados em memória; nenhum aplicado.

## Prova e causa do NO-GO

Lock: um segundo processo recebeu código 73 ao disputar o `flock`; após liberação, obteve o lock com o capturador ativo. Captura passiva iniciada às 16:12:35 UTC, em sessão/PID próprios, sem FD de lock/SSH. Cron real executou `phase=proof` às 16:13:01 com ancestralidade `python3→sh→cron→cron→systemd`, independente da sessão SSH. Leitor `docker logs --follow` usou o ID imutável da API. O estado foi armado como `scheduled` às 16:13:53; o cron iniciou o supervisor às 16:14:01.

O supervisor entrou brevemente em `baseline`, mas **não registrou nenhuma amostra de baseline**. `supervise()` chama `start_capture()` incondicionalmente; a prova já havia iniciado outro capturador no mesmo diretório `capture/`. `Archive.__init__()` usa `Path.mkdir()` sem `exist_ok` e abre `aggregates.jsonl` com `O_EXCL`; uma segunda abertura do mesmo caminho produz `FileExistsError`, reproduzido em diretório temporário local. Como stdout/stderr do filho são `/dev/null`, o evento produtivo registra apenas o `CaptureFailure` do contrato de saúde, não a exceção interna do filho. A sequência observada e a reprodução sustentam a colisão de ciclo de vida como causa; não há evidência de novo `input_queue_overflow`.

A captura original continuou saudável até ser encerrada pelo PID/nascimento/caminho verificados. Leu 495.450 bytes em 145,38 s; arquivo 573 bytes/3 linhas, zero gap, reconexão ou truncamento, nenhum agregado ITS esperado em GTFS-only. `--verify`: `integrity_valid=true`, `archive_complete=true`, `evidence_complete=true`, SHA256 `2246482834376378182eab28a064f64a3b5514020b53cdeb4d4006c2d0301182`. Cópia idêntica preservada em `artifacts/hybrid-window3-retry-20261026T161200Z/capture-aggregates.jsonl`. CPU do capturador entre início e encerramento: 0,30 s; RSS 19.540 KiB; não é medição de custo ITS. O cron marcado foi removido após confirmar `precondition_failed`, `activated=false` e GTFS efetivo; crontab voltou a ausente. Nenhum processo temporário permanece, lock livre. `phase=complete` só é emitida após rollback de uma ativação; não se forçou essa fase fictícia num preflight abortado.

## Frota observada em GTFS, sem baseline híbrido

As duas sondas de saúde, antes e depois, não substituem o baseline formal de 120 s. A 838 tinha 19 veículos e a 898 tinha 12 antes, suficientes para prosseguir **até** a proteção falhar. Valores: veículos / quantidades com GPS físico até 40/60/180 s; idade mediana/p90 em segundos. Não há dados “durante híbrido”.

| Linha | Antes: veículos / 40/60/180 | Med/p90 | Depois: veículos / 40/60/180 | Med/p90 |
|---|---:|---:|---:|---:|
| BUS 838 | 19 / 0/14/19 | 55,1/71,1 | 19 / 2/17/19 | 47,0/62,0 |
| BUS 898 | 12 / 0/9/12 | 56,6/141,1 | 10 / 3/9/10 | 46,0/51,0 |
| BUS 006 | 0 / 0/0/0 | — | 1 / 0/1/1 | 52,0/52,0 |
| BUS 457 | 23 / 0/11/23 | 60,1/69,1 | 23 / 0/19/23 | 52,0/62,0 |
| BUS 483 | 23 / 0/13/23 | 57,1/66,1 | 24 / 7/23/23 | 46,0/56,0 |
| BUS 355 | 5 / 0/3/5 | 59,1/64,1 | 6 / 0/6/6 | 49,0/55,0 |
| BUS 805 | 1 / 0/0/1 | 65,1/65,1 | 1 / 0/1/1 | 56,0/56,0 |
| BRT 35 | 10 / 0/8/8 | 50,6/200,1 | 10 / 8/10/10 | 35,5/43,0 |
| BRT 60 | 15 / 1/14/14 | 47,1/53,1 | 17 / 11/15/17 | 37,0/82,0 |
| BRT 67 | 11 / 0/11/11 | 44,1/50,1 | 12 / 9/12/12 | 36,5/43,0 |

## Entregas não observadas e próximo passo

Sem baseline 120 s, ITS não foi adquirida; portanto duração de aquisição/página/janela, rejeições, contribuição ITS/GTFS, frescor híbrido, correlação 898 por geração, CAS/ACK/REST/SignalR híbridos e gate de 90 s são **não aplicáveis**, não zeros. A captura original íntegra não torna a prova híbrida completa. Nenhum critério foi afrouxado.

**NO-GO para repetir a janela com a sequência atual.** Corrigir e testar localmente o handoff entre prova passiva e `supervise()` para que somente um capturador use cada diretório/arquivo e a transição preserve evidências e orçamento. Depois exigir novo preflight, cron/watchdog fresco, baseline 120 s e autorização para nova ativação. Esta tarefa terminou ao falhar a proteção, como autorizado. Não houve push, merge, deploy, migration, alteração de banco/BRT/TTL/cadência, reinício de API ou ativação permanente.

Ver [resumo estruturado](hybrid-window3-retry-20261026.summary.json). Testes de código não reexecutados: o hash dos cinco arquivos não mudou e nenhuma correção funcional foi feita nesta tarefa. Executada apenas reprodução local da colisão com `Archive` em diretório temporário, além das provas operacionais descritas.
