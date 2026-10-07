# Release candidata 3A.3 — checkpoint para coleta ETA/ML

Data: 2026-10-07. Preparação para revisão e commit futuro; nenhum staging, commit, deploy, migration ou artefato de produção executado/gerado. Somente este documento e acréscimo em HISTORICO.md foram editados nesta etapa; build/testes atualizaram saídas locais ignoradas.

## 1. Inventário e delimitação

Base: `fcad913cf6fa36a9949c5e25614cd4f13ddb39cf`.
SDK local: `9.0.306`; projeto `net9.0`. Índice vazio na inspeção.
O HEAD isolado continua insuficiente.

Comparação inicial com 3A.2: os mesmos 22 arquivos rastreados modificados e 23 não rastreados. Diff rastreado inicial: 614 inserções, 61 remoções. Documentos relevantes estão em `NoPonto/docs`, ignorado por `NoPonto/.gitignore`; o SQL foi restaurado manualmente para `NoPonto/ETA_ML_CANDIDATOS_3A.sql`. Os comandos abaixo preservam explicitamente sete documentos ignorados e o SQL no path restaurado, incluindo este novo relatório. As listas abaixo são também o inventário de 53 paths propostos para o checkpoint, somados aos arquivos inalterados já existentes no commit base. O staging existente na atualização documental foi preservado.

Entre os modificados: 15 arquivos runtime, seis testes/fixture e .gitignore. Entre os não rastreados: seis arquivos runtime, 14 testes, uma migration, AGENTS e HISTORICO. Não omitir os partials ou o repository Redis: o build local pode passar com arquivos que desapareceriam em um checkout do HEAD.

Configurações, compose, csproj e demais migrations já rastreados fazem parte da base, sem diferenças propostas nesta etapa. O csproj compila implicitamente os novos arquivos GPS/repositories/migration e os testes em 5-Testes no mesmo projeto; não é necessário criar referência manual. As exclusões existentes de código legado/lab-output foram mantidas.

## 2. Segurança e exclusões

Revisados os paths candidatos, configurações appsettings, launchSettings e compose, sem publicar valores sensíveis. A busca por padrões de credenciais nos candidatos/documentos não identificou segredo de ambiente a incluir. O literal de autenticação encontrado em ViagemOperacionalIntegracaoTests.AclSemXadd_NaoImpedeCommitPostgres pertence ao usuário ACL temporário criado pelo próprio teste, removido no finally; não é credencial existente. Compose referencia variáveis de ambiente, sem acrescentar seus valores ao checkpoint. Uma busca por padrões não garante ausência universal de secrets: a revisão do diff staged futuro continua obrigatória.

Excluir .env, .env.local.example local, qualquer credencial/override privado, bin, obj, caches, dumps, logs, lab-output, saídas de testes e artefatos locais. Nenhum desses paths está na lista de staging. A enumeração de ignorados encontrou árvores bin/obj/lab-output com caminhos excessivamente longos no Windows; isso limita a enumeração completa, não autoriza copiá-las ou limpá-las. Nenhuma limpeza realizada. Não foram encontrados paths rastreados de bin/obj/.env/dump/log no filtro consultado.

Deixar fora das novas inclusões os documentos de contexto/ideias pessoais `NoPonto/docs/CONTEXTO_NOPONTO.md` e `IDEIAS_FUTURAS_NOPONTO.md`: não são necessários para reproduzir esta estabilização e permanecem intactos no workspace. `NoPonto/docs/rail-schedule-architecture.md` já é rastreado e permanece na base, sem necessidade de staging adicional. Não forçar a pasta docs inteira nem incluir arquivos locais por glob.

## 3. Consistência do conjunto

| Requisito | Implementação incluída / evidência |
| --- | --- |
| Mudança operacional | ViagemOperacional.Mudanca.cs, repository e MatchingOperacionalPlausivel produzido pelo enriquecimento |
| Codec compatível | ViagemOperacionalCodec.cs, array de 27 posições preservado; candidatos adicionais validados |
| Integridade circular | partials de regra/repository, extensão JSONB separada, proteção de identidade e recuperação |
| Migration | 20261006180000_IntegridadeCircularDuravel: coluna nullable IntegridadeCircular + check de objeto JSONB |
| Motivo de fim | PerdaContinuidadeCircular em ViagemOperacional.Integridade.cs; HistoricoEventoRepository valida/preserva motivo |
| Retry Redis | RetryOperacionalGps.cs e PendenciaOperacionalGpsRepository.cs; predecessor físico, reenriquecimento e confirmação durável |
| ML/ETA shadow | TelemetriaMl.cs e EtaV2Shadow.cs recusam identidade incompatível/protegida |
| Sampling existente | TelemetriaMlSampling.cs já rastreado na base; política e registros existentes preservados |
| Dataset offline | EtaDataset.cs + testes + SQL/documentação explicitamente preservados |

