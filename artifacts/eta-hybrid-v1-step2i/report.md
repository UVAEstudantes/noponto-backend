# ETA híbrido V1 — publicação 2I

1. **Versão:** commit `823057cd2a885029904429ac6bb59e9ba1d5226e`, branch `feat/eta-hybrid-v1-2i`, com push concluído. Imagem `noponto-api:eta-hybrid-2i-823057cd2a88`, ID `sha256:946a0bb63230681bd31397718d483a3c3bb759310c941fba10d8c58346112ce7`. Hash da DLL conferido no container e preservado no manifesto privado.

2. **Deploy via SSH, somente API.** A produção usava a base manual `hybrid-network-27`, com funcionalidades já publicadas ausentes na main. Recompilamos essa base preservada com overlay exclusivo do ETA e três registros DI. A imagem herda a base ativa e substitui DLL/PDB, preservando versões das dependências. Configuração anterior congelada; alterados somente imagem e flags ETA. Worktree isolado de `origin/main` (`8e30a5f`), sem publicar WIP de outras tarefas. Não houve merge na main: seu CD poderia remover funcionalidades da base manual. Banco, Redis e demais containers não foram recriados; nenhuma migration ou nova coleta externa. Deploy protegido por `deploy.lock`, snapshots e health.

3. **Híbrido ativo no endpoint público** `/paradas/{id}/eventos`, confirmado no servidor e por HTTP/Tailscale desta máquina. `PhysicalEta__HybridPublicEnabled=true`, referência 20 km/h, físico habilitado com idade máxima de 90 s. EMA nos primeiros 300 m; restante nominal 20. Todas as linhas rodoviárias elegíveis, sem configuração por linha. Origem `HybridFixed20`, qualidade `hipotese_nominal`; trechos curtos usam `HybridRecentEma`/`estimativa_recente`. Fallback `PhysicalCausal` quando indisponível. Sem novos campos no DTO; cache GPS mantém cálculo e prova físicos. Candidatos 2E/2F preservados. Terminal, saída, viagem, sentido, itinerário e ML não foram alterados. O híbrido só substitui eventos físicos elegíveis.

4. **Monitoramento concluído:** 05/11/2026, **18:45:25–19:00:27 (UTC−3), 902,498 s — 15 min 2 s**. Amostragem aproximadamente a cada 30 s: 29 amostras principais e 20 adicionais durante 10 min, em outras paradas para observar candidatos ativos. Logs cobrem toda a janela.

- **430 HTTP 200, zero erros:** 381 consultas de eventos e 49 health. Eventos: mediana **45,65 ms**, p95 **153,40 ms**, máximo **432,27 ms**.
- Zero reinícios/OOM; API sempre running; PostgreSQL/Redis healthy, Redis PONG em todas as amostras. Zero erros nos logs da API/PG e nenhum erro/OOM Redis. PG sem deadlocks ou arquivos temporários. Transações rollback aumentaram 167→615 sem erro nos logs; não foi estabelecida relação com o estimador.
- **65 ciclos normais de polling**, zero falhas. Duração mediana **2,869 s**, máxima **22,518 s**; intervalo entre inícios mediano **15,000 s**, máximo **27,519 s**, configurado 15 s. Matching até 10,335 s e commit de viagem até 9,762 s. O polling não aguarda o worker. Teste de handoff público com consumidor bloqueado: 1,84 ms. O contador legado `eta_etapa_ms` não mede o híbrido.

| Container | CPU máxima amostrada¹ | RAM máxima amostrada |
|---|---:|---:|
| API | 97,76% | 414,8 / 768 MiB |
| PostgreSQL | 110,17% | 903,4 MiB |
| Redis | 29,70% | 167,6 / 640 MiB |

¹ Percentual Docker por núcleo. Host: CPU agregada máxima 37,91%; memória disponível mínima 2.022,5 MiB. Baseline curto já tinha PG até 133,69%. Seus 20 HTTP tiveram p95 197,58 ms, mas carga/coorte diferem: **não há A/B suficiente para provar melhoria ou custo zero por contenção**.

**Worker:** 37 lotes, 57.586 entradas processadas (não veículos únicos), 21.972 descartadas por limite/substituição e 60 ignoradas por filtros. **289 consultas PostGIS em background**, soma de 79,192 s de processamento; lote mediano 2,725 s, máximo 3,078 s. Uma falha de consulta contida às 21:55:27 UTC, em lote de 3,040 s; telemetria agregada não registra a causa exata. Sem erro HTTP ou falha do polling associado. Endpoint: **nenhuma consulta SQL adicional por requisição**, verificado por teste de contagem e reutilização das distâncias. Fila limitada a um lote pendente, um consumidor; sem acúmulo ilimitado.

