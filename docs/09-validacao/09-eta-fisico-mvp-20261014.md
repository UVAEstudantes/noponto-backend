# ETA físico MVP local — 14/10/2026

Entrega funcional local, OFF por padrão. Reutiliza o matching atual, sua distância longitudinal na geometria versionada, o calculador puro `EtaV2LongitudinalSpeedV0.Predict` (sem ativar EtaV2) e o endpoint existente `GET /paradas/{id}/eventos`. Não importa código GPS antigo nem altera cursor, cruzamentos, TTL, esquema ou treino.

## Implementação e demonstração

`PhysicalEta.cs` exige identidade estrutural completa, próxima ocorrência explícita, distância de rota válida, matching temporal plausível, predecessor na mesma versão, bearing confiável, avanço de fração e par temporal até 180s. Fração regressiva, wrap sem par dirigido inequívoco e primeira observação sem predecessor ficam indisponíveis. Média causal válida tem precedência à instantânea (1–160 km/h), como no baseline histórico Python. Instantânea zero/inválida impede herdar média móvel para prever partida. Sem fallback de 15 km/h ou fallback aprendido de TRAIN.

`GpsPollingService`: com a flag ON dispensa inferência pública XGBoost e calcula antes do CAS GPS, nos DTOs já enriquecidos, sem IO ou consulta adicional. Com OFF mantém o caminho anterior. Não modifica decisões/identidades operacionais ou a seleção de amostras ML. Os campos existentes EtaProximaParadaSegundos/EtaConfianca conservam a previsão e seu marcador `physical_causal:`; não adiciona campo persistido nem endpoint.

`EventosParadaService`: com ON consome somente previsão física marcada, mesma ocorrência/versão/linha/sentido/padrão/código e GPS atual. Não reutiliza ETA legado para completar uma previsão ausente. EstimatedAt é ancorado em TimestampGps + ETA; SecondsUntilEvent desconta o tempo já decorrido. Distância válida é a longitudinal capturada no matching, não a distância direta nem fração × comprimento aproximada. Usa Source=PhysicalCausal e Quality=estimativa_fisica/gps_desatualizado/previsao_indisponivel nos campos existentes.

Exemplo **sintético executado**: distância restante 1.000m, média causal 36 km/h, instantânea 20 km/h, GPS 2026-10-14T12:00:00Z → ETA 100s (1,67min). Consulta cinco segundos depois → 95s, EstimatedAt=12:01:40Z. Velocidade zero, versão divergente, regressão, falta de predecessor e GPS antigo → nenhum ETA numérico. Não emite aguardando_saida: parada/velocidade zero não comprovam uma partida agendada.

Escopo mínimo: próxima ocorrência do matching observacional. Outras ocorrências solicitadas no endpoint permanecem sem previsão numérica no modo físico; não extrapola uma velocidade a destinos não resolvidos. Não afirma viagem auditada ou precisão contra chegada. Sem novas consultas geométricas, não prevê wraps implícitos/posição ambígua. Baseline público observacional só pode ser comparado ao Shadow quando observação, alvo, versão e volta forem compatíveis; o adapter histórico existente já recusa comparação com alvo divergente.

## Reuso confirmado

- Backend MVP `feature/eta-ml-mvp`: HEAD 5a13e523102577b7880109cbdca0cf20e1a55598; fechamento 06 consultado. Baseline Python histórico usa a mesma fórmula, mas possui fallback estatístico apenas TRAIN: ele não foi promovido a fallback público.
- ML `feature/eta-history-mvp`: HEAD 6c0f7b5313f1d953105d0211f08df6cdaf098e48. ExtraTrees/pipeline/gates e FastAPI testados sem alterações. Modelo real qualificado continua ausente; 16 viagens de um dia e zero amostras certificadas não autorizam treino real. Fits dos testes são sintéticos.
- Shadow histórico existente em `noponto-backend-eta-shadow-gps-current`: testes do adapter/fila/falhas HTTP e polling GTFS-RT reutilizados. **Não foi transportado para fix/gps nesta tarefa**; demonstração isolada evita misturar seu exportador e diagnóstico anterior. Sem modelo: ready/inferência 503, health 200; nenhuma substituição pública pelo Shadow. Comparação de erro contra chegadas depende de labels canônicos auditados, não destas fixtures.

## Visibilidade no terminal

Condições locais identificadas, sem atribuir causa ao caso real:

