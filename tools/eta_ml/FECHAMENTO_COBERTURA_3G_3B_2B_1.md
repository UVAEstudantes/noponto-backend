# 3G.3B.2B.1 — cobertura adquirida, vínculo prospectivo e recuperação

Implementação local, 2026-10-08. Certificação automática **REPROVADA/OFF**. Nenhum perfil produtivo autorizado, nenhum contrato persistido alterado, nenhuma viagem histórica certificada.

## Implementação efetiva

`EtaGpsIngressCoverage` admite a lista adquirida SPPO/BRT antes de agrupamento, idade, deduplicação, enriquecimento e confirmação. Observação/veículo ficam associados ao owner local existente, independentemente do sampling ML. Fingerprint SHA-256 usa somente campos brutos. Duplicata idêntica é contabilizada sem segunda admissão; mesmo ID com payload diferente contamina permanentemente com Ambiguity/MissingDecision. Falhas de serialização tornam cobertura desconhecida e não interrompem o fluxo público. Descartes conhecidos resolvem sua admissão; o finally resolve como desconhecido tudo que ficou sem decisão. Cache de fingerprints e batch: até 3.200 entradas; pending: até 16 por owner. Expulsar fingerprint não expulsa pending nem reconstrói confiança.

O registro de scope local começa sem viagem. `EtaEvidenceBoundaryWriter` só prepara binding quando existe Begin operacional validado por `EventoViagemValidator`, com identidade igual ao estado operacional e timestamp posterior à aquisição do owner. O vínculo fica provisório até o COMMIT; a confirmação lê estado, head, outbox de qualidade e Begin operacional realmente persistidos, reutilizando a conexão do commit. Estado usa UUID N no codec; payload usa UUID D. Não há adoção a partir de mapa ou Redis. A identidade estrutural vem do evento/estado operacional validado; este hook não cria uma nova auditoria física da geometria.

`EtaEvidenceRecoveryRepository` consulta somente viagens explícitas (até 200), em REPEATABLE READ/READ ONLY, com PKs e journal dessas viagens; limita inventário a 10.000 envelopes/viagem. Mostra head, Begin persistido, journal e recibos pendentes. Todos os resultados são NaoVerificada: ausência de head não prova rollback, e Close anterior não é autorização de certificação. Não restaura witness, não escreve, não registra epoch nem se integra automaticamente ao startup.

Reciclagem local exige Close durável, journal completo e todos os recibos de qualidade processados, além de witness/contadores/flags iguais à memória, nenhum pending e nenhuma admissão recusada após o selo. Não apaga evidência durável. Saturação permanece desconhecida; não existe LRU de owners/bindings.

## Matriz de cobertura e limites

| Componente/fluxo | Situação demonstrada | Limite |
|---|---|---|
| Ingresso adquirido, duplicatas, filtros, falhas | Implementado; regressões offline | Não comprova entrega completa do provedor nem parsing anterior à lista adquirida |
| Início prospectivo | Confirmado na fixture com Begin operacional real; pré-COMMIT recusado | Registro de scope é interface local interna; nenhum epoch de produção |
| Finalização/Closing/Close | Pending impede Close; mesma versão operacional; recibos antes de reciclar | Não finaliza com lacunas limpas; MissingDecision permanece |
| Cancelamento/candidato/reancoragem/circularidade | Regras monotônicas existentes preservadas; regressões operacionais | Reancoragem sem continuidade e candidato contaminam; não certifica esses fluxos |
| Substituição/renascimento | Novo trip não substitui binding antigo em memória | Antes de Close reconciliado, falha conservadora; não existe handoff automático |
| Rollback e commit incerto | Rollback/idempotência e incerteza pós-COMMIT testados | Binding provisório não é limpo por ausência de row; recuperação/novo owner requer decisão posterior |
| Restart/perda Redis | Inspeção durável conservadora preparada; fixture com estado PG/Redis | Não há orquestração automática de restart nem prova de continuidade da origem |
| Limites e checkpoints | Owners/bindings 200; pending 16; 60s/75s experimentais mantidos | Cadências simuladas não validam distribuição real de atraso |
| Fencing legado | Apenas CAS cooperativo de qualidade demonstrado | REPROVADO; proposta separada, nenhuma role/ACL implementada |
| Certificação automática | OFF, cobertura incompleta, MissingDecision mantido | REPROVADA; verificador ML independente continua sem autorizar v2 |

