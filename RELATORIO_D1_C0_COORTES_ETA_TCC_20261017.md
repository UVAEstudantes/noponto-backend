# D1 — C0: coortes locais ETA — 17/10/2026

**Decisão C — sem ground truth demonstrado. C0: NO-GO.** Existe uma coorte física de GPS, passagens e eventos de fronteira; não foi demonstrada uma coorte com auditoria integral de execução que sustente TRAIN/VALIDATION/TEST independentes. Não é bloqueio exclusivamente de volume. Não autoriza treino, comparação empírica ou uso produtivo. Os diretórios inacessíveis permanecem desconhecidos; esta decisão refere-se à evidência acessível e não afirma que dados sejam zero em outras árvores.

## 1. Custódia e escopo

Raiz comum `D:/repositorio_github/NoPonto/`. B = `noponto-backend/`, M = `ml/`, V = `noponto-backend-eta-ml-mvp/`, D = `noponto-docs/`. Destino deste único arquivo: B, junto à D0. Branch/HEAD B observados: `main`, `90f7be72e4b55198bc721f4f104a58f37def3f2d`. D0 e dois documentos de plano/diário já estavam untracked. Não foram modificados.

Primeiro foram listados M/outputs, M/lab-output, B/tools/eta_ml e V. Depois foram enumerados B/tools/eta_ml/outputs, B/tools/validation, V/tools e a referência direta `../tools/output`. As referências locais levaram ao bundle e ao CSV abaixo. Não houve busca de disco inteiro. V/tools/eta_ml/outputs não existe nesta consulta. M/lab-output contém relatório legado de posição/ETA, não dataset canônico de chegadas auditadas. A enumeração de M/outputs negou acesso a:

- `readiness-3g1-current-01/result`;
- `readiness-3g1-current-02/result`;
- `mvp-preflight-979edf88398c4dd28fa9b6fdd4884f34/result`.

Esses três conteúdos **não foram verificados**, sem alteração de permissões ou repetição por outro acesso. Não há prova acessível de que contenham coorte real utilizável; seus nomes não sustentam certificação. M/outputs/evidence-verifier-3g3b1-* tem artefatos de fixture/política/verificador, não prova externa das viagens reais do bundle.

Referências principais consultadas: B/AGENTS.md:1, B/NoPonto/HISTORICO.md (trecho final), V/AGENTS.md:1; B/AUDITORIA_D0_ETA_ML_TCC_20261017.md, seções 3/5/6; D/09-validacao/06-fechamento-eta-ml-mvp-20261011.md:40; D/09-validacao/10-protocolo-comparativo-eta-offline.md:29. As contagens abaixo foram recalculadas a partir de arquivos atuais; relatórios anteriores serviram como índices de busca.

## 2. Matriz de artefatos e integridade atual

Todos os caminhos desta tabela são relativos a B/tools/eta_ml/outputs, salvo indicação. “Elegíveis” significa após auditoria integral, não apenas associação técnica.

