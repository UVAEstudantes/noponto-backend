# Etapa 3A — dataset histórico do MVP ETA/ML

Revisão de 07/10/2026. Entregues **contrato, regras de elegibilidade e gerador CSV offline testável**, com consulta SQL de descoberta. **Não entregue extrator PostgreSQL automaticamente certificador de histórico:** procedência/intervalos protegidos não são completamente reconstruíveis nas fontes persistidas atuais. Nenhum dataset real foi extraído ou modelo treinado. O gerador exige fonte de execuções completas com auditoria explícita; não atribui confiança por UUID, ausência de marker atual ou data arbitrária.

## Workspace e escopo

Antes de editar, lidos AGENTS/histórico e inspecionados status, diffs e arquivos novos. Há22 arquivos rastreados com mudanças anteriores e diversos arquivos novos de etapas anteriores, preservados. O relatório `ENCERRAMENTO_OPERACIONAL_2A_2P.md` solicitado não existe neste checkout; consultada a entrada P do histórico e comunicada ausência. Não recriado nem sobrescrito relatório anterior. Também não assumir que os relatórios N/O/O.1 ausentes estejam disponíveis no filesystem desta execução.

Arquivos exclusivos desta etapa: novos `2-Application/Services/GPS/EtaDataset.cs`, `5-Testes/EtaDatasetTests.cs`, `5-Testes/EtaDatasetPostgresTests.cs`, `ETA_ML_CANDIDATOS_3A.sql`, este relatório e acréscimo em `HISTORICO.md`. Nenhum conflito funcional com as alterações preexistentes, nenhuma edição do Program/polling/operação/codec/ETA público. Sem reset/clean/stash/checkout/restore/rebase/branch/commit/push.

## Inventário real das fontes

| Fonte/arquivo | Conteúdo efetivamente utilizável | Limites |
|---|---|---|
| Cache GPS Redis/polling | Observação corrente e predecessor para processamento | Não é arquivo histórico completo; deduplicação/sampling/backpressure/retry podem reduzir coverage |
| `TelemetriaVeiculoMl` | TelemetriasVeiculoMl: ObservacaoId, veículo/modal/provedor/linha, coordenadas recebidas, velocidade, timestamps, versão/ocorrência, volta/viagem, distância próxima e média causal | Identidade nullable, sem versão do contrato de qualidade; coordenadas projetadas não preenchidas pela factory |
| `HistoricoPassagem` | HistoricoPassagens: execução/volta/versão/ocorrência, parada, código/sentido, fração e timestamps | Campos legados nullable; TempoDesdeParadaAnteriorSegundos/DistanciaTrechoMetros não preenchidos pelo materializador atual |
| `EventoViagemPersistido` / EventosViagem | Journal com EventId, tipo, Payload e TimestampEvento; passagem contém linha/padrão/sentido/versão/ocorrência/volta | Pode conferir identidade/payload da passagem; não contém trilha completa de entrada/saída de ambiguidade |
| `ViagensOperacionais` | Último snapshot + extensão IntegridadeCircular | Estado corrente, não histórico de proteção por intervalo; ausência de marker atual não prova ausência passada |
| OcorrenciasParadasPadroes → PadroesVersoes → PadroesOperacionais → Sentidos → Linhas | Ocorrência, parada, ordem, PosicaoTracado, geometria, topologia, padrão, sentido/linha | Versão deve ser preservada/imóvel; não remapear GPS antigo para geometria mais recente |
| PrevisoesEtaV2 | Shadow armazena previsão/realização e identidade do alvo | Seu label parte de TimestampPrevisao, não TimestampGps: objetivo diferente do solicitado nesta etapa |

Referências: `TelemetriaMl.cs` (contrato/factory, linhas9–147), `TelemetriaMlRepository.PersistirLoteAsync` (15–83), `HistoricoEventoRepository.PersistirLoteAsync` (45–127), `ViagemOperacional.TimestampPassagem` (256), `DbContext` (índices140–175), `EtaV2Shadow`/`EtaV2Repository`. Referências relativas a NoPonto e ao código atual.

**Timestamps:** TimestampGps é instante da observação física; TimestampEnvioFonte/ServidorFonte são metadados da fonte; RecebidoEmUtc é recepção; EventoCriadoEmUtc é formação da telemetria; CreatedAt é persistência. TimestampPassagem vem da regra operacional: pode ser interpolado entre GPS ou usar o GPS corrente como fallback. TimestampGps no histórico corresponde à observação que confirmou o cruzamento; TimestampRegistro é inserção no servidor. Label usa SOMENTE TimestampPassagem − TimestampGps da telemetria, nunca TimestampRegistro. “Confirmada” significa cruzamento operacional confirmado, não sensor físico independente na parada. Qualidade interpolada versus fallback não é discriminada no armazenamento atual.