Falha antes/depois do mapa não transforma mapa aceito em prova operacional. `ingress-frontier-not-attested` permanece porque predecessor/destino e entrega da fonte ainda não são comprovados integralmente. Viagens anteriores à autorização não herdam certificação. A etapa fecha componentes locais possíveis, não todos os bloqueios de ativação.

## Benchmark conectado observado

Fixture exclusiva PostgreSQL 16/PostGIS 3.4 + Redis 7, removida pelo harness. Relatório: `outputs/evidence-3g3b1-5d6d6c8e2d224e5599b84c7c09b7a8b4/evidence-3g3b1.trx` (saída local ignorada pelo Git). Modos: 0 baseline, 1 OFF, 2 ON. Mesmo `CommitHot` Lua real em todos, `ProjectDurable` nos checkpoints. GPS a cada 10s, tempo simulado 120s, commit operacional a cada 60s; qualidade reaproveita transação. Inicialização/Begin excluídos da medição. Quatro rounds de aquecimento + oito medidos, fases em ordem fixa: não é ensaio estatístico de produção. p90 e p95 coincidem por amostra pequena.

| Veículos | Modo | p50 ms | p90/p95 ms | Transações | WAL bytes no intervalo |
|---:|---|---:|---:|---:|---:|
|1|baseline|1,216|9,832|2|992|
|1|OFF|1,185|5,840|2|992|
|1|ON|1,309|13,853|2|6.920|
|10|baseline|14,739|56,867|20|10.352|
|10|OFF|18,832|83,677|20|10.304|
|10|ON|14,735|181,036|20|72.168|
|50|baseline|64,890|252,302|100|55.640|
|50|OFF|52,640|240,003|100|55.840|
|50|ON|73,317|678,025|100|366.424|
|200|baseline|221,567|884,587|400|231.088|
|200|OFF|223,506|996,770|400|474.136|
|200|ON|237,027|2.648,068|400|2.367.416|

Verificar números completos no TRX; WAL inclui aquecimento, full-page images e atividade auxiliar, portanto diferença observada não é custo causal isolado. ON menos baseline: +0,093/+(-0,004)/+8,428/+15,460 ms de p50 e +5.928/+61.816/+310.784/+2.136.328 bytes de WAL em 1/10/50/200. Zero transações adicionais; qualidade acrescenta escritas de head/outbox dentro das mesmas transações. Checkpoints ON: 2/20/100/400, pending final zero.

Heap retido ON: +77.272/+8.704/+432/−432 bytes; working set ON: aproximadamente 126/128/137/163 MB. São amostras do processo de testes, afetadas por GC/JIT; não medem RSS do homeserver nem provam ausência de leak por longa duração. Duas conexões observadas, MaxPoolSize=2; workload serial, não demonstra desempenho sob concorrência ampla. Testes de CAS concorrente e retomada idempotente são separados do benchmark.

**Regressão relevante:** lote ON de 200 teve p95 2,65s, contra 0,885s baseline. Não aprovar orçamento de polling produtivo com esse resultado. Dispersão de checkpoints e contenção devem ser homologadas após fencing; não esconder custo aumentando timeout. O benchmark não executa API completa, provedor HTTP ou enriquecimento PostGIS por veículo.

## Compatibilidade, arquivos e reprodução

v1/v2, migration aditiva existente, fixtures compartilhadas e bundles selados preservados. ML não foi alterado; validação independente e política OFF continuam instaladas. `coverage.sources.json` fixa hashes normalizados da revisão e linhas de retorno, incluindo ingresso e recuperação; hash não comprova cobertura em execução.

Arquivos desta etapa: ingresso novo; coordenador/boundary writer estendidos; leitor de recuperação novo; hooks pequenos em polling/repositório e DI; testes de ingresso e fixture conectada; sourcepins/regressões estáticas; este relatório, proposta de fencing e acréscimo ao histórico. Alterações de outras etapas presentes no working tree não foram removidas.

