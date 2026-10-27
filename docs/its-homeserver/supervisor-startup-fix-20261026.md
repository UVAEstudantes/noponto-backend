# Correção local da supervisão de startup — 26/10/2026

**Pronta para revisão local; não autoriza outra janela.** Base `51a5a93584a420891140ee1591d13994d6f7682b`. Nenhum SSH, acesso ao homeserver, cron real, provedor, deploy, push/merge ou banco foi usado. O último estado produtivo conhecido continua sendo release151 em GTFSRT_BUS/FullFallback/trace OFF, BRT GTFSRT_BRT; não foi reinspecionado nesta tarefa.

## Diagnóstico e reproduções vermelhas

O caminho anterior de `supervise()` fazia a troca de fonte e depois chamava `healthy(ITS_CURRENT)`, cujo laço de até60s aguardava ambas as contribuições. Só depois iniciava `sample()` e avaliava o gate898. O relógio da janela já corria, mas nenhuma fiscalização de90s interrompia aquela espera, nem havia leituras REST durante ela. SignalR seguia num leitor, porém sem amostras/correlação arquivadas. Isso explica o bloqueio de supervisão/observação; não atribui à ITS os tempos internos de matching, CAS ou publicação.

Antes da alteração, o teste `StartupDeadline.test_real_supervisor_startup_does_not_delay_898_rollback_past90` exercitou o supervisor real com boot35s e dependências falsas: **rollback aos95s**, falhando a assertiva ≤90s. Três testes da captura também falharam antes da correção: timeout já resumido reaberto por HTTP tardio (contador1 em vez de0), resumo de sucesso conciliado prematuramente antes de páginas tardias (gap indevido) e timing sem resumo declarado completo.

As hipóteses confirmadas foram: espera sequencial sem deadline independente; observação REST condicionada à saúde completa; e descarte prematuro dos contadores por aquisição ao receber o resumo. Não houve reprodução de rede/provedor ou nova coleta operacional.

## Fluxo corrigido e diff

`its_window.py` ganhou `ActivationDeadline`, `ActivationObserver` e `run_activation()`; o restante do baseline e a normalização/backend não foram reestruturados.