| Conjunto | Procedência/integridade atual | Período/regime | Elegíveis / viagens independentes auditadas | Passagens e rejeições | Status |
|---|---|---|---|---|---|
| `snapshot-3b2b-v2-real-01` | Bundle físico, manifesto e 24/24 hashes SHA-256 conferidos agora; contagens dos CSV JSON conferidas | 07/10/2026, um dia UTC; perfil documental pós-fix antigo, anterior ao GTFS-RT | 0 demonstradas / 0 demonstradas; 16 viagens com par de eventos início/fim | 188 chaves viagem/ocorrência/volta, 188 eventos PassagemParada; auditoria integral ausente | Real preliminar, não certificado |
| `comparison-3g2-real-01` | Hash do report coincide com comparison.json; vínculo ao mesmo manifesto e CSV de candidatos | Mesma coorte/regime declarado | Não acrescenta viagens nem observações elegíveis | 758 candidatas, 17 sem label, segundo relatório atual; CSV confirmado abaixo | Diagnóstico preliminar, training_dataset=false |
| `comparison-3g2-real-02` | Mesmo teste de hash; mesma origem/candidatos | Mesma coorte | Não somar à versão 01 | Mesmos 758/17; todas as 16 viagens NaoVerificada no report | Diagnóstico preliminar, training_dataset=false |
| `diagnostic-3b2c-copy-hotfix02/diagnostic-candidates.csv` | Hash coincide com candidate_file_sha256 das comparações; 775 registros JSON lidos | Mesmo dia/coorte | 0 demonstradas / 0 demonstradas | 758 associações com GPS anterior à chegada, 154 chegadas-alvo distintas; 17 sem passagem | Candidatos, não CSV canônico |
| `snapshot-3b2b-real-01` | Só pedido e scripts de exportação/planos nesta pasta | Não certificável por scripts | Desconhecido | Sem bundle completo nessa pasta | Preparação anterior |
| `snapshot-3b2b-real-01-exported` | Exportação anterior fisicamente presente; não revalidada integralmente; não adicionada ao volume v2 | Procedência/qualidade integral não demonstradas aqui | Desconhecido | Sem prova nova de auditoria integral | Snapshot anterior, não certificado |
| `diagnostic-3b2c-*` e `distance-evidence-*` | Relatórios/revisões locais da investigação, não exportações canônicas auditadas | Não atribuir regime apenas pelo nome | Desconhecido fora da coorte reconciliada | Não somados ao bundle/candidatos | Diagnósticos |
| `postgis-3b1-probe/export-a/dataset.manifest.json` | Diz data_kind=real, mas collection.SnapshotReference=fixture-docker-3b1-known-full-lifecycle, linhas FIXTURE e seed.sql gerador | Fixture, sem regime real | Excluído da análise real | Manifesto relata 1004 exportados; isso não é volume GPS real | Fixture com marcação antiga enganosa |
| `postgis-3b1-{corrected,final}`, `postgis-3g2-03`, `synthetic-3b` | Manifestos locais identificam synthetic; variantes de falhas/journal também são fixtures | Sintético | Excluídos | Não contar como labels reais | Testes |

Não foi localizado CSV **canônico real com auditoria externa integral** nas árvores acessíveis. Foram encontrados CSVs canônicos de fixtures. Modelos sintéticos e modelos legados mencionados na reconciliação de M não comprovam corpus ETA atual; nenhum pickle/joblib foi carregado. A existência de arquivos de histórico não equivale a sensor independente de chegada.

Hashes de custódia:

| Arquivo | SHA-256 |
|---|---|
| `snapshot-3b2b-v2-real-01/snapshot.manifest.json` | `47ac2c28a1a962b236bf408f61baeb6e0e3413d3cedb28c872c3839b3fc37550` |
| `comparison-3g2-real-01/report.json` | `c50e172fa118389127c49c83dafd0cf7b8a33fd7c0b5e5e959e82e06eb1760a3` |
| `comparison-3g2-real-02/report.json` | `518d0d3aed320c18c5c33c461e0dc1820da20ce33c1fa234939f2872223e90ab` |
| `diagnostic-3b2c-copy-hotfix02/diagnostic-candidates.csv` | `fdd958c042b27c18439ba38b46e7781456c0749dc7e6dcb657eeaddd1003ae0e` |

Os 24 hashes provam integridade em relação ao manifesto local, não autenticidade de captura ou qualidade operacional. O manifesto documenta commit `70d7bcb8041e167df063237b4fdcafd4971b01d6`, migration `20261006180000_IntegridadeCircularDuravel`, cutoff `2026-10-07T21:00:57.997530Z`, contrato `noponto-eta-gps-v1` e sampling habilitado 10%/60min. Esses campos são compatíveis com M/tools/collection_profiles.json:10 (`official-post-fix`). Não foi demonstrada por testemunho externo a continuidade da política durante todas as viagens: regime **declarado/documentado pós-fix pré-GTFS-RT; regime efetivo integral não comprovado**. Não reclassificar como pós-GTFS-RT nem misturar regimes.

## 3. Contagens e qualidade efetivamente examinadas

Leitura segura dos CSVs: Import-Csv e ConvertFrom-Json sobre a coluna `row`, sem executar scripts do bundle. GPS=775, viagens distintas=16, históricos=188, chaves distintas de passagem=188, EventosViagem=220: 16 ViagemIniciada, 16 ViagemFinalizada e 188 PassagemParada. Cada viagem agrupada tem exatamente um início e um fim registrados; isso comprova fronteiras **registradas**, não limites físicos externamente auditados nem ausência de proteção entre elas. Todos os 220 eventos têm volta 0. Veículos distintos: não calculados, pois contagem de viagens bastou e não se expôs identidade de veículo.

