# Correção local da captura após NO-GO diurno — 26/10/2026

A [tentativa de terceira janela](hybrid-window3-20261026.md) terminou no preflight, antes de qualquer ITS: primeira lacuna `input_queue_overflow` após29,73s de leitura GTFS;32ocorrências e32reconexões levaram ao `stream_budget`. O arquivo tinha SHA válido e34gaps, `archive_complete=false`. A produção não foi acessada nesta correção local e permanece, conforme última inspeção, na release151/GTFSRT_BUS/FullFallback/traceOFF/BRTGTFSRT_BRT.

## Diagnóstico verificável

O leitor de `DockerStream` colocava **toda** linha de `docker logs --follow` numa fila de256, antes de `Archive.ingest` descartar o que não pertence às famílias permitidas. `put_nowait` disparava `queue.Full` se um burst de logs comuns chegasse antes do próximo `Engine.step` (~1s, além do tempo de inspect/fsync). Um teste com300linhas comuns e uma página ITS ao final reproduziu exatamente `input_queue_overflow` sem Docker, API, provedor ou parsers caros. A mesma classe de erro ocorreu para400páginas válidas quando o consumidor foi atrasado artificialmente: falta de backpressure também importava. A hipótese de que HTTP/parsing ITS causou a falha diurna é excluída para esta ocorrência: ITS não estava ativa. Não é possível afirmar a composição real do burst produtivo, pois as linhas brutas não foram retidas.

Uma vez sinalizado o erro, `Engine.step` continuava reabrindo o stream ativo, embora `self.failure` já impedisse declarar saúde; no dado real, isso consumiu32aberturas. Um teste com erro injetado reproduziu a segunda tentativa indevida. Reconexões após EOF/erro temporário de transporte permanecem possíveis e marcadas como lacuna; `input_queue_overflow` e outros erros sem possibilidade de evidência completa agora encerram a captura imediatamente.

## Correção pequena, sem mudança de limite

- O leitor reconhece somente a marca Docker+prefixo aprovado antes de enfileirar. Logs comuns não entram na fila, mas continuam contados no orçamento bruto64MiB/linha16KiB e atualizam o timestamp usado no reconnect. O conteúdo bruto deles não é armazenado.
- Linhas **com prefixo reconhecido** continuam passando por `Archive.ingest`; schema inválido gera `schema_rejected`, sem persistir token/URL/payload. Agregados válidos aguardam até6s numa fila ainda limitada a256; se o consumidor não acompanhar, o mesmo `input_queue_overflow` é registrado e a evidência continua fail-closed.
- Fechamento do leitor libera produtor bloqueado e entrega linhas pendentes ao arquivo antes do footer. Uma linha parcialmente emitida quando o produtor é encerrado marca lacuna, sem arquivo falsamente completo.
- Erro terminal evita o ciclo de reabertura; o orçamento32aberturas continua a proteger falhas transitórias de transporte. Engine detecta o mesmo motivo e `run_capture` sai com footer incompleto. Nenhum outro escritor, requisição ao provedor, alteração de API, Redis/SQL, GPS, TTL, cadência, gate89890s, imagem ou BRT.

Orçamentos inalterados:8MiB/12000linhas de saída,64MiB de entrada,16KiB por linha, fila256,2streams,4containers,32aberturas, duração1000s. A espera limitada6s altera só o leitor passivo; ele não segura deploy.lock nem atrasa rollback da API.

## Validação local

O teste `test_daytime_burst_of_unrelated_logs_keeps_its_page_without_overflow` foi **vermelho antes** (`input_queue_overflow`) e passou depois. O teste `test_valid_burst_waits_for_bounded_consumer_without_losing_pages` também foi vermelho e agora preserva400páginas reconhecidas com fila256. `test_reader_overflow_is_terminal_without_reconnect_storm` falhava tentando reabrir após erro e agora confirma uma abertura/lacuna. Cursor após linhas descartadas, schema inválido, orçamento bruto, tratamento de EOF/reconnect, fechamento com fila cheia e privacidade têm testes diretos.

`python -B tools/gps/test_its_log_capture.py`:27/27. `python -B tools/gps/test_its_window.py`:41/41. Total68/68, em Windows sem Docker nem rede. Sintaxe Python, diff, hashes canônicos e orçamento conferidos. Testes .NET/offline/CI não foram executados porque nenhum código do backend C# mudou. A correção ainda **não foi provada no Debian com fluxo real**; o teste local não autoriza nova janela.

## Cinco hashes operacionais para uma futura prova, UTF-8/LF

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed` |
| `its_log_capture.py` | `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

## Estado e próximo gate

**NO-GO para ativar ITS** até uma nova prova somenteGTFS no Debian em horário/carga representativos demonstrar captura contínua, sem gap, saturação, troca de ID fictícia ou estouro de orçamento, com encerramento e `--verify` completos. Como o capturador mudou, o hash `its_log_capture.py` anterior deixou de ser válido; usar os cinco acima em um diretório novo. A prova nova precisa verificar processo independente, cron/lock e ausência de escrita adicional, sem reutilizar state/PID/prova do preflight abortado. Só então avaliar nova autorização explícita para a janela híbrida; esta tarefa não incluiu SSH, deploy ou ativação.
