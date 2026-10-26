# Preflight POSIX no Debian 12 — 26/10/2026

**APROVADO para as primitivas POSIX testadas. GO limitado para considerar uma autorização separada de janela supervisionada, com preflight completo novo.** Esta tarefa não solicita, arma ou executa essa janela. Não houve ITS, mudança de fonte, recriação da API, rollback produtivo, Compose mutável, cron, deploy, migration ou limpeza de dados.

Base `369f745147dedbd53a769161334119dba29ac29e`, branch `feat/its-current-sppo`. Horários UTC. Prova em Debian GNU/Linux12 (bookworm), Python3.11.2. Estado produtivo inicial/final saudável, release151 no mesmo digest aprovado.

## Auditoria e isolamento antes do SSH

Foi revisado o fluxo de `ActivationDeadline`, sinais, `run`, observador, cleanup e watchdog. O guard instala handlers apenas no thread principal, usa SIGALRM para o deadline e SIGTERM/SIGINT para parada, e os restaura/desarma ao sair. O rollback real permanece no controlador sob lock, antes de joins e captura; o observador só lê. O `run()` usa `subprocess.run`, que cancela/recolhe seu **filho direto** ao propagar a exceção. O watchdog possui um caminho separado com `killpg` após verificar PID/nascimento/cmdline; isso não significa cancelamento transacional de uma operação no daemon Docker.

O harness foi criado e auditado localmente antes de conectar. Ele leu o arquivo canônico aprovado e compilou somente seis definições por AST: `ActivationDeadline`, `SafetyFailure`, `CommandFailure`, `CaptureFailure`, `run` e `capture_health`. Não importou o supervisor inteiro nem executou seu bloco CLI. As definições de `switch`, `rollback`, `supervise`, `run_activation`, `tick`, `compose`, `validate`, `start_capture`, `ensure_capture`, `take_lock` e `save` não foram carregadas; seus nomes receberam funções que sempre lançam `PermissionError`. As onze negativas foram executadas, inclusive com argumento de captura supostamente saudável. `capture_health()` sem leitor retornou falso e não conferiu qualquer autorização.

O acesso a subprocesso dentro do `run` extraído foi limitado a um vetor exato: Python executando o script descartável próprio, sem shell. Outros comandos eram rejeitados. O script filho só grava metadados privados de PID/nascimento e dorme; não inicia descendentes. A outra chamada Popen do harness cria somente um filho próprio em sessão/grupo exclusivo, para provar `killpg` com identidade verificada. Nenhum comando Docker existe no harness de sinais. O runner de saúde separado carregou somente funções de leitura e permitiu inspect/ps/stats e os comandos de saúde RedisPING/pg_isready; os caminhos de controle ficaram bloqueados também nele.

Nenhum `state.json` foi criado, nem houve transição para `scheduled`, `baseline` ou `its`. Não se usou `run_activation()` como prova. Os deadlines curtos foram definidos apenas em `startup_deadline` de instâncias descartáveis do guard; os valores produtivos90/600 e os cinco arquivos canônicos permaneceram inalterados. Um wrapper de medição registrou a entrada no handler e delegou ao método original, sem substituir `check`, `arm` ou `cancel`.

## Estado antes/depois

| Verificação | Antes, 21:57:50 | Depois, 22:01:52 |
|---|---|---|
| Release / digest | 151 / `sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8` | idênticos |
| BUS / fallback / modo / trace | GTFSRT_BUS / vazio / FullFallback / false | idênticos |
| BRT | GTFSRT_BRT | idêntico |
| API escritora / containers | 1 / 8 | 1 / mesmos8 |
| IDs, imagens, hashes de configuração, inícios, reinícios e Compose | registrados | todos idênticos |
| OOM / janela ou supervisor | nenhum | nenhum |
| Deploy lock / cron | livre / ausente | livre / ausente |
| HTTP raiz | 34,06ms | 6,30ms |
| Redis / PostgreSQL | PONG / aceitando conexões | idênticos |
| SignalR em35s | 24 mensagens | 20 mensagens |
| Erros / duplicatas / regressões / GPS inválido no socket | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| RAM disponível | 2.267.052KiB | 2.328.380KiB |
| Swap livre | 4.896.760KiB | 4.897.528KiB |
| Disco livre | 19.462.717.440bytes | 19.456.102.400bytes |
| Load1, quatro CPUs | 0,951 | 0,903 |

As amostras de recursos não atribuem a variação normal de carga/disco ao harness. Não houve baseline híbrido ou comparação de cobertura. Estado persistido da release e imagem conferidos; nenhuma configuração/conexão foi impressa além das chaves públicas de fonte.

## Arquivos privados e hashes

Diretório novo `/home/guilherme/.noponto-deploy/posix-deadline-preflight-20261026T215400Z`, modo0700. Cinco arquivos exportados dos blobs Git em UTF-8/LF e conferidos no Debian, todos0600:

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `33255a6477418f64a05ad2dd88e9f6aa723b78587bd9ff1495cb95a0fd1e90a0` |
| `its_log_capture.py` | `055fa54b065d08d44b54d697e9b7924792af34323f27bc9631dc9146919d49ac` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

Harness privado `posix-proof.py`, SHA256 `a961a02f62edf958fac446353221f291d34d2049fcb3e1f7d022e6c893196403`. PID1017342/nascimento12283713, sessão própria1017342, stdio privado/DEVNULL e close_fds. Foi lançado desacoplado do SSH e terminou autonomamente. Scripts descartáveis, metadados e resultados também0600, sem sobrescrever arquivos históricos. Não houve instalação de cron, captura de logs produtivos ou coleta ITS.