Rótulos T01–T16 abaixo são índices locais ordenados pelo primeiro timestamp GPS, sem publicação de correspondência ou IDs reais. Não são identificadores persistentes.

| Viagem | Linha | GPS | Passagens registradas | Início/fim registrados | Viagens integralmente auditadas |
|---|---:|---:|---:|---|---:|
| T01 | 73 | 72 | 9 | 1/1 | 0 demonstradas |
| T02 | 73 | 68 | 9 | 1/1 | 0 demonstradas |
| T03 | 73 | 48 | 9 | 1/1 | 0 demonstradas |
| T04 | 73 | 58 | 9 | 1/1 | 0 demonstradas |
| T05 | 50 | 48 | 16 | 1/1 | 0 demonstradas |
| T06 | 28 | 103 | 18 | 1/1 | 0 demonstradas |
| T07 | 73 | 47 | 9 | 1/1 | 0 demonstradas |
| T08 | 764 | 71 | 29 | 1/1 | 0 demonstradas |
| T09 | 73 | 46 | 9 | 1/1 | 0 demonstradas |
| T10 | 73 | 45 | 9 | 1/1 | 0 demonstradas |
| T11 | 50 | 39 | 7 | 1/1 | 0 demonstradas |
| T12 | 73 | 40 | 9 | 1/1 | 0 demonstradas |
| T13 | 50 | 34 | 8 | 1/1 | 0 demonstradas |
| T14 | 73 | 33 | 9 | 1/1 | 0 demonstradas |
| T15 | 80 | 7 | 11 | 1/1 | 0 demonstradas |
| T16 | 60 | 16 | 18 | 1/1 | 0 demonstradas |

| Dia UTC / regime declarado | Linha | GPS lidos agora | Viagens | Candidatas no report vinculado | Sem label no report |
|---|---:|---:|---:|---:|---:|
| 2026-10-07 / official-post-fix | 28 | 103 | 1 | 103 | 0 |
| idem | 50 | 121 | 3 | 118 | 3 |
| idem | 60 | 16 | 1 | 15 | 1 |
| idem | 73 | 457 | 9 | 447 | 10 |
| idem | 80 | 7 | 1 | 5 | 2 |
| idem | 764 | 71 | 1 | 70 | 1 |
| Total | — | 775 | 16 | 758 | 17 |

A leitura independente do CSV candidato confirma 758 GPS com passagem e timestamp GPS estritamente anterior ao timestamp da passagem; os demais 17 não têm passagem associada. As 758 associações têm igualdade de viagem, versão, sentido, volta, ocorrência-alvo e linha/versão estrutural do join; as 758 têm journal com mesma chave viagem/ocorrência/volta. Representam **154 chegadas-alvo distintas**, não 758 chegadas independentes. Não foi recalculada geografia/PostGIS; geometry_checked e distância conferida são evidências previamente produzidas e vinculadas por hash, não execução nova de geometria.

Nos 188 históricos há exatamente um evento de passagem com mesma chave; os timestamps coincidem em 29 casos à precisão lida, e todos diferem no máximo 0,0009 ms. Não se exigiu igualdade de representação acima da precisão PostgreSQL; isso não substitui auditoria semântica do journal. Dos 188 timestamps de passagem, 176 diferem do GPS de registro e 12 coincidem. O produtor B/NoPonto/2-Application/Services/GPS/ViagemOperacional.cs:266 interpola entre GPS anterior e atual quando versão/progresso/intervalo até 180s são válidos; caso contrário usa timestamp GPS atual. Portanto:

- 176 são compatíveis com timestamps interpolados; a reprodução de cada interpolação e a validade física do par anterior/atual não foram certificadas;
- 12 coincidem com GPS e podem ser extremidade de interpolação ou fallback; não são automaticamente observação independente de chegada;
- referências de chegada observadas por fonte independente demonstradas: **0**; chegadas fisicamente auditadas: **0 demonstradas**, não afirmação de inexistência real.

