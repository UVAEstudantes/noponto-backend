# Etapa 2A.2O.1 — recuperação operacional Redis de melhor esforço

## Resultado e escopo

Implementado retry limitado dentro do polling existente. O problema era a separação entre o aceite GPS no Redis e a transação operacional PostgreSQL: uma falha posterior ao aceite deixava o timestamp deduplicado sem processamento operacional recuperável. O retry mantém uma pendência independente desse aceite e não abre os filtros normais para duplicados.

Os dois casos locais vermelhos da etapa O passaram, preservando a expectativa de uma segunda chamada operacional para o mesmo timestamp, inclusive com nova instância e store preservado. A validação local ampliada aprovou 444 casos, sem falhas ou ignorados, recompilando com `--no-restore`. Isso comprova contratos locais e regressões; scripts Lua, matching PostGIS, transações e recuperação SQL desta implementação ainda precisam dos testes reais preparados. Nenhum serviço externo foi executado pelo agente.

As conclusões históricas N/O foram preservadas. A decisão atual aceita explicitamente recuperação de melhor esforço e substitui o requisito anterior de recuperação garantida após perda total do Redis. Não certifica implantação nem qualidade integral dos labels externos.

## Fluxo implementado e arquivos

1. `GpsPollingService.ExecuteAsync` solicita recuperação no início do ciclo, antes dos filtros da fonte. Assim, uma pendência pode avançar sem a fonte reenviar o GPS.
2. O GPS normal continua passando por filtros, matching e CAS do cache do mapa. `ConfirmarPosicaoAsync(ResultadoEnriquecimentoGps, ...)` tenta recuperar a pendência do veículo e distingue aceite do mapa de processamento operacional.
3. Falha operacional de infraestrutura/conflito, ou indisponibilidade diagnosticada do matching após aceite, registra pendência. GPS novo aceito no mapa aguarda operacionalmente quando há backlog conhecido; somente observações operacionalmente elegíveis ou com falha de matching diagnosticada são enfileiradas.
4. `RetryOperacionalGpsService.RegistrarAsync` usa o identificador estável existente `TelemetriaMlContrato.ObservacaoId`, guarda observação, predecessor físico, contexto anterior e prazos. Retira identidade derivada e prova efêmera. O fingerprint inclui os dados físicos da observação e do predecessor; uma repetição idêntica não renova prazo nem sobrescreve o payload original.
5. `PendenciaOperacionalGpsRepository` utiliza o multiplexer existente: hash limitado, índice global de prazos, índice temporal por veículo e lease com token. Lua faz cadastro, claim, reagendamento e remoção condicionada atomicamente. Não há worker, container ou tabela adicional.
6. `RecuperarVeiculoAsync` verifica prazo, idade GPS, limite de tentativas, autoridade PostgreSQL e contexto atual. Progresso durável igual reconhece commit perdido; progresso posterior dispensa observação superada. Progresso apenas quente ou mudança de contexto causa descarte conservador diagnosticado, nunca ACK durável inventado.
7. O predecessor precisa corresponder a veículo, coordenadas e timestamp anterior; o intervalo deve ser positivo e até 180 segundos, com coordenadas válidas e plausibilidade física existente. Ausência de evidência descarta a pendência sem criar passagens.
8. `GpsEnriquecimentoService`, pela implementação de `IEnriquecimentoRetryOperacionalGps`, cria instância isolada do serviço produtivo e enriquece predecessor e observação novamente. Reconsulta matching e plausibilidade, sem regredir a memória de histerese da instância normal. O retry também rejeita alteração da identidade física da observação pelo enriquecimento.
9. O serviço/repositório operacional existente executa regras, proteção circular, CAS e transação de estado/integridade/outbox. A diretiva interna `ExigirPersistenciaDuravel` força checkpoint somente no retry quando a política normal dispensaria escrita.
10. `PersistidoDuravelmente` só é retornado após commit. Somente essa confirmação ou leitura autoritativa permite concluir por recuperação durável. `Updated` isolado reagenda. Rejeições operacionais explícitas são diagnosticadas como descarte; falhas transitórias usam backoff.

