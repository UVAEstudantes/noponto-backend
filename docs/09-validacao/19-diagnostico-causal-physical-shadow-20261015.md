# Motivo causal do PhysicalEta no Shadow

## Escopo e decisão de implementação

Base: main `a11984f85cbc82f47b627df6a0515d8c60890e06` (run124). Worktree nova `noponto-backend-physical-shadow-diagnostics`, branch `feat/physical-shadow-causal-diagnostics`. Worktrees anteriores, inclusive a investigação local sem patch funcional, preservadas. Reutilizados somente os testes dessa investigação, por patch dos dois arquivos de testes.

É seguro obter o diagnóstico no próprio Apply: `ResultadoEnriquecimentoGps` já é efêmero, anterior ao CAS e externo ao DTO público/persistido. `PhysicalEta.Apply` agora usa **uma única decisão** para os gates e o motivo. A conjunção, fórmula, faixa de velocidade, idades e resultados públicos permanecem os da release124. Não há um segundo classificador posterior repetindo os predicados.

Quando o opt-in Shadow está ativo, o resultado guarda `PhysicalEtaCalculation`, imutável, com enum de motivo e referência ao DTO exato produzido pelo Apply. O hook pós-CAS/decisão entrega o enriquecimento completo ao Shadow. Antes de copiar o DTO, confere a referência do cálculo; dados ausentes, objeto substituído ou enum desconhecido geram `not_recorded`. Não se reconstrói uma suposta causa a partir do DTO posterior ou do relógio do worker.

Nenhuma causa é atribuída retroativamente aos cinco registros reais. Conforme operador, suas idades24,43–45,44s e ausência de `expired` afastam expiração por90s para esses casos; faltam os resultados causais que esta PR passa a capturar. Nenhuma precisão operacional foi demonstrada.

## Opt-in, métricas e JSONL

Reutiliza `HistoricalEtaShadow:Enabled`, OFF por padrão. Nenhuma nova flag nem alteração nos quatro overrides, preflight, deploy ou rollback. Shadow OFF: não gera testemunho, não conta motivos nem exporta diagnóstico. PhysicalEta OFF + Shadow ON: apenas registra `physical_disabled`, preservando o DTO legado sem limpar/substituir ETA.

Para tentativas estruturalmente elegíveis no hook pós-CAS:

- `physical.apply.<motivo>`: resultado original do Apply; soma acompanha `eligible`, não o total de GPS, registros gravados ou previsões válidas.
- `physical.capture.available`, `.unavailable`, `.proof_rejected`: separa valor aceito no adapter de Apply disponível mas prova de captura recusada. O código existente de CapturedSpeed e seus limites não foi alterado.
- Os contadores entram no `HistoricalEtaShadowMetrics` já existente, com frequência/limites atuais, sem log por veículo. Tentativas com fila cheia, circuito aberto ou parada do worker também contam; perdas e persistência continuam nas métricas próprias.
- Registros efetivamente exportados podem conter o campo opcional **`physical_apply_reason`**, limitado ao enum. É adição ao artefato privado JSONL v2, sem contrato HTTP/Redis/schema público. O avaliador aceita campos adicionais; arquivos anteriores permanecem legíveis. O campo não muda `physical_status` nem transforma diferença entre previsões em erro contra chegada.

`available` com `physical_unavailable` não é reclassificado: significa que Apply calculou o ETA, mas o adapter recusou sua prova posteriormente. `not_recorded` significa ausência de testemunho válido, nunca uma causa presumida de indisponibilidade física. `physical_version` continua sendo identificador do algoritmo, não prova de sucesso.

## Motivos e precedência

O motivo é o primeiro gate relevante que falha na decisão única, não uma lista de todas as falhas nem prova de causa externa exclusiva. Com matching não plausível e histórico físico incompleto, registra ausência do predecessor; com histórico presente, preserva motivos temporais/bearing explícitos antes do motivo genérico de plausibilidade. São apenas acessos a dados já calculados, sem novos critérios de aceitação.

