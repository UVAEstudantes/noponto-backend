# ETA historico + ExtraTrees Shadow — PR 2 (2026-10-15)

## Decisao

Infraestrutura integrada, observavel e OFF por padrao. **Nenhuma comparacao real de precisao foi demonstrada nesta PR.** Nao foi localizado um modelo ExtraTrees real qualificado nem um conjunto de chegadas novas auditadas para esta integracao. Os resultados abaixo sao engenharia com fixtures; nao medicao de acuracia no Rio. Nenhum deploy, ativacao, treino ou acesso a producao.

Base: main 2043478196ae4a8e2d3acd0c9006f2afc482cf2f, incluindo PR67/68. Worktree/branch exclusiva `noponto-backend-eta-real-shadow` / `feature/eta-real-shadow-integration`. Preservados os 13 workspaces inventariados na PR1, incluindo backend main suja, ML e Shadow antigo.

## Reuso e dependencias

- feature/eta-shadow-gps-current (HEAD 6f64538 com WIP): worker/fila, resolver READ ONLY, estatisticas, exportacao JSONL e avaliador offline 4D; transportados apenas esses componentes e seus testes. Nao copiados GPS/Program antigos, fencing ou arquitetura experimental3G.
- feature/eta-ml-mvp (5a13e52): referencia historica de contrato, nao substituiu codigo atual.
- ML feature/eta-history-mvp (6c0f7b5): FastAPI `eta_history_service.py`, ExtraTrees, qualificacao/volume existentes e contrato de inferencia. Repositorio nao alterado. O arquivo `modelo_eta.joblib` do legado XGBoost NAO e modelo aprovado para este fluxo. A readiness local consultada informa ready=false, por ausencia de manifesto real qualificado.
- tools/eta_ml/pipeline.py: agrupamento historico TRAIN reaproveitado, sem usar sua velocidade fallback/global no ETA publico. O exportador novo produz somente medianas de grupos com pelo menos 5 labels auditados TRAIN, sem fallback global para grupo ausente.
- Laboratorio TCC permanece sintetico e separado. Lab.targets agora evita incluir tipos duplicados quando Shadow ja esta no backend; experiment.py usa fontes integradas como proveniencia. ML-root continua dependencia explicita; referencia externa antiga permanece arquivada, nao constitui aprovacao produtiva. Nenhum treino/laboratorio FastAPI foi repetido nesta tarefa.

Hashes originais dos arquivos selecionados antes das adaptacoes:

- `NoPonto/2-Application/Services/GPS/HistoricalEtaShadow.cs`: `16c43a17fe5dc46f8816d644a26b195bfe0068c05852fe3c04de5866d30c11ae`
- `NoPonto/2-Application/Services/GPS/HistoricalEtaOperationalStatistics.cs`: `c800530b61decbbf8f56afc994a227360e2b78a70275f25da96dee52f61f301b`
- `NoPonto/2-Application/Services/GPS/HistoricalEtaOfflineExport.cs`: `4a8e91a8a9f6a1f2c3bc7c664b54479973a4eefdbd0a1af582651028a9570019`
- `NoPonto/5-Testes/HistoricalEtaShadowTests.cs`: `61ed7c3e30c287dee4fb8fd8bfd78430de04c5469b1fea548aad170ac0f8ffff`
- `NoPonto/5-Testes/HistoricalEtaOfflineExportTests.cs`: `54f22870a817c29103742ab8bd089836ace1a3beacae3498a7f523a4f20ab857`
- `tools/eta_ml/evaluate_shadow.py`: `e82a55801037ddeb2f41acc7dce461a31a29798aab8e9a7ce0401ce24bd13311`
- `tools/eta_ml/test_evaluate_shadow.py`: `3cdcaf6e5ec32b99b35288bb5b51e50e7134f3345223e4042f9b06790a1d6114`
- `NoPonto/5-Testes/HistoricalEtaGtfsPollingTests.cs`: `1c42d43ac50c0badaae4222cf205bf1e9a084f90dfed4361192785938bc54aa8`

## Referencia causal e integracao

Hook unico apos GPS aceito por CAS e retorno da decisao operacional. Apenas TryCapture/TryWrite, sem await, HTTP, SQL ou disco no caminho GPS. Excecoes sao contidas no hook e no worker. Ausencia de identidade vira rejeicao contada, sem inventar viagem.

