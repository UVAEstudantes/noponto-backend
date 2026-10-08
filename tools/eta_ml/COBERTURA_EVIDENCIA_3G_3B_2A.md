# 3G.3B.2A — cobertura prospectiva e fechamento de qualidade

Data: 2026-10-08. Implementação exclusivamente local; certificação real **REPROVADA/OFF**.
Esta entrega prepara os hooks, o witness e a persistência local, mas **não comprova cobertura completa do polling**.
Não existe ativação de epoch, fencing contra binários legados, certificação retrospectiva ou autorização de uso em treino.

## Contratos e implementação

`IEtaDecisionCoverageSource` é implementado por `EtaDecisionCoverageCoordinator` e registrado como singleton.
`EtaDecisionCoverageOptions.Enabled=false` não tem binding com configuração da API. Os únicos bindings de owner/trip são internos e explícitos nas fixtures.
Não há registro/ativação automática de producer. Um owner desconhecido ou excedido contamina a cobertura conservadoramente.

Por owner: counters admitted/settled, até 16 IDs pendentes, flags OR irreversíveis e um digest cumulativo.
Até 200 owners e 200 bindings de writer; os limites não são ampliados silenciosamente por opções.
Não são retidos arrays de GPS ou séries de witness. A janela de owners não é reciclada: saturação exige protocolo futuro de encerramento/reconciliação, não expulsão de evidência.
Ausência de predecessor/versão explícitos, retorno não resolvido, exceção ou identidade de owner divergente nunca recebem default confiável.

`Admission.Dispose` resolve um caminho desconhecido contaminando-o; `LeavePending` mantém a pendência até ACK/descarte explícito.
Retry confirmado resolve o contador, mas não limpa flags anteriores. ACK obsoleto/falso não é sucesso.
CandidateStarted e Protection sobrevivem a cancelamento, substituição, recuperação circular e sucesso posterior.

Foram encontrados dois limites do v1: versão operacional estritamente crescente por envelope e proibição de qualquer registro depois de Close.
Uma falha após COMMIT, durante projeção Redis ou restante do processamento, não poderia ser incorporada a um Close antecipado.
O usuário aprovou separadamente revisão isolada/versionada, preservando v1 e certificação OFF.

`eta-trip-evidence-v2` mantém o shape e algoritmo de digest; acrescenta `Closing`:

1. Begin/Transition/Checkpoint/Closing são gravados com estado/outbox na transação operacional existente.
2. Closing liga o evento ViagemFinalizada e exige versão operacional maior que a fronteira anterior; pode conter decisões pendentes.
3. Após resolução, Close usa transação exclusiva de qualidade, mesma versão/evento/GPS da Closing e pending=0.
4. Falha posterior ao COMMIT acrescenta unknown flags ao Close. Falha de qualidade anterior ao COMMIT aborta estado/head/outbox juntos.
5. Close continua terminal. Misturar v1/v2, apagar flags, mudar identidade, finalização, timestamp ou contadores falha.

O seal local serializa admissão versus freeze de Close em memória: pending impede seal; admissão após seal é recusada conservadoramente; falha no fechamento libera seal com CommitUncertain.
Esse seal não é fencing durável e não cobre escritores legados/outros processos.

`EtaEvidenceBoundaryWriter` exige loopback e nome de fixture exclusivo com UUID. Nenhuma escrita adicional é feita por GPS normal com a flag OFF.
Com binding local ON, checkpoints/flags usam as transações duráveis; somente fechamento resolvido abre transação própria de qualidade.
Begin sem evento prospectivo correspondente falha, inclusive para uma viagem já aberta/histórica.
Todos os envelopes produzidos aqui carregam `MissingDecision`/coverage_proven=false: a completude ainda não foi demonstrada.

V1, migration da fundação, fixtures v1, política instalada, qualify_real e bundles antigos foram preservados.
O verificador Python v2 é independente do reducer C# e verifica cadeia técnica; sempre retorna NaoVerificada/complete=false.
Ele não substitui a autoridade v1 do qualify_real. Não há perfil v2 autorizado nem caminho para converter esse relatório em audit positivo.
Fixture sintética v2 compartilhada entre repos: SHA256 `3a37f89622fbd80e3c081ddbeec4520d484fcbf1b990184e3490dc8844eb5101`.

## Matriz de cobertura dos retornos