**Persistência ML realmente existe:** polling cria EventoTelemetriaMl apenas no fluxo normal elegível; publisher usa channel limitado/TryWrite e stream `noponto:ml:telemetria`; worker lê grupo `telemetria-ml-postgres`, valida/persiste lote e só então ACK. TelemetriaMlRepository insere PostgreSQL com ON CONFLICT ObservacaoId DO NOTHING. Assim, publicação não basta: canal cheio, sampling, indisponibilidade ou backlog podem impedir persistência. Retention atual inspecionada limita stream Redis/DLQ; não inferir retenção SQL daí. Retry operacional não republica ML nem corrige identidade de telemetria antiga.

**Passagens:** transação operacional inclui estado/integridade/outbox; ViagemOutboxWorker materializa journal/histórico e confirma outbox atomicamente. Só PassagemParada gera linha em HistoricoPassagens. Fim/início/baseline não geram chegada artificial. EventId canônico é `passagem:{ViagemId}:{OcorrenciaId}:{Volta}`; índice único correspondente no histórico preserva idempotência para identidades completas.

## ETA e treino existentes

`GpsEtaClient` envia `/eta/batch` em chunks200: linha, hora_dia, dia_semana, distancia_metros, velocidade_media (ausente vira0), posicao_na_rota e IDs de versão/ocorrência/sentido/linha. O horário atual é derivado do relógio do processo/ToLocalTime; esta etapa não muda esse comportamento. Resposta usa ETA e confiança por veículo. Serviço externo ML não está neste checkout: não foi alterado nem treinado.

Shadow usa `LONGITUDINAL_SPEED`, identidade operacional validada e TimestampPrevisao; EtaV2Repository realiza previsão com passagem compatível. Não é treinamento de novo modelo GPS. AdminMlController contém encaminhamento `/retreinar`, mas `NoPonto.csproj` exclui Controllers/Admin da compilação: não declarar endpoint ativo apenas porque há arquivo. `Program.cs` registra cliente ml-admin, não prova existência do treinamento externo. Docs legadas citam treinar.py/dataset_v1.py e o modelo Itinerario/ParadaItinerario; scripts não encontrados no checkout e seus contratos não são V2 vigente. Não reutilizados como se executáveis.

## Contrato `noponto-eta-gps-v1`

MVP: **uma observação → próxima ocorrência operacional explícita da mesma execução e volta**, no trecho ainda à frente sem atravessar wrap. Não expande para todas as paradas futuras nesta etapa.

| Grupo | Colunas |
|---|---|
| Proveniência/partição, não features | observacao_id, viagem_id, volta, veiculo, provedor, timestamp_gps, split |
| Identidade espacial/categorias | modal, linha_id, codigo_linha, sentido_id, padrao_id, versao_id, ocorrencia_id, parada_id, topologia |
| Features causais | posicao_gps, posicao_destino, distancia_metros, velocidade_kmh nullable, velocidade_media_causal_kmh nullable, hora_dia, dia_semana |
| Auditoria/label, jamais features | timestamp_passagem, label_segundos |

EtaDataset.Features é allowlist explícita, disjunta de Labels. Viagem/veículo são preservados para auditoria e split, não alimentam este MVP. Hora/dia derivam do GPS em UTC−03:00 (convenção Fortaleza, 0=domingo). CSV usa UTC ISO8601 e números invariantes, campos quotados e escape de aspas. Velocidade inválida/negativa/>160km/h vira ausente;160 é filtro conservador do dataset, não alteração da plausibilidade operacional. Comprimento/fração têm validação estrutural; distância usada é geography conferida sobre a mesma geometria publicada, não `(fração_destino−fração_gps)×comprimento`.

Preservados padrão/versão/ocorrência/parada e posições para evolução futura com trechos compartilhados. Não implementada chave de segmento universal nem features de veículos à frente/GNN. Agregados históricos não entram automaticamente: exigirão estatística fit somente no treino e dados conhecidos antes do GPS. Média causal atual é do próprio veículo; valores futuros do histórico de passagem não são features.

## Implementação e uso

