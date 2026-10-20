# Instabilidade Shadow observada na validação do motivo de fim — 2026-10-20

Continuidade do registro do [relatório 35](35-instabilidades-validacao-evidencias-20261020.md), sem alterar ETA/ML, testes, timeout ou paralelismo.

Na primeira seleção oficial offline desta etapa, **757/758** passaram. Falhou `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`, BUS, `batch=true`, `failure="export_failure"`, com `TaskCanceledException` no helper `Until` (linha 264), chamado na linha 144 ao aguardar `shadow.Results.Count > 0`. O helper tem limite existente de três segundos. As asserções posteriores desse caso não foram executadas nessa rodada; não é aprovação parcial desse cenário.

O que foi comprovado é a condição não observada antes do limite. O TRX não demonstra a causa originária nem permite atribuir o timeout à alteração do motivo de fim, a carga, ao agendamento ou ao exportador. A primeira rodada offline coincidiu com parte da seleção integrada; essa coincidência não comprova causalidade. Nenhuma investigação profunda ou retomada do ML foi feita.

Depois de terminar a seleção integrada, os 48 casos do método passaram isoladamente (**48/48**). A repetição completa da allowlist passou **758/758**, com as 28 classes verificadas. A fonte `HistoricalEtaGtfsPollingTests.cs`, as fontes ETA/ML e a configuração continuam idênticas à base `a2f0abc`. Os testes não foram removidos, ignorados ou relaxados. Esses passes não demonstram correção da intermitência histórica.

A seleção integrada ampliada passou **293/293**, inclusive o teste estrito `CorridaMesmoTimestamp_CasOutboxSomenteUmaVez`; nenhuma falha de concorrência foi observada nesta execução. Não é evidência de estabilidade de frota nem resolução da ocorrência anterior do relatório 35.

Os comandos, resultados, nomes das falhas e hashes dos TRXs estão no [manifesto sanitizado](operational-end-reason-validation-20261020.json). Antes de futura habilitação, manter a investigação de Shadow separada: capturar de forma limitada por que a condição de conclusão não apareceu no prazo, sem aumentar timeout para esconder a falha nem mudar regras GPS/ETA nesta entrega.