Inventário reproduzível em `coverage.sources.json`: dez arquivos, hashes UTF-8 com newline normalizado e linhas de return/continue/catch.
O guard offline exige nova revisão quando qualquer fonte muda. O inventário textual não é análise de fluxo C# nem prova de completude.

| Caminho | Admissão/resolução ou pendência | Evidência conservadora |
| --- | --- | --- |
| Polling: aquisição SPPO/BRT, cache/fail-open, lote vazio, idade inválida, duplicado antes do commit, enriquecimento e publicação | Ainda sem fronteira de ingress individual comprovada | Guard de ciclo `ingress-frontier-not-attested`: MissingDecision em todos os owners abertos |
| Polling: ConfirmarPosicao, entrada, retry antes do mapa, cache mapa | Admission por ObservacaoId antes do aceite do mapa; retorno resolve; exception/cancelamento dispara Dispose | Falha Redis do mapa: RedisLost; resto não coberto: MissingDecision |
| Polling: backlog ou conflito/infra operacional | LeavePending; mapa continua independente | MissingDecision permanente; retry não transforma mapa aceito em cobertura limpa |
| Polling: sampling/telemetria/shadows | Sem mudança nas regras existentes; fora da autoridade de qualidade | Não usados como prova de cobertura; publicação posterior está no bloqueio de ingress |
| Polling: ciclo falha ou encerramento | Invalidação dos owners conhecidos | Gap ou Restart, nunca sucesso silencioso |
| Serviço: matching ausente | Retorno null preservado | MissingDecision |
| Serviço: leitura do contexto falha | Retorno null preservado | MissingDecision |
| Serviço: Created/Updated, RejectedOlderOrEqual | Resolução do admission do polling | Sucesso operacional não implica certificação; duplicado/fora de ordem mantém guard conservador |
| Serviço: InvalidState/Sequence/Occurrence, ItineraryChanged/Conflict/Infrastructure | Resultado operacional preservado | MissingDecision; infra/conflito podem continuar pendentes |
| Repository: validação inicial, contexto null, GPS antigo, estrutura/transição inválida, CAS/version/integridade divergente | Retorno preservado, capturado pelo serviço/dispose | MissingDecision; chamada direta sem admission não comprova cobertura |
| Repository: avaliação Mudanca, primeira evidência, candidato mantido/substituído/cancelado/confirmado | Inspeção de candidato anterior e resultado antes da persistência | CandidateStarted irreversível na execução antiga |
| Repository: projeção operacional inválida, versão divergente, baseline/reancoragem | Regras operacionais intactas | Rejeições propagadas; Reanchor quando continuidade não comprovada |
| Decidir: linear, circular, PossivelFim/retorno, finalização/início, passagens | Sem alterar reducer operacional; resultado inspecionado pelo repository | Eventos existentes preservados; começo/fim ligados pelo writer local |
| Integridade: proteção, recuperação na mesma viagem/volta, nova execução após ambiguidade | Estado anterior protegido e decisão atual observados | Protection permanece; baseline conservador; nenhuma recuperação limpa histórico |
| Redis: contexto vazio/inválido/exceção, hot CAS falha, projeção pós-COMMIT falha/cancela | Fallback/rejeição/projeção preservados | RedisLost, MissingDecision ou CommitUncertain |
| PG: retorno CAS anterior ao write, falha codec, rollback/falha de transação, falha pós-COMMIT | Transação existente preservada; head/outbox dentro dela | InvalidState conservador; erro genérico CommitUncertain, sem afirmar rollback conhecido |
| Retry: orçamento/claim vazio, backoff/lease, matching insuficiente | Não gera resolução positiva | Pending original continua; heartbeat não substitui decisão |
| Retry: commit durável reconhecido ou recuperação durável + ACK Redis verdadeiro | ResolveRetry(proved=true) | Flags anteriores preservadas; ACK não limpa contaminação |
| Retry: expirado, limite tentativas, superado, contexto/percurso/enriquecimento divergente, rejeição | ACK/descarte resolve com proved=false | MissingDecision; descarte nunca vira commit comprovado |
| Retry: fila recusada, ACK falso, Redis indisponível, timeout, cancelamento | Recusa/descarte conservador ou pendência preservada | MissingDecision/RedisLost; perda/restart não reconstituem witness confiável |
| Epoch/token/release divergente ou revogado | Freeze/CAS/persistência rejeitam identidade/epoch divergentes | Sem adoção silenciosa; ausência de fencing real continua bloqueadora |
| Chamada futura/retorno não inventariado | Hash da fonte falha em teste; guard de ingress continua ativo | Não pode resultar em certificação automática |

