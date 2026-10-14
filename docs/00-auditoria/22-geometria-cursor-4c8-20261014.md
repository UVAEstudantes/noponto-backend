# GPS 4C.8 — geometria, ocorrências e cursor (14/10/2026)

As oito amostras não demonstram defeito da seleção SQL. Todas têm alvo acima do limite superior da consulta. A causa operacional permanece aberta: falta a fração da ocorrência do cursor e a sequência das quatro versões. Não houve patch funcional, acesso remoto, consulta a banco, build Docker ou nova instrumentação.

## Condições verificadas

- `OcorrenciaParadaRepository.cs:13–74`: a sequência é da versão pinada, ordenada por Ordem/Id. Validação rejeita ordem inválida/duplicada, fração fora de [0,1], regressão entre ocorrências, cursor inexistente ou Id/ordem incompatíveis. Frações iguais são permitidas. Não valida `fração(cursor) <= posição anterior`.
- Para LINEAR normal: `atual >= anterior AND Ordem > ultima_ordem AND PosicaoTracado > anterior AND PosicaoTracado <= atual`. Cursor final é o máximo entre o cursor anterior e as ordens incorporadas. Próxima é a menor ordem superior ao cursor, independentemente de proximidade física. Terminal é a maior ordem da versão, não necessariamente a fração exatamente 1.
- Baseline incorpora ocorrências até a fração inicial e não gera passagens. `ViagemOperacionalRepository.cs:223–243,373–380`: baseline quando não há estado, finalizada, versão diferente, proteção circular ou candidato habilitado. Nova execução após fim usa posição atual; normal usa posição confirmada anterior.
- `ViagemOperacional.cs:149–188`: normal grava a fração atual, inclusive regressiva, e conserva o cursor recebido da SQL. Não reinicia viagem LINEAR por regressão. Fim depende de terminal incorporado/sem próxima, seguido de confirmações; estar fisicamente no início não prova nova execução. Logo cursor alto + fração baixa é possível após regressão, sem qualquer mudança de versão. Reset automático permitiria reemitir ocorrências já incorporadas.
- `ViagemOperacional.Integridade.cs:97–139`: recuperação/reancoragem com prova e baseline é específica de CIRCULAR, não uma explicação automática para LINEAR. Mudança confirmada, quando habilitada, cria nova execução/baseline (`ViagemOperacional.Mudanca.cs`). Com flag false, divergência ativa sem projeção aceitável retorna `ItineraryChanged` antes da atualização; não reancora repetidamente.
- `ViagemOperacionalRepository.cs:20–88,302–320`: estado durável é autoridade e cache é projeção; reiniciar processo ou perder cache não significa reiniciar cursor. Controle de versão/conflict impede sobrescrever estado durável concorrente. Não há evidência de que recuperação, restart ou baseline tenham ocorrido nos quatro veículos.

## Origem das frações

`GtfsProjecaoService.cs:28–58,64–82`: projeção sequencial em segmentos, distância métrica acumulada/total; candidatos não regressivos, desempate geométrico apoiado em shape_dist_traveled quando disponível. Ordem é índice + 1; SourceSequence mantém stop_sequence. `GtfsEstruturalV22Service.cs:145–171` persiste fração/ordem na geometria da versão, acumulada como fração × comprimento, origem GTFS_AUTORITATIVO.

`EstruturaFinalRebuildService.cs:247–264,406–420`: ordena stop_sequence, projeta na geometria escolhida, recusa regressão; terminal circular repetido pode receber explicitamente 1. `GtfsDatarioPlanPersister.cs:240–250`: origem GTFS_DATARIO usa shape_dist/shape length com clamp [0,1] (ausência gera zero). `ArcGisParadasReconciliador.cs:386–414` persiste associações por ordem sugerida e suas frações. Legado V24 copia relações antigas, mas está excluído da compilação atual; não confundir código presente com origem efetivamente usada. A origem das quatro versões exige ler MetodoConstrucao/AlgoritmoVersao, não inferir pela modalidade.

Há também diferença de parametrização: projeção GPS via `ST_LineLocatePoint` em geometry 4326 versus frações métricas de alguns importadores. Isso pode deslocar limites; não demonstra o salto observado e não autoriza conversão ou alteração de dados. Fração 1 é válida, inclusive terminal. Ordens 6/12/63/92 só podem ser certificadas como terminais conhecendo `max(Ordem)` da versão.

## Consistência das amostras

| Cursor/alvo | Última posição | Alvo | Resultado comprovado |
|---|---:|---:|---|
| 11/12 | .523808 | .999999966 | Alvo não cruzado |
| 5/6 | .422581 | 1 | Alvo não cruzado |
| 62/63 | .858762 | 1 | Alvo não cruzado |
| 91/92 | .070241 | .990565268 | Alvo não cruzado |