Qualidade exigiria par causal válido, mesma versão/ocorrência/volta, ausência de salto/regressão/proteção contaminante e evidência de execução inteira. O manifesto contém início/fim e referência de auditoria, mas não apresenta atestação externa integral no formato exigido. Os reports atuais mantêm as 16 viagens NaoVerificada. Uma referência textual e o estado corrente não suprem essa prova.

Exclusões sem dupla contagem: 775 observações ficam fora de uma coorte auditada por ausência de prova integral das 16 viagens; dentro desse universo, 17 também não têm label. As 758 restantes são apenas tecnicamente associáveis. O report 3G.2 registra zero nos demais primeiros motivos técnicos e 401 rejeições semânticas de distância removidas em relação à revisão antiga; esses zeros não certificam proteção/completude e os 401 não são amostras adicionais. Rejeições geográficas ou de regime após auditoria integral: desconhecidas. Nenhum estado foi promovido, fronteira inventada ou campo completado com constante/futuro.

## 4. Aptidão e efeito específico de 4C.9

Não há split auditado plausível demonstrado: a premissa de viagens integralmente confiáveis falha antes de escolher cortes. Não foram escolhidos cortes nem distribuídas viagens por resultado de ML. Um dia e 16 viagens brutas não satisfazem os gates oficiais de 10.000 observações, 14 dias UTC e 200/50/50 viagens; a maior linha tem nove viagens, abaixo de 30/10/10. A insuficiência não se limita a esses gates: faltam auditoria integral, certeza do regime efetivo e exportação canônica com pacote externo. Não há base para condição B só por poder repartir 16 IDs em três grupos. Em eventual reavaliação, purgar viagens que atravessem cortes/regimes antes de contar os splits.

B/docs/00-auditoria/23-cursor-adiantado-4c9-20261014.md:5 documenta quatro cursores e :34 pede prova específica de fronteira. Nenhum vínculo individual desses quatro casos com as 16 viagens de 07/10 foi demonstrado nesta inspeção. **Não foi atribuída contaminação 4C.9 a nenhuma viagem desta coorte**, nem generalizados quatro cursores para toda a base. O que se aplica aqui é a insuficiência de estado Ativa/Finalizada, fração ou journal isolados para certificar nova execução/completude; o bundle tem eventos adicionais, mas ainda não prova externa integral. Não baixar cursor nem interpretar regressão como viagem nova. Os quatro casos não explicam por si os 17 labels ausentes.

`qualify_real --dry-run` não foi executado: não há dataset CSV canônico real qualificado, manifesto correspondente, configuração de cortes reais e pacote `eta-external-audit-evidence-v1` com suporte integral para essas viagens. M/tools/eta_ml/qualify_real.py:20 exige hashes de audit/options/sql, :58 atestações por viagem com intervalo integral e arquivos de suporte; :112 recusa sintético/exploratório. O executor importa pipeline/train e pode criar/publicar diretório de saída mesmo sem treino; foi somente inspecionado. Não simular sucesso com candidatos, exemplos ou fixture marcada real. Python local não iniciou por indisponibilidade do executável; Get-FileHash não estava disponível nessa sessão. As contagens e hashes foram concluídos com PowerShell e SHA256 do .NET, sem necessidade de instalar ou contornar acesso.

## 5. Exatamente uma próxima ação que pode mudar C0

**Solicitação única ao custodiante/operador: disponibilizar localmente um pacote de evidência integral da coorte já existente `snapshot-3b2b-v2-real-01`, sem nova coleta por esta tarefa.** Origem: registros contemporâneos de sessão controlada externa ou journal/ledger durável de proteção e coleta, sob custódia do operador, cobrindo desde o início até o fim das viagens candidatas de 07/10. Não é possível obter essa prova apenas dos estados atuais ou fabricar atestação a partir dos CSVs.

Entrada: pasta nova autorizada ao lado do bundle, com manifesto SHA-256, ligação às viagens já presentes, release/configuração/corte efetivos e evidência original de completude/ausência de proteção durante a execução inteira; incluir os pares GPS que sustentam timestamps de passagem e distinguir referência observada/interpolada. Para sustentar estudo independente, ao menos uma viagem auditada utilizável em cada um de três intervalos cronológicos disjuntos, sem viagem atravessando corte. Não pedir telemetria de toda a base ou ativação Shadow.