## Provas nativas e latência

Os três casos SIGALRM abaixo medem **entrada do handler menos deadline monotônico**. O tempo até a exceção chegar ao chamador também inclui a limpeza do subprocesso/contexto. São amostras pontuais, não p95 ou garantia sob qualquer carga.

| Prova | Prazo curto | Atraso na entrada do handler | Tempo total até capturar a exceção | Resultado |
|---|---:|---:|---:|---|
| `time.sleep` bloqueante | 350ms | +0,061ms | 350,217ms | interrompido |
| `run()` com filho Python descartável | 800ms | +0,040ms | 800,920ms | filho cancelado e recolhido |
| Thread observador bloqueado em Event.wait | 350ms | +0,078ms | 350,265ms | main interrompido com observador ainda bloqueado |

SIGTERM e SIGINT enviados exclusivamente ao PID do próprio harness também interromperam a espera real. **Envio do sinal → captura da exceção**: 0,250ms e0,260ms, respectivamente; o motivo configurado em memória foi `operator_rollback`. Isso prova o handler e a propagação do motivo controlado, não o fluxo completo `--request-stop` com arquivo/PID do supervisor. Esses números não são a medição de entrada do handler SIGALRM. Os Timers remetentes foram encerrados e unidos após cada caso.

| Requisito da prova | Evidência |
|---|---|
| Instalação no main e espera interrompida por tempo monotônico | três casos SIGALRM nativos acima, com classe aprovada |
| Disparo no prazo | diferenças medidas acima; nenhum alarme foi transformado em sucesso de API |
| Cancelamento/reap sem órfão | filho1017343/nascimento12283755 ausente de `/proc` após a exceção, `reaped=true`, órfãos0; nenhum neto foi criado |
| Parada do operador | SIGTERM eSIGINT reais contra o próprio PID, motivo preservado |
| Handlers restaurados / timer residual | comparados após cada caso e no fechamento; todos restaurados, getitimer=(0,0) |
| Falha fechada | timer real já ocupado foi rejeitado sem ser substituído; construtor em thread secundário também rejeitado |
| Observador bloqueado | thread ainda viva no momento da interrupção; Event liberado e join concluído depois |
| Grupos de processo | filho em sessão/grupo exclusivo, birth/cmdline/PGID verificados e sem descendentes; SIGTERM somente nesse grupo e wait/reap concluídos |
| Captura não autoriza ativação | onze caminhos de controle bloqueados e testados; sem state, nenhuma função de ativação carregada |

A falha fechada nativa cobriu timer ocupado e thread inadequado; não se provocou uma falha de permissão do kernel em setitimer. O cancelamento de grupo foi uma prova separada com um grupo descartável próprio, **não** uma execução do watchdog produtivo e **não** uma demonstração de que `run()` cancela árvores arbitrárias de processos.

## Recursos, encerramento e limites da conclusão

Provas duraram **2,259s** no corpo do harness. CPU do processo inteiro: usuário0,067078s + sistema0,004256s; CPU dentro das provas0,007857s. Filhos:0,074742s de CPU. RSS máximo do harness22.952KiB (~22,4MiB); máximo dos filhos também22.952KiB, não a soma simultânea. Rusage registrou zero blocos de entrada e8 de saída no pai antes de gravar o resultado final; não é um total de I/O de todos os filhos/arquivos. Não foram alterados limites produtivos.

O resultado fechado e fsyncado confirmou apenas o thread principal antes da saída; a inspeção posterior encontrou **zero processos do diretório**, stderr vazio e todos os arquivos privados0600. O filho de comando e o grupo descartável já haviam terminado/reap. Não foi necessário sinalizar qualquer outro processo. Nenhum job foi instalado ou removido; o crontab preexistente permaneceu ausente. Evidências privadas foram preservadas também em `artifacts/posix-deadline-preflight-20261026T215400Z/`; laboratórios e artefatos anteriores foram mantidos.

**Limites:** nenhum Docker Compose mutável, sinal em container produtivo, rollback da API, cron watchdog real, novo handoff da captura, pipeline ITS/898 ou cliente híbrido foi exercitado. A prova não demonstra cancelamento de uma operação já aceita pelo daemon, nem cobre travamento de kernel ou qualquer árvore de descendentes. Não houve leitura opcional `docker logs`; a prova do cancelamento foi somente com Python descartável. O programa de controle completo continua dependendo dos guards/lock/watchdog e de supervisão operacional.

**Decisão:** primitivas POSIX aprovadas no Debian; GO limitado para considerar autorização separada de outra janela. Ainda seriam necessários preflight imediato novo, cron/watchdog/handoff/captura novos, baseline representativo e todos os gates originais90/600 para uma execução autorizada. Não há aprovação do híbrido por esta prova e não se solicitou nem armou uma janela automaticamente. Produção terminou exclusivamente GTFS-RT, sem alterações.

Validação documental: hashes canônicos, sintaxe/auditoria AST do harness e comparação dos fingerprints antes/depois. Os testes117/117 do commit-base não foram repetidos porque não houve alteração de código; .NET/CI/E2E produtivo não foram executados. [Resumo JSON](posix-deadline-debian-preflight-20261026.summary.json).