## Checkpoints e tempos

Somente homologação local: target=60s; ausência de observação >=60s e fronteira de qualidade atrasada >75s lancham Gap/MissingDecision.
O relógio de qualidade é independente do checkpoint operacional. Tick não faz I/O nem renova cobertura.
Due força checkpoint operacional somente se há owner local registrado e nenhuma persistência já necessária.
QualityCommitted atualiza relógio após COMMIT; recovery/heartbeat não apagam flags.
Snapshots distinguem GPS, último processamento, COMMIT confirmado e fronteira de qualidade.
RecordedUs permanece parte do envelope. O relógio do processo ainda não é prova independente/autenticada do tempo do banco.
Os testes de relógio controlado verificam 60/75s; não houve replay conectado de longa duração para medir frequência real sob carga.

## Arquivos desta subetapa

Backend criados: `EtaDecisionCoverage.cs`, `EtaTripEvidenceV2.cs`, `EtaEvidenceBoundaryWriter.cs`, `EtaDecisionCoverageTests.cs`, fixture v2, `homologate_coverage.ps1`, `coverage.sources.json`, `test_coverage.py`, este relatório.
Backend alterados: `GpsPollingService.cs`, `ViagemObservadaService.cs`, `RetryOperacionalGps.cs`, `ViagemOperacionalRepository.cs`, `EtaTripEvidenceRepository.cs`, `EtaTripEvidenceLocalTests.cs`, `Program.cs`, `test_evidence_foundation.py` e HISTORICO (somente acréscimo).
ML criados: `verify_trip_evidence_v2.py`, `test_trip_evidence_v2.py`, fixture compartilhada v2. Nenhuma política de treino/modelo alterada.
Outras alterações anteriores dos working trees foram preservadas.

## Testes e medições

Backend: **219 testes offline aprovados**, zero falhas/ignorados, incluindo ausência GPS após ACK e seal versus admissão concorrente; build passou com warnings preexistentes.
Rodada final: `outputs/coverage-3g3b2a-offline-06/coverage.trx`. A tabela de benchmark conserva a rodada medida 04, sem sobrescrever evidências.
Python backend: 74 testes aprovados (snapshot/exporter/pipeline/diagnóstico/sourcepins e cobertura).
ML: 109 testes Python aprovados, incluindo seis regressões v2 e qualificação existente.
PostGIS16/3.4 e Redis7: uma fixture composta exclusiva com novo nome/UUID, marker, loopback e limpeza somente de seus próprios recursos.
Valida rollback atômico, CAS/epochs concorrentes, revogação, Closing pendente, falha pós-COMMIT incorporada, Close com mesma versão, dispatch/ACK, deduplicação Redis, lease concorrente e ACK obsoleto.
Não foi induzida perda real da rede durante COMMIT, restart físico nem takeover de binário legado. Esses cenários não são declarados comprovados.
Primeiras tentativas corrigiram fixture de CoveredFrom, assinatura do worker, opção Redis `name` e tipo IPEndPoint; resultados finais não omitem essas falhas preliminares.

Benchmark pareado: 100 rodadas medidas após 20 warmups, ordem baseline/OFF/ON rotativa, 1/10/50/200 veículos.
Executa `ConfirmarPosicaoAsync` real com repositórios em memória; todos os modos têm exatamente os mesmos aceites do mapa/chamadas operacionais.
Não executa aquisição/enriquecimento completo nem banco no benchmark pareado. Cada número é milissegundos por rodada/lote.

| Veículos | Baseline p50/p90/p95 | OFF p50/p90/p95 | ON p50/p90/p95 | ON menos baseline p50 |
| --- | --- | --- | --- | --- |
| 1 | 0,0033 / 0,0062 / 0,0069 | 0,0030 / 0,0055 / 0,0065 | 0,0349 / 0,0520 / 0,0605 | 0,0316 |
| 10 | 0,0200 / 0,0262 / 0,0293 | 0,0196 / 0,0263 / 0,0299 | 0,2815 / 0,3594 / 0,3979 | 0,2615 |
| 50 | 0,0625 / 0,0705 / 0,0723 | 0,0630 / 0,0690 / 0,0816 | 0,8241 / 1,0410 / 1,3244 | 0,7616 |
| 200 | 0,3132 / 0,5591 / 0,6502 | 0,3673 / 0,6270 / 0,8431 | 4,3740 / 11,2635 / 12,5434 | 4,0608 |

