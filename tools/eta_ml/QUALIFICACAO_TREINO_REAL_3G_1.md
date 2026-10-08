# 3G.1 — qualificação e execução offline condicionada

**Prontidão real: REPROVADA.** Não existe CSV certificado/modelo real nesta etapa. Snapshot exploratório não foi treinado ou recertificado. Foram lidos AGENTS, histórico, documentação 3A/3B.2C/3C/3F, mapa de migração e implementações nos dois repositórios. Nenhuma produção, SSH, Docker, banco, API, migration, agendamento, deploy ou promoção foi executada.

## 401 distâncias: evidência e decisão

| Etapa/campo | Significado efetivo |
|---|---|
| GpsItinerarioRepository.CombinedSetBased global_resultado/operacional_resultado | ST_Distance(coordenada recebida, Localizacao da parada matching)::geography; distância direta |
| DTO DistanciaProximaParadaMetros | copia distancia_parada_metros; não o trecho pela rota |
| TelemetriaMl factory DistanciaProximaParadaMetros | copia DTO sem recomputar distância para o alvo operacional |
| TelemetriaMl OcorrenciaParadaPadraoId | próxima operacional quando disponível, senão fallback matching |
| TelemetriaMl ProximaOcorrenciaParadaPadraoId | próxima operacional explícita, sem fallback |
| SQL3A distancia_rota_conferida_metros | ST_Length(ST_LineSubstring(PadroesVersoes.Geometria, PosicaoNaRota, alvo.PosicaoTracado)::geography) |
| EtaDataset.Avaliar | valida trecho positivo/finito/comprimento+10 e depois compara distância direta informada com trecho, tolerância max(10m,10%) |
| diagnose.technical | reproduz essa comparação; sourcepins impedem divergência silenciosa |
| CSV distancia_metros | trecho conferido, caso candidato passe; não a distância direta |

SQL 3A faz descoberta/joins/geografia; **não aplica a exclusão de divergência**. O descarte é de EtaDataset (e diagnóstico que espelha a política). SQL seleciona destino por próxima ocorrência operacional e associa HistoricoPassagens pela mesma viagem/volta/ocorrência. Journal e estrutura versionada foram conferidos no diagnóstico; essa conferência comprova associação e origem da distância calculada, não ausência de proteção durante toda a viagem nem exatidão física do matching recebido.

Reexecução de distance_sample, somente arquivos selados/hashes conferidos, reproduziu 401 divergências, 400 a <=10m da aproximação esférica direta para a parada operacional, 283 com distância informada menor que trecho. Saída privada nova: `outputs/distance-evidence-3g1-01/distance-sample.json`; não modifica bundle ou relatório original. A amostra determinística por linha/outlier permanece reproduzível. Direta esférica não é prova esferoidal PostGIS; não confundir comparação aproximada com tolerância da política.

Um outlier tem ~45,04m informados, trecho184,22m e direta ao alvo operacional~191,16m. O alvo original de matching foi substituído na identidade da telemetria; não é possível explicar individualmente essa diferença apenas com os campos preservados. Não atribuir todos os401 a dado fisicamente válido. Identidade/tempo/posição/journal não apontaram outros descartes técnicos nos758 associados, mas faltam evidências independentes de qualidade do matching/proteção. Há diferença semântica comprovada e possível descarte indevido; não há prova retrospectiva de elegibilidade definitiva.

Menor revisão proposta: manter distância de treino como trecho geography da versão e alvo operacional, mantendo validações de identidade, frações, alvo à frente/mesma volta, comprimento, label e journal; separar distância direta como diagnóstico com semântica explícita. Não aumentar tolerância nem substituí-la por infinito, não substituir trecho por direta e não modificar labels. Isso altera **política de validação 3A**, embora preserve features/labels; requer versionamento da política/exporter e revisão de sourcepins/testes/documentação, para distinguir artefatos obtidos antes/depois. A aprovação do usuário para **revisão futura isolada com regressões** foi recebida nesta sessão; como solicitado na pergunta, SQL3A/EtaDataset/401 descartes continuam inalterados nesta etapa. Essa implementação fica em alteração própria, não acoplada à orquestração.

Regressões necessárias nessa revisão: rota curva com direta legitimamente diferente; geometria versionada/alvo divergente; trecho nulo/não finito/negativo/maior que comprimento; wrap proibido; coordenada/projeção inválida; identidade original matching indisponível; journal/label/split inalterados. O outlier continua investigado/excluído conforme evidência, sem reclassificação automática.

## AuditadaSemProtecao: o que falta

Evidências existentes: perfil oficial pós-fix, cutoff/release/migration/sampling conhecidos; snapshot v2 selado e restore/conteúdo/geometrias atestados pelo operador; journal início/fim/passagem; associações operacionais e distâncias do SQL3A. Todos permitem verificações necessárias de integridade, limites, execução, alvo, label e split, não certificação universal.