Se `fração(cursor) <= posição anterior`, o par cursor/posição é consistente mesmo com número de ordem alto. Se maior, existe cursor adiantado em relação à posição; a reprodução demonstra um caminho possível (regressão LINEAR), não sua ocorrência real. É preciso separar o estado válido de deduplicação após regressão de um matching/projeção geometricamente errado. Uma SELECT atual não reconstrói quando/por que houve regressão nem prova qual trajeto físico foi percorrido.

## Investigações secundárias

`GpsEnriquecimentoService.cs:155–169,407–431` exige orçamento temporal positivo; resultado inelegível também cobre ausência de projeção combinada e avanço fora do orçamento. `GpsItinerarioRepository.CombinedSetBased.cs:100–128` e `.Batch.cs:859–890` exigem versão pinada, padrão/sentido/linha esperados, código da linha recebida igual ao da versão pinada, comprimento positivo, vencedor global diferente, faixa não vazia, distância espacial admissível; batch também exige projeção não regressiva.

BRT trocar sentido no matching global não é, sozinho, rejeição da projeção pinada: a identidade verificada é a da versão antiga contra o estado antigo. A geometria/alcance pode não ser elegível. BUS trocar código de linha pode falhar no predicado `c.codigo=e.codigo`; isso é proteção deliberada. `ProjectionIneligible` é resultado composto: os logs fornecidos não identificam qual subpredicado falhou. Não desabilitar proteção.

Gap entre aceites ~58s excede TTL ativo 40s: compatível com expiração, não prova isolada. `GpsTimestampDeduplication.cs:16–41` consulta watermark quando ativo ausente e ainda recusa timestamps antigos/iguais. `PosicaoVeiculoCacheRepository.cs:58–88` GET não renova TTL. Aceite não equivale necessariamente ao instante da última escrita, e falha/corrupção/remoção continuam alternativas. TTL maior manteria predecessor mais antigo e alteraria disponibilidade para validação temporal; retenção recente e watermark são controles distintos. Nenhuma mudança feita.

## Testes e evidência mínima restante

`GpsCursor4C8Tests.cs`: quatro fixtures com as frações do resumo e identidades sintéticas verificam decisão sem passagens/cursor preservado; quinta fixture demonstra regressão .98→.050787 e avanços posteriores com cursor 91 preservado na mesma viagem. Transição é fornecida como fixture: esses testes executam a regra real, não PostgreSQL e não certificam sequência produtiva. Nenhum teste RED de bug funcional: a causa ainda não foi comprovada.

Consulta preparada: `22-geometria-cursor-4c8-20261014.sql`, NÃO EXECUTADA. Parâmetros: versão, cursor Id/ordem e limites completos retirados de `query` dos registros 4C7 já coletados; os UUIDs não constam da tabela resumida recebida. Executar futuramente somente após autorização, uma vez para cada versão distinta, sem nomes/identificadores de veículos. Retorna sequência completa, origem/hash/versão/topologia, máximo de ordem, fração do cursor e predicados de cruzamento. Tabelas: PadroesVersoes e OcorrenciasParadasPadroes. Custo: lookup de versão por PK e faixa de ocorrências pelo índice (PadraoVersaoId,Ordem), janelas apenas nessa versão; proporcional ao número de suas ocorrências, sem histórico nem PostGIS pesado. Não supor quantidade pela ordem 92. Usar timeout de leitura da sessão; não executar EXPLAIN ANALYZE em produção nesta etapa.

Esta leitura encerra a dúvida estrutural (terminal e cursor/fração), não necessariamente a causa temporal. Se cursor estiver adiantado, usar a primeira amostra existente anterior à regressão, se disponível, para discriminar matching errado de retorno físico legítimo; não reiniciar cursor por hipótese.

Validação executada: `dotnet test NoPonto/NoPonto.csproj --no-restore --filter "FullyQualifiedName~GpsCursor4C8Tests|FullyQualifiedName~GpsPassagens4C6Tests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~IntegridadeCircularRegraTests"`: 87 aprovados, zero falhos/ignorados, incluindo cinco casos novos; cinco warnings preexistentes. TRX: `D:\repositorio_github\NoPonto\gps-4c8-validation-20261014\gps4c8.trx`. Não executados: SQL, integração com bancos, testes conectados, build Docker ou operação remota. Alterações desta etapa: somente novo teste, relatório, SELECT preparada e acréscimo no histórico; preservado o trabalho anterior.