Alocações por rodada baseline/OFF/ON: 1 veículo 1477/1477/4925 bytes; 10 14221/14221/48706; 50 70861/70861/243266; 200 283261/283261/973675.
São alocações da fixture (incluem DTO/asserts), não memória retida ou pico. Banco/WAL/novas transações no benchmark em memória são zero por construção.
O pool da fixture conectada é limitado a 2. Begin+Transition, Closing e Close produzem quatro envelopes: duas transações simulando fronteiras operacionais e uma exclusiva de qualidade; oito writes head/outbox, mais materialização/ACK. Há uma tentativa adicional rollback.
No intervalo conectado v2 foram medidos 31.080 bytes de WAL, incluindo aquisição de owner, rollback e dispatch; não é WAL incremental pareado por GPS.
Na mesma execução, a fundação mediu 21 transações: p50 10,990ms/p90 13,925ms/p95 14,198ms, delta de heap retido 316.360 bytes e working set 138.481.664 bytes (processo de testes). Não são pico de memória nem overhead produtivo.
Evidência conectada: `outputs/evidence-3g3b1-b21d56c4de0048e488c7f072cf9a2dea`; benchmark pareado: `outputs/coverage-3g3b2a-offline-04/coverage.trx`.
Revalidação final após seal local: `outputs/evidence-3g3b1-ac7a1e91dc5d44b3bdab35bcc61f7cac`, fixture composta aprovada; WAL31.056bytes, fundação p50/p90/p95=8,702/10,976/12,645ms; heap retido331.320bytes, working set134.950.912bytes. São medições separadas, com os mesmos limites de interpretação.
Medições conectadas da fundação são separadas; não se apresentam como overhead incremental do polling. Evidências TRX preservadas nos diretórios outputs.

## Bloqueios antes de declarar completude ou habilitar certificação

1. Fronteira de aquisição/filtro/enriquecimento ainda sem admission por ingresso bruto: guard global deliberadamente mantém MissingDecision. Não há prova por caminho completo.
2. Binding prospectivo automático da nova ViagemId ao owner/epoch ainda não existe. Fixture faz binding explícito e não certifica execuções reais.
3. Admissão versus Close possui seal em memória testado, mas não fencing durável independente/entre processos; Close local é sempre não certificável. Não habilitar resultado positivo com esse protocolo.
4. Restart perde witness em memória; faltam reconciliação durável, invalidation dos owners abertos após restart e barrier de escritor único legado. Nenhum restart pode recuperar um status limpo por default.
5. Snapshot/retention/receipts v3 e autorização de perfil v2 não estão ativos. O verificador técnico v2 não implementa autorização positiva nem promove v2 no qualify_real.
6. Benchmark integral pareado com PG/Redis, WAL incremental, frequência real de checkpoints, concorrência de pool e retenção/memória de longa duração permanece pendente. Não extrapolar a tabela da fixture em memória ao homeserver.

Decisão: preparado e testado localmente para continuar desenho de fencing **com estes bloqueios visíveis**; **REPROVADO para alegar cobertura completa, habilitar certificação ou uso real**.

## Reprodução PowerShell

```powershell
Set-Location D:\repositorio_github\NoPonto\noponto-backend
dotnet test NoPonto/NoPonto.csproj --no-restore --filter "FullyQualifiedName~EtaDecisionCoverageTests|FullyQualifiedName~EtaTripEvidenceTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~EtaDatasetTests"
python -m unittest discover -s tools/eta_ml -p "test_*.py"
pwsh -NoProfile -File tools/eta_ml/homologate_coverage.ps1
git diff --check

Set-Location D:\repositorio_github\NoPonto\ml
$env:PYTHONPATH="tools/eta_ml"
python -m unittest discover -s tests
git diff --check
```

O harness cria recursos novos e exclusivos; não recebe connection string operacional. Não executar API completa, ativar configuração ou reaproveitar banco existente.
O verificador v2 é uma função offline `verify_chain(events)` usada pelos testes; não emite certificado/CLI de promoção.