Procedimento seguro: recebimento local pelo operador e reconciliação estática somente leitura, por hashes e chave viagem/versão/ocorrência/volta, com saída exclusivamente agregada por viagem/dia/linha/regime e rejeição. Sem conexão, restore, treino ou execução dos scripts embarcados. Saída esperada: número não vazio de viagens com prova integral e labels rastreáveis, mais análise explícita das interpolações; a divisão em três intervalos deve sobreviver ao purge para permitir o estudo solicitado. **GO C0** somente com essas provas; **NO-GO** se faltar suporte integral, fronteira confiável ou referência de chegada não ambígua. Se o operador confirmar que os registros contemporâneos não foram retidos, parar: esta coorte não pode ser retroativamente certificada por declaração. O resultado dessa única ação decide se há material para estudar splits; não promete volume oficial nem autoriza treino.

## 6. Reprodução e verificações

Reprodução dos totais: ler `Import-Csv <bundle>/TelemetriasVeiculoMl.csv`, `HistoricoPassagens.csv`, `EventosViagem.csv` e converter exclusivamente `row` com `ConvertFrom-Json`; contar objetos, Group-Object ViagemId e chaves compostas. Nos candidatos, contar passagem não nula, comparar TimestampGps < TimestampPassagem e agrupar pela chave da passagem. Nunca emitir os objetos ou grupos com nomes reais; emitir somente Count e índices locais. Ordenação dos índices T usa primeiro TimestampGps por viagem. Datas agregadas são UTC.

Reprodução de integridade, PowerShell/.NET, somente leitura (o resultado é contagem, sem conteúdo bruto):

```powershell
function ShaLocal($p) {
  $s = [Security.Cryptography.SHA256]::Create()
  try {
    ([BitConverter]::ToString($s.ComputeHash(
      [IO.File]::ReadAllBytes((Resolve-Path $p))))).Replace('-','').ToLower()
  } finally { $s.Dispose() }
}
$b = 'tools/eta_ml/outputs/snapshot-3b2b-v2-real-01'
$m = Get-Content -Raw "$b/snapshot.manifest.json" | ConvertFrom-Json
$checks = @($m.files_sha256.PSObject.Properties | ForEach-Object {
  (ShaLocal (Join-Path $b $_.Name)) -eq $_.Value
})
@($checks | Where-Object { $_ }).Count # 24
```

Arquivos de dados JSON/manifestos de linha única: `snapshot-3b2b-v2-real-01/snapshot.manifest.json:1`, `comparison-3g2-real-01/report.json:1`, `comparison-3g2-real-02/report.json:1`, seus `comparison.json:1`, `diagnostic-3b2c-copy-hotfix02/report.json:1`; CSVs: cabeçalhos em :1 e objetos nas linhas seguintes. `postgis-3b1-probe/export-a/dataset.manifest.json:1` e `postgis-3b1-probe/seed.sql:2` sustentam a exclusão da falsa aparência real. Código de contrato: B/NoPonto/2-Application/Services/GPS/EtaDataset.cs:60; passagem: B/NoPonto/2-Application/Services/GPS/ViagemOperacional.cs:164 e :266; prova externa: M/tools/eta_ml/qualify_real.py:20 e :58; catálogo: M/tools/collection_profiles.json:10. Esses arquivos foram usados sem executar seus produtores.

Realizado: inventário delimitado, leitura de documentação/instruções, parsing local, 24 hashes do bundle, vínculos dos dois reports e candidato, contagens e joins agregados, inspeção estática de qualificador/fixtures, Git local branch/HEAD/status. Não realizado: certificação externa, reprodução integral da interpolação, geografia SQL, dry-run, suites C#/Python, replay, comparador, fit, inferência, bancos, Redis, Docker, APIs, rede, SSH, cloud ou Git remoto. Sem mudanças funcionais, flags, dados originais, scripts ou histórico; a instrução específica de criar somente este relatório prevalece sobre o acréscimo geral ao HISTORICO. Nenhum intermediário temporário foi criado. Limites remanescentes estão expressamente marcados como desconhecidos, sem transformar nomes de pastas ou relatórios antigos em prova atual.