Exige Created/Updated, identidade operacional confiavel, estado Ativa/PossivelFim, mesma observacao original, veiculo, linha/codigo, sentido, padrao/versao, timestamp, fracao confirmada, volta e proxima ocorrencia. O alvo operacional precisa ser exatamente o alvo observacional do PhysicalEta. Reancoragem divergente, ItineraryChanged, wrap/ocorrencia de outra volta e posicao incompatibilizada sao recusados. O modulo nao corrige cursor, matching, TTL ou PassagemParada.

O resolver usa diferenca de comprimentos dos prefixos geograficos da geometria, como o matching/ETA publico. Recusa diferenca acima de 1cm em relacao a distancia longitudinal do DTO. Nao usa Haversine, distancia reta ou fracao vezes comprimento. Consulta em lote <=200, transacao READ ONLY, statement_timeout 500ms e lock_timeout100ms; worker sequencial, orcamento combinado de resolver/HTTP1000ms por default.

PhysicalEta so entra na comparacao quando possui o marcador causal e CapturedSpeed valido; um ETA legado nao e aceito como baseline. Nenhuma previsao Shadow volta ao DTO ou aos endpoints publicos. O alvo desta captura e a proxima ocorrencia comum, NAO todas as paradas arbitrarias consultadas no Radar.

Modal/provedor sao provas efemeras e nao fazem parte do JSON Redis. Nao sao reconstruidos do cache: a captura utiliza o DTO original do polling. Teste conectado confirma que um payload Redis isolado e recusado pelo adapter. O fallback recente do PhysicalEta da PR67 permanece intocado.

## Inferencia e historico

Opt-ins separados: Enabled, MlEnabled, HistoricalEnabled e OfflineExport.Enabled; todos false por default. Flags de ML legado/EtaV2/URL nao ativam Shadow. OFF nao registra worker/resolver/client/fila. Configuracao Shadow invalida e recusada sem impedir a API de iniciar; motivo generico sem valores sensiveis.

Antes de chamar HTTP, exige manifesto local explicito <=256KiB com SHA256 fixado, data_kind real, ExtraTreesRegressor, contrato dataset v1, qualificacao/volume ready, politica atual e hashes vinculados, alem de digest do modelo aprovado. Sem isso: model_unavailable, motivo qualified_model_unavailable e zero HTTP. O FastAPI existente continua responsavel por seus gates completos de qualificacao; esta PR nao certifica dados nem altera politica de treino. Hash e fixacao de artefato revisado, nao assinatura nem prova autonoma de qualidade.

Resposta HTTP limitada a256KiB, sem redirects/cookies, conexao unica, correlacao completa e ordenada por request_id, digest identico ao aprovado e X-Model-Data-Kind=real. Resposta sintetica/divergente ou erro descarta previsao; circuit breaker30s, sem retry no ciclo GPS. Modelo ausente no servidor responde indisponibilidade, sem fallback ML publico.

Historico: artifacto JSON de grupos TRAIN explicito <=512KiB/2000grupos e SHA fixado, mesmo dataset do manifesto qualificado e fim_treino exato. `export_shadow_baseline.py` reutiliza auditoria/labels canonicos, aceita somente dados reais e grupos >=5. Grupo inclui modal, codigo, sentido, versao, ocorrencia, faixa de distancia e faixa de hora; versao/ocorrencia ja vinculam linha/padrao/parada pelo resolver. Grupo ausente nao usa mediana global. O nome legado HistoricalEta para a resposta ExtraTrees foi corrigido: ml_eta e historical_eta sao origens distintas.

Inferencia/avaliacao devem ocorrer apos todo o intervalo de dados usado pelo artefato (config.fim), e viagens da colecao usada no modelo sao excluidas. Baseline exige TRAIN e chegadas anteriores ao corte. Isso nao resolve auditoria ausente: somente um artefato real qualificado e revisado pode ser fixado para ativacao.

## Retencao e avaliacao

JSONL privado v2, explicitamente habilitado em diretorio absoluto novo. Nenhuma escrita produtiva implicita. Fila128, arquivo1MiB, total16MiB, ate32arquivos, retencao24h por default (limites configuraveis e validados). Somente arquivos proprios expiram. Usar diretorio novo dedicado a v2; nao apontar para snapshots/artefatos antigos. Unix: diretorio700/arquivos600. Quota/erro de disco descarta captura e gera contadores, sem interromper GPS.