Program.cs registra ViagemOperacionalRepository (linha 500), options/store/reenriquecimento/retry (503–508), enriquecedor (531), outbox (575), sampling (578–580), ingresso/persistência/worker ML (584–591). EtaDataset é componente offline, sem endpoint/hosted service novo.

**Schema obrigatório mesmo com flag desligada.** LerDuravelAsync consulta IntegridadeCircular incondicionalmente. Program.cs:623 chama Database.Migrate no startup; iniciar o backend não é uma verificação somente de leitura. A implantação deverá verificar/aplicar previamente a migration conforme plano aprovado, sem depender de startup experimental.

Não misturar writers antigos com esta versão. Binários antigos podem desconhecer candidatos/proteção/motivo de fim. Down da migration bloqueia remover a extensão para evitar perda de proteção. Rollback operacional exige binário compatível com contrato e dados atuais; desligar a flag não desfaz eventos/proteções.

Retry é de melhor esforço, limitado por TTL/tentativas/capacidade Redis; não substitui journal durável, não garante recuperação após perda de Redis e não republica telemetria ML histórica. Essas limitações aceitas não foram alteradas.

## 4. Verificação local executada

```powershell
dotnet --version
dotnet build NoPonto/NoPonto.csproj --no-restore --verbosity quiet
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~IntegridadeCircularRegraTests|FullyQualifiedName~WrapDuranteCandidatoTests|FullyQualifiedName~CancelamentoCandidatoCursorTests|FullyQualifiedName~MudancaOperacionalRegraTests|FullyQualifiedName~MudancaOperacionalIntegracaoLogicaTests|FullyQualifiedName~ViagemOperacionalRegraTests|FullyQualifiedName~ViagemOperacionalCodecCompatibilidadeTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~TelemetriaMlTests|FullyQualifiedName~GpsEnriquecimentoServiceTests|FullyQualifiedName~GpsPollingCadenciaTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsMatchingBatchOrquestracaoTests|FullyQualifiedName~EtaV2FoundationTests|FullyQualifiedName~EtaV2HardeningTests|FullyQualifiedName~ViagemObservadaServiceTests|FullyQualifiedName~ViagemOutboxCleanupPolicyTests|FullyQualifiedName~GpsBrtTelemetriaTests|FullyQualifiedName~RetryOperacionalGpsTests|FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlSamplingTests)&FullyQualifiedName!~Protecao_OutageGlobal_ExecutaUmaSondaEPulaChunksRestantes' --verbosity quiet
git diff --check
```

Resultados: SDK 9.0.306; build aprovado, zero erros e zero warnings reportados pelo build incremental, aproximadamente 1,96 s. Testes com compilação: **492 aprovados, zero falhas, zero ignorados**, duração reportada dos testes 2 s. Diff check aprovado. Git avisa normalização LF/CRLF em arquivos preexistentes; não houve normalização deliberada.

O filtro acima registra exatamente as classes executadas: dataset, retry, circularidade, cancelamento, mudança operacional, codec, identidade ML, shadow, polling e outbox lógico. O teste Protecao_OutageGlobal_ExecutaUmaSondaEPulaChunksRestantes foi excluído explicitamente porque utiliza sonda de infraestrutura; não foi contado como aprovação. Classes de integração PostgreSQL/PostGIS/Redis foram compiladas, sem execução. Aprovações reais manuais anteriores permanecem registradas no histórico, não foram repetidas aqui.

**Localização SQL resolvida manualmente:** o arquivo foi restaurado para `NoPonto/ETA_ML_CANDIDATOS_3A.sql`, compatível com a busca por ancestrais de EtaDatasetPostgresTests.LocalizarConsulta. Test-Path confirmou o arquivo. Conforme resultado informado pelo operador, EtaDatasetPostgresTests foi reexecutado manualmente no ambiente descartável: **1 aprovado, 0 falhas**. Consulta, journal, geography e paginação continuam aprovados. Não existe mais bloqueio de reprodução relacionado ao path do SQL. O agente não repetiu a integração nem alterou SQL, código ou testes nesta atualização.

Build --no-restore usa dependências já presentes localmente; não prova restore em uma máquina limpa. Nenhum download/restore, serviço externo ou artefato Release/publicado nesta execução.

## 5. Staging e commit — comandos FUTUROS, NÃO EXECUTADOS

Executar somente após revisar este plano e quaisquer mudanças ocorridas depois da auditoria. A partir da raiz do repositório. Já existe staging nesta atualização: revisar seu conteúdo antes de qualquer comando futuro, sem remover trabalho existente. Nenhum comando abaixo foi executado pelo agente.