| Grupo | Códigos |
|---|---|
| Resultado/configuração | `available`, `physical_disabled`, `options_invalid`, `calculator_unavailable` |
| Tempo/status GPS | `gps_time_invalid`, `gps_old`, `gps_future`, `no_signal` |
| Predecessor/matching | `predecessor_absent` (histórico físico incompleto), `matching_not_plausible`, `projected_predecessor_absent`, `previous_position_invalid` |
| Identidade/geometria | `identity_invalid`, `target_absent`, `topology_invalid`, `position_invalid`, `diagnostic_identity_mismatch`, `projection_mismatch` |
| Diagnóstico temporal/bearing | `diagnostics_absent`, `temporal_invalid`, `bearing_invalid`, `advance_insufficient`, `temporal_timestamp_invalid`, `temporal_interval_invalid` |
| Distância/velocidade | `distance_invalid`, `route_length_invalid`, `speed_invalid` |
| Testemunho não disponível | `not_recorded` |

Há28 códigos controlados; teste impõe teto32. Não acrescenta IDs como dimensões. Frações regressivas/iguais continuam recusadas; não é correção de PassagemParada, TTL ou matching.

## Custo, privacidade e falhas

- Sem consulta, HTTP, disco, await, timer ou callback novo no GPS. Fórmula executada uma vez; nenhum recálculo no Shadow para obter motivo.
- Com Shadow ON, no máximo um pequeno testemunho por resultado Apply, liberado com o lote. A fila já limitada recebe só enum + observação existente, não o testemunho. São no máximo28 chaves por motivo +3 por resultado de captura, sem crescimento por frota/viagem/linha.
- Mantidos filas, sink, timeouts, circuit breaker, quotas, retenção e exportação assíncrona. Fila cheia/falha de escrita/logger não altera o ETA público. O diagnóstico respeita o catch fail-open do hook existente.
- Nenhum identificador físico, coordenada, payload, velocidade ou timestamp adicional é exportado pelo diagnóstico: apenas o código enum. Testes conferem JSON público/persistido idêntico e ausência do testemunho na serialização.
- Métricas não são duráveis e não comprovam escrita. JSONL pode ter perdas por limites já existentes; motivos descrevem somente observações efetivamente capturadas/exportadas. Não substituem labels auditados.

## Testes e arquivos

Runtime: `PhysicalEta.cs` (decisão única), `ProjecaoOperacional.cs` (testemunho efêmero), `GpsPollingService.cs` (propagação no fluxo existente), `HistoricalEtaShadow.cs` (vínculo/métricas), `HistoricalEtaOfflineExport.cs` (campo privado opcional).

Regressões: `PhysicalEtaCausalDiagnosticsTests.cs` compara os resultados com cópia congelada do Apply124 **somente no teste**, cobre26 motivos de decisão, precedência temporal/bearing, opt-in OFF, troca/ausência de testemunho, enum inválido, relógio posterior, fila cheia, privacidade e cardinalidade. `HistoricalEtaGtfsPollingTests.cs` cobre BUS/BRT individual/lote até JSONL, físico válido e predecessor ausente. `HistoricalEtaRuntimeTests.cs` mantém os gates e identidades da investigação anterior. A seleção CI inclui os testes do físico/resolver necessários à refatoração; não altera workflow ou promoção.

Validação local: build Release por `dotnet test` (zero erros/5 warnings preexistentes), **613 testes C# aprovados**, zero ignorados, seleção CI completa e TRX verificado; 26 testes Python do avaliador aprovados; seleção `.ci` no Windows:6 aprovados/4 verificações POSIX ignoradas, executadas no CI Linux. Resultado remoto registrado na PR. Artefatos externos: `D:/repositorio_github/NoPonto/physical-shadow-causal-20261016`.

Sem acesso ao homeserver/banco/Redis reais, ativação Shadow, deploy, alteração de flags remotas ou publicação produtiva. A próxima coleta exige revisão e autorização operacional separadas, usando o mecanismo já existente; o merge desta PR não altera os arquivos de configuração OFF.
