# Instabilidades de validação separadas da correção operacional — 2026-10-20

## Shadow/ETA offline

O diagnóstico 33 registrou uma rodada 758/758, depois duas rodadas 757/758 por `TaskCanceledException` em `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`, caso `export_failure`: BUS individual e BRT set-based. Os 48 casos do método passaram isoladamente entre as rodadas. Esses resultados foram preservados em `0d93ada`, sem omitir as falhas.

Na etapa de correção de continuidade, a seleção oficial offline passou **758/758**, com as 28 classes verificadas. Nenhum teste/fonte/configuração de ETA/ML, timeout ou paralelismo foi alterado. Não houve exportação, treino, acesso produtivo ou retomada do ML. O resultado atual não demonstra que a instabilidade histórica tenha sido corrigida; sua causa permanece aberta. Não foi feito novo diagnóstico profundo de Shadow nesta tarefa.

## Concorrência integrada

Na primeira seleção ampla da correção, `CorridaMesmoTimestamp_CasOutboxSomenteUmaVez` recebeu seis `InfrastructureFailure` entre 20 concorrentes. A asserção que exige um único `Updated` passou; a asserção de que os demais devem ser `RejectedOlderOrEqual` falhou. Como essa falha interrompeu o teste, a asserção posterior do tamanho do stream não foi executada nessa rodada. Não é evidência suficiente para certificar todo o Outbox daquela execução nem para atribuir a falha a timeout PostgreSQL/Redis.

O teste usa logger nulo, e o artefato não contém a exceção de infraestrutura originária. Não foi demonstrada causa de capacidade, rede ou regressão funcional. Os caminhos de transação/CAS e a asserção do teste permanecem idênticos à base. O teste passou depois em uma seleção focada com 20 cenários de reinício (**21/21**) e na repetição completa (**248/248**), inclusive a asserção do stream. Isso confirma essas execuções, não estabilidade sob carga de frota.

Quatro outras falhas na primeira seleção eram expectativas antigas de candidata sobreviver ao reaquecimento sem predecessor. Foram atualizadas para exigir duas novas evidências positivas após a invalidação, conforme o requisito aprovado; não são classificadas como intermitência ou problema de Shadow.

## Encaminhamento

Não há falha nas seleções finais da entrega: 248 integrados, 108 unitários direcionados e 758 offline. As ocorrências intermediárias ficam nos TRXs e no [manifesto sanitizado](evidence-continuity-validation-20261020.json), com hashes. Nenhuma falha foi aceita como status permitido, teste removido/ignorado ou timeout aumentado.

Para uma futura habilitação, observar erros de infraestrutura/latência de commit e obter validação repetível no ambiente alvo, mantendo os casos estritos de unicidade. Se houver nova reprodução de Shadow, investigá-la em tarefa própria, com captura limitada do motivo da espera; se houver nova reprodução da corrida, capturar a exceção de infraestrutura sem alterar CAS ou regras operacionais. Esta tarefa não muda essas funcionalidades nem declara suas causas resolvidas.