5. **Distribuição:** contadores do processo até 22:00:17 UTC: **1.482 híbridos / 3.809 físicos — 28,01% / 71,99% dos numéricos**, mais 4.680 indisponíveis. Esse último log precede o fechamento em 10 s e inclui requisições além das coortes.

Nas coortes registradas: 8.809 eventos, 4.796 numéricos; **1.458 híbridos / 3.338 físicos — 30,40% / 69,60%**, e 4.013 indisponíveis. Coorte principal: 17 híbridos/2.889 físicos; adicional: 1.441/449. São respostas repetidas em paradas selecionadas, **não cobertura da frota nem observações independentes**. Histórico ausente e descartes continuam limitando a seleção; investigação fica para a próxima etapa.

Máximo híbrido **133,45 min**, linha 2336, 45,16 km; máximo físico fallback **1.440,92 min**, linha 2338, 24,04 km. Revisão absoluta consecutiva máxima: híbrido→híbrido **17,11 min**; físico→físico **1.129,39 min**. Trocas físico→híbrido até 483,49 min e híbrido→físico até 490,32 min. **Extremos físicos e descontinuidades entre modelos permanecem.** Sem cap artificial ou nova suavização. Nominal 20 não comprova precisão, sobretudo em viagens longas. Idade máxima inferida pelo horário e segundos arredondados: 90,38 s, compatível com gate 90 s ±0,5 s.

6. **Validação:** 130 testes direcionados na base main; 97 sobre a base produtiva; 31 no Release publicado. Todos aprovados, zero falhas/ignorados nos resultados finais; grupos sobrepostos. Três testes Python do executor aprovados. Replay 2D preservado: 446/1.295 candidatos nominais 20, sem nova coleta. Falhas iniciais de preparação foram resolvidas copiando replays privados e recuperando proto/recurso embutido ausentes do zip antes de publicar. Health textual do monitor corrigido antes da ativação. Nenhuma alteração de cálculo após o deploy; auxiliares finais só acrescentam seleção de coorte e registro de metadados. TRX e `analysis.json` preservados.

| Regra | Teste dirigido |
|---|---|
| Híbrido, fallback, rollback e SQL igual | `HybridEtaPublicTests.PublicSelection_UsesHybrid_WithSameSqlCount_AndRollbackRestoresPhysical`, `UnavailableHybrid_FallsBackOnlyToEligiblePhysical` |
| Endpoint real | `HybridEtaPublicTests.RealHttpController_ReturnsHybridThroughExistingPublicContract` |
| Polling isolado e falhas | `HybridEtaTests.HybridOnlyWorker_SubmissionDoesNotWaitForSlowShadow_AndShutdownCompletes`, `AdaptiveEtaShadowWorkerTests.RealPostgis_LockTimeoutDoesNotHoldProducer_AndShutdownCancelsBlockedSql` |
| Terminal, saída e replay | `PhysicalEtaMvpTests.NoProgressWithPositiveMeanSpeed_OnlyForwardDepartureEnablesPrediction`, regressões `ViagemOperacionalRegraTests`, `HybridEtaTests.Frozen2D_BackendMatches2GReferences_AndPhysicalFieldsRemainUnchanged` |

7. **Rollback exato, sem nova imagem/build/pull:**

```powershell
ssh -i "$env:USERPROFILE/.ssh/id_ed25519_noponto" guilherme@100.122.72.14 'python3 /home/guilherme/.noponto-deploy/eta-hybrid-2i/eta-hybrid-release.py rollback-flag'
```

Recria **somente a API**, na mesma imagem, com `HybridPublicEnabled=false`; há uma pequena interrupção durante a recriação. Com as demais flags shadow desligadas, o worker também fica inativo. Health e metadados são verificados. Alternativa de emergência: trocar `rollback-flag` por `restore-base` para restaurar integralmente imagem/configuração anteriores. Não executar rollbacks concorrentes.

Estado privado em `/home/guilherme/.noponto-deploy/eta-hybrid-2i/active.json`; baseline congelado em `original.json`. Registro `active.manual.json` atualizado e anterior preservado. Nenhum rollback necessário nesta janela. Observadores finitos encerrados; clone PostGIS local novamente parado; API e dependências produtivas continuam ativas.
