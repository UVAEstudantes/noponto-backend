# D2 — núcleo de replay operacional offline — 18/10/2026

**C0 permanece NO-GO, conforme D1.** Este incremento verifica engenharia com
fixtures; não executa replay real do bundle, não treina modelo e não produz
métricas de acurácia. A comparação científica continua bloqueada por evidência.

## Estado reutilizado

Consultados AGENTS.md, cabeçalho/entradas recentes/trechos ETA do HISTORICO,
auditoria D0, relatório D1 e plano/diário Shadow de 17/10. Backend em
`90f7be72e4b55198bc721f4f104a58f37def3f2d`, com alterações locais preservadas.
Não refeito inventário de repositórios ou de viagens.

Já existiam projeção operacional `EventosParadaService.ProjectRoadEvents`,
calculador `EstimateRoadSeconds`, PhysicalEta, exportadores, avaliador Shadow
e laboratório sintético físico/ExtraTrees. O laboratório não é replay do ETA
operacional do endpoint. Não foi encontrado runner dedicado desse replay nas
ferramentas consultadas; não se transportaram implementações de outras branches.

Reutilizado `tools/eta_ml/outputs/snapshot-3b2b-v2-real-01`: manifesto SHA-256
`47ac2c28a1a962b236bf408f61baeb6e0e3413d3cedb28c872c3839b3fc37550`, igual a D1;
**24/24 arquivos conferidos** sem executar scripts embarcados. Inspecionados
somente nomes dos campos necessários, sem divulgar registros privados. As
contagens/limitações de D1 permanecem a referência, sem nova auditoria da coorte.
Integridade local não certifica captura, regime efetivo ou ground truth.

O CSV canônico discutido em D0 não contém todos os insumos de replay. O bundle
bruto conserva `ComprimentoRotaMetros` no GPS e `ComprimentoMetros` nas versões;
isso permite estudar a recuperação estrutural sem inventar comprimento. O GPS
do bundle não conserva `EtaProximaParadaSegundos`/`EtaConfianca` originais do DTO.
Não se pode presumir que fossem nulos nem substituir sua precedência por um
fallback. PublicEta Shadow também não supre essa falta.

## Incremento limitado

`NoPonto/5-Testes/OperationalEtaReplay.cs` implementa um núcleo interno de
replay no suporte de testes existente, sem DI ou consumidor produtivo. Recebe
snapshots em memória e devolve previsões; sua execução nesta etapa ocorre
exclusivamente pela suíte de fixtures. **Ainda não há CLI para replay de CSV ou
leitor de sidecar real.** Esse é um incremento anterior ao runner de arquivos.

- Chave preservada: observação, viagem, versão, ocorrência-alvo, volta e tGPS.
  Linha/sentido/padrão são conferidos entre DTO e snapshot estrutural. O vínculo
  da viagem/observação à captura deve ser comprovado pelo futuro leitor: o DTO
  público sozinho não o demonstra.
- Relógio explícito, sem UtcNow; cenário primário `PredictionAt = tGPS`.
  Políticas simuladas `operational-legacy-frozen-v1` e
  `operational-physical-captured-frozen-v1`, sem inferir flags de produção.
- Invoca a projeção real do endpoint, sem reimplementar cálculo ou seleção de
  ocorrência. Legado conserva ETA contínuo do calculador e arredondamento
  exibido; físico capturado conserva precisão temporal de 100 ns do endpoint.
- Ausência do DTO original ou snapshot estrutural gera linha indisponível com
  motivo. Disponibilidade declarada pelo chamador **não é auditoria**. DTO
  comprovadamente com ETA nulo continua distinto de DTO original desconhecido.
- Preserva perdas, recusa chaves repetidas e ordena a saída de modo estável.
  Não muda o alvo para conseguir previsão. O físico deste núcleo só admite o
  alvo capturado; outra parada exige resolver/distância causal versionada,
  ainda não implementados no harness. Janela física congelada em 30 s.
- Não recebe labels, não ajusta modelo e não calcula erros/métricas. Observado,
  interpolado e estimado são categorias de origem; auditoria é condição
  adicional. Não há promoção de registros não auditados.

Fontes operacionais inalteradas usadas nesta revisão (SHA-256):

| Fonte | Hash |
| --- | --- |
| EventosParadaService.cs | `3560c908b6b0d5aa2e68574fa952182039f723808926772112d1473384d4b7f7` |
| PhysicalEta.cs | `f6f5afc28c2f435b7edc2e64fbb9548d91c09d3f458e1d68636c089201e7501b` |

Mudanças futuras de política/código devem registrar novos hashes e revisão;
esses nomes de política não congelam automaticamente uma versão futura.

## Verificação

Suíte nova `OperationalEtaReplayTests`, incluída na allowlist offline existente
de `.ci`. Fixtures com IDs sintéticos fixos, sem arquivos privados, rede, banco,
serviços, migrations ou modelo. Comando de reprodução da seleção completa:

```powershell
$filter = py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj --no-restore --filter $filter --logger 'trx;LogFileName=offline.trx' --results-directory NoPonto/lab-output/eta-replay-20261018-final --verbosity quiet
py -3.13 -B .ci/offline_tests.py verify NoPonto/lab-output/eta-replay-20261018-final/offline.trx
```

Tolerância congelada das expectativas numéricas: nove casas decimais nos
segundos contínuos e 1 µs no instante esperado; arredondamento exibido e
comparação diferencial com a projeção usam igualdade exata. Isso preserva
`239.99999999999997` do fallback, sem alterar a fórmula para obter 240 exato.

Evidências por comportamento:

| Comportamento | Teste exato |
| --- | --- |
| Precedência DTO, média >2, instantânea >2, fallback 15; sem aplicar limite físico ao legado | `LegacyPrecedence_ReplaysDtoMeanInstantaneousAndFallback` |
| Contínuo e arredondamento midpoint-to-even | `ContinuousEta_PreservesFractionAndEndpointRounding` |
| Consulta posterior: sem compensação no legado, com compensação no físico | `LegacyQueryClock_PreservesEndpointSemanticsWithoutAgeCompensation`; `PhysicalCapture_SubtractsQueryAgeAndKeepsArrivalInstant` |
| Limite 30/31 s; outro alvo sem resolver | `PhysicalCapture_MaxAgeBoundaryKeepsMissingRow`; `PhysicalCapture_AnotherTargetRequiresResolverAndRemainsUnavailable` |
| Insumos desconhecidos e exclusões explícitas | `MissingSnapshot_DoesNotInventFallback`; `InvalidOrUnavailableInput_PreservesExplicitExclusion` |
| Repetições da parada e chave do alvo | `RepeatedStop_SelectsFirstFutureOccurrenceWithoutChangingPairKey` |
| Determinismo serializado, denominador e duplicatas | `Batch_ShuffleProducesIdenticalSerializedPredictionsAndRejectsDuplicates` |
| Equivalência com o endpoint nas duas políticas | `Replay_DifferentialMatchesActualEndpointProjection` |

Resultado final: **641 aprovados, zero falhas/ignorados, em 27 classes**,
incluindo **28/28 casos novos de replay**. TRX real aceito por
`.ci/offline_tests.py verify`, com todas as classes presentes e nenhuma fora
da seleção. Os seis testes do seletor/verificador passaram via
`py -3.13 -B -m unittest discover -s .ci -p test_offline_tests.py -v`.
Build sem restore: zero erros e cinco warnings anteriores (CS8981, CS7022,
xUnit2031). A seleção existente inclui fixtures HTTP efêmeras em loopback;
nenhum serviço externo ou banco foi conectado. O núcleo novo não abre sockets.
`git diff --check` aprovado.

Duas rodadas anteriores falharam apenas nas novas expectativas/fixtures:
primeiro, sete casos por literais int incompatíveis com double? no xUnit;
depois, um por exigir igualdade exata de 240 com seu resultado IEEE-754.
Corrigidos literais e congelada a tolerância acima; nenhuma fórmula produtiva
foi alterada. Essas rodadas não são prova RED de um bug de produção.
O sandbox inicialmente impediu iniciar o Python instalado; seletor/verificador
e seus testes foram executados fora dele mediante aprovação da ferramenta.

Não executados: replay de registros reais, suites conectadas PostgreSQL/Redis,
geografia PostGIS, qualificação real, treino/inferência ExtraTrees ou laboratório
sintético completo. Não havia motivo para repetir essas etapas neste incremento.

## Evidência pendente e próxima etapa

1. Receber o pacote integral sob custódia descrito na seção 5 de D1: suporte
   contemporâneo de fronteiras, proteção/completude e regime durante as viagens;
   pares GPS e origem da referência de chegada. Sem retenção desse suporte, não
   certificar retroativamente esta coorte. C0 não mudou.
2. Para C2, localizar captura causal original do DTO e vinculá-la ao bundle por
   chave/hash, junto do snapshot estrutural versionado. Se não existir, registrar
   impossibilidade de replay fiel para essas linhas; um cenário sem override DTO
   seria outro estimador. Não utilizar ETA/label futuro como substituto.
3. Com esses insumos, acrescentar o menor leitor offline/sidecar a este núcleo,
   congelando proveniência, hashes e política antes de TEST. Reutilizar exportador
   3G.2/qualificador/avaliadores já existentes, sem contornar os gates.
4. Só após C0: cortes por viagem completa com purge, origem dos labels separada,
   análise de sensibilidade das interpolações e qualificação C1. Parear operacional
   e ExtraTrees nas mesmas chaves e instantes; registrar cobertura individual e
   interseção sem descartar perdas. Um dia/16 viagens brutas não satisfaz o gate
   oficial. Não produzido CSV comparativo ou modelo oficial/exploratório real.

Sem API/worker operacional iniciado, alteração de contratos/flags/frontend,
treino, pickle, banco/Redis, Docker, migrations, deploy, push ou operação destrutiva.