- A origem monotônica e o UTC de controle são fixados uma vez antes de solicitar a troca. O prazo da898 é90s e o máximo é600s, incluindo boot. Estado/prova de ativação anterior e chamada fora de `baseline` são rejeitados; não há rearmamento da janela por recuperação da fonte.
- Um timer POSIX do **processo controlador**, com checagem no máximo a cada250ms e alarme ajustado ao instante exato dos deadlines, interrompe esperas no thread principal, inclusive o comando de troca. Não espera concluir outra aquisição/amostra. Indisponibilidade do timer, outro timer ativo ou execução fora do thread principal impedem ativação. SIGTERM/SIGINT durante ativação interrompem a espera e preservam o motivo operacional. O timer é desarmado antes do rollback, que não pode ser interrompido pelo deadline da própria janela.
- `subprocess.run` cancela seu filho CLI quando a exceção de segurança o interrompe; o controlador segue para o rollback serial sob o mesmo deploy lock. Não há thread de escrita Docker/GPS. O teste usa o `switch()` real com I/O falso e comprova que um stop interrompido não continua para outro `up`; outro teste confirma o cancelamento do subprocesso. A semântica POSIX/Docker real ainda requer prova controlada, descrita abaixo.
- Um único observador **passivo** começa antes da troca e reutiliza `sample()` para REST, trace, socket e infraestrutura. Ele mantém o intervalo existente de15s após a conclusão da amostra; não consulta diretamente o provedor, não escreve GPS nem controla containers. As amostras `its_sample` passam a incluir `hybrid_state` e `activation_elapsed`, inclusive durante startup. Falhas de sonda usam `its_observation_error`, com rota/status/classificação seguros. Vazio legítimo continua tratado pelo contrato existente.
- Estados: `starting` enquanto a API/contribuições ainda iniciam; `degraded` quando uma fonte suporta a operação; `healthy` somente com ambas em sucesso e contribuições positivas; `failure` para falha das fontes/observação. ITS com timeout e GTFS saudável é registrado como degradado, sem aprovar saúde híbrida. A confirmação inicial de saúde continua limitada aos60s após a troca, mas já não bloqueia a fiscalização/observação. HTTP indisponível por30s mantém a interrupção prevista no runbook.
- Capture health e demais gates continuam obrigatórios desde o startup. `normalized_page` não é exigida de uma aquisição que falhou antes de obter resposta; todas as famílias anteriormente obrigatórias continuam exigidas para considerar o híbrido saudável. Não se declara evidência completa com base na disponibilidade do suporte.
- A prova898 continua exigindo `linked_complete_new` do servidor **e renovação física recebida pelo cliente SignalR**, após uma amostra REST completa. Ela precisa ser admitida antes do limite; uma amostra terminada em90s ou depois falha fechada, e um ACK92s não aprova seleção79s. Envio SignalR do servidor sozinho, REST sozinho e posições antigas no cliente não passam. Não foram alterados timestamps físicos.
- O instante da prova admitida é incorporado ao estado e persistido pelo heartbeat. O cron independente também verifica a expiração898, mesmo com heartbeat recente; só aceita prova persistida numérica no intervalo `[0,90)`. Se o controlador morrer antes de persistir uma prova válida, o watchdog retorna conservadoramente aGTFS. Sua frequência de cron e guards de PID/nascimento/cmdline/lock permanecem os existentes; cron não substitui o timer principal para precisão subminuto.
- Motivos previstos viraram `SafetyFailure`/`classification=safety_gate`, como `gate_898_unproven`, `hybrid_startup_unconfirmed` e `backend_unavailable`. O rollback precede o relatório do erro, parada/verificação da captura e joins dos observadores. Se o rollback falha, o estado fica `rollback_failed`, a captura independente permanece dentro do seu orçamento e o watchdog só a encerra/verifica depois de retornar aGTFS. Uma sonda que falha **após** GTFS confirmado não recria a API nem desfaz `phase=complete`.

`its_log_capture.py` mantém contadores por aquisição até o fechamento, incluindo presença de timing e resumo. Reconciliar sucesso/vazio só no fechamento evita acusar páginas ainda chegando. Um HTTP tardio não reabre uma aquisição que já possui ambos os eventos terminais. Resumo sem ID não é associado por adivinhação a uma página vizinha; continua incompleto. Resumos conflitantes ou página genuinamente ausente continuam produzindo gap; duplicatas exatas continuam deduplicadas. Aquisição interrompida sem timing/resumo **não** vira completa.

O footer preserva `acquisitions_without_timing` por compatibilidade (contador legado de evidência de aquisição incompleta), acrescentando `missing_timing`, `missing_summary` e `unassociated_summaries` para distinguir as causas. Não é uma contagem confiável de veículos nem uma inferência de quantas aquisições houve quando o resumo não possui ID. O estado em memória continua limitado pelo orçamento de registros/bytes do arquivo; não há buffer ilimitado de payloads. Privacidade, permissões, criação exclusiva e budgets permanecem intactos.

## Evidência dos testes

Comandos locais completos, exit0:

```text
python -B tools/gps/test_its_window.py       → 84 testes, OK
python -B tools/gps/test_its_log_capture.py  → 33 testes, OK
```

**117/117; 32 casos novos** sobre os85 existentes. Tempo/alarme, subprocessos, Docker, cron e I/O de aplicação foram simulados; os arquivos de arquivo/replay usam diretórios temporários. Nenhuma requisição produtiva ou espera de90/600s real foi necessária.