```powershell
# Offline (não executar toda suíte conectada com configurações operacionais)
dotnet test NoPonto/NoPonto.csproj --no-restore --filter "FullyQualifiedName~EtaIngressCoverageTests|FullyQualifiedName~EtaDecisionCoverageTests|FullyQualifiedName~EtaTripEvidenceTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~ViagemOperacionalRegraTests" --verbosity minimal
python -m unittest discover -s tools/eta_ml -p 'test_*.py'
# Cria recursos novos exclusivos, valida isolamento e remove somente os próprios recursos.
pwsh -NoProfile -File tools/eta_ml/homologate_coverage.ps1
git diff --check
# No repositório ML
Set-Location D:\repositorio_github\NoPonto\ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests
git diff --check
```

Não executar API completa, produção, SSH, migrations operacionais ou banco existente. Próximo passo: revisar e aprovar o desenho de autoridade em `PROPOSTA_FENCING_3G_3B_2B_2.md`; somente então implementar sua fixture e reavaliar os custos. Não treinar nem emitir AuditadaSemProtecao nesta etapa.

## Regressões executadas

Build do projeto passou com warnings preexistentes. **262 testes C# passaram**, zero falhas/ignorados: ingresso/conflito/concorrência, pending/close, limites, regras operacionais linear/circular/mudança, retry, telemetria e dataset. Python: 76 testes backend e 109 ML passaram, sem falhas. `git diff --check` passou nos dois repositórios; não houve alteração desta etapa no ML.

A fixture conectada composta passou duas vezes. A rodada final adicionou recusa de reciclagem antes dos ACKs, nova instância sem adoção de witness e perda controlada de chave Redis com recuperação da projeção sem limpar RedisLost. Relatório novo `outputs/evidence-3g3b1-7908fd11b2ba479cb2dd8f03fac5d066/evidence-3g3b1.trx`: 1 teste composto aprovado, aproximadamente 48,5s, cobrindo múltiplas asserções PostgreSQL/Redis. Nenhum recurso existente foi reutilizado. Não se simulou partição de rede durante COMMIT nem restart do processo/API inteira.

O benchmark completo dessa repetição teve p95 ON 28,310/227,070/935,874/2.886,603ms para 1/10/50/200, baseline 6,852/94,853/739,325/1.475,156ms e OFF 6,766/115,533/439,524/1.378,846ms. Delta p50 ON-baseline 0,386/1,953/−31,102/29,229ms; WAL incremental observado 5.928/61.816/306.704/1.244.776bytes, zero transações adicionais e os mesmos checkpoints/pool. Heap ON +77.456/+8.680/+488/−6.744bytes, working set ON 126.205.952/132.231.168/131.538.944/136.552.448bytes. Variação entre rodadas e melhora aparente em 50 veículos reforçam a limitação do desenho sequencial/amostra pequena, sem alegar ganho causal. Todos os valores finais completos permanecem no TRX novo; a tabela anterior mantém a primeira rodada, sem sobrescrever evidência.

Arquivos exatos criados/alterados nesta etapa:

- `NoPonto/2-Application/Services/GPS/EtaGpsIngressCoverage.cs` (novo), `EtaDecisionCoverage.cs`, `GpsPollingService.cs`.
- `NoPonto/4-Data/Repositories/EtaEvidenceRecoveryRepository.cs` (novo), `EtaEvidenceBoundaryWriter.cs`, `ViagemOperacionalRepository.cs`.
- `NoPonto/Program.cs`, `NoPonto/5-Testes/EtaIngressCoverageTests.cs` (novo), `EtaTripEvidenceLocalTests.cs`.
- `tools/eta_ml/coverage.sources.json`, `test_coverage.py`, este relatório e `PROPOSTA_FENCING_3G_3B_2B_2.md` (novos).
- `NoPonto/HISTORICO.md` (somente acréscimo).

O harness `homologate_coverage.ps1` já preparado na etapa anterior foi reutilizado, sem alteração nesta etapa. Demais diferenças do working tree pertencem ao trabalho anterior e foram preservadas.
