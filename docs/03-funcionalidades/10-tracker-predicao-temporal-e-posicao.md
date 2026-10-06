# Tracker, predição temporal e posição ferroviária

## De ETA em estação a âncora

Uma observação normalizada fornece origem, destino consultado, linha, sentido, `trainCode` e minutos até o evento. A topologia deve resolver a evidência para exatamente um padrão compatível e uma ocorrência. A âncora representa uma passagem prevista na estação; nova evidência da mesma ocorrência substitui a anterior. Cada run mantém no máximo 32 âncoras.

## Perfil temporal

O perfil nominal ordena ocorrências e horários. Horários após meia-noite recebem acréscimo de um dia e a sequência precisa ser estritamente crescente. Para uma passagem prevista em uma ocorrência, o preditor desloca o perfil no tempo. Entre duas ocorrências, usa interpolação linear:

```text
fração = (instante - tempoAnterior) / (tempoSeguinte - tempoAnterior)
distância = distânciaAnterior + clamp(fração, 0, 1) × Δdistância
```

Não há ML nesse cálculo. O motor base ainda carrega um perfil estático empacotado de Santa Cruz; o runtime schedule-aware fornece projeções da grade ativa. Essa duplicidade é dívida técnica e limita generalização sem grade/perfil válido.

## Máquina de posição

- antes da primeira ocorrência: `AwaitingDeparture`, imóvel na origem;
- até 30 s após uma âncora passada: `Dwell`, imóvel;
- entre âncoras: `InSegment`, interpolação temporal na distância longitudinal;
- após a última: `TerminalHold`, imóvel no terminal;
- sem resolução ou após esgotar fallback: `Unresolved`/não publicável;
- reversão de padrão só é aceita após `TerminalHold` e confirmação por uma segunda âncora crescente; o run anterior termina e outro é criado.

A distância nunca retrocede: é limitada pelo valor anterior e pelo intervalo `[0, comprimento]`. Múltiplos satélites produzem `MultiSatelliteAnchored`; uma evidência recente produz `RealtimeAnchored`. Uma única âncora pode usar o perfil temporal, com a mesma monotonicidade.

## Fallback e freshness

Sem nova evidência, o estimador pode projetar por curto horizonte em direção ao alvo conhecido. Depois de `freshUntil + 180 s`, a posição deixa de ser oferecida e passa a `StalePrediction`/`Unknown`. A publicação schedule-aware também elimina candidato stale, sem mapeamento ou sem projeção espacial. Nunca se deve extrapolar indefinidamente.

## Projeção por grade

O binding confirmado calcula atraso entre evento observado e stop esperado e o aplica aos stops do `ExpectedRun`. A posição temporal encontra stop anterior/próximo; a espacial resolve suas ocorrências no padrão e interpola pelo intervalo ajustado. `EXACT` e `SUBSET_COMPATIBLE` são aceitos; ambiguidade, conflito, ocorrência repetida sem par único ou geometria ausente bloqueiam a projeção.

## Precisão interpretável

`RealtimeEstimated` continua sendo estimativa; `ScheduleEstimated` combina atraso observado e grade; `ScheduledEstimated` é apenas horário programado. Nenhuma fonte é GPS. A precisão depende da qualidade do ETA, do binding, da grade, do padrão estrutural e da geometria. Paradas expressas, short starts, alterações operacionais e atrasos não uniformes podem divergir do modelo linear.

