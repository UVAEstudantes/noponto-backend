# 3G.3B.1 — fundação da evidência durável e verificador independente

08/10/2026. **Fundação validada em fixtures locais. Certificação automática real: REPROVADA/OFF.** A aprovação recebida autoriza a arquitetura C e esta fundação local; não autoriza ativação, perfil prospectivo, produção, migration operacional, deploy ou treinamento. Nenhuma viagem histórica foi certificada.

## Implementação e contratos

`EtaTripEvidence.cs` define `eta-trip-evidence-v1`: Epoch, intervalo de owner, Head por ViagemId, Begin/Transition/Checkpoint/Close, classificações e a interface reservada `IEtaDecisionCoverageSource`. Não há implementação dessa interface ou chamada pelo polling, retry, Program/DI ou repository operacional. Nenhuma escrita extra por GPS foi introduzida.

O head contém início e último envelope completo. O envelope fixa viagem/scope/epoch/token, hash do perfil e da identidade estrutural, sequência de qualidade, versão operacional, tempos UTC em microssegundos, intervalo coberto, contadores admitidos/resolvidos/pendentes, flags cumulativas e testemunho de decisões. Identidade estrutural externa contém linha/sentido/padrão/versão/topologia/volta; seu hash é imutável na cadeia e confrontado com os vínculos de início/fim. Não é feature de treino.

| Flags negativas irreversíveis | Flags desconhecidas irreversíveis |
|---|---|
| 1 proteção; 2 candidato iniciado; 4 ambiguidade | 1 gap; 2 restart; 4 takeover; 8 release; 16 Redis perdido; 32 rollback; 64 commit incerto; 128 reancoragem; 256 decisão sem cobertura; 512 owner obsoleto |

Cancelamento não limpa candidato. Reancoragem não comprovada é desconhecida. Proteção comprovada com gap é `ProtegidaOuAmbigua`, `complete=false`. Sem proteção comprovada e com gap, `NaoVerificada`. O reducer produtor não emite certificado positivo por olhar apenas o head.

JSON exige todos os parâmetros, inclusive null explícito, rejeita campos desconhecidos/duplicados. GPS ausente pode ser null somente em Transition desconhecida, sem ampliar o intervalo coberto e sem alegar cobertura; Begin/Close exigem GPS. Isso preserva desconhecido sem inventar timestamp. `coverage_proven=false` exige flag 256. Um checkpoint com relógio avançado, contadores parados ou witness repetido é recusado mesmo que tenha sido rehashado.

Digest SHA256: array ordenado dos campos do envelope exceto digest, todos representados como strings ASCII; inteiros decimais invariantes, UUIDs canônicos, booleanos `true/false`, GPS ausente `null`. A ordem corresponde aos contratos C#/Python, incluindo `identity_hash`. Perfil/identidade usam SHA256 de JSON com chaves ordenadas e separadores compactos. Fixture compartilhada tem bytes idênticos nos dois repos. Tempos/contadores não usam float.

`eta-trip-certifier-v1` gera relatório determinístico com hashes do input normalizado, política e código verificador; classificação, motivos, completude, limites por viagem e `physical_arrival_certified=false`. Resultado sintético tem `data_kind=synthetic-fixture`. O CLI cria arquivo novo exclusivamente, sem sobrescrever evidência.

## Armazenamento e transações

Migration aditiva `20261008090000_EtaTripEvidenceFoundation`: quatro tabelas novas — `EtaProducerEpochs`, `EtaEvidenceOwners`, `EtaEvidenceHeads`, `EtaEvidenceJournal`. PKs, FKs RESTRICT, unique viagem/sequência e unique owner aberto por scope. Não altera tabelas operacionais, integridade circular, labels, sampling ou migrations anteriores. Não faz backfill. Down destrutivo bloqueado. Como os stores operacionais raw SQL, esta fundação não acrescenta entidades ao modelo EF/snapshot antigo.

