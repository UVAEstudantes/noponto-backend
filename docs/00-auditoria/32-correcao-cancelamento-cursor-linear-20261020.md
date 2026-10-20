# Cancelamento de candidata: preservar cursor LINEAR — 2026-10-20

## Resultado e escopo

Corrigido exclusivamente o retorno à mesma execução/versão LINEAR atrás do cursor incorporado. O GPS atual pode atualizar posição, progresso físico e timestamp sem reabrir ocorrências anteriores. A próxima ocorrência operacional continua depois do cursor preservado. O baseline de uma **nova execução confirmada** permanece independente da execução anterior.

Base `7f56cce1aaa72638978fb6056ce5d245494324ab`, branch `audit/gps-bus-brt-pipeline`. `MudancaOperacionalHabilitada` permanece **false por padrão**; true somente nas opções dos testes descartáveis. Nenhum acesso à produção nesta etapa, nem deploy, push, merge ou PR. Sem alteração de frontend, ETA, ML, matching, EventId, contrato/validação de payload, idempotência do Outbox, regra circular ou configuração/infraestrutura versionada.

## Causa-raiz e correção

O relatório [31](31-validacao-mudanca-operacional-20261020.md) demonstrou: A incorpora ocorrências 1/2, uma candidata B é persistida e o retorno a A cancela B. O repositório usava baseline pela posição adotada mesmo preservando o ID de A. Retornar a 0,11 reduzia o cursor 2 a zero. Um cruzamento posterior tentava gravar o EventId da passagem antiga com outro timestamp/payload; o Outbox recusava corretamente e o repositório retornava `InvalidState` repetidamente. O rollback preservava o histórico, mas bloqueava a operação.

Alteração funcional: **17 linhas adicionadas em `ViagemOperacionalRepository.cs`**. O repositório guarda o resultado existente `CandidatoCancelado`. Se o baseline dessa mesma versão LINEAR ficar abaixo do cursor incorporado, reutiliza a consulta normal de transição, com posição anterior igual à atual e o cursor antigo. Essa consulta já valida pertencimento/ordem, não seleciona cruzamentos e resolve cursor, próximo alvo e terminal em conjunto. A adoção continua sem passagens retroativas. Não foi criado outro mecanismo de mudança nem alterado SQL compartilhado.

O caminho de `MudancaConfirmada` continua usando baseline novo com cursor inicial próprio, volta zero e outra identidade. A condição corretiva não é alcançada por nova execução, outra versão, circular, candidata insuficiente ou flag desligada.

Posição/progresso físico e cursor têm significados diferentes: a posição confirmada segue o GPS aceito, inclusive atrás do cursor; o cursor registra ocorrências já incorporadas. Não se falsifica uma posição adiantada para preservar o cursor.

## Reprodução e RED/GREEN

1. Na base sem alteração funcional, `OperationalRecovery_ReturnBehindCursor_ExposesOutboxConflict` passou 2/2 por **caracterizar a falha**, individual/lote. Cada caso comprovou dois `InvalidState` e zero duplicação materializada.
2. Substituída a caracterização pela regressão `OperationalRecovery_ReturnBehindCursor_PreservesIncorporatedCursor`, exigindo cursor 2, próximo alvo 3, novos GPS aceitos, histórico intacto e ausência de `InvalidState`. Com runtime original: **RED, 0 aprovados/2 falhas**, ambos `Expected: 2 / Actual: null` no cursor após cancelamento.
3. Aplicadas somente as 17 linhas do repositório, sem mudar a regressão entre essas duas execuções: **GREEN, 2/2**.
4. A regressão foi ampliada para 12 combinações e acompanhada de quatro casos adicionais de concorrência/rollback/terminal. A seleção final e seus TRXs constam da [evidência sanitizada](cursor-floor-validation-20261020.json).

Uma montagem inicial anterior ao RED válido falhou na compilação por uma chave excedente no arquivo de teste; não conta como RED comportamental. Na primeira ampliação, 6/14 passaram e oito expectativas de recuperação foram refutadas: retornos próximos ao cursor já cancelavam a candidata em t=120, e t=150 era atualização quente sem checkpoint obrigatório. A expectativa foi corrigida para recuperar o snapshot do cancelamento realmente persistido, mantendo o cursor e verificando o avanço seguinte. Nenhuma mudança adicional de runtime foi feita. A matriz corrigida passou 14/14.

## Matriz e verificações

| Cenário | Verificação |
|---|---|
| BUS/BRT × matching individual/set-based × retornos 0,11/0,39/0,55 | 12 casos no pipeline HTTP local/GTFS-RT, PostGIS, Redis, repositório e Outbox reais. 0,11 retorna antes da primeira ocorrência; 0,39 fica antes do cursor 2; 0,55 é o controle depois dele. |
| Recruzamento de ocorrências incorporadas | Novos GPS em 0,25/0,46 ou 0,41/0,46 atualizam estado, timestamp e progresso; cursor 2 e alvo 3; nenhuma passagem antiga reemitida. |
| Próxima ocorrência inédita e terminal | Uma única passagem da ocorrência 3, mesma viagem/versão/sentido/linha; depois duas confirmações e finalização pela regra existente. As três passagens permanecem únicas no histórico. |
| Histórico/Outbox | Payloads históricos comparados integralmente antes/depois; EventIds distintos; reentrega do lote ao materializador não duplica histórico. |
| Perda do Redis/reconstrução | Repositório novo recupera do PostgreSQL o cancelamento com cursor preservado; admite novo GPS e passagem inédita. Não é reinício de container ou crash do sistema operacional. |
| Concorrência | Dois cancelamentos com o mesmo snapshot: somente um `Updated`; outro `Conflict` ou `RejectedOlderOrEqual`. Sem eventos no cancelamento e sem duplicar a passagem futura. |
| Rollback | Falha injetada após escrita do Outbox conserva candidata, cursor e estado durável; stream/Outbox não mudam. Retry cancela corretamente e a ocorrência futura é emitida uma vez. |
| Terminal já incorporado | Dois controles com/sem candidata preservam cursor terminal, sem reabrir passagem. Mantida a regra existente de confirmações por cursor terminal/ausência de próximo alvo, inclusive com recuo físico. |
| Nova execução confirmada | Reexecutados os 20 casos de recuperação de estados antigos e os controles existentes de linha/sentido: nova identidade, baseline próprio, sem importar cursor anterior, fim lógico sem parada inventada. |
| Proteções anteriores | Reexecutados CAS, fencing, rejeição de GPS antigo, retry/reinício, integridade circular e flag desligada das três suítes de integração existentes; pipeline completo inclui aquisição, remoções e paridade REST/SignalR. |

