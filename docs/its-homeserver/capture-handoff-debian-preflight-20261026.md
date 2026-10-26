# Prova Debian do handoff da captura — 26/10/2026

**Resultado: handoff aprovado em `phase=proof`, sem ativação ITS. GO para solicitar autorização separada de outra janela híbrida; não é autorização para executá-la.** A prova usou a correção local de `54fedee` e um processo efêmero, exclusivo do preflight, acionado pelo cron real. Não publicou imagem nem mudou a configuração produtiva.

## Separação da janela

A auditoria confirmou que `scheduled` chama `supervise()`, entra em `baseline` e pode acionar `switch()` para ITS. Portanto, esta prova **não** usou `scheduled`, `baseline` ou `its`, nem agendou `its_window.py --tick`. O cron chamou somente um harness privado em `phase=proof`, que importou os cinco arquivos aprovados, bloqueou por substituição local as funções `start_capture`, `switch`, `supervise` e `rollback`, e executou `adopt_capture()` seguido de `ensure_capture()`. O harness conferiu estado, imagem, fonte GTFS, contrato completo, saúde e identidade do arquivo antes e depois. Não tinha caminho para alterar a fonte. O teste da transição integral `proof → scheduled → baseline` continua coberto apenas pelos testes determinísticos locais; esta limitação foi deliberada para não armar uma janela real.

## Estado antes e depois

| Verificação | Antes | Depois |
|---|---:|---:|
| Release / imagem | 151 / `sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8` | idêntica |
| BUS / fallback / modo / trace | `GTFSRT_BUS` / vazio / `FullFallback` / OFF | idênticos |
| BRT | `GTFSRT_BRT` | idêntico |
| API escritora / containers | 1 / 8 | 1 / mesmos 8 IDs, imagens, configuração, inícios e reinícios |
| Lock / cron da janela | livre / ausente | livre / ausente |
| HTTP raiz | 7,2 ms | 4,2 ms |
| SignalR em 35 s | 16 mensagens | 24 mensagens |
| Erros, duplicatas, regressões ou GPS inválido no SignalR | 0 | 0 |
| Redis / PostgreSQL | PONG / aceitando conexões | PONG / aceitando conexões |
| RAM disponível | 2.264.256 KiB | 2.266.924 KiB |
| Swap livre | 4.916.728 KiB | 4.916.472 KiB |
| Disco livre | 19.961.249.792 bytes | 19.955.445.760 bytes |

Compose efetivo, release persistida, imagem e parâmetros da API foram conferidos antes/depois. Não houve recriação, restart induzido, alteração de banco ou de serviço. A sonda REST/SignalR permaneceu saudável. Não foi executado baseline formal, teste híbrido ou coleta ITS.

## Arquivos aprovados e prova privada

Diretório novo `/home/guilherme/.noponto-deploy/capture-handoff-20261026T164300Z`, modo 0700; arquivos operacionais, estado, health e arquivo em 0600. Os cinco arquivos foram exportados dos bytes canônicos do Git em UTF-8/LF e conferidos no Debian:

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `75bfe9a999858fbf964e79b2a6281e6b8a4fc3c3c6d228845515d5f799862286` |
| `its_log_capture.py` | `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

O harness temporário teve SHA256 `7f3f9effa4cf51aaedfb743116905f322a79af223deae6d09a564d7d76fb5619`, sem alterar os cinco arquivos aprovados. O override de rollback foi **renderizado somente em memória**, sem execução. O segundo controlador não conseguiu adquirir `deploy.lock` durante a posse do primeiro; após a liberação, a captura não reteve esse lock.

## Handoff e arquivo

`start_capture()` abriu uma única captura, com contrato completo no `state.json`. Na execução seguinte do cron real, o harness rodou em processo separado; a cadeia de processos continha `cron`/`systemd`, após o término da sessão SSH que havia iniciado a captura. `adopt_capture()` e `ensure_capture()` aceitaram **o mesmo PID 857333, nascimento `10399705`, sessão, container e device/inode**. O inode do arquivo permaneceu `3670597`; havia apenas um leitor e um arquivo. O heartbeat avançou até o encerramento, sem gap, truncamento ou falha terminal. O estado continuou `proof` e BUS continuou `GTFSRT_BUS` durante toda a prova.

A captura durou **82,20 s**: 256.016 bytes de entrada, 574 bytes/3 registros agregados arquivados, zero duplicatas, gaps ou truncamento. Sem ITS/trace, a ausência das famílias ITS é esperada e não prova parsing ITS real. `docker logs --follow` usou o ID imutável do container existente. O processo Python apresentou cerca de **0,2% de um núcleo**, RSS de **21.488 KiB**, I/O de bloco lido **1.593.344 bytes** e escrito **339.968 bytes** no instante amostrado; CPU/RAM do filho Docker não foram medidos isoladamente nesta prova curta.

O cron temporário foi removido preservando o crontab anterior (vazio). Somente o PID/nascimento verificados da captura recebeu SIGTERM. O coletor fechou o arquivo com `state=closed`; `--verify` retornou `integrity_valid=true`, `archive_complete=true` e `evidence_complete=true`, SHA256 `35141ef9e0e6077584ce02e2cc8e59b038a06f4db910ab98b7c91879631decbb`. Não restaram processos da prova, cron temporário ou lock retido. Evidências privadas ficaram no diretório exclusivo, sem reutilização futura.

## Decisão e limites

- **Correção do handoff no Debian: APROVADA para o caminho de adoção isolado.** PID/nascimento, sessão, inode, heartbeat, cron, arquivo e encerramento foram confirmados no sistema real. O erro anterior de segundo capturador não ocorreu.
- **Prontidão para solicitar outra janela híbrida: GO.** A próxima autorização ainda exige preflight imediato novo, diretório/state/PID novos, cron/watchdog/lock novos, baseline representativo, uma API escritora, gates da 898 e rollback obrigatório. Esta prova não substitui esses gates.
- **Limites pendentes:** esta tarefa não percorreu `scheduled → baseline → its` no Debian; a transição integral só foi testada localmente. Não houve logs ITS, correlação da 898, troca de container nem rollback real. Esses pontos só podem ser observados numa execução ITS autorizada. Os testes Python existentes (58 supervisor + 27 captura) não foram repetidos porque nenhum código mudou; .NET, CI remota e suíte offline não foram executados nesta prova operacional.

Dados estruturados de conferência: [resumo JSON](capture-handoff-debian-preflight-20261026.summary.json). A produção terminou exclusivamente em GTFS-RT, sem deploy, push, merge, migration, cron ativo ou ativação ITS.