O harness executa **UpOperations da migration real** somente no banco aleatório descartável que ele cria, com PostgreSQL16/PostGIS3.4 e marcador exclusivo previamente conferidos. Não executa a cadeia de migrations da API nem usa banco existente. A fixture anterior mínima conserva estado e IntegridadeCircular durante os testes.

`EtaTripEvidenceRepository` recebe connection/transaction do chamador, sem abrir outra conexão/transação. O ponto futuro fica entre a escrita de estado/outbox e o Commit já existentes em `ViagemOperacionalRepository`; nesta fase não foi conectado. Se qualquer operação falhar, o chamador deve abortar a transação inteira, sem capturar a exceção e confirmar estado parcial.

RegisterEpoch grava o horário de registro pelo relógio PG e retorna o registro persistido. Só aceita epoch **inativo, não revogado e sem barreira comprovada**. Não há método de ativação. Aquisição serializa scope, encerra intervalo anterior e incrementa token durável; nova aquisição não adota prova limpa de viagem antiga. Revogação usa relógio PG e bloqueio do epoch; leituras compartilhadas impedem commit concorrente atravessando revogação.

Persist valida owner corrente, epoch/perfil, head CAS e reducer. Há CAS também no próprio INSERT/ON CONFLICT, com scope/token/epoch/seq; isso cobre a corrida de head inexistente entre scopes diferentes. O CTE só insere outbox se o CAS de head gravou. Retry exato do último envelope é idempotente, útil para reconciliar ACK de commit perdido; divergência não é recuperada como limpa. A reconciliação não apaga flag CommitUncertain quando ela foi registrada.

O worker existente separa envelopes de qualidade dos eventos operacionais. Materializa em `EtaEvidenceJournal` e confirma lease/ACK na mesma transação do batch. O fallback de qualidade também faz materialização+ACK atômicos. Duplicata igual é idempotente; divergente permanece erro. Contrato de qualidade desconhecido não recebe ACK nem cai no journal operacional. `ViagemIniciada`, `ViagemFinalizada`, `PassagemParada`, seus validadores e a materialização legada permanecem com a semântica anterior.

## Verificador ML e qualificação

`ml/tools/eta_ml/verify_trip_evidence.py` não importa reducer C# ou código produtor. Reconstrói sequência, hashes, monotonicidade, tempos, cobertura, identidade, epoch/release/perfil, intervalo/exclusividade de owner, Begin/Close, vínculos operacionais e recibos do outbox. Ordem de materialização não importa; duplicata idêntica é tolerada. Campo obrigatório removido, hash adulterado, proteção limpa por recuperação, owner sobreposto, contrato desconhecido ou ACK pendente não geram aprovação.

A política instalada `ml/contracts/eta-trip-certification-policy-v1.json` tem `enabled=false`, nenhum perfil/epoch autorizado e nenhum limite temporal implicitamente aceito. Nenhuma flag do bundle liga essa política. A política positiva compartilhada é **exclusivamente fixture**, com release/image fictícios e indicação sintética. Isso testa o algoritmo, não autoriza coleta ou certificado real.

`qualify_real.py` conserva todos os gates de CSV, volume, split, labels, procedência, perfis oficiais e política3G.2. Para `source=durable-protection-ledger`, exige referências hashadas `ledger` e `verification_report`, recomputa o verificador com a política **instalada**, compara relatório inteiro e exige a mesma viagem/intervalo, resultado positivo completo, autorização e input não sintético. Booleans antigos sem prova não bastam. Atestados externos de sessão controlada mantêm o caminho anterior; não são criados por esta tarefa. Pipeline, modelos e qualificação de distâncias não foram modificados.

## Exclusividade: bloqueio real e proposta específica

Tokens/CAS foram testados entre escritores que usam esta API. **Não cercam binário antigo**: o repository operacional e o Lua atuais ignoram esses novos tokens. Advisory lock da evidência também não é uma barreira ao progresso Redis de produtor legado. O inventário completo de retornos negativos ainda não foi instrumentado.