request_id agora inclui tambem viagem no hash: um teste RED comprovou a colisao da versao antiga entre duas viagens distintas com o mesmo GPS/alvo/versao/volta. A correlacao v2 recusa esse compartilhamento. O FastAPI existente apenas ecoa o request_id e aceita os metadados causais adicionais sem alterar features.

Registro contem observation_id, request_id, trip, linha/modal/sentido/padrao/versao, ocorrencia/parada/volta, GPS/captura/emissao, distancia, duracoes/horarios previstos por origem, versoes/digests, corte temporal, status e motivos. Nao grava veiculo em claro, coordenadas, payload bruto, connection string ou credenciais. O sink em memoria tambem e limitado em capacidade/TTL e nao e armazenamento duravel. Metricas em logs agregados nao incluem identificadores de observacoes/veiculos; falhas de logger sao contidas, inclusive IsEnabled.

Metricas operacionais: candidatos, recusas, profundidade/high-water, perdas, indisponibilidades, latencias e cobertura; success significa resposta ML qualificada. Diferenca entre previsoes NAO e erro de chegada. Contadores reiniciam com a instancia; resumo do export preserva perdas conhecidas, mas queda abrupta pode perder cauda/resumo. Quota e retencao nao garantem cobertura completa de GPS.

`evaluate_shadow.py` reutiliza dataset canonicamente exportado com auditoria, hashes e viagens fechadas. O exportador existente verifica historico + journal PassagemParada e fronteiras inicio/fim da mesma execucao. Join exato, incluindo observacao GPS/volta/ocorrencia, nunca proximidade temporal. Label ausente/incompleto e perda de cobertura; auditoria com qualidade inadequada e recusada. Nenhum ETA vira label e nenhuma passagem e criada/alterada.

Saida local evaluation.json/evaluated.csv: MAE, mediana absoluta, P90, vies (previsto - observado; positivo=mais tarde), cobertura por origem/modelo, pares contra PhysicalEta e intersecao three_way_common para as tres origens na MESMA identidade e recortes por linha/modal/horizonte com minimos30observacoes/3viagens. Horizonte e retrospectivo, definido pelo tempo real entre GPS e chegada. Predicao emitida apos chegada, expirada ou com corte temporal incompatibilizado e excluida. A secao antiga arrival_error e diagnostico ML; arrival_sources explicita origens e cobertura. Sem endpoint/dashboard novo ou consultas pesadas de avaliacao na API.

Registros privados v1 sao recusados pelo avaliador v2; nao reinterpretar o antigo historical_eta (ML) como baseline historico. Nenhum arquivo antigo foi migrado/apagado.

Exemplo offline (somente arquivos locais auditados fornecidos pelo operador):

```bash
python tools/eta_ml/export_shadow_baseline.py --dataset DATASET.csv --manifest DATASET.manifest.json --audit AUDITORIA.json --model-manifest MODEL.manifest.json --output NOVO_BASELINE.json
python tools/eta_ml/evaluate_shadow.py --predictions CAPTURA_V2.jsonl --dataset DATASET.csv --manifest DATASET.manifest.json --audit AUDITORIA.json --output NOVO_DIRETORIO_RELATORIO
```

--fixtures e exclusivo de testes sinteticos do avaliador; nao aceita misturar fixture com dataset real nem qualifica modelo.

## Ativacao gradual futura — NAO EXECUTADA

