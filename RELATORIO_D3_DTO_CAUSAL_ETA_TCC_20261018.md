# D3 — busca do DTO causal e compatibilidade do replay — 18/10/2026

**DTO causal original não localizado no acervo local examinado. Sem replay de
previsões reais; C0 permanece NO-GO conforme D1.** Não se conclui inexistência
fora dos caminhos consultados. Nenhuma captura/consulta produtiva foi feita.

## Evidência dirigida

Consultados AGENTS, relatório D2, conclusões de D1, núcleo/testes OperationalEtaReplay
e última entrada do HISTORICO. Reutilizadas as conclusões anteriores, sem novo
inventário de viagens, splits ou modelos. Busca por nomes de campos do DTO em
artefatos locais JSON/JSONL/CSV/log, sem emitir registros, executar scripts de
snapshots, consultar serviços ou abrir configurações/credenciais.

| Fonte local / implementação | Resultado e limite |
| --- | --- |
| `tools/eta_ml/outputs/snapshot-3b2b-v2-real-01` | Inspecionada a união dos campos de todos os 775 GPS, 220 eventos e seus payloads, e 188 históricos. ETA/confiança do DTO ausentes. Timestamps de GPS/criação/evento/passagem não demonstram o instante de emissão desse DTO. |
| `tools/eta_ml/outputs/snapshot-3b2b-real-01-exported` | Mesmas contagens e mesma ausência dos campos nas três fontes; não soma nova coorte. Três arquivos conferem com o respectivo manifesto local, cuja autenticidade externa não foi certificada. |
| Demais diagnósticos/comparações em `tools/eta_ml/outputs` e artefatos locais pesquisados | Não localizado sidecar contendo o par ETA/confiança original. Candidatos associados a passagens e ETAs calculados por outros métodos não são cópias do DTO. |
| Dois `eta-history-profile-*.jsonl` em `outputs/startup-3f-*` | Chaves de estágio/tempo/RSS/carregamento NumPy/sklearn; perfis de startup, não previsões. |
| Referências documentais `../eta-real-shadow-20261015` e `../physical-shadow-causal-20261016` | Diretórios ausentes localmente. Não recuperados remotamente; conteúdo permanece desconhecido. A menção a cinco registros reais nos documentos não disponibiliza os registros nem sua cadeia de custódia. |
| `HistoricalEtaShadow` / `HistoricalEtaOfflineExport` | `PublicEta` é filtrado por confiança física, prova de velocidade e alvo. Null pode significar recusa do adapter, não ETA original nulo. JSONL v2 não conserva confiança original, fração/velocidades completas nem snapshot integral do DTO. `CapturedAt` é o hook e `RecordedAt` é gravação; não são o instante original de cálculo. Nenhum `eta-shadow-*.jsonl` foi localizado nesta busca local. |
| Cache de posição / estado causal / EtaV2 | O cache serializa o DTO completo, mas sobrescreve chaves ativo/recente com TTL; não foi localizado arquivo histórico desse payload. Não lido Redis. EstadoCausalPosicao conserva contexto/amostras de movimento, não o par ETA/confiança. EtaV2 possui previsão própria, não cópia do ETA operacional. |

Manifesto v2: SHA-256
`47ac2c28a1a962b236bf408f61baeb6e0e3413d3cedb28c872c3839b3fc37550`,
igual a D1/D2. Nesta etapa conferidos especificamente os três arquivos lidos:

| Arquivo v2 | SHA-256, coincidente com manifesto |
| --- | --- |
| TelemetriasVeiculoMl.csv | `683be9b38ea14de36290e8c049fa6f28cc142d42f02d18746befd2b0b18f8e40` |
| EventosViagem.csv | `12f563728aa805038cd63e6aaf013ae3844e53071f86fb2231b7629d6fcc2d94` |
| HistoricoPassagens.csv | `fdac658f8251eb32acc5b85abe478643e2847d327a9b2274fd232b635ab79156` |

Manifesto da exportação anterior: SHA-256
`ae695e0db1937c6871795dbcda209c7906b56aae86572903f51c988107972ec2`.
Hashes locais provam correspondência de bytes, não verdade da chegada, completude
ou autenticidade da captura. As fronteiras/auditorias de D1 continuam pendentes.