Antes de habilitar certificação, uma etapa aprovada separadamente precisa demonstrar: ausência de instância antiga concorrente, fencing do caminho quente Redis e do commit PG, identidade de release autorizada e revogação na troca/restart. Proposta concreta para revisão: owner/token carregados no CAS Lua e contexto operacional; validação do owner no commit PG sob a ordem de locks definida; barreira de acesso que impeça writer legado de modificar as chaves/estado operacional sem o contexto autorizado. Uma barreira forte pode exigir ACL/roles distintas e acesso de escrita por interface PG controlada, ou controles equivalentes de instâncias/rede comprovados. Trigger que aceita uma variável de sessão livremente forjável, sozinho, não basta.

**Essa parte foi parada no desenho**, como solicitado: não criados roles, permissões, ACLs, triggers operacionais, credenciais ou alterações de infraestrutura. É necessária proposta/autoridade operacional própria antes de executá-la. Sem essa barreira, o perfil de certificação permanece OFF mesmo após a futura instrumentação.

Hash chain detecta adulteração/perda em relação a uma raiz confiável; não autentica produtor e não detecta uma cadeia inteira forjada com todos os hashes recalculados. Snapshot selado e sua raiz precisam ser confiados externamente, além da allowlist de binário/epoch/política. A fundação não inventa assinatura/chave ou autoridade de ativação.

## Snapshot v3 preparado, não ativado

`snapshot-v3.contract.json` descreve as novas fontes, filtros seletivos, FKs, hashes, políticas e bloqueadores. `snapshot.py`, seu VERSION v2, catálogos, funções/triggers, bundles v1/v2 e todos os manifests selados permanecem intactos. A seleção estrutural de viagens não é certificação `AuditadaSemProtecao`.

Head/journal são filtrados pela lista explícita ViagemId; owners pelos scopes/tokens e intervalos sobrepostos relevantes; epochs pelos IDs alcançados. Outbox por **lista explícita de EventIds**, lookup na PK, sem scan global. Nenhuma fonte pesada completa é transportada. Fonte ausente no v1/v2 não ganha default confiável.

Limite importante: o verificador desta fundação exige recibo processado para cada envelope, e o cleanup existente do outbox retém por7dias default. Se recibos expirarem antes de serem selados, resultado será desconhecido. Não alteramos retenção. Antes do v3 operacional, decidir entre arquivar snapshots/evidências dentro dessa janela ou persistir recibo de materialização de qualidade na mesma transação, com contrato/migration revisados. Não há implementação de export/restore v3 nesta subetapa, nem retenção infinita/limpeza automática de prova.

## Checkpoints propostos para 3G.3B.2 — aprovação ainda necessária

Proposta concreta inicial: **janela de qualidade de60s**, com relógio próprio, independente do checkpoint operacional; produzir no próximo commit durável devido, junto da transação operacional, e obrigatoriamente em Begin, primeira contaminação relevante e Close. Não emitir por toda atualização de âncora circular ou GPS normal. Se o commit não ocorrer dentro do limite autorizado, latchear Gap/MissingDecision, sem manter a janela como limpa.

Para a homologação seguinte, proponho limite explícito inicial de75s entre fronteiras de evidência (60s de janela + até15s de atraso de scheduling conhecido), atraso recorded/GPS separado e limite de ausência de observação/decisão de60s. **Esses valores não estão ativos nem aprovados**: a fixture usa60s e5s apenas para testar limites. Não toleram falhas desconhecidas dentro da janela; atraso desconhecido, relógio inconsistente ou perda do witness sempre contaminam. Os limites finais devem constar do perfil/política versionados e ser aprovados após medir a cadência efetiva.

Cada admissão de decisão precisa incrementar contador/token sob owner exclusivo antes de qualquer caminho capaz de alterar estado; todo retorno deve resolver a admissão ou registrar pendência/falha. Um rolling digest limitado deve vincular ordinal, predecessor, resultado, GPS real quando conhecido, identidade e flags. Flags são OR antes de retornar; cancelamento, recuperação e ACK não as apagam. Freeze copia cumulativos sob o mesmo owner, sem task relevante pendente ao Close. A implementação futura deve detectar perda de continuidade local, CAS, Redis, retry e epoch, não reconstruí-la do estado final.