Arquivos funcionais desta etapa: `RetryOperacionalGps.cs` (novo), `PendenciaOperacionalGpsRepository.cs` (novo), `GpsPollingService.cs`, `GpsEnriquecimentoService.cs`, `ProjecaoOperacional.cs`, `ViagemObservadaService.cs`, `ViagemObservadaState.cs`, `IViagemObservadaRepository.cs`, `ViagemOperacionalRepository.cs`, `PosicaoApiDto.cs` e registros em `Program.cs`. Testes: `ViagemObservadaServiceTests.cs`, `MudancaOperacionalPontaAPontaTests.cs`, novos `RetryOperacionalGpsTests.cs` e `RetryOperacionalGpsRedisIntegracaoTests.cs`. Documentação: este relatório e `HISTORICO.md`. Outros diffs do workspace são anteriores e foram preservados.

## Contrato, integridade e limitações

Aceite para mapa, pendência e conclusão operacional são conceitos distintos. O caminho normal conserva sua política de checkpoints; uma atualização quente válida sem necessidade de evento não cria pendência apenas para obter ACK SQL. Nenhuma escrita PostgreSQL adicional por GPS normal foi introduzida.

Uma pendência é processada por ordem temporal no veículo. Lease/token impede duas tentativas da fila de possuírem simultaneamente o item; conclusão de proprietário antigo não remove trabalho do novo proprietário. CAS e timestamps produtivos continuam protegendo o estado contra retrocesso. A ordem não é garantia global de exactly-once: registro após falha e processamento normal concorrente têm janelas distintas. Observações já ultrapassadas são dispensadas conservadoramente, podendo deixar lacunas.

O retry não contorna proteção circular, não altera volta/cursor diretamente, não reconstrói passagens retroativas e não reemite telemetria ML/ETA. Identidades operacionais incertas continuam suprimidas no fluxo normal. Assim não duplica observações de ML durante replay, mas também não preenche posteriormente uma identidade ausente no registro original. Algoritmos ETA/ML, histórico, contratos públicos e array de 27 posições não foram alterados.

Rejeição `InvalidState` é diagnosticada como rejeição operacional, não sucesso. A classificação atual de conflito de payload do outbox como `InvalidState` permanece: o retry não resolve uma colisão de identidade nem mascara sua causa. Integridade circular previamente implementada deve impedir tais colisões nos casos previstos.

Não há recuperação garantida após perda do Redis, falha antes de registrar pendência, TTL físico, evicção, limites atingidos ou indisponibilidade prolongada. Também podem ser descartadas observações por predecessor perdido, contexto alterado, idade GPS, prova insuficiente ou progresso quente posterior. Uma nova execução criada pelo predecessor pode invalidar o contexto de itens seguintes: o comportamento conservador é dispensar, sem transportar evidência entre viagens.

Expiração lógica e descartes enquanto o polling está ativo produzem logs com motivo. Expiração física de chaves enquanto o processo está parado, perda/evicção Redis ou falha antes do registro não permitem diagnóstico individual posterior; não existe auditoria durável dessa perda nesta alternativa. Não prometer rastreabilidade completa. Indisponibilidade é registrada no máximo uma vez por minuto por instância; criação, tentativa e encerramento têm logs leves, sem log adicional por GPS normal.

## Limites e custo esperado

Opções registradas/validadas em `RetryOperacionalGps`; nenhum arquivo de configuração ou flag de mudança operacional foi ativado.

| Parâmetro | Padrão |
|---|---:|
| TTL lógico por pendência | 180 s |
| TTL físico de segurança das estruturas | 420 s (`2 × TTL + lease`) |
| Backoff inicial | 20 s, exponencial, limitado pelo prazo |
| Máximo de tentativas operacionais | 4 |
| Pendências por veículo / globais | 3 / 2.000 |
| Claims com item por ciclo e instância | 4 |
| Timeout cooperativo da tentativa | 3 s |
| Lease por veículo | 60 s |

Claims vazios devolvem orçamento. Um item por veículo/chamada evita laço de recuperação agressivo; a cabeça em backoff bloqueia os itens seguintes. Timeout cooperativo não substitui timeout nativo Redis nem assegura que toda dependência respeite cancelamento. Uma falha durante liberação deixa o lease expirar; eventual commit é reconciliado pela autoridade PG na tentativa seguinte.

No caminho normal, acrescentam-se até duas leituras Redis por posição aceita (claim vazio e consulta de backlog), mais consulta limitada de prazos por ciclo. Sem pendências, não há gravação de fila nem consultas extras PostgreSQL. Nas exceções há leituras autoritativas/contextuais, reenriquecimento do par físico e eventual commit adicional. Não há varredura completa do histórico.