| Requisito | Evidência exata |
|---|---|
| Reproduzir95s e iniciar rollback90s no supervisor real | `StartupDeadline.test_real_supervisor_startup_does_not_delay_898_rollback_past90` |
| Boot lento / ITS timeout com GTFS / observação desdeT0 | `ActivationLoop.test_slow_start_and_its_timeout_keep_gtfs_observed_until_exact90` |
| Seleção79s / ACK92s, sem aprovação no limite ou tardia | `ActivationLoop.test_selection79_ack92_never_proves_the_gate`; `TraceProof.test_server_generation_and_client_renewal_both_required` |
| Prova completa antes90s e teto600s | `ActivationLoop.test_complete_server_rest_and_client_proof_before90_keeps_max600` |
| REST/servidor sem cliente; cliente sem renovação física | `ActivationLoop.test_rest_and_server_without_signalr_client_do_not_pass`; `test_898_proof_is_not_server_signalr_send_without_client_observation`; `test_healthy_rest_and_stale_client_positions_never_simulate_physical_renewal` |
| Espera bloqueada / observador parado / relógio UTC alterado | `ActivationLoop.test_blocked_source_switch_is_interrupted_without_second_writer`; `test_observer_that_never_finishes_cannot_hold_the_deadline`; `test_wall_clock_jump_does_not_move_monotonic_898_deadline` |
| Não continuar `up` após interrupção / cancelar filho CLI | `ActivationLoop.test_runtime_gate_abort_terminates_command_before_late_up`; `StartupRecovery.test_deadline_exception_cancels_subprocess_instead_of_waiting_for_result` |
| Timer indisponível, erro de instalação e parada do operador | `ActivationLoop.test_no_interruption_capability_blocks_before_activation`; `test_timer_installation_failure_restores_handlers_before_any_source_switch`; `test_operator_stop_interrupts_boot_and_keeps_operational_reason` |
| Não reutilizar prova/deadline nem ativar emproof | `ActivationLoop.test_existing_activation_or_proof_cannot_reset_the_deadline`; `test_proof_phase_cannot_call_the_activation_path` |
| Suporte degradado não aprova Hybrid / capture e backend falhos | `ActivationLoop.test_degraded_support_with_898_proof_still_cannot_approve_hybrid_health`; `test_missing_normalized_page_is_expected_only_while_its_is_unavailable`; `test_startup_http_failure_is_observed_and_interrupts_at30`; `test_recorder_failure_is_fail_closed_without_waiting_for90` |
| Watchdog independente e prova persistida não tardia | `StartupRecovery.test_fresh_heartbeat_cannot_hide_expired_898_from_independent_cron`; `test_only_timely_persisted_proof_prevents_gate_watchdog` |
| Falha de rollback; cleanup só após GTFS; não repetir troca por erro posterior de sonda | `StartupRecovery.test_failed_rollback_defers_capture_cleanup_until_independent_watchdog_recovers`; `test_post_rollback_probe_failure_cannot_recreate_api_or_undo_safe_state`; `CaptureSafety.test_supervisor_rolls_back_before_stopping_capture` |
| HTTP após resumo/timing / páginas e duplicatas fora de ordem | `CaptureTests.test_timeout_http_after_timing_and_summary_does_not_reopen_completed_acquisition`; `test_success_summary_reconciles_late_pages_and_duplicate_overlap_at_close` |
| Cancelamento real continua incompleto | `CaptureTests.test_completed_timeout_plus_cancelled_acquisition_counts_only_real_incomplete`; `test_rollback_during_acquisition_preserves_partial_without_waiting_for_timing` |
| Ausência genuína de timing/resumo e lacunas não eliminadas | `CaptureTests.test_timing_without_summary_is_not_complete_evidence`; `test_summary_without_timing_cannot_be_associated_by_guessing_nearby_page`; `test_conflicting_summary_is_a_gap_even_with_late_complete_pages`; `test_missing_page_is_detected_by_success_summary_reconciliation` |

