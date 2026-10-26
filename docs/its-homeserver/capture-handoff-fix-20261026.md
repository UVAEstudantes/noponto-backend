# Handoff da captura entre prova e supervisor — correção local de 26/10/2026

**Correção local validada; novo preflight Debian obrigatório antes de outra janela.** Nenhum acesso SSH, cron produtivo, deploy, ativação ITS ou alteração de banco ocorreu nesta tarefa. A produção conhecida continua na release 151 em GTFSRT_BUS/FullFallback/trace OFF, BRT em GTFSRT_BRT; esse estado não foi reinspecionado aqui.

## Causa e fluxo antigo

No preflight, o operador chamava `start_capture(state)` em um processo curto. A função gravava sessão/PID no `state.json` e abria `capture/aggregates.jsonl` exclusivamente. Depois da prova de cron, o estado passava de `proof` para `scheduled`; o cron executava `tick()` em outro processo. `supervise()` entrava em `baseline` e chamava **novamente** `start_capture()`, que gerava outra sessão e tentava usar o mesmo `capture/`. O leitor original permanecia vivo, mas o segundo processo falhava ao criar o diretório/arquivo exclusivo. O supervisor registrava `CaptureFailure` antes de qualquer amostra de baseline ou ITS. A [tentativa abortada](hybrid-window3-retry-20261026.md) mostrou esse estado e um teste local reproduziu `FileExistsError` na segunda abertura. Não houve `input_queue_overflow` nessa tentativa.

## Fluxo corrigido

`start_capture()` agora só cria uma captura quando o estado e o diretório estão vazios. Aguarda até cinco segundos pela primeira saúde, valida os cinco hashes, imagem/container, PID/nascimento/cmdline, sessão, heartbeat e arquivo, e grava no `state.json` um contrato completo: sessão, SHA256, PID/nascimento, ID de container e device/inode do diretório e arquivo. Se o processo não ficar válido, sinaliza apenas esse filho e falha fechada; não apaga nem substitui a evidência.

No cron, `supervise()` chama `ensure_capture()`. Se há contrato, `adopt_capture()` valida a captura existente e anexa uma referência ao **mesmo PID**; não cria outro processo ou arquivo. A validação inclui o script aprovado, argumentos exatos (`--directory`, `--session`, `--seconds 1000`), nascimento em `/proc`, sessão do arquivo `begin`, prefixo já persistido e completo, saúde atual, ausência de gaps/truncamento/falha, modos 0700/0600 e, em POSIX, sessão própria, stdio em `/dev/null` e nenhum FD do deploy lock. O prefixo é limitado pelos contadores já fsyncados no heartbeat, evitando falso erro se o leitor acrescentar uma linha simultaneamente. Um estado antigo/incompleto, arquivo órfão, PID morto/reusado, sessão alheia, arquivo substituído ou container divergente impede a ativação. Se não há contrato **nem diretório**, o supervisor ainda pode criar uma captura nova.

`capture_health()` continua verificando processo e arquivo durante baseline/janela. Após uma recriação controlada da API, exige o **ID atual** do container passado pelo supervisor, mantendo sessão/PID/arquivo do capturador; o ID inicial só é obrigatório no handoff. Na saída normal, o supervisor sinaliza apenas o PID verificado; o rollback da API ocorre **antes** dessa etapa. Se o supervisor cair, o cron faz seu rollback GTFS e solta o lock **antes** de sinalizar o leitor órfão por PID/nascimento e verificar o arquivo. A verificação é registrada uma única vez. Um timeout ou arquivo incompleto permanece marcado como evidência incompleta, sem prolongar ITS para salvar logs. Nenhum limite de captura, cadência, TTL, gate da 898 ou prazo de 600 segundos mudou. `its_log_capture.py` permanece com `mkdir` exclusivo e `O_EXCL`.

## Mudança necessária no próximo preflight