`EtaDataset.ExportarCsvAsync(IFonteDatasetEta, TextWriter, OpcoesDatasetEta, CancellationToken)` recebe páginas de execuções completas, em ordem UUID textual ordinal, com candidatos/GPS/passagem/destino/journal/distância conferida. IFonteDatasetEta é contrato offline, **sem adapter SQL registrado ou endpoint público**. Fonte certificadora não foi inventada a partir de dados que não provam qualidade.

ExecucaoDatasetEta exige início/fim reais da execução, Qualidade e ReferenciaAuditoria. AuditadaSemProtecao é atestado fornecido pela fonte/auditoria, não detecção automática feita por enum. Fonte precisa provar execução completa e ausência de proteção no intervalo; informar flag sozinho não resolve o impedimento. A API não verifica autenticidade criptográfica do manifesto. Use somente input auditado; testes fornecem dados sintéticos explicitamente conhecidos.

```csharp
// Biblioteca offline; fonteAuditada deve cumprir a procedência descrita acima.
var limites = new OpcoesDatasetEta(inicioUtc, fimUtc, fimTreinoUtc, fimValidacaoUtc);
var contagens = await EtaDataset.ExportarCsvAsync(fonteAuditada, writerCsv, limites, ct);
// Publicar o CSV somente após sucesso. Persistir manifesto separado com contagens,
// critérios/versão, fronteiras, referências de auditoria, snapshot e SHA256 do CSV.
```

Por padrão1 execução/página, máximo10.000 candidatos/execução,1.000 páginas e label até3.600s. Limites são escolhas iniciais configuráveis para evitar consumo excessivo, não benchmark. Fonte deve aplicar limites antes de alocar páginas e não fragmentar execução; exporter também verifica. Duplicatas idênticas geram uma linha e contagem de descarte; mesmas chaves com features/labels divergentes excluem TODAS as versões antes de escrever aquela execução. Paginação repetida/fora de ordem/sem progresso ou capacidade excedida gera exceção: arquivo parcial não é dataset concluído. Writer/fonte pertencem ao chamador; usar arquivo temporário e promover só após sucesso, sem sobrescrever histórico.

`ETA_ML_CANDIDATOS_3A.sql` prepara descoberta read-only com linha e intervalo [inicio,fim), keyset TimestampGps/Id, limite de observações, LEFT JOIN para preservar candidatos incompletos, relações do destino e journal por EventId canônico. ST_LineSubstring::geography calcula trecho à frente. Não é fonte certificadora, não fornece sozinho fronteiras/procedência das execuções e não produz dataset de treino. Índice existente CódigoLinha/TimestampGps ajuda paginação; joins usam identidade única da passagem/PKs estruturais. SQL preparado mas NÃO EXECUTADO; plano e casts espaciais precisam PostgreSQL/PostGIS descartável. Não adicionar índice por hipótese.

## Regras de exclusão

Motivos contabilizados (primeiro motivo por candidato): procedência não auditada/protegida; journal ausente/divergente; dados incompletos; origem diferente de REAL/modal fora ONIBUS/BRT (valores produtivos); viagem/veículo divergentes; volta negativa/diferente; linha/sentido/padrão/versão/parada/ocorrência incompatíveis; topologia inválida; fronteiras inválidas; GPS fora intervalo; execução que cruza corte; tempo não positivo/>horizonte/passagem posterior ao GPS confirmador; alvo atrás/frações inválidas; distância geography ausente/inválida/divergente; duplicada; duplicada conflitante.

Journal precisa PassagemParada schema2, EventId e payload compatíveis com histórico e destino. Comparação de timestamps admite somente9ticks (0,9 microssegundo) pela precisão timestamptz versus JSON; discrepâncias maiores são rejeitadas. GPS exige ambos IDs de próxima ocorrência iguais ao destino; OcorrenciaParadaPadraoId sozinho pode ser observacional e não autoriza label. Nulo no alvo operacional é exclusão, mesmo havendo distância. Diferença entre distância persistida e conferida acima de max(10m,10%) é exclusão conservadora inicial e configurada no código, não correção dos dados. Campos legado/NULL e observações ambíguas não recebem backfill.

Circular: aceita somente ocorrência adiante na mesma volta, sem modulo/reinterpretação de fração menor. Próxima parada depois do wrap pertence à volta seguinte e está fora do MVP solicitado de mesma volta; descartar, não trocar identidade. Execução inferida usa novo ViagemId: não juntar GPS da antiga com passagem da nova. Mesmo viagem/volta é condição necessária, não suficiente; auditoria do intervalo continua obrigatória.