Um timer prova apenas que o processo acordou. Contadores e witness são requisitos necessários, **não prova autônoma de que todos os caminhos foram instrumentados**. O verifier já recusa heartbeat parado, mas um produtor mentiroso poderia fabricar contadores/hashes; allowlist, cobertura dos retornos e fencing continuam pré-condições.

| Ponto futuro obrigatório | Evidência/resultado mínimo |
|---|---|
| GpsPollingService antes de confirmação/enriquecimento/commit | admissão e identidade do predecessor; retorno sem dado não vira sucesso limpo |
| ViagemOperacional.Decidir e .Mudanca | candidato iniciado contamina a antiga; cancelamento não limpa; Close/Begin na mesma tx da troca |
| .Integridade | proteção/ambiguidade e recuperação, inclusive decisões com eventos operacionais vazios |
| Repository commit quente/durável | token/fencing em ambos; CAS/Redis perdido/rollback/commit incerto causam dirty; quality clock separado |
| RetryOperacionalGps | expiração, superação, recalculo, falha e ACK com motivo; zero pendências no fechamento |
| Startup/takeover/release/revogação | epoch/intervalo proprietário explícito; viagens abertas afetadas desconhecidas |
| Worker/cleanup/snapshot | materialização e ACK; ausência/expiração da prova não substituída por contagem |

Não será possível declarar cobertura produtiva apenas adicionando callback no repository. Todos esses caminhos serão responsabilidade da3G.3B.2; coleta/sampling e método do label continuam separados.

## Testes e medidas

Resultados finais e comandos abaixo registram somente execução local. Fixture de qualidade é sintética; o teste conectado usa PostgreSQL16.4/PostGIS3.4.3 em container/rede/database exclusivos, loopback, tmpfs256MiB, limite512MiB/1CPU. Nenhum banco operacional/restaurado existente foi acessado.

- C#:122 regressões offline aprovadas/zero ignoradas (28 da fundação), incluindo reducer/digests/flags/contratos, dataset3G.2, Shadow e identidade de telemetria. Build bem-sucedido, zero erros e cinco warnings preexistentes. Mais1teste composto conectado aprovado.
- Python ML:103 testes aprovados, incluindo9do verifier,8de qualificação, serviço/contratos/migração/política existentes; nenhuma execução real de treino.
- Python backend:54 testes aprovados, incluindo snapshot v2, exporter/proteções, auditoria/diagnóstico e guards da fundação. Uma invocação inicial sem PYTHONPATH falhou na importação do teste exporter; corrigido o comando, sem alteração do exporter.
- Teste conectado composto: rollback estado/head/outbox preservando IntegridadeCircular; retry exato após commit confirmado sem ACK cliente; journal/ACK revertidos quando lease é perdido; materialização idempotente/conflitante; CAS concorrente no mesmo scope e entre scopes; tokens simultâneos, owner obsoleto, troca/revogação de epoch; contrato desconhecido sem ACK. O teste injeta ausência de ACK, **não uma perda real de rede durante COMMIT**.
- Uma primeira tentativa de harness Windows PowerShell falhou no tratamento de stderr nativo e a próxima expôs expectativa de exceção errada no cenário de recuperação. Recursos próprios foram removidos e os cenários corrigidos; sem reutilizar banco parcialmente preparado.

Medições finais: `outputs/evidence-3g3b1-8736bb7ac6864421a29e1483a4740877`, test.log/TRX/versões/docker stats. 21transações: p50=13,114ms/p90=14,162ms/p95=17,553ms; delta heap retido327.624bytes, working set do processo de testes141.791.232bytes; container PG137,3MiB amostrados, sem medição de pico. CLI offline também conferido, em `ml/outputs/evidence-verifier-3g3b1-6c56333227db45f2966786f439e9b791`: default→NaoVerificada, política fictícia→AuditadaSemProtecao/synthetic-fixture; arquivo já existente recusado sem alteração. O teste amostra21transações de evidência (conexão/tx/locks/head/outbox/commit), heap retido do processo de testes e working set. Não compara uma carga operacional antes/depois nem mede WAL, pico de RSS, autovacuum ou muitos veículos. Não extrapolar esses números para produção ou custo adicional por GPS.