1. Criar diretório **novo** 0700 e copiar os cinco arquivos em bytes canônicos UTF-8/LF, 0600, conferindo os hashes abaixo. Não copiar `state.json`, PID, arquivo ou cron de uma tentativa anterior.
2. Preparar o estado em `phase=proof` com digest 151, manifesto dos cinco hashes e marcador exclusivo do cron. Chamar `start_capture(state)` **uma única vez**; ele deve retornar somente após persistir `state.capture` completo. Confirmar sessão, PID/nascimento, container, heartbeat, device/inode e ausência de gaps. Não iniciar um capturador externo adicional no mesmo diretório.
3. Provar cron/watchdog e lock com o state ainda em `proof`; encerrar a sessão SSH de instalação e observar o mesmo PID/sessão por outra conexão. Cron em `proof` só grava prova e não ativa fonte.
4. Após todos os gates e frota representativa, alterar **somente** `phase` para `scheduled`; preservar `state.capture` byte a byte. O cron executará `supervise()`, que deve adotar o PID antigo. Conferir `baseline_sample` antes de permitir ITS. Falha de adoção é NO-GO, sem remover/recriar `capture/`.
5. Após retorno seguro a GTFS, conferir `capture_verification`, `phase=complete`, fontes/saúde e retirar apenas o cron marcado. Se o aborto ocorrer antes de ITS, exigir `precondition_failed`, `activated=false`, GTFS efetivo e encerramento do PID verificado antes da limpeza; não fabricar `phase=complete`. Preservar os arquivos privados e overrides referenciados.

O procedimento de 26/10 que deixava uma captura da prova ativa **sem contrato completo** não é reutilizável. A nova prova Debian deve exercitar especificamente `proof → scheduled → baseline`, além da captura GTFS sob carga; os testes locais não substituem PID/fd/cron/Docker reais.

## Testes determinísticos

`python -B tools/gps/test_its_window.py`: **58/58**. `python -B tools/gps/test_its_log_capture.py`: **27/27**. Total **85/85**, offline, sem Docker/provedor/banco/SSH. Sintaxe Python e `git diff --check` aprovados. O primeiro conjunto novo foi executado vermelho antes da função de adoção e verde depois; a reprodução mínima de segunda abertura exclusiva produziu `FileExistsError` em diretório temporário.

| Requisito | Evidência direta |
|---|---|
| Adoção sem segundo processo, repetição e continuidade | `test_healthy_proof_capture_is_adopted_without_second_process`; `test_archive_prefix_survives_handoff_and_file_identity_is_stable` |
| Ausência legítima de captura | `test_no_existing_capture_can_start_one_fresh_reader` |
| Sessão/PID/nascimento, script, container, heartbeat, gap, arquivo e processo morto | `test_adoption_rejects_other_session_and_reused_pid`; `test_adoption_rejects_bad_script_image_container_and_stale_or_gapped_health`; `test_tampered_script_cmdline_archive_or_inherited_lock_is_no_go`; `test_existing_archive_without_live_process_is_no_go`; `test_incomplete_contract_wrong_archive_session_and_torn_prefix_are_no_go`; `test_process_disappearing_after_adoption_invalidates_health_and_cannot_be_signalled` |
| Falha antes de ITS e ciclo proof→scheduled→baseline | `test_invalid_handoff_fails_before_baseline_and_never_switches_source`; `test_proof_cron_scheduled_baseline_adopts_then_aborts_before_its` |
| Encerramento, lock, rollback e verificação uma vez | `test_precondition_abort_signals_only_adopted_pid_and_verifies_once_after_safe_state`; `test_supervisor_rolls_back_before_stopping_capture`; `test_capture_requires_exact_release_and_all_five_hashes`; `test_new_capture_terminates_if_identity_cannot_be_sealed` |
| Container recriado sob supervisão | `test_controlled_api_recreation_keeps_reader_but_requires_current_container_id` |
| Watchdog após queda do supervisor | `test_watchdog_returns_gtfs_and_releases_lock_before_orphan_cleanup`; `test_watchdog_only_signals_orphan_with_verified_birth_and_files` |

## Hashes operacionais canônicos da próxima prova

| Arquivo | SHA256 Git UTF-8/LF |
|---|---|
| `its_window.py` | `75bfe9a999858fbf964e79b2a6281e6b8a4fc3c3c6d228845515d5f799862286` |
| `its_log_capture.py` | `9eaeaec2475fb084f9085dfecb83fc28a2a86e1c2867211d9af8f6608760859f` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

**Limite da conclusão:** os testes POSIX/Docker/cron são simulados no Windows. Falta provar no Debian a adoção do mesmo PID e inode, os modos/FDs reais e o fechamento/verificação após `precondition_failed`. Sem esse preflight e autorização nova, **NO-GO operacional** para a janela híbrida.