## Incremento concreto independente da evidência faltante

D2 representava `OperationalReplayKey.ObservationId` como Guid, incompatível
com `TelemetriaMlContrato.ObservacaoId` e a coluna real, ambas SHA-256 textual.
Verificação agregada, sem divulgar identidades: **775/775** IDs têm 64 caracteres
hexadecimais minúsculos e coincidem com o hash da composição canônica existente
modal/provedor/veículo/tGPS. Isso verifica a identidade da telemetria, não a
existência/identidade de um DTO histórico ausente.

Corrigido somente o suporte offline em `NoPonto/5-Testes/OperationalEtaReplay.cs`:
string canônica de 64 caracteres, preservada integralmente, validação de formato
e ordenação ordinal. Sem converter, truncar ou reidentificar hashes históricos.
Fixtures agora usam o gerador existente de ObservacaoId. Nenhum consumidor
produtivo ou contrato público usa esse tipo interno de suporte.

Não implementado leitor de previsões históricas: sem o DTO original ele apenas
fabricaria uma entrada ou devolveria as mesmas indisponibilidades já previstas
em D2. Leitura real nesta tarefa foi inspeção estática de estrutura/integridade
e identidade; a execução do núcleo continua restrita a fixtures sintéticas.

## Campos faltantes e contrato mínimo de captura futura — proposta, não ativada

Reutilizar o hook pós-CAS/decisão operacional e a fila/exportador JSONL privado
existentes. `HistoricalEtaObservation` já contém a cópia de `Gps` e a identidade
da viagem/alvo; aproveitar esse objeto, sem novo exportador, worker ou banco.
Extensão opcional aninhada proposta: `operational_dto`, com versão própria. Sua
ausência nos arquivos antigos significa **não capturado**. Não reinterpretar
`PublicEta`, `physical_apply_reason`, `MlEta` ou `HistoricalEta` para preenchê-la.

| Grupo mínimo | Conteúdo e regra |
| --- | --- |
| Versão/proveniência | Versão do contrato privado, commit/hash do código e política efetiva de ETA, parâmetros relevantes (Enabled/MaxAge), origem do ETA do DTO. Sem URL, conexão, token ou ambiente integral. Manifesto local liga captura, estrutura e bundle por SHA-256/bytes/contagens e origem sob custódia. |
| Identidade | Reutilizar observation_id SHA-256, trip_id, linha/sentido/padrão/versão, target_id, stop_id, lap e timestamp_gps. Distinguir o próximo alvo do DTO do alvo consultado; conferir igualdade quando o comparador exigir. Nunca associar por proximidade temporal. |
| ETA original e confiança — ausentes no bundle | Propriedades obrigatoriamente presentes `eta_proxima_parada_segundos` e `eta_confianca`, aceitando null explícito quando realmente capturado assim. Objeto ausente/campo omitido = indisponível; não converter para null conhecido. Copiar os valores exatos do DTO final, antes de qualquer transformação do adapter ou inferência ExtraTrees. |
| Relógios — emissão original não demonstrada | tGPS, recebimento, instante de avaliação/emissão do DTO no produtor, instante do hook e de gravação separados. O produtor deve guardar seu relógio junto da referência ao DTO final; o worker não pode reconstruí-lo. Identificar o estágio de captura e corte temporal dos insumos. |
| Estado necessário ao replay | Fração atual, velocidade instantânea/média causal, próximo alvo original, topologia/status e, no físico, distância restante e prova de captura ativa usadas pelo endpoint. Capturar os escalares exatos ou ligar a fonte causal por chave/hash; não exportar DTO completo com coordenadas/identificador físico. Ausência de qualquer campo usado implica indisponibilidade explícita. |
| Estrutura versionada | Comprimento da rota, ocorrência/parada/ordem/fração e identidades do StopContext, inclusive ocorrências repetidas da mesma parada. Reutilizar snapshot estrutural hashado do bundle; não consultar geometria atual para preencher passado. |
| Perdas e limites | Manter filas/quotas/retenção/contadores já existentes e captura opt-in OFF. Publicação só após CAS aceito; nenhuma operação de disco/rede síncrona no GPS. Registrar faltantes/exclusões, arquivar artefatos por procedimento autorizado antes da expiração e testar limites de bytes após a extensão. |