`IntegridadeCircular` é estado corrente durável por veículo (ViagemOperacionalRepository substitui Estado/IntegridadeCircular), não ledger completo de todos os períodos. ViagemOperacional.Integridade pode reiniciar o registro e o elimina para topologia não circular. TelemetriaMl não persiste qualidade versionada/estado de proteção por decisão. Ausência de erro/marker no snapshot, journal completo ou identidade válida em GPS amostrados não prova que a viagem inteira esteve sem proteção; sampling10%/blocos60 não cobre todo instante. Também não prova ausência de perda da própria telemetria/outbox nem todos os estados não amostrados.

Certificação automática retrospectiva das16 viagens: **não demonstrável com essas fontes**. Referência de auditoria seletiva operacional do snapshot não foi promovida a AuditadaSemProtecao. Auditoria externa de sessão controlada só é aceitável se possuir evidências completas reais e responsável; esta ferramenta confere arquivos/intervalos, não decide a veracidade do atestado.

Proposta prospectiva separada, não implementada: ledger durável versionado por viagem/execução com início/fim, entrada/saída de proteção/ambiguidade e motivos, release/versão estrutural, sequências/gaps/restarts e confirmação de gravação. Ausência de um evento só vale como ausência de proteção se completude do ledger estiver comprovada. Eventos devem capturar decisões não amostradas; preservar sampling GPS e marcar desconhecido em perda/restart. Priorizar journal/outbox existentes após auditoria de schema/volume; qualquer contrato/migration/coleta requer aprovação própria. Esta sessão não aprova sua implantação.

## Comando ML e execução segura

Novo `ml/tools/eta_ml/qualify_real.py` reutiliza pipeline.load, dataset_profile e volume_report (mesmos gates de check_volume). Não altera os15 arquivos originalmente migrados nem treina para preencher faltas. Valida:

- CSV/hash/contrato/features/perfil completo, procedência existente e rejeição de synthetic/fixture_seed/relatório exploratório;
- qualidade/labels/tempo, causalidade de hora, viagens inteiras/splits e duplicatas, usando load sem regras paralelas;
- volume canônico: >=10.000 linhas, >=14 dias UTC, >=200/50/50 viagens TRAIN/VALIDATION/TEST e alguma linha com30/10/10 viagens e8horas TRAIN;
- audit/options/sql do exporter preservados por hash, audit idêntico à collection e limites do config idênticos aos do exporter; SQL idêntico à cópia canônica 3A em contracts;
- pacote externo de evidências íntegro: referências existentes, responsável, intervalo que cubra a viagem inteira, fonte declarada, verificação de ausência de proteção/completude e arquivos de suporte com hashes. Caminhos limitados ao diretório do pacote.

`evidence.real.example.json` contém placeholders e flags false: **não passa**. Não preencher true sem prova. Um documento do próprio relatório exploratório não é prova de ausência de proteção. Esse pacote adicional fecha a lacuna de referências que antes eram só strings; não autentica identidade do auditor nem é assinatura digital. Arquivos/artefatos são insumos locais confiáveis, hashes garantem integridade, não autenticidade contra falsificação deliberada. A garantia é fail-closed para inputs insuficientes/inconsistentes/sintéticos identificáveis; não existe solução criptográfica que detecte alguém reescrevendo maliciosamente todos os arquivos e hashes sem raiz de confiança externa.

Dry-run (default, ou --dry-run) escreve `OUTPUT/result/qualification.json` e retorna2 se não pronto. --execute valida tudo **antes** de chamar o treino existente; congela CSV/manifest/config, reconfere hashes/evidências, treina/avalia no staging, acrescenta recibo vinculado aos hashes no model.manifest e publica resultado por rename local atômico. Modelo/metrics em `OUTPUT/result/model/`; inputs em `OUTPUT/result/inputs/`. Falha não publica saída e limpa somente staging temporário próprio. Não sobrescreve output existente: repetição deixa o resultado anterior intacto e exige nome novo. Reports iguais para mesmos inputs; caminho temporário não entra no recibo. Seed/modelo continuam os anteriores; reprodutibilidade é condicionada às mesmas bibliotecas/inputs.

Esse comando não exporta, não acessa snapshot/banco e não dá certificação automática. Snapshot.verify e restauração permanecem no backend; exporter local READ ONLY continua autoritativo para associação e gates3A. Futuro agendador fora do homeserver pode chamar --execute com novo output/run ID; nenhum cron/serviço ativo/promotor foi criado.