## Impedimento preciso e menor próximo passo

As tabelas permitem candidatos estruturalmente compatíveis e labels numéricos. Porém não há contrato de qualidade versionado por observação nem histórico completo de proteção; IntegridadeCircular corrente não certifica intervalos antigos. Não há timestamp de implantação confiável fornecido ao agente. Datas de commits/migrations não equivalem a execução em produção. Journal não distingue toda a qualidade de interpolação e eventos legados podem faltar. **Não é seguro marcar automaticamente o histórico existente inteiro como pronto para treino.**

Menor caminho sem nova tabela: coleta prospectiva controlada e manifesto externo com versão/build, início real de coleta, fontes, versão estrutural e execuções completas verificadas; inicialmente linhas lineares e viagens iniciadas depois da janela auditada, sem mistura de escritores. Fonte deve conferir início/fim/journal, excluir históricos anteriores/viagens incompletas e documentar gaps. Para circular/intervalos protegidos não verificáveis, manter exclusão ou fornecer evidência externa de execução completa.

Se for necessária certificação AUTOMÁTICA geral, decidir em etapa seguinte um marcador de versão/qualidade da coleta e evidência temporal persistida de proteção, reaproveitando telemetria/journal existentes. Isso pode exigir mudança de contrato/schema e aprovação antes de implementação; nenhuma migration/tabela foi criada nesta etapa. Não é proposta de nova arquitetura de ingresso e não reabre retry estabilizado. Não escolher retrospectivamente um cutoff e chamar de prova.

## Avaliação futura na 3B

Separação temporal por execução completa: TRAIN antes do primeiro corte, VALIDATION entre cortes, TEST depois. Execuções atravessando cortes são purgadas integralmente; toda observação da viagem permanece no mesmo split. Datas fornecidas explicitamente e registradas no manifesto, nunca split aleatório de linhas. Caso queira agrupar várias voltas da mesma viagem, agrupamento segue ViagemId e não volta.

Baseline: velocidade do veículo causal com fallback à mediana histórica do treino por linha/sentido/alvo/faixa de horário. Também baseline simples distância/velocidade constante calibrada só no treino. Nenhum baseline foi treinado ou valor de MAE inventado. Encoder/imputação/estatísticas fit no treino; categorias raras/desconhecidas devem usar fallback, não retirar linhas difíceis do teste silenciosamente.

Medir MAE/RMSE em segundos, cobertura (GPS elegíveis/observados, candidatos com alvo/journal, válidos/exportados, previsões emitidas), descartes por motivo e resultados por linha/sentido, hora, faixa de distância e modal. Labels ausentes não são0. Linhas com poucos exemplos: reportar suporte e fallback; não prometer estimativa específica aprendida. Não misturar erro do modelo com qualidade do tempo interpolado.

## Exemplo exclusivamente ilustrativo

Fixture sintética: ônibus A1, linha292, viagemA/volta0, GPS `2026-10-10T12:00:00Z`, próxima ocorrênciaO na mesma versão/sentido, distância geography200m, velocidade20km/h, média causal18km/h, passagem confirmada `12:01:00Z`. Features locais: hora9/dia6 (sábado); label60s. Manifesto sintético da fixture certifica intervalo sem proteção. Isto NÃO é linha extraída do PostgreSQL nem resultado de treino.

## Custo, testes e pendências

Memória proporcional ao limite de execuções/página e candidatos/execução, não ao histórico inteiro; buffering de uma execução detecta conflitos antes da escrita. Ordenação por viagem deve ser feita pela fonte (a query de descoberta é ordenada por GPS, não diretamente consumível como IFonteDatasetEta). Para volumes maiores, spool offline por viagem, nunca carregar tudo no backend. JSON e metadados duplicados custam bytes; sem benchmark ou estimativa de throughput. Sem custo adicional no caminho GPS porque nada foi registrado no Program.

SQL parametrizado precisa janela congelada e transação READ ONLY, limite validado e índices reais revisados; não executar EXPLAIN ANALYZE em produção. Preparado `EtaDatasetPostgresTests.ConsultaCandidatos_JournalGeographyEPaginacaoTimestampIgual`, reutilizando ViagemOperacionalFixture: telemetrias pelo repository produtivo, passagem/journal pelo materializador, leitura READ ONLY e duas observações no mesmo timestamp com UUIDs diferentes; verifica payload/label, distância positiva, paginação e preservação da incompleta para descarte. Dados exclusivos são removidos em finally. Compilado, NÃO EXECUTADO. Teste não certifica procedência do dataset nem simula matching operacional inteiro; valida a consulta e fontes relacionais. Cobertura futura após adapter inclui circular real/payload divergente/outbox pendente. Não foi criado teste que fabrique procedência simplesmente setando enum no banco. SQL não foi testado em PostGIS nesta execução.