```powershell
git status --short
git diff --cached --name-only
git add -- `
  'NoPonto/.gitignore' `
  'NoPonto/2-Application/DTOs/PosicaoApiDto.cs' `
  'NoPonto/2-Application/Services/GPS/EtaV2Shadow.cs' `
  'NoPonto/2-Application/Services/GPS/GpsEnriquecimentoService.cs' `
  'NoPonto/2-Application/Services/GPS/GpsPollingService.cs' `
  'NoPonto/2-Application/Services/GPS/GpsPoolingOptions.cs' `
  'NoPonto/2-Application/Services/GPS/ProjecaoOperacional.cs' `
  'NoPonto/2-Application/Services/GPS/TelemetriaMl.cs' `
  'NoPonto/2-Application/Services/GPS/ViagemObservadaService.cs' `
  'NoPonto/2-Application/Services/GPS/ViagemObservadaState.cs' `
  'NoPonto/2-Application/Services/GPS/ViagemOperacional.cs' `
  'NoPonto/4-Data/Interfaces/IViagemObservadaRepository.cs' `
  'NoPonto/4-Data/Repositories/HistoricoEventoRepository.cs' `
  'NoPonto/4-Data/Repositories/ViagemOperacionalCodec.cs' `
  'NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs' `
  'NoPonto/5-Testes/EtaV2FoundationTests.cs' `
  'NoPonto/5-Testes/GpsBrtTelemetriaTests.cs' `
  'NoPonto/5-Testes/TelemetriaMlTests.cs' `
  'NoPonto/5-Testes/ViagemObservadaServiceTests.cs' `
  'NoPonto/5-Testes/ViagemOperacionalFixture.cs' `
  'NoPonto/5-Testes/ViagemOperacionalIntegracaoTests.cs' `
  'NoPonto/Program.cs'
```

Arquivos não rastreados necessários:

```powershell
git add -- `
  'AGENTS.md' `
  'NoPonto/2-Application/Services/GPS/EtaDataset.cs' `
  'NoPonto/2-Application/Services/GPS/RetryOperacionalGps.cs' `
  'NoPonto/2-Application/Services/GPS/ViagemOperacional.Integridade.cs' `
  'NoPonto/2-Application/Services/GPS/ViagemOperacional.Mudanca.cs' `
  'NoPonto/4-Data/Repositories/PendenciaOperacionalGpsRepository.cs' `
  'NoPonto/4-Data/Repositories/ViagemOperacionalRepository.Integridade.cs' `
  'NoPonto/5-Testes/CancelamentoCandidatoCursorTests.cs' `
  'NoPonto/5-Testes/EtaDatasetPostgresTests.cs' `
  'NoPonto/5-Testes/EtaDatasetTests.cs' `
  'NoPonto/5-Testes/IntegridadeCircularPostgresTests.cs' `
  'NoPonto/5-Testes/IntegridadeCircularRegraTests.cs' `
  'NoPonto/5-Testes/MudancaOperacionalIntegracaoLogicaTests.cs' `
  'NoPonto/5-Testes/MudancaOperacionalPontaAPontaTests.cs' `
  'NoPonto/5-Testes/MudancaOperacionalRegraTests.cs' `
  'NoPonto/5-Testes/RetryOperacionalGpsRedisIntegracaoTests.cs' `
  'NoPonto/5-Testes/RetryOperacionalGpsTests.cs' `
  'NoPonto/5-Testes/TelemetriaMlIdentidadeTests.cs' `
  'NoPonto/5-Testes/ViagemOperacionalCodecCompatibilidadeTests.cs' `
  'NoPonto/5-Testes/WrapDuranteCandidatoIntegracaoTests.cs' `
  'NoPonto/5-Testes/WrapDuranteCandidatoTests.cs' `
  'NoPonto/HISTORICO.md' `
  'NoPonto/ETA_ML_CANDIDATOS_3A.sql' `
  'NoPonto/Migrations/20261006180000_IntegridadeCircularDuravel.cs'
```

Somente os documentos ignorados aprovados:

```powershell
git add -f -- `
  'NoPonto/docs/AUDITORIA_2A_2N.md' `
  'NoPonto/docs/RECUPERACAO_GPS_2A_2O.md' `
  'NoPonto/docs/RETRY_OPERACIONAL_2A_2O_1.md' `
  'NoPonto/docs/ENCERRAMENTO_OPERACIONAL_2A_2P.md' `
  'NoPonto/docs/ETA_ML_DATASET_3A.md' `
  'NoPonto/docs/ETA_ML_COLETA_LIMPA_3A_2.md' `
  'NoPonto/docs/RELEASE_CANDIDATA_3A_3.md'