Os 12 casos novos de pipeline usam fontes sintéticas locais e matching real. Os quatro casos novos de concorrência/rollback/terminal entram na fronteira do repositório com identidade/matching explícitos e banco/Redis reais; não equivalem a quatro reproduções completas de aquisição. Reinício da matriz nova significa reconstrução do repositório e perda da projeção; os casos anteriores da suíte completa também reconstroem host/serviços.

## Comandos e isolamento

Resultado final: **138/138 testes integrados e 758/758 offline**, 896 casos distintos, zero falhas/ignorados nas seleções finais. Pipeline: 58 casos; três suítes anteriores de repositório/mudança/circular: 80 casos. A verificação oficial offline confirmou todas as 28 classes da allowlist. Projeto Release compilado com os cinco avisos anteriores (CS8981 ×2, CS7022, xUnit2031 ×2), sem novos avisos. Não foi necessário build separado da solução.

Reutilizado sem alterações `tools/gps/test_pipeline_disposable.ps1`, com imagens locais Redis 7.4/PostGIS 16/3.4, sem pull, rede própria, tmpfs, portas aleatórias loopback e guards existentes. Migrations apenas nos schemas descartáveis autorizados. Os TRXs, manifests de isolamento e teardown ficam em `NoPonto/lab-output/cursor-floor-20261020-*` (ignorados pelo Git); somente contagens, hashes e observações sanitizadas são versionados.

As sete rodadas descartáveis preservaram os quatro containers preexistentes; cada teardown registrou zero containers próprios restantes e remoção da rede própria. Conferidos hashes de 22 arquivos protegidos, incluindo opções/configuração, aquisição BRT, predecessor, matching, SignalR/REST, regra/eventos, SQL de ocorrências e Outbox/materialização. Históricos e diagnósticos anteriores preservados por prefixo; nenhuma credencial ou telemetria produtiva nova nos artefatos.

Comando da seleção integrada final (diretório de resultados novo, absoluto):

```powershell
& ./tools/gps/test_pipeline_disposable.ps1 -Run -ResultsDirectory $results -Filter 'FullyQualifiedName~NoPonto.Tests.GpsPipelineDisposableIntegrationTests.|FullyQualifiedName~NoPonto.Tests.ViagemOperacionalIntegracaoTests.|FullyQualifiedName~NoPonto.Tests.MudancaOperacionalPontaAPontaTests.|FullyQualifiedName~NoPonto.Tests.IntegridadeCircularPostgresTests.'
```

Offline, sobre a compilação Release já produzida pela rodada integrada:

```powershell
$cursorFloorOfflineFilter = python .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $cursorFloorOfflineFilter --logger 'trx;LogFileName=offline.trx' --results-directory NoPonto/lab-output/cursor-floor-20261020-offline --verbosity quiet
python .ci/offline_tests.py verify NoPonto/lab-output/cursor-floor-20261020-offline/offline.trx
```

## Riscos e limites restantes

- Custo: uma consulta adicional de ocorrências somente quando o cancelamento reduziria o cursor da mesma versão LINEAR. Sem benchmark de frota/carga; não é uma consulta adicional por GPS normal.
- A correção previne a perda de cursor no cancelamento futuro; não repara automaticamente snapshots que já perderam o cursor, nem reescreve eventos históricos.
- A regra terminal existente usa terminal incorporado e ausência de próximo alvo para contar confirmações; os controles comprovam que isso também ocorre com posição física atrás do terminal. Esta etapa preserva essa semântica e não acrescenta exigência espacial nova. O evento de finalização não deve, isoladamente, ser tratado como nova passagem/chegada.
- ETA físico e alvo operacional continuam sendo calculados por caminhos distintos; durante o recuo podem apontar para necessidades diferentes. Os novos GPS e ETA permanecem disponíveis na fixture e a suíte anterior de paridade continua necessária; não houve mudança de ETA nem validação de latência sob carga.
- Padrões BRT diferentes no mesmo sentido continuam sem política de nova execução; evidências positivas ainda podem confirmar sem serem consecutivas. Nenhuma dessas regras foi ampliada.
- Não executados produção, fonte externa, APK/frontend, ML, carga/replicação/backplane ou crash arbitrário.

Recomendação: manter a flag global desligada. Esta correção pode ser revisada isoladamente como proteção do cursor; uma decisão futura de habilitação ainda precisa tratar os limites do relatório 31 e os consumidores do encerramento lógico. Nenhuma reparação histórica ou ativação está incluída.