Comando manual futuro, somente depois de confirmar POSTGIS_TEST_CONNECTION/REDIS_TEST_CONNECTION exclusivamente descartáveis; fixture cria schema, migra e remove com CASCADE:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetPostgresTests' --verbosity normal
```

Testes locais novos cobrem associação, viagem/volta/sentido/ocorrência/versão, tempo anterior, circular sem wrap, nova execução inferida, incompletos, journal, deduplicação/conflito, paginação/limites, features sem campos futuros, splits e distância não aproximada por fração. Comandos executados recompilando código, sem `--no-build`:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetTests' --verbosity quiet
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~TelemetriaMlTests|FullyQualifiedName~EtaV2FoundationTests|FullyQualifiedName~IntegridadeCircularRegraTests)' --verbosity quiet
```

Resultado final: **131 aprovados, zero falhas/ignorados**, incluindo28 casos novos do dataset; execução dos testes227ms, com recompilação --no-restore. Primeira compilação detectou dois erros de inferência params/new nos testes, corrigidos antes da validação;23 casos iniciais passaram. Rodadas intermediárias126/129/130 passaram, sem falha. Aviso de nulidade introduzido no novo código foi corrigido antes da rodada final; restaram cinco avisos preexistentes. Verificações finais incluem distância geography, allowlist, precisão dos timestamps e modais produtivos ONIBUS/BRT. Teste real compilado, não executado. Não executados SQL/serviços reais nem treino.

**Pronto para3B:** schema/allowlist, gerador CSV, validação local, contadores e estratégia de avaliação. **Ainda necessário:** fonte auditada/manifesto de coleta, fechamento das execuções, adapter offline, teste PostGIS e mensuração de coverage/tamanho; só então dataset real e baseline/modelo simples. Entrega deliberadamente independente conforme fallback autorizado quando a persistência não certifica todos os labels. Sem produção/SSH/Docker/deploy/migrations/infraestrutura/ETA público/algoritmos operacionais/histórico real alterados, sem dependências ou commit.

## Etapa 3A.1 — correção da preparação, 07/10/2026

Primeira execução real informada pelo usuário em PostgreSQL16/PostGIS3.4+Redis7 descartáveis: chegou à preparação da fixture e falhou em HistoricoEventoRepository.PersistirAsync, ANTES da consulta do dataset. Causa comprovada, classificação A: o teste criou TimestampEvento=GPS+70s e TimestampPassagem=GPS+60s. EventoViagemValidator.Validar (linha213) exige igualdade entre esses instantes e rejeitou com Passagem estrutural incompleta (217). Nenhum UUID/campo estrutural estava ausente; a divergência era temporal. Produção constrói Evento(atual,"PassagemParada",passagem), usando o mesmo instante no evento/passagem, com GPS confirmador separado (ViagemOperacional.cs155–161).

Correção exclusivamente no harness: CriarPassagem compartilhada entre teste real e regressão local, argumentos nomeados/estrutura completa e TimestampEvento=TimestampPassagem=GPS+60s, mantendo TimestampGps=GPS+70s. Fábrica produtiva Evento é privada; helpers equivalentes encontrados em ViagemOutboxBatchTests são privados/dependentes de fixture e usam timestamps coincidentes, sem representar esta interpolação. Não alterada produção para expor helper. Nova regressão chama o mesmo builder do teste real e validator produtivo; exige estrutura aceita e rejeição do formato anterior. Reproduzida uma falha local antes da correção. SQL/assertivas de journal/label/geography/paginação/limpeza permanecem intactos.

Correção ainda depende de NOVA EXECUÇÃO REAL manual pelo comando EtaDatasetPostgresTests acima. O agente não executou serviços externos nem a consulta; não declarar SQL aprovado a partir da regressão local. Resultados locais/compilação finais registrados no HISTORICO.

### Esclarecimento de semântica — alvo operacional e elegibilidade (3B.2A)