1. `GtfsRealtimeGps.cs:85–103` aceita velocidade zero; exige timestamp válido/atual e identidade/posição válidas. Fonte pode omitir observações de veículos parados; o código não comprova disponibilidade do provedor no terminal observado.
2. Deduplicação recusa timestamps antigos/iguais e não renova TTL nesses casos. Defaults: ativo 40s, recente/índice da linha 180s. `VeiculosLinhaRuntimeReader` usa recente como SemSinal quando ativo falta; não filtra velocidade zero ou estado Finalizada.
3. `GpsEnriquecimentoService` só resolve rota com bearing disponível; ausência/incompatibilidade pode produzir PadraoVersaoId nulo. Isso não apaga o veículo no backend por ser parado.
4. Frontend `services/veiculosMapa.ts:173–180` exclui veículos sem versão pertencente ao conjunto estrutural visível, usado por `hooks/useMobilidadeRio.ts:325–334`. `mapScript.ts:1254–1263` remove marcadores ausentes de visibleKeys no snapshot exibido. Velocidade <1 em `mapScript.ts:320` apenas suspende dead-reckoning, não remove marcador.
5. `ProjectRoadEvents` requer versão/progresso e ocorrência adiante; sem matching compatível, o veículo não vira candidato de chegada. Radar usa `/paradas/{id}/eventos`, filtra por contexto e prefere estimatedAt, com fallback temporal a secondsUntilEvent (`radarParada.ts:69–76`).

Não identificado bug demonstrável no terminal real; nenhum filtro/TTL alterado. Informação mínima para atribuir causa, se já disponível: verificar na resposta local gravada se o veículo continua presente, timestamp muda e PadraoVersaoId é nulo/fora do conjunto visível. Sem nova coleta/produção nesta tarefa.

## Testes realmente executados

- **250 C# aprovados, zero falhos/ignorados**, filtro explícito offline: PhysicalEtaMvpTests, EventosParadaProjectionTests, GpsEnriquecimentoServiceTests, GtfsRealtimeGpsTests, GpsEtaClientTests, GpsPollingCadenciaTests, GpsPollingFontesTests, EtaV2FoundationTests. Inclui **16 casos novos**, enriquecedor real com matching fake de duas observações e **HTTP real Kestrel em loopback/porta efêmera**, controller original com serviço-fixture. Essa API de teste não executa Program.cs, polling, migrations ou conexões operacionais. Não é integração PostGIS do matching.
- **64 C# aprovados** na worktree Shadow: HistoricalEtaShadowTests e HistoricalEtaGtfsPollingTests, dependências em memória/HTTP simulado, sem banco.
- **41 Python aprovados**: serviço TestClient, pipeline ExtraTrees, qualificação, ciclo offline, contrato, política e hashes de migração. Primeira chamada falhou com sete erros de importação por PYTHONPATH ausente; repetição com tools/eta_ml no path aprovada. Nenhuma dependência/modelo/dataset modificados.
- Ressalva de execução: dois filtros C# iniciais incluíram indevidamente quatro VeiculosLinhaRuntimeReaderTests, que usaram Redis **local existente localhost:6380 (transporte_redis)**, sem comprovar previamente descartabilidade. Esses testes escrevem somente chaves TEST com UUID e as removem em DisposeAsync; passaram. Não contabilizados como integração descartável autorizada. Erro reconhecido; validação final excluiu essa classe. Sem acesso ao homeserver/produção e sem PostgreSQL.
- TRX em `D:/repositorio_github/NoPonto/eta-mvp-validation-20261014`: physical-eta-verified.trx (final250), shadow-reused.trx (64); execuções intermediárias preservadas. Cinco warnings C# preexistentes. Não executados matching PostGIS em ambiente descartável, modelo real ou avaliação contra chegadas reais.

## Habilitação futura local e isolamento Git

Somente em ambiente local isolado preparado: `PhysicalEta__Enabled=true`, `PhysicalEta__MaxAgeSeconds=30` (limite 1–300). Manter ML__ETA__Enabled=false, EtaV2__Enabled=false, EtaV2__ShadowEnabled=false; HistoricalEtaShadow também OFF até homologação específica na candidata que o contém. Não executar a API completa com as configurações operacionais locais existentes; o teste HTTP acima já demonstra o contrato sem startup produtivo. Sem override de appsettings/Compose nesta entrega. OFF volta ao comportamento anterior, incluindo o fallback legado já existente.

Inventário inicial: fix/gps, 17 rastreados modificados, nenhum staged, demais arquivos novos preservados; hashes em `D:/repositorio_github/NoPonto/eta-mvp-task-initial-state-20261014.json`.

Arquivos preexistentes que receberam **somente acréscimos/adaptações desta tarefa**: GpsPollingService.cs (hook/opção e bypass do cliente público somente ON), Program.cs (binding PhysicalEta), HISTORICO.md (append). Arquivo anteriormente limpo alterado: EventosParadaService.cs. Novos: PhysicalEta.cs, PhysicalEtaMvpTests.cs e este relatório. Nenhum arquivo preexistente foi restaurado/removido, nenhuma branch/worktree alterada, nenhum commit/push/merge, build Docker, deploy ou configuração produtiva modificada.

Conferência final de preservação: **38 arquivos preexistentes fora do conjunto autorizado permanecem byte a byte iguais**, pelos SHA-256 iniciais; zero alterações inesperadas. Resultado em `D:/repositorio_github/NoPonto/eta-mvp-validation-20261014/preservation.json`. `git diff --check` aprovado. Repositório ML permanece com apenas lab-output/ e outputs/ não rastreados, já preexistentes.
