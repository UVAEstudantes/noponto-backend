# D5 — Captura causal opcional pelo Shadow existente

Data: 18/10/2026. A integração usa o DTO operacional final e o fluxo Shadow existente; não altera fórmulas, DTO/API público, persistência GPS, banco ou infraestrutura.

## Instrumentação e dados temporais

Em `GpsPollingService`, o witness é preparado imediatamente após `PhysicalEta.Apply`, que recebe o relógio `physicalNow` já existente. O enriquecimento interno leva o witness até `ConfirmarPosicaoAsync`; apenas após o CAS aceito o hook chama `HistoricalEtaShadow.TryCapture`, que o associa à observação canônica e ao estado operacional confirmado (viagem, versão, volta e ocorrência/parada-alvo). Rejeição e falha do CAS não produzem observação Shadow.

A opção nova `HistoricalEtaShadow:OperationalDtoCaptureEnabled` é `bool`, desligada por padrão e subordinada a `HistoricalEtaShadow:Enabled`. Com a opção desligada, o enrichment retorna sem witness e o exportador continua omitindo a propriedade nula; a saída JSON anterior não ganha campos.

Para cálculo físico, `CalculatedAt` registra o instante de referência já passado ao cálculo. Para o ETA legado, o cliente não expõe instante de emissão, portanto o campo fica nulo. `FeatureCutoffUtc` permanece nulo em ambos os caminhos: o ETA legado não informa corte e a fila de velocidades guarda valores sem timestamp. Não se infere corte a partir de tGPS, recebimento ou captura. `TimestampGps`, `RecebidoEmUtc`, `CapturedAt` e `RecordedAt` preservam seus significados existentes. A extensão privada reutiliza o contrato D4 e a projeção por allowlist do `PosicaoVeiculoDto`; valores não finitos são representados explicitamente e o registro continua sujeito às quotas existentes.

Uma captura física pode ser decodificada pelo reader D4, mas `ToReplay` a bloqueia enquanto o corte temporal não existir. A captura legada também não é elegível porque o instante de cálculo não é fornecido pelo cliente. Isso é intencional: guardar o DTO não comprova por si só que a entrada causal está íntegra.

## Isolamento e validação

As novas variantes da fixture `HistoricalEtaGtfsPollingTests` executam o polling real com fonte Protobuf sintética, handlers HTTP locais, cache/repositórios em memória e exportador com diretório temporário `Fixture=true`. Cobrem flag desligada, ETA legado e físico, extensão lida pelo contrato D4, relógios ausentes, limite de tamanho do registro, falha de diretório/exportação e CAS rejeitado. Exportação falha ou item acima do orçamento não interrompe aceitação GPS, atualização da viagem ou publicação de telemetria da fixture.

- D5 direcionado: `FullyQualifiedName~HistoricalEtaGtfsPollingTests&DisplayName~d5_`: 24 aprovados, 0 falhas, 0 ignorados.
- Classe de polling completa: 76 aprovados, 0 falhas, 0 ignorados.
- Seleção oficial `.ci`: 705 aprovados, 0 falhas, 0 ignorados. O verificador confirmou todas as 27 classes no TRX `NoPonto/lab-output/eta-capture-d5-20261018-ci-repeat/d5-offline-repeat.trx`.
- `git diff --check`: execução final registrada após atualização deste relatório/histórico.
- Uma execução inicial da seleção `.ci` teve 704 aprovados e um timeout numa fixture preexistente de `export_failure`; a repetição isolada passou e a seleção completa seguinte também passou. Rodadas intermediárias D5 apontaram configuração incompleta da fixture (flag aplicada só ao exportador e worker não iniciado); a fixture foi alinhada e o teste direcionado final passou.

Nenhuma fixture acessa rede externa, PostgreSQL ou Redis. Docker, migrations, serviço real e treinamento não foram usados.

## Pendências experimentais e científicas

A opção continua desativada e não foi habilitada em configuração de desenvolvimento/produção. Antes de qualquer ensaio controlado será necessário medir tamanho de linha/quota e retenção, confirmar acesso ao diretório privado e revisar política de dados. Para replay causal ainda faltam timestamps reais do ETA legado e do corte de features; no caminho físico, falta timestamp para cada amostra que compõe `VelocidadeMedia` para provar o corte. A flag atual não converte esses casos em replay válido.

D1/C0 segue NO-GO: falta DTO causal histórico, pacote integral com proveniência e fronteiras de viagem, vínculo auditável entre previsão, viagem/versão/parada, e ground truth independente de chegada. Labels interpolados ou estimados não suprem essa evidência. Não houve replay histórico nem métrica científica.
