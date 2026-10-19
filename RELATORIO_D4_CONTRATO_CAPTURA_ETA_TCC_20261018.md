# D4 — Contrato offline para captura causal do ETA operacional

Data: 18/10/2026. Escopo limitado ao suporte interno de laboratório. Nenhum DTO/API público, serviço produtivo, hook ativo, fila ou exportador foi alterado.

## Ponto de origem e estado da infraestrutura

Em `GpsPollingService`, o DTO final é montado em `todosProcessados` depois da projeção legada de ETA e da aplicação de `PhysicalEta.Apply` (linhas 475–486). Ele segue para gravação/CAS em `ConfirmarLoteDetalhadoAsync`. O hook existente `_historicalEtaShadow?.TryCapture(enriquecimento, viagem)` ocorre depois do CAS aceito (linha 1180); `HistoricalEtaAdapter` copia `enriquecimento.Posicao` para `HistoricalEtaObservation.Gps`. Esse é o ponto em que o DTO operacional final está disponível e vinculado à decisão aceita.

O Shadow atual preserva a cópia do DTO em memória, a chave da observação (SHA-256 textual), viagem, volta, ocorrência-alvo, `CapturedAt` e `PublicEta` filtrado. O JSONL existente registra `TimestampGps`, `CapturedAt` e `RecordedAt`, mais identidades e estimadores Shadow. Não preserva o horário de cálculo nem o DTO original. `PublicEta` não substitui o ETA/confiança do DTO. O DTO completo contém dados privados e não deve ser serializado integralmente.

## Contrato preparado

`OperationalEtaCaptureContract` é uma projeção de laboratório sobre o `PosicaoVeiculoDto` real, com allowlist por `nameof`, sem manter uma segunda definição de campos/tipos. A extensão opcional `operational_dto` contém schema, instante de cálculo, corte temporal das features, recebimento original, política e idade máxima configurada, prova interna de captura física recente e o DTO projetado. A projeção retém linha/código, GPS, IDs de linha/sentido/padrão/versão/alvo, ETA e confiança originais, posição/comprimento/distância de rota, velocidades, topologia e status. Campos selecionados são obrigatórios na desserialização: ausência não vira valor padrão; `null` explícito permanece `null`.

A linha externa continua sendo `HistoricalEtaOfflineRecord` v2: nela já existem observação, viagem, volta, versão, alvo/parada, linha/sentido/padrão, modalidade, `TimestampGps`, `CapturedAt` e `RecordedAt`, além dos valores públicos/Shadow. O contrato não lê esses estimadores como original. A identidade canônica permanece texto SHA-256, conferida por `TelemetriaMlContrato.ObservacaoId`; não há conversão para GUID. O produtor/custodiante ainda terá de atestar origem e integridade do snapshot estrutural por manifesto/hash.

Os estados são distintos: extensão ausente (`not_captured`), DTO explicitamente indisponível (`dto_unavailable`), DTO presente com ETA nulo (`prediction_unavailable`), previsão finita não negativa inclusive zero (`calculated`) e valor negativo/não finito (`invalid_prediction`). Valores inválidos são preservados no round-trip e rejeitados como entrada de replay. Um DTO incompleto não recebe zeros inventados. `CalculatedAt` e `FeatureCutoffUtc` nulos são serializáveis, mas bloqueiam replay; quando presentes, o GPS deve anteceder recebimento, corte e cálculo, e cálculo deve anteceder captura e gravação. A previsão é avaliada no instante real de cálculo, sem usar chegada futura.

`ToReplay` valida hash, identidade pareada, parada/alvo/versão, política suportada e ordem causal antes de montar a entrada existente do D2. A idade máxima 90 s não é normalizada: o núcleo D2 implementa somente a política física congelada em 30 s. A origem local privada usada para validar a identidade não é exportada.

## Validação offline

As fixtures sintéticas passam pelo adapter e exportador Shadow existentes (`Fixture=true`, relógio fixo, diretório temporário exclusivo), acrescentam a extensão em memória, fazem round-trip estrito e alimentam o replay D2. Cobrem previsão presente, zero, nula, extensão/DTO ausentes, campos ausentes, incompletude, hash e pareamento incompatíveis, estimadores concorrentes, timestamps não causais, política física e valor original inválido. Não há leitura de dados históricos nem captura real.

- Direcionado: `dotnet test NoPonto/NoPonto.csproj --no-restore --filter "FullyQualifiedName~NoPonto.Tests.OperationalEtaReplayTests" ...`: 68 aprovados, 0 falhas, 0 ignorados.
- Seleção completa conforme `.ci/offline_tests.py filter`: 681 aprovados, 0 falhas, 0 ignorados, 27 classes. O verificador existente aceitou `NoPonto/lab-output/eta-replay-d4-20261018-ci/offline.trx` e confirmou todas as classes presentes.
- `git diff --check`: aprovado. Build sem erros; 5 warnings preexistentes (dois nomes de migration em minúsculas, ponto de entrada gerado e dois avisos xUnit2031).
- Uma execução focada intermediária teve 31 falhas porque a fixture escolhia também a linha de resumo do mesmo exportador; o seletor passou a localizar a linha de observação pela identidade e a rodada final passou. A primeira compilação também revelou um argumento nullable incompatível na fixture, corrigido com o valor sintético conhecido. Nenhuma falha permaneceu nas rodadas finais.
- Primeira tentativa de executar o verificador Python no sandbox foi bloqueada por acesso negado ao executável Python; a mesma verificação foi executada com a permissão de ferramenta e passou. Nenhuma falha de teste D4 restou.

## Integração ainda necessária e limites científicos

O incremento não liga captura ativa. O menor passo futuro é, mediante autorização para tocar o caminho de GPS/ETA/Shadow, criar no produtor um witness imutável junto à montagem do DTO final: referência/estado do mesmo DTO aceito, instante de cálculo, corte efetivo das features, política e idade realmente aplicada. Depois do CAS aceito, o hook Shadow deve transportar esse witness junto à observação e o record/exporter existente deve serializar a extensão opcional, respeitando limites de bytes, fila, retenção, identidade e compatibilidade de linhas antigas. Sem witness do produtor, os campos de relógio preenchidos em fixture não provam causalidade real. O contrato e os testes atuais não executam esse passo.

D1/C0 continua NO-GO. Ainda faltam DTO causal original histórico e vínculo auditável com viagem/versão/parada/instante; pacote histórico integral com proveniência e fronteiras de viagem; e ground truth independente de chegada com origem e incerteza preservadas. Rótulos interpolados/estimados não servem como ground truth independente. Portanto não houve replay real, avaliação pareada nem métrica de acurácia científica; a captura futura não certifica retroativamente previsões antigas.
