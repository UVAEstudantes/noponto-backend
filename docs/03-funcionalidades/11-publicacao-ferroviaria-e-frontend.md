# Publicação ferroviária e frontend

## Merge de publicação

O provider público parte do snapshot do motor realtime e mescla candidatos schedule-aware por `trainCode`. Candidatos stale, fora da janela operacional, sem mapeamento ou sem posição espacial são suprimidos. Quando baseline e schedule representam o mesmo trem, vence a evidência mais recente; empate preserva o baseline. A saída remove duplicidade por run.

Com schedule-first, um `ExpectedRun` pode aparecer antes de ser observado como `Scheduled`/`ScheduleOnly`. Após vínculo confirmado, recebe `trainCode`, atraso e passa a `Live` enquanto a evidência é fresca; depois fica `Estimated` até expirar. O `ExpectedRunId` estabiliza a identidade visual. A publicação informa partida programada/estimada, destino, próxima estação e respectivos segundos apenas quando calculáveis.

## Contrato HTTP

`GET /rail/vehicles/snapshot` aceita `linhaId` e `sentidoId`. Retorna `generatedAtUtc` e veículos nos estados `InSegment`, `Dwell`, `AwaitingDeparture` ou `TerminalHold`. Campos centrais:

- IDs de run, veículo, linha, sentido e versão do padrão;
- distância/instante de referência e distância/instante alvo;
- `positionSource`, `positionQuality`, `freshUntilUtc`, última evidência e flags de estimativa/clamp;
- dados de origem/partida, destino, plataforma, próxima estação e `operationalStatus`.

A geometria não vai no snapshot; o app chama `/veiculos/padrao-versao/{id}/geometria` e a armazena em cache por versão.

## Consumo no app

`useRailRealtime` só opera com modal trem selecionado, faz polling (default local de 5 s), pausa chamadas enquanto o app não está ativo e limpa veículos ao desabilitar. Cada item sem geometria válida é descartado. O modo demo só entra se a resposta estiver vazia, a flag explícita estiver ativa e houver padrão de demonstração; seu estado distribuído em produção não foi verificado.

`railDistanceAtTime` interpola somente `InSegment`, entre reference e target, e nunca depois de `freshUntilUtc`. `Dwell`, `AwaitingDeparture` e `TerminalHold` permanecem imóveis. A coordenada é obtida sobre a LineString preparada, por distância acumulada Haversine; o bearing usa pontos próximos ao longo da linha.

## Apresentação e identidade

O frontend traduz `ScheduledEstimated/ScheduleOnly` em **Programado**, fontes ancoradas em grade em **Estimado** e realtime estimado em **Ao vivo**. O texto “Ao vivo” deve ser entendido como ETA recente, não GPS. Eventos de parada localizam o item por `railVehicleId` ou `expectedRunId`; o identificador visual usa o prefixo `rail:` para não colidir com o runtime rodoviário.

## Falhas visíveis

- erro/ausência de snapshot vira lista vazia, sem estado de erro dedicado nesse hook;
- erro de geometria remove o trem do mapa;
- cache de promessa mantém inclusive resultado nulo até reinício/limpeza;
- polling é HTTP, não SignalR;
- o cliente congela no limite de freshness, mas o próximo snapshot normalmente remove o item stale;
- relógio incorreto do dispositivo afeta interpolação e contagens regressivas.