Revisão das assertivas limitada ao diff: reforçados o limite exato90, conjuntos não vazios, identidade do estado, cancelamento de comando e ausência de `up`/segunda troca. Não houve mutation testing exaustivo nem alegação de cobertura percentual. Testes C#/.NET, CI e E2E com feeds reais não foram executados porque o backend não mudou e o escopo é exclusivamente local.

Replay temporário dos **31 agregados reais** da tentativa anterior: `integrity_valid=true`, `archive_complete=true`, `evidence_complete=false`; contador legado passou de2 para**1**, com `missing_timing=1`, `missing_summary=1`, `unassociated_summaries=0`. A aquisição interrompida continua explicitamente incompleta. Arquivo original SHA256 `9d282358219807acdbc64f6f3ee55e988067188b5156a38a07bf25b25ffd4df8` foi somente lido e permaneceu intacto; não foram inventados eventos de conclusão.

## Cinco hashes canônicos atualizados

Calculados sobre os blobs do index Git em UTF-8/LF, considerando `core.autocrlf=true` no Windows. Somente supervisor e capturador mudaram; probe e overrides permanecem iguais.

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `33255a6477418f64a05ad2dd88e9f6aa723b78587bd9ff1495cb95a0fd1e90a0` |
| `its_log_capture.py` | `055fa54b065d08d44b54d697e9b7924792af34323f27bc9631dc9146919d49ac` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

## Limites e procedimento antes de qualquer execução futura

1. Esta entrega é para revisão local. Não instalar scripts, armar cron ou pedir automaticamente outra janela. Qualquer nova atuação no homeserver depende de autorização própria.
2. Antes de uma ativação futura, os hashes novos substituem os antigos; exportar pelos blobs Git para diretório privado novo, com state/PID/prova/arquivo novos. Manter digest151 e GTFS como estado seguro, uma API, Compose efetivo preservado, lock e cron/watchdog independentes.
3. Ainda falta provar **no POSIX real**, em preflight GTFS-only autorizado, instalação/restauração do timer e interrupção de um filho descartável/`docker logs` somente leitura, sem tocar na API. Os testes Windows simulam o alarme e não são prova de scheduler/daemon Docker. A disponibilidade deve ser verificada antes de armar `scheduled`; `run_activation()` é proibida emproof. Manter prova de handoff da mesma captura, permissões e orçamento.
4. A distinção entre **disparar rollback** no deadline e **concluir** stop/boot Docker permanece necessária: o relógio não é estendido para concluir uma aquisição ou salvar logs; a duração do retorno deve ser medida separadamente. O timer principal não substitui o watchdog em morte do processo/kernel/Docker indisponível, e o cron tem sua resolução existente. Lock, guard de escritor e rollback serial continuam obrigatórios; não há garantia nova de cancelamento de uma operação já aceita pelo daemon baseada apenas em mocks.
5. Baseline formal120s, seteBUS/trêsBRT de controle, representatividade838/898, gate90 e teto600 continuam. A futura supervisão deve registrar estados de startup, erros de sonda, prova conjunta898 e horários do disparo/retorno; saúde GTFS de suporte não aprova o híbrido.
6. Provas da captura são provisórias até fechar e reconciliar todo o arquivo. Ausência final de página, timing, resumo, ID de associação ou aquisição interrompida impede declarar evidência completa. Não atrasar rollback por essa conciliação. Se `activation_observer_closed.closed=false`, também registrar a observação em voo como incompleta, independentemente do hash do arquivo.

Limites preservados: janela600s, gate89890s, captura1000s, entrada64MiB, saída8MiB/12.000linhas, filtros de privacidade, criação exclusiva, backpressure e todos os TTLs/intervalos de GPS. Artefatos e laboratórios preexistentes preservados. [Resumo JSON e nomes dos testes novos](supervisor-startup-fix-20261026.summary.json).