Serviço HTTP agora exige real_volume_readiness aprovado e recibo3G.1 consistente com hashes do dataset/manifest/config/evidências antes de unpickle para artefatos reais. Modelos reais antigos sem recibo são recusados (nenhum certificado existente); fixtures sintéticas conservam opt-in exclusivamente local. Liveness continua leve; pipeline pesado não é importado para verificar o recibo. Recibo não torna um modelo sintético real e não substitui mounts locais confiáveis/validação de procedência.

## Prontidão da amostra disponível

| Critério | Estado |
|---|---|
| Snapshot v2/contagens/hashes | aprovado; restore/geometrias atestados anteriormente pelo operador |
| Associação GPS/passagem/journal |758/775 conferidos;17sem label |
| Elegibilidade técnica sob política atual |357preliminares;401divergentes preservados |
| Ausência de proteção na viagem inteira | reprovado: evidência insuficiente |
| Dataset real certificado/exportado | indisponível |
| Volume | reprovado:16viagens/1dia/<=775GPS, aquém dos gates; sem splits de treino certificados |
| Treinamento real | bloqueado; não executado |
| Modelo/inferência real/Shadow produtivo | bloqueados; Shadow OFF |

Dry-run executado com caminho futuro ainda ausente e config pós-fix: config aprovado, manifesto ausente, demais gates bloqueados, ready=false/training_executed=false. Evidência local ML final `outputs/readiness-3g1-current-02/result/qualification.json`, incluindo hash do orquestrador. Não fabricar manifesto para fazê-lo passar. O relatório não substitui uma medição de volume de CSV real inexistente.

## Reprodução PowerShell

Validação executada: 90 testes ML em tests (3,567s), 13 testes do pipeline original (28,981s; fit somente fixtures sintéticas), 60 testes C# EtaDataset/identidade (305ms de testes) e 11 testes backend Python de distância/diagnóstico (0,101s), todos aprovados/zero ignorados. Testes positivos da orquestração usam trainer fake e volume substituído exclusivamente dentro do teste; não criam modelo real. Build backend incremental1,90s/zero warnings/erros. git diff --check nos dois repos e whitespace dos arquivos novos conferidos. Não executados PostgreSQL/Docker, treino real, agendamento ou avaliação real. Testes incluem insuficiência, evidência ausente/incompleta/corrompida, perfil/hash/split/tempo/duplicatas, fixture retagged, resultado determinístico, no-overwrite, bloqueio antes de fit/unpickle e cleanup após falha.

Arquivos 3G.1: ML `tools/eta_ml/qualify_real.py`, `tools/eta_ml/evidence.real.example.json`, `tests/test_qualify_real.py`, guard em `eta_history_service.py`, regressão em `tests/test_eta_history_service.py` e README. Backend: este relatório, acréscimos em HISTORICO e documentação3B; saídas privadas novas de distância/dry-run. Nenhum arquivo dos15 hashes originais migrados foi reescrito. O working tree inclui alterações anteriores, preservadas.

```powershell
Set-Location D:/repositorio_github/NoPonto/ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests -p 'test_*.py' -q
python -m unittest discover -s tools/eta_ml -p test_pipeline.py -q

# Após exportar CSV real certificado e preparar evidências reais externas:
$run = [Guid]::NewGuid().ToString('N')
python tools/eta_ml/qualify_real.py --dry-run --dataset outputs/dataset-real-qualified/dataset.csv --manifest outputs/dataset-real-qualified/dataset.manifest.json --config tools/eta_ml/config.real.example.json --evidence evidence/real/evidence.json --output "outputs/preflight-$run"
# Config example é apenas exemplo de cortes: deve coincidir com options da exportação.
# Somente após dry-run ready=true, em PC de treino fora do homeserver:
python tools/eta_ml/qualify_real.py --execute --dataset outputs/dataset-real-qualified/dataset.csv --manifest outputs/dataset-real-qualified/dataset.manifest.json --config tools/eta_ml/config.real.example.json --evidence evidence/real/evidence.json --output "outputs/training-$run"
git diff --check

Set-Location D:/repositorio_github/NoPonto/noponto-backend
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlIdentidadeTests' --verbosity minimal
python -m unittest tools.eta_ml.test_distance_sample tools.eta_ml.test_diagnose -q
git diff --check
```

Para reproduzir a amostra: `python -m tools.eta_ml.distance_sample tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02/diagnostic-candidates.csv tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02-completo/report.json tools/eta_ml/outputs/distance-evidence-NOVO`. Nenhuma senha, conexão ou UUID real necessário no comando/documentação pública.

Caminho mínimo: implementar revisão de distância aprovada isoladamente → produzir evidência prospectiva/controlada suficiente sem inferência de ausência → coletar volume mínimo → snapshot/exporter local com audit verdadeiro e cortes inteiros → dry-run verde → treino offline/avaliação/artefato em novo diretório → revisão humana e benchmark de readiness/memória → Shadow local. Produção/promoção/retreino ativo requerem decisões separadas.