## Arquivos desta subetapa

Backend: novos `EtaTripEvidence.cs`, `EtaTripEvidenceRepository.cs`, migration `20261008090000_EtaTripEvidenceFoundation.cs`, `EtaTripEvidenceTests.cs`, `EtaTripEvidenceLocalTests.cs`, `tools/eta_ml/fixtures/trip-evidence-v1.json`, `homologate_evidence.ps1`, `test_evidence_foundation.py`, `snapshot-v3.contract.json` e este relatório. Alterados somente dispatch em `ViagemOutboxWorker.cs` e histórico por acréscimo. O working tree já tinha alterações das etapas anteriores, preservadas.

ML: novos `tools/eta_ml/verify_trip_evidence.py`, `contracts/eta-trip-certification-policy-v1.json`, `contracts/evidence-fixtures/trip-evidence-v1.json`, `tests/test_trip_evidence.py`; alterados `tools/eta_ml/qualify_real.py` e `tests/test_qualify_real.py`. Nenhum dos15 arquivos originais da migração3C foi alterado nesta subetapa; seus manifests/revisionpins continuam válidos.

## Reprodução PowerShell

```powershell
Set-Location D:\repositorio_github\NoPonto\noponto-backend
$env:PYTHONPATH = 'tools/eta_ml'
python -m unittest tools.eta_ml.test_evidence_foundation tools.eta_ml.test_snapshot tools.eta_ml.test_exporter tools.eta_ml.test_audit_sql tools.eta_ml.test_diagnose -q
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~EtaTripEvidenceTests|FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~TelemetriaMlIdentidadeTests)' --verbosity quiet
& tools/eta_ml/homologate_evidence.ps1
git diff --check

Set-Location D:\repositorio_github\NoPonto\ml
$env:PYTHONPATH = 'tools/eta_ml'
python -m unittest discover -s tests -p 'test_*.py' -q
git diff --check
```

O harness é a única execução conectada: cria/remove somente recursos próprios, confere marcador/loopback/nome vazio, timeout120s do cenário e readiness até180s; não usa POSTGIS_TEST_CONNECTION de integração operacional. Uma execução normal cabe em5–7min. Não rodar suíte operacional completa automaticamente para esta fundação.

Comparar verifier desligado com autorização **fictícia**, sem certificar dado real:

```powershell
Set-Location D:\repositorio_github\NoPonto\ml
$fixture = Get-Content contracts/evidence-fixtures/trip-evidence-v1.json -Raw | ConvertFrom-Json
$out = Join-Path $env:TEMP ('eta-evidence-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$fixture.bundle | ConvertTo-Json -Depth 100 | Set-Content -Encoding utf8 (Join-Path $out 'bundle.json')
$fixture.policy | ConvertTo-Json -Depth 100 | Set-Content -Encoding utf8 (Join-Path $out 'fixture-policy.json')
python tools/eta_ml/verify_trip_evidence.py --input "$out/bundle.json" --output "$out/disabled.json"
python tools/eta_ml/verify_trip_evidence.py --input "$out/bundle.json" --policy "$out/fixture-policy.json" --output "$out/fixture-only.json"
```

Primeiro resultado é NaoVerificada; o segundo testa AuditadaSemProtecao somente como synthetic-fixture, e é recusado por qualify_real para dados reais. Não reutilizar essa política no snapshot real. Não há comando de ativação, migration de produção ou treino autorizado.

**Próximo passo:** revisar/aprovar a barreira de escritores e os limites de checkpoints para3G.3B.2, instrumentar os caminhos negativos em fixtures e fechar receipt/retention/snapshotv3. Ativação e autorização de uso real continuam decisões separadas. Qualidade física de chegada/labels não foi certificada.