```

Revisão final e commit proposto, igualmente futuros:

```powershell
git diff --cached --check
git diff --cached --stat
git diff --cached --name-only
git diff --cached
git status --short
git commit -m "checkpoint: estabiliza viagens e prepara coleta limpa ETA/ML"
git rev-parse HEAD
```

Confirmar todos os 53 paths propostos e ausência de secrets/saídas locais no staged diff; as contagens não substituem revisar o conteúdo. Não executar git add . ou git add -A. Não incluir modificação posterior sem nova revisão. Nenhum desses comandos de staging/commit foi executado pelo agente.

## 6. Manifesto de reprodução e implantação futura

Registrar junto da release, fora de arquivos com credenciais:

- Base commit acima e commit futuro real (ainda inexistente); árvore limpa/revisada do checkpoint e lista de paths.
- SDK 9.0.306, target net9.0, versões de dependências do csproj e ambiente real de build. Registrar sistema operacional/RID e comandos de restore/build/publish usados futuramente.
- Hash SHA-256 do pacote/imagem efetivamente produzido, DLLs principais e manifesto de configuração sanitizado; digest imutável da imagem se houver. **Ainda não há artefato nem hashes de produção.**
- Migration esperada 20261006180000_IntegridadeCircularDuravel e estado efetivo de __EFMigrationsHistory/schema antes de iniciar writers.
- Resultado/filtro local acima e referências às integrações manuais anteriores, sem apresentá-las como reexecução deste working tree.
- Configuração efetiva (não apenas defaults): conexões por identificador de ambiente, flags operacionais/ETA/shadow, sampling e limites de retry. Nunca registrar senhas ou connection strings completas.
- Marco UTC real de coleta somente após health e persistência comprovada; manifesto de qualidade/proveniência para novas execuções completas, conforme ETA_ML_COLETA_LIMPA_3A_2.md.

Flags/configuração não foram alteradas: MudancaOperacionalHabilitada default false; manter desligada até autorização específica. appsettings/default compose de sampling estão desabilitados; configuração efetiva de destino não foi consultada. Para coleta amostrada, Enabled precisa estar true no ambiente aprovado, LinePercentage=10 e BlockMinutes=60, mantendo seed/algoritmo existente. Conferir flags TelemetriaMl/ETA/shadow efetivas no destino e preservá-las conforme decisão aprovada, sem inferir seu valor a partir do workspace. Valores de defaults não comprovam overrides do destino.

## 7. Sampling e checklist mínimo

Sampling atual **não é Bernoulli por GPS**. Usa deterministic hash por modal + código da linha + bloco UTC de 60 minutos; LinePercentage=10 e Enabled=true são necessários para aplicar esse contrato. O bloco inteiro de determinada linha/modal pode ser selecionado ou não. O relógio de avaliação determina o bloco; não assumir sorteio independente por veículo/observação. Não houve mudança de política, seed, taxa ou flags. Enabled=false não significa coleta aleatória de 10%; acompanhar a política efetiva antes de declarar coverage.

Antes do deploy:

1. Revisar staged diff e produzir commit real, preservando o SQL restaurado em NoPonto/ETA_ML_CANDIDATOS_3A.sql e o resultado da reexecução manual aprovada.
2. Produzir artefato a partir desse commit com toolchain registrada, hash/digest; não empacotar o workspace local inteiro.
3. Confirmar backup/recuperação e plano de rollback com binário compatível; nenhuma remoção de IntegridadeCircular.
4. Validar schema/migration e coordenar versão de writers/consumers do monólito. Não iniciar binário antigo ou novo como probe read-only.
5. Revisar configuração efetiva sanitizada, sampling 10% habilitado conforme autorização, mudança operacional ainda desligada, flags ETA/shadow aprovadas; sem aumento de taxa implícito.
6. Executar health funcional: GPS novo, Redis, avanço durável, outbox/journal/passagens idempotentes, telemetria persistida e recursos. GET raiz sozinho não comprova esses componentes.
7. Registrar marco UTC e começar seleção oficial por execuções novas/completas e qualidade auditada; não certificar viagens antigas apenas pela data de implantação.

Depois do deploy medir linhas/modal candidatas e selecionadas, coverage por linha/hora, telemetrias persistidas, atraso/backlog de outbox/ML, falhas/conflitos operacionais, proteções circulares e pendências/expiração de retry. Comparar selecionadas com persistidas para detectar perdas; 10% determinístico não garante 10% observado em toda janela ou linha. Não apagar histórico/proteções/outbox nem fazer purge para obter health.

## 8. Estado final

Preparação documental e regressões locais concluídas; localização do SQL resolvida e reexecução manual de EtaDatasetPostgresTests aprovada (1 aprovado, 0 falhas). Não existe mais bloqueio de reprodução relacionado ao path do SQL. Checkpoint ainda não existe como commit/artefato: depende de revisão/commit manuais, manifesto efetivo e implantação aprovada. Persistem a validação de configuração de destino e os limites já documentados de retry/coverage/qualidade de labels. Nenhuma nova funcionalidade, configuração, sampling ou feature flag foi modificada. O staging existente foi preservado nesta atualização.