1. Antes de merge com CI/CD automatico, confirmar no preflight autorizado que todas as flags HistoricalEtaShadow estao ausentes/false. Esta PR nao verifica nem muda ambiente produtivo. PhysicalEta=true/90 e ML legado/EtaV2/ShadowV2=false permanecem como configurados pelo operador.
2. Revisar/homologar a PR com flags OFF. Para futura janela autorizada, disponibilizar diretorio v2 novo privado e com quota, sem reutilizar snapshots4C5/4C7. Comecar somente captura com Enabled=true, OfflineExport.Enabled=true, MlEnabled=false, HistoricalEnabled=false. Proposta conservadora para revisar: QueueCapacity64, BatchSize16, SinkCapacity128, TimeoutMs500, MaxAgeSeconds90; observar perdas/latencia/CPU/RAM/DB por uma janela curta antes de ampliar. Nao aplicados aqui.
3. Conferir logs HistoricalEtaShadowMetrics e JSONL v2, identidades comuns e indisponibilidades. Ausencia de identidade confiavel ou falta de passagens confirmadas impede avaliacao; nao relaxar regras para preencher cobertura. Ao fim da janela desligar Shadow e preservar artefatos dentro da retencao por procedimento autorizado.
4. Exportar labels somente de snapshot autorizado e execucoes realmente auditadas, com journal/passagens confirmados. A regressao operacional4C9 nao foi declarada resolvida. Sem essas evidencias a comparacao real continua bloqueada.
5. Somente apos modelo real qualificado, fixar QualificationManifestPath/Sha256 e ExpectedModelDigest, configurar o FastAPI isolado existente (ML6c0f7b5, /eta/batch, modelo read-only; sem ETA_ALLOW_SYNTHETIC), e revisar seus limites existentes0.5CPU/512MiB/128PIDs. Medir seu consumo com artefato real antes de habilitar MlEnabled. Historico requer tambem HistoricalBaselinePath/Sha256 exportados de TRAIN e seu opt-in separado. Nenhum desses arquivos/modelos foi produzido com dados reais nesta PR.
6. Interromper Shadow em caso de pressao operacional: flags OFF e procedimento API/rollback existente preservando override PhysicalEta. Nao aumentar filas/timeouts/limites de idade por hipotese. Nao ativar ML legado ou EtaV2; nao alterar Redis operacional, matching, passagens ou banco para acomodar o experimento.

## Validacao e limites restantes

Resultados finais e links TRX estao no historico desta PR e no diretorio externo eta-real-shadow-20261015. Fixtures nao demonstram precisao, cobertura real nem custo de inferencia ExtraTrees em producao. Custo do servico cientifico com modelo real permanece pendente; testes HTTP usam fakes com digest/metadata de fixture.

O harness `tools/eta_ml/test_shadow_disposable.ps1 -Run -ResultsDirectory CAMINHO_ABSOLUTO_NOVO` exige Docker Desktop local, imagens ja existentes, dois containers novos com guard token/portas loopback e teardown somente dos IDs criados. Recusa defaults de conexao e contexto remoto. O teste conectado nao esta na selecao offline; CI nao acessa bancos existentes. Nao executa migrations operacionais. A compilacao explicita Lab.targets foi validada sem treinar ou iniciar FastAPI, evitando duplicacao de tipos Shadow.

Resultados executados nesta candidata:

- Build Release aprovado, 0 erros / 5 warnings preexistentes. Selecao CI completa: 529 testes C# aprovados, zero falhas/ignorados; verificador .ci confirmou todas as classes. TRX ci-release-final.trx.
- 26 Python do avaliador/exportador aprovados; seis testes Python da selecao CI aprovados (quatro de permissoes POSIX ignorados localmente no Windows; executados pelo CI Linux).
- Integracao real PostGIS/Redis descartaveis: um teste aprovado; harness-verified3/disposable.trx. Checagem TCP evita aceitar o servidor temporario do bootstrap; timeout120s. Guard tokens, DB exclusiva e portas loopback comprovados; nenhum container de fixture permaneceu apos teardown. Duas falhas anteriores do harness ficaram nos artefatos como evidencia, sem impacto operacional.
- Regressao de correlacao: identity-red.trx falhou antes da inclusao da viagem no hash; agora aprovada na selecao final.
- Build explicito Lab.targets aprovado sem ShadowRoot externo, nenhum treino nem FastAPI iniciado. A pequena mudanca de hash e opaca ao protocolo de eco do laboratorio.
- Ensaio isolado: 10.000 TryCapture em84,151ms; CPU do processo156,250ms e delta de heap retido6.664bytes; fila/sink permaneceram em4itens. Isso nao e RSS nem consumo exclusivo de todo o pipeline/host. Integracao resolver+export emfixture702,721ms; CPU do processo375ms; heap retido488.272bytes. Sao amostras locais, nao limites de aceitacao produtiva ou custo do ExtraTrees real.
- Sem alteracoes em PhysicalEta.cs, EventosParadaService.cs, TTL, matching, passagens, schema, ML repo ou frontend. Workflow recebeu somente regressao Python no validate; gatilhos/publicacao/deploy/approvals nao mudaram.

Regressao adicional no polling real com fixtures GTFS-RT: PhysicalEta ON/90s e ML legado OFF nos modais BUS/BRT, matching individual/lote; primeiro GPS sem prova permanece sem ETA e o segundo calcula o fisico57,6s, preservado mesmo com HTTP Shadow falho. Esses quatro casos integram os529 testes finais.
