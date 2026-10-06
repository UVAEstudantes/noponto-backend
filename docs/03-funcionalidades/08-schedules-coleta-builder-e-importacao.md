# Grades ferroviárias — coleta, builder e importação

## Fronteiras do pipeline

As etapas são independentes e não devem ser tratadas como sinônimos:

1. **coleta FULL:** captura evidência observada de `/trips/plan`;
2. **builder:** reconstrói corridas, paradas e padrões e produz validações;
3. **auditoria:** aceita ou rejeita os artefatos e suas anomalias;
4. **importação:** valida contratos, resolve a estrutura V2 e persiste uma versão;
5. **ativação:** torna uma versão a fonte operacional da linha.

## Coletor `extrair-trem`

O coletor Python trabalha sequencialmente e consulta cada estação em direção ao terminal nos calendários `WEEKDAY`, `SATURDAY` e `SUNDAY`. Mantém request sequence, registros brutos append-only, conjuntos de deduplicação e checkpoint gravado atomicamente. `--resume` recompõe o estado dos arquivos e prossegue sem transformar uma retomada em nova coleta lógica. Erros transitórios, rede e respostas elegíveis usam retry/backoff; `Retry-After` é respeitado quando presente.

Uma opção incidental de outra linha pode aparecer na resposta. Ela é catalogada, mas só opções válidas da linha-alvo, direção e viagem direta entram na evidência aceita. A estratégia estação→terminal observa short starts, padrões que pulam estações e horários após meia-noite, mas continua sendo um snapshot observado, não contrato oficial de feriados ou mudanças temporárias.

### Situação de Japeri

O run local `run_20261004_235458` terminou `COMPLETED` após uma falha e retomada: 186/186 tarefas estação-direção, 5.206 tentativas, 4.662 respostas bem-sucedidas, 544 falhas finais de requisição e 6.543 opções únicas aceitas. Portanto, a **coleta FULL foi concluída**. Não foi encontrada saída de builder/auditoria/importação para Japeri; o relatório textual `READY_FOR_FULL` ficou historicamente desatualizado diante do run posterior.

## Builder

`rail_schedule_builder.py` consolida base e repair scopes, encadeia observações por âncoras temporais, atribui IDs determinísticos, calcula minutos absolutos e `dayOffset`, identifica short starts e travessia de meia-noite e agrupa assinaturas de estações em padrões. Colisões de encadeamento permanecem `AMBIGUOUS`; duração excessiva, aumento de paradas e terminal já observado viram anomalias, não fatos silenciosamente corrigidos.

Saídas: `summary.json`, `patterns.json`, `scheduled_runs.jsonl`, `scheduled_stops.jsonl`, `anomalies.jsonl` e CSVs de inspeção. O summary só fica `APPROVED` se passarem monotonicidade temporal, ausência de paradas duplicadas, ordenação terminal, isolamento do repair scope, preservação do fim de semana, calendário/direção homogêneos e exclusão de opções inválidas.

Santa Cruz V1 está `APPROVED`: 246 runs, 7.433 stops, 23 padrões, seis short starts, uma travessia de meia-noite, zero ambiguidades e 59 anomalias registradas.

## Importação e ativação

`RailScheduleDatasetLoader` exige os quatro arquivos canônicos, schema 1, status aprovado, validação positiva, cardinalidades exatas, IDs únicos, calendários suportados, referências válidas e stops monotônicos. Calcula SHA-256 do conteúdo.

A importação é transacional e idempotente por `linha + contentHash`. Resolve unicamente linha, estações e sentidos pela estrutura V2. Cada padrão é classificado como `EXACT`, `SUBSET_COMPATIBLE`, `UNRESOLVED` ou `CONFLICT`; apenas os dois primeiros podem sustentar projeção espacial. IDs de versão, padrão, corrida e stop são determinísticos. Ativar desativa a versão ativa anterior da mesma linha na mesma transação.

O calendário operacional escolhe `WEEKDAY`, `SATURDAY` ou `SUNDAY` pela data local. Não foi identificada regra de feriados; logo feriado não deve ser apresentado como coberto.

## Riscos

- grade observada pode omitir exceções, serviços especiais e alterações posteriores;
- respostas falhas podem deixar cobertura desigual, ainda que a tarefa tenha sido encerrada;
- um padrão estrutural ambíguo/conflitante bloqueia posição espacial;
- importar sem ativar não altera runtime; ativar é mudança operacional separada;
- Japeri ainda precisa builder, auditoria e importação antes de uso.