Veredito A, pela implementação e contrato atuais: a confiabilidade da viagem não torna todo GPS elegível. Este documento já exige ambos os IDs de ocorrência iguais ao destino (Regras de exclusão); isso é intencional no contrato noponto-eta-gps-v1, não comparação de parada atual com próxima.

1. O DTO PosicaoVeiculoDto.ProximaOcorrenciaParadaPadraoId contém o alvo do matching observacional; não é um ID de parada atual/última parada. O registro persistido não conserva esse matching separadamente quando existe próxima operacional: EventoTelemetriaMlFactory.Criar grava OcorrenciaParadaPadraoId = operacional?.ProximaOcorrenciaOperacional?.Id ?? posicao.ProximaOcorrenciaParadaPadraoId. Portanto o campo geral é próximo alvo operacional ou fallback, não sempre ocorrência atual.
2. EventoTelemetriaMl.ProximaOcorrenciaParadaPadraoId recebe exclusivamente operacional?.ProximaOcorrenciaOperacional?.Id. Se não nulo, os dois campos recebem o mesmo ID por construção. TelemetriaMlRepository persiste ambos diretamente, sem permuta. Sem próxima operacional confiável, ViagemId/Volta podem permanecer e fallback pode existir, mas nenhum alvo operacional é inventado.
3. SQL3A liga OcorrenciasParadasPadroes o.Id ao GPS.ProximaOcorrenciaParadaPadraoId, obtendo parada/posição/versão/padrão/sentido/linha do destino. HistoricoPassagens é associado por ViagemId + ocorrência desse alvo + Volta, e journal pelo EventId canônico da passagem. SQL não exige igualdade do campo geral do GPS com a próxima; preserva candidatos incompletos via LEFT JOIN, a filtrar depois.
4. EtaDataset.Avaliar recebe g=telemetria, h=passagem, d=destino e j=journal. Primeiro valida journal/identidades mínimas; próxima operacional nula/vazia é DadosIncompletos. Depois exige g.ProximaOcorrenciaParadaPadraoId=d.Id, g.OcorrenciaParadaPadraoId=d.Id e h.OcorrenciaParadaPadraoId=d.Id, além de versão/sentido/parada/viagem/volta compatíveis. O campo geral concorda com o alvo explícito pelo contrato da factory; a ocorrência histórica identifica a passagem pela parada que gerará a label.
5. Após procedência/fronteiras/split, exige posição destino > posição GPS na mesma volta, sem wrap, distância conferida e label positiva dentro do horizonte. Label = h.TimestampPassagem - g.TimestampGps. GPS/timestamp da passagem confirmadora não vira feature futura.

Cenário “próxima operacional válida e campo geral diferente por fallback” não é produzido pela factory inspecionada: fallback só é escolhido quando o alvo operacional é ausente. Valores assim podem ser montados/adulterados, antigos ou de outro escritor; nenhum caso real foi observado pelo agente e eles não são elegíveis automaticamente no contrato atual. Não há evidência para correção B. Alterar a factory para conservar matching separado exigiria revisar explicitamente contrato, e não relaxar validação retrospectivamente.

Cobertura: TelemetriaMlIdentidadeTests.Compativel_PreservaIdentidadeEContratoDoConsumidor usa IDs distintos para matching e operacional e confirma que ambos os campos do evento recebem o operacional. ProximaOperacionalNula_CompativelMantemViagemEFallbackObservacional cobre viagem/volta confiáveis com próxima nula e fallback. EtaDatasetTests já cobria próxima ausente em incompleto, destino/passage incompatíveis, label60s e circular sem wrap, mas não adulteração isolada do campo geral. Acrescentados um fato (viagem confiável/fallback sem próximo alvo rejeitada) e uma teoria de dois casos (próximo alvo válido com geral diferente/nulo rejeitado). São controles negativos do contrato atual, não prova de um caso elegível real. EtaDatasetPostgresTests e fixture/geradorPostGIS3B.1 usam IDs iguais nos positivos e não cobrem essa adulteração isolada no banco; integração não executada nesta análise.

A auditoria3B.2A permanece inventário estrutural mais amplo: obs opcional/existente/mesma versão e alvo operacional obrigatório. Aceitar obs distinto/nulo nesse inventário não implica aceitar na exportação3A; são anomalias frente à factory atual, a investigar seletivamente por viagem. A separação não autoriza fallback como label, não atesta AuditadaSemProtecao e não demonstra que a antiga igualdade descartava dados produtivos válidos. Sem alteração em SQL3A/EtaDataset/runtime/auditoria SQL nesta análise.
