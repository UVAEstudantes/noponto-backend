# Expected runs, scanner e evidências

## Materialização de `ExpectedRun`

`ExpectedRunService` lê a versão ativa e materializa corridas para a data de serviço em `America/Sao_Paulo`. A janela consulta a data atual e a anterior para preservar serviços que cruzam meia-noite. `dayOffset` é aplicado antes da conversão local→UTC; horário local inválido por DST é rejeitado. O cache é limitado a oito chaves `(versão, data)` e é invalidado quando muda a versão ativa.

Corridas simultâneas continuam distintas porque `ExpectedRunId` é um hash determinístico de versão, corrida programada e data de serviço. Padrões em conflito não são materializados espacialmente. Não há calendário de feriados implementado.

## Gate schedule-aware

O gate examina ontem, hoje e amanhã. Para cada probe, abre a janela cinco minutos antes do primeiro stop esperado e fecha após o último stop, acrescido de atraso confirmado e dez minutos de tolerância. Há corte rígido local às 01:00; sem grade aplicável o probe não é elegível. Assim, ausência de chamada fora da janela não significa indisponibilidade do provedor.

## Scanner adaptativo

O catálogo deriva probes em ambos os sentidos a partir da topologia publicada, avançando por ocorrências com stride configurável. Cada resposta pode conter várias linhas; somente partidas cujo external line id é o alvo contam como hit, criam pursuit ou alimentam o motor ferroviário. Off-target é telemetria, não evidência da linha.

O scheduler combina descoberta periódica, peso de cobertura, trem esperado, tempo desde último poll, transição terminal, penalidades por vazio/`NoService` e pursuits downstream. O pursuit é limitado, tem atraso inicial, retry, TTL e supressão por headway. O coordenador alterna descoberta, aquisição, tracked refresh e reacquisition; evidência fresca cancela reacquisition pendente. O canário aplica o orçamento final e evita starvation entre probes core e scanner.

## Binding entre trem observado e corrida esperada

A chave observada é `provider + data local + trainCode`. Um candidato deve ter linha/sentido compatíveis, probe coberto, origem presente e destino posterior na sequência. A busca usa uma janela ampla em torno do evento projetado e rejeita diferença temporal acima de 30 minutos. O score é a distância temporal absoluta até o stop programado; empate próximo torna o resultado `Ambiguous`, em vez de escolher arbitrariamente.

Estados: `Untrackable`, `NoCandidate`, `Ambiguous`, `Provisional`, `Confirmed` e `RejectedTemporal`. Uma primeira âncora pode formar vínculo provisório; evidências compatíveis adicionais confirmam. Os bindings guardam até oito âncoras, expiram em três horas e têm capacidade 1.024.

## Tracker e causalidade

O tracker exige `trainCode`, conserva até 16 observações por trem, marca stale após três minutos e expira após 15. A evolução do ETA (`Progressing`, `Unchanged`, reset etc.) é diagnóstico; não movimenta diretamente o ícone. Ao atingir capacidade, prefere expulsar o stale mais antigo e, sem candidato seguro, rejeita a inclusão.

`predictedAt = requestStartedAtUtc + minutesUntil`: usar o início da requisição evita deslocar a previsão pela latência HTTP. O estado é local ao processo e some em restart. Códigos de trem reutilizados são separados pela data local, mas uma reutilização anômala no mesmo dia continua sendo risco de identidade.

## Observabilidade

Há métricas/logs para hits alvo/off-target, descoberta, pursuit, backoff, aquisição, refresh, reacquisition, bindings provisórios/confirmados, atraso calculável e posições temporais/espaciais. Logs incluem identificadores operacionais; não devem carregar credenciais nem payloads completos em documentação.