Este contrato ainda precisa de implementação/homologação em tarefa autorizada;
não altera API, DTO público/Redis, flags ou captura produtiva nesta D3. Uma futura
implementação deve provar vínculo do testemunho ao mesmo DTO (como já faz o
diagnóstico físico), inclusive ramos legado/físico e null explícito. Habilitar
captura em produção exige autorização operacional separada.

Replay retrospectivo em tGPS continua sendo reconstrução simulada: um DTO
calculado depois não prova disponibilidade prospectiva em tGPS. Documentar o
corte dos insumos e não usar observações posteriores; estudo de previsão
efetivamente emitida usa seu relógio real, horizonte e envelhecimento próprios.
Arquivos de ExtraTrees/model_digest e de labels permanecem separados. Nenhuma
previsão é chegada observada; qualidade/origem de labels não é alterada aqui.

## Verificações efetivas

- RED: `CanonicalObservationHash_RoundTripsAndFeedsReplay`, um teste executado,
  falhou antes da correção com JsonException em ObservationId/Guid. GREEN: mesmo
  teste passou, preservando o hash e alimentando o núcleo com snapshot sintético.
- `ObservationIdentity_RejectsMalformedOrNoncanonicalHash`: sete casos verificam
  null, vazio, Guid, comprimentos 63/65, maiúsculas e caracteres fora de hexadecimal.
- Seleção `.ci` existente, sem editar allowlist/infra: **649 aprovados, zero falhas
  ou ignorados**, incluindo **36/36 replay** (28 anteriores + 8 novos). TRX aceito
  por `.ci/offline_tests.py verify`, todas as 27 classes presentes.
- A primeira seleção completa teve 648 aprovados e um timeout na espera de 3 s
  de `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`
  (BUS/batch/export_failure), fixture preexistente. Repetição sem mudança de
  código passou os 649; instabilidade registrada, sem atribuir causa ao replay
  ou afirmar que a fixture foi corrigida. Build: zero erros/5 warnings anteriores.
- Não repetidos testes Python dos utilitários CI, que não foram alterados.
  Nenhum replay real, serviço externo, banco, PostGIS, treino/inferência ou
  avaliação de acurácia. Fixtures existentes incluem HTTP efêmero em loopback;
  núcleo/novos testes não usam sockets. `git diff --check` aprovado.

Comandos efetivamente usados no GREEN (filtro gerado pelo seletor existente):

```powershell
$filter = py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj --no-restore --filter $filter --logger 'trx;LogFileName=offline.trx' --results-directory NoPonto/lab-output/eta-replay-d3-20261018-final --verbosity quiet
# Repetição motivada pelo timeout; sem alterar binário ou seleção:
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=offline.trx' --results-directory NoPonto/lab-output/eta-replay-d3-20261018-repeat --verbosity quiet
py -3.13 -B .ci/offline_tests.py verify NoPonto/lab-output/eta-replay-d3-20261018-repeat/offline.trx
```

## Menor próximo passo

Pergunta factual ao custodiante: existe arquivo original retido do DTO completo
ou sidecar correspondente a estas observações, com manifesto e relógios de
captura? Se existir, disponibilização **local** delimitada permite validar e
então implementar o leitor. Sem retenção, encerrar o replay fiel dessa coorte;
o próximo incremento implementável é a extensão privada de captura acima,
primeiro com fixtures, sem ativá-la em produção. Separadamente, o pacote integral
de evidência previsto em D1 continua necessário para C0 e comparação científica.

Não modificados fontes/contratos produtivos, frontend, infraestrutura, bancos,
labels ou artefatos originais. Sem acesso remoto/produção, migrations, deploy,
push, merge, treino ou operações destrutivas. Alterações locais anteriores
preservadas; histórico atualizado exclusivamente por acréscimo.