Memória fica limitada em quantidade de itens, cada um contendo duas observações e metadados; não há limite estrito em bytes. Medir tamanho JSON/overhead Redis, p95/p99 do polling e matching, tempo de recuperação, volume de rejeições por prazo/limite, CPU e WAL antes de ajustar padrões. Não foi feito benchmark; quatro tentativas lentas podem consumir aproximadamente 12 segundos de trabalho cooperativo por ciclo, além do I/O Redis e de dependências que não respeitem cancelamento. Esse custo é limitado por instância, não por todo o cluster.

## Validação local e testes reais pendentes

Cobertura local: dois vermelhos originais convertidos em verdes por recuperação legítima; 16 casos novos de retry incluem commit perdido, Updated sem confirmação, contexto ultrapassado, predecessor inválido, ordem/backoff, concorrência, indisponibilidade/perda Redis, prazo/capacidade, ausência de escrita no caminho normal, prova não revalidada, identidade física alterada e timeout. Regressões de mudanças operacionais, circularidade, codec, cursor, ML, ETA e polling compõem os 444 casos aprovados. Foi excluído explicitamente o caso preexistente de outage por socket `Protecao_OutageGlobal_ExecutaUmaSondaEPulaChunksRestantes`; classes reais não entraram no filtro local. Cinco avisos preexistentes, sem erro de compilação.

O teste real antigo foi renomeado para `FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampRecuperaCandidato`, convertendo a caracterização negativa em regressão positiva. `RecuperacaoPendente_MesmoGpsDepoisRollback_DeveRecuperarCandidatoEConfirmar` preserva a expectativa de recuperação. Ambos ainda precisam ser executados nesta implementação.

Cinco casos reais novos, compilados e NÃO EXECUTADOS:

- `Retry_RespostaPerdidaDepoisCommit_ReconheceDuravelSemDuplicarEventos`: reconhece commit sem repetir chamada, confirma candidato depois e exige eventos únicos/sem passagens artificiais.
- `Retry_NovaInstanciaComRedisPreservado_RecuperaSemReplayDaFonte`: novo harness com mesma fila, matching produtivo e sem replay da fonte. Simula troca de componentes; não simula desligamento real do SO.
- `Retry_CircularAposRollback_PreservaProtecaoESuprimeIdentidadeMl`: rollback, remoção da projeção operacional Redis, reenriquecimento real, proteção durável, cursor/volta preservados e identidade ML suprimida.
- `Lua_OrdenacaoLimitesLeaseCasEBackoff_PreservamPayload`: Lua real, payload idempotente/conflitante, limites, ordenação, backoff e competição de tokens.
- `Lua_PendenciaExpirada_EncerraSemChamarOperacao`: expiração lógica sem processamento operacional.

Reutilizada `ViagemOperacionalFixture`. Prefixos exclusivos e limpeza em `finally` evitam remover pendências de outros testes; fixture continua exigindo banco descartável comprovadamente identificado, pois cria/migra/limpa dados. Só executar manualmente com `POSTGIS_TEST_CONNECTION` e `REDIS_TEST_CONNECTION` apontando exclusivamente ao ambiente local de testes já revisado. Não executar com credenciais ou conexões de produção.

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~RetryOperacionalGpsRedisIntegracaoTests' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~RecuperacaoPendente_MesmoGpsDepoisRollback|FullyQualifiedName~FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampRecuperaCandidato|FullyQualifiedName~MudancaOperacionalPontaAPontaTests.Retry_)' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~MudancaOperacionalPontaAPontaTests' --verbosity normal
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~ViagemOperacionalIntegracaoTests|FullyQualifiedName~ViagemOutboxBatchTests|FullyQualifiedName~ViagemDuravelPostgresTests)&FullyQualifiedName!~Chaos_RedisPara_ReiniciaVazio_ContinuaMesmaViagem' --verbosity normal
```

## Implantação futura e trabalho adiado

Antes de implantação, executar os casos reais, observar carga/custos e validar compatibilidade da proteção circular anterior. Rollout coordenado continua necessário: escritores antigos podem ignorar integridade circular. Este retry não muda requisitos de schema da etapa L, não aplica aquela migration e não torna uma mistura de versões segura. Binários antigos não consomem a nova fila; pendências expiram. Chaves novas são internas, separadas da projeção GPS e operacional.

Ingress durável PostgreSQL, recuperação após perda completa Redis, auditoria durável de descartes, replay integral, garantias fortes entre instâncias e reparação/qualificação externa de labels ficam adiados para depois do TCC. A implementação atual entrega recuperação limitada de falhas transitórias, com dispensas conservadoras, sem alegar durabilidade que o Redis não oferece.
