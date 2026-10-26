# Prova Debian da captura corrigida — 26/10/2026

**GO para solicitar autorização separada de uma nova janela híbrida; ITS não foi ativada.** A prova passiva GTFS-only durou 364,86 s, de 15:57:59 a 16:04:03 UTC (12:57:59–13:04:03 em Brasília). O capturador corrigido do commit `eff3a3acf2276d558860236e951e078458534ea8` leu continuamente os logs reais da API em Debian 12, ultrapassando o ponto de falha anterior de 29,73 s sem `input_queue_overflow`.

## Estado produtivo e limites da prova

Antes e depois: release 151, imagem `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`; BUS `GTFSRT_BUS`, fallback vazio, `FullFallback`, trace `false`; BRT `GTFSRT_BRT`. Uma API escritora, lock livre, crontab ausente e nenhum supervisor de janela ativo. Os oito containers mantiveram os mesmos IDs, imagens, hashes de configuração, horários de início e contadores de reinício; zero OOM. Os arquivos Compose efetivos permaneceram iguais. Redis respondeu PONG, PostgreSQL aceitou conexões e HTTP raiz respondeu antes/depois. Sondas SignalR de 35 s receberam 18 e 27 mensagens, respectivamente, cada uma com uma conexão e zero erros, duplicatas, regressões ou coordenadas inválidas. São sondas de funcionamento, não benchmark de frescor.

Host: quatro CPUs; RAM disponível 2.273.876→2.271.344 KiB; swap livre 4.913.656→4.916.216 KiB; disco livre 20,010→19,998 GB. A diferença de disco inclui atividade normal dos serviços e não é atribuível apenas à captura. API Docker stats pontuais: 237,3→269,5 MiB de 768 MiB e 69,99%→45,53% de um núcleo; não se infere causalidade a partir de duas amostras. Nenhum container foi recriado, reiniciado ou sinalizado pela prova; nenhuma configuração, banco, Compose ou imagem foi alterada.

## Instalação temporária privada

Novo diretório `/home/guilherme/.noponto-deploy/capture-burst-20261026T155540Z`, modo 0700. Cinco arquivos copiados de `git show eff3a3a:path` em bytes canônicos UTF-8/LF, modo 0600. O render de baseline/candidato/rollback foi validado **somente em memória**; nenhuma configuração foi aplicada. Arquivos históricos e evidências anteriores permanecem preservados.

| Arquivo | SHA256 no Debian |
|---|---|
| `its_window.py` | `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed` |
| `its_log_capture.py` | `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

O coletor iniciou em sessão/PID próprios, com stdin/stdout/stderr em `/dev/null`, sem herdar o lock ou descritores SSH. Um segundo processo foi rejeitado pelo `flock` enquanto o lock estava retido (código 73); obteve o lock depois, já com a captura em execução. A sessão SSH de instalação terminou, e 12 consultas SSH independentes confirmaram PID/nascimento, sessão, ID imutável do container, heartbeat e lock livre. Não se instalou cron nesta prova; a prova anterior de cron não foi reutilizada como autorização para uma futura janela.

## Resultado da captura

Leitura bruta: **1.394.206 bytes**, 2,08% do orçamento de entrada de 64 MiB. Arquivo filtrado e fechado: **574 bytes/3 linhas** (begin, stream_start, end), 0,0068% de 8 MiB e 0,025% de 12.000 linhas. Nenhuma família ITS/GPS flow apareceu, como esperado com ITS e trace desligados. Os logs comuns foram lidos e contabilizados, mas não enfileirados nem persistidos. Houve um único `stream_start`, nenhuma reconexão, mudança de container, lacuna, truncamento, duplicata ou falha de leitor. Heartbeat máximo nas 12 amostras: 0,89 s.

Encerramento por SIGTERM **apenas no PID do capturador**, após confirmar nascimento e caminho do script. `--verify` no Debian: `integrity_valid=true`, `archive_complete=true`, `evidence_complete=true`, `gap_count=0`; arquivo 0600, SHA256 `335069fdecdf412678e16b091f6f7caa42e842419089afec641bc0b26bf0afea`. Cópia idêntica em `artifacts/capture-burst-debian-20261026T155540Z/capture-aggregates.jsonl`. Nenhum processo temporário ficou ativo; crontab permaneceu ausente e deploy lock livre. O arquivamento não precisou bloquear nenhum rollback.

Entre amostras separadas por 307,6 s, Python consumiu 0,70 s de CPU (~0,23% de um núcleo), RSS estável em 19.576 KiB, leitura física adicional ~3,51 MiB e escrita física ~1,17 MiB. O filho `docker logs` consumiu 0,27 s de CPU (~0,09% de um núcleo), RSS amostrado ~30–32 MiB. I/O do filho Docker não foi medido isoladamente. A carga de 1 minuto ficou entre 0,94 e 1,14 nas amostras de borda; a RAM disponível mínima das amostras durante a prova foi 2.237.076 KiB e o swap livre mínimo 4.913.656 KiB. Esses valores medem custo incremental observável, não custo de uma futura captura com logs ITS detalhados.

## Comparação e decisão

No NO-GO anterior, ainda em GTFS, o capturador antigo estourou a fila de 256 em 29,73 s e repetiu a falha até 32 aberturas; o arquivo terminou com 34 gaps e completude falsa. Nesta execução com o mesmo limite de fila, a filtragem antes da fila e o backpressure limitado sustentaram 364,86 s de logs reais sem overflow, reconexão ou perda de evidência. A causa exata do burst anterior não foi registrada em logs brutos; esta prova confirma o comportamento corrigido sob a carga observada hoje, sem provar todas as cargas possíveis.

**GO apenas para solicitar autorização de uma nova janela**, sujeito a novo preflight imediato: confirmar digest/fonte/saúde, criar diretório novo, repetir prova real do cron independente e do watchdog, confirmar lock e escritor único, iniciar captura íntegra, obter baseline representativo e manter os gates/rollback obrigatórios. Esta prova GTFS-only não valida parsing/volume ITS real, troca de container, nem executa o supervisor em fase `scheduled`, `baseline` ou `its`. A ausência de ITS no arquivo não é evidência de cobertura ITS. Nenhuma nova janela, push, merge, deploy, migration, ativação ITS ou alteração produtiva ocorreu.

Ver [resumo estruturado](log-capture-burst-debian-preflight-20261026.summary.json). Testes locais reexecutados nesta tarefa: `python -B tools/gps/test_its_log_capture.py`, 27/27. Supervisor 41/41 já validado no commit base; não reexecutado, pois não foi modificado. Testes .NET/offline/CI não executados: nenhum código do backend mudou.
