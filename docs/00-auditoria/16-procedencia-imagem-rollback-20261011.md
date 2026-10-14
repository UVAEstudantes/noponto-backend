# Etapa 4C.3 — Procedência da imagem produtiva e retorno

Data: 2026-10-11. Continuação do [relatório 15](15-preparacao-release-diagnostica-20261011.md). **BLOQUEADA para liberar deploy: origem Git da imagem dbe503 ainda não comprovada.** Encontrada execução pública de build/deploy com revisão exata, mas não obtido o digest publicado nem a ligação desse digest ao Image ID observado. Horário, nome de tag e sucesso do job não substituem esse vínculo.

## Evidências e limites de acesso

AGENTS.md, HISTORICO.md, relatório 15, workflow e scripts .deploy consultados. Git preservado: fix/gps sem upstream, HEAD 6f64538b47cb3987c5bf023d9878931b69228dac, mesma referência origin/main LOCAL. Sem fetch/checkout/merge/reset/clean/commit/push. Estado preexistente preservado.

Nesta etapa houve exclusivamente leitura pública do GitHub Actions/API e tentativa de leitura de metadados GHCR, autorizadas pelo escopo. Não houve SSH, produção, Docker build/pull/push, serviços novos, migrations ou deploy. Não foram extraídas credenciais locais. `gh` não disponível no ambiente. Respostas de tokens/segredos não foram impressas nem registradas. A tentativa GHCR anônima retornou HTTP 401; logs do run via API retornaram HTTP 403. Não houve tentativa de contornar controle de acesso.

### Metadados produtivos fornecidos pelo operador

Aceitos como evidência verificada pelo operador, sem nova leitura do host pelo agente:

| Item | Valor fornecido |
| --- | --- |
| Image ID observado | sha256:dbe503a480387dc1ab6e54857cc7f052d3524a61991666fc14e022dac833af79 |
| Referência local informada | ghcr.io/guilhermedesales/noponto-api@sha256:dbe503a480387dc1ab6e54857cc7f052d3524a61991666fc14e022dac833af79 |
| Construção / arquitetura | 2026-10-10T03:19:25Z; linux/amd64 |
| Container / identidade Compose | noponto_api; noponto/api |
| Recursos / logs | 1 CPU, 768 MiB, sem swap adicional, 256 PIDs; json-file |
| Arquivos Compose | /opt/stacks/noponto/docker-compose.yml e /home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml |
| Hash Compose informado | fdbd6a072ff339e47b7c0e4e2ebecc285b9726369a46419af4dfe34ea251efc3 |
| Banco | 23 migrations correspondentes ao inventário candidato; IntegridadeCircularDuravel presente; última 20261010002416_TarifasPagamentosMvp |

Configuração informada: Production, fontes GTFSRT_BUS/BRT, crosswalk true, velocidades KilometresPerHour, ML__ETA__Enabled=false, EtaV2__Enabled/ShadowEnabled=false, EnriquecerTodasLinhas=true, polling BUS15/BRT20, aquisição GTFS30 e TTL ativo40. Esses dados corrigem as lacunas de defaults/composição do relatório 15; o Compose base local sozinho continua inadequado para deploy. Hash informado não permite reconstruir os arquivos externos nem afirmar byte a byte a composição; não foi recebido o método de cálculo do hash. Preservar a composição real e o snapshot privado, sem publicar env/segredos.

MudancaOperacionalHabilitada permanece **valor efetivo não certificado**. Ausência no ambiente não prova OFF: o default local em GpsPoolingOptions.cs é false e o binding usa GpsPolling, mas a imagem/arquivos/providers efetivos não estão certificados. Não mudar a flag; obter seu valor resolvido ou prova dos providers ao registrar baseline.

## Execução GitHub encontrada

[Run 114 / ID 38020006164](https://github.com/UVAEstudantes/noponto-backend/actions/runs/38020006164), tentativa 1:

| Metadado / job | Resultado consultado |
| --- | --- |
| event / branch / revisão | push / main / 6f64538b47cb3987c5bf023d9878931b69228dac |
| path workflow | .github/workflows/deploy.yml |
| criado / concluído | 2026-10-10T03:16:55Z / atualizado 03:23:16Z; success |
| CI offline, job 114118691806 | success; 03:16:58–03:18:18Z |
| Build e Push, job 114118945752 | success; 03:18:21–03:19:42Z |
| Deploy API, job 114119192631 | success; 03:22:25–03:23:15Z |

O blob remoto do workflow no SHA desse run é ff0e26ef7edd5cd69576777f28bdf77a6ab6d28d, exatamente igual ao blob em HEAD local. Isso vincula a revisão do run ao workflow revisado, não ao Image ID do container. A construção informada 03:19:25Z cai dentro do job de build; é evidência de compatibilidade temporal, **não prova de procedência**. A inicialização de produção informada anteriormente em 14:37:46Z pode decorrer de recriação posterior e não identifica quem construiu os bytes.

Leituras concretas:

- [API de runs de 10/10](https://api.github.com/repos/UVAEstudantes/noponto-backend/actions/runs?created=2026-10-10&per_page=30): também mostra runs 112/113/111; PR não publica conforme workflow. Nenhum run foi acionado.
- [Metadados do run 114](https://api.github.com/repos/UVAEstudantes/noponto-backend/actions/runs/38020006164) e [jobs](https://api.github.com/repos/UVAEstudantes/noponto-backend/actions/runs/38020006164/jobs): revisão/jobs/tempos acima.
- [Artefatos do run](https://api.github.com/repos/UVAEstudantes/noponto-backend/actions/runs/38020006164/artifacts): total_count=0; nenhum manifesto baixável obtido. Não demonstra ausência de provenance em manifesto OCI.
- [Workflow no SHA exato](https://github.com/UVAEstudantes/noponto-backend/blob/6f64538b47cb3987c5bf023d9878931b69228dac/.github/workflows/deploy.yml): configuração e blob conferidos.
- [Página pública do job build](https://github.com/UVAEstudantes/noponto-backend/actions/runs/38020006164/job/114118945752): HTTP 200, sem strings sha256 de publicação no HTML obtido. API de download de logs: HTTP 403.
- Consulta anônima de metadados GHCR para noponto-api/build-114: HTTP 401 antes de obter manifesto/config/attestation; não foi resolvida a tag nem recebido digest.

Não foi obtido: steps.publish.outputs.digest, resumo de publicação contendo digest, manifesto OCI, config blob, attestation/provenance ou manifesto de fontes da imagem dbe503. **Origem Git dessa imagem: desconhecida.** O run 114 é uma hipótese verificável, não revisão atribuída ao container.

## Image ID, digest e tag

Image ID identifica o objeto/configuração da imagem no engine; digest de registry pode identificar um index OCI/multiarch/attestations ou um manifesto de plataforma; a tag build-114/latest é alias mutável. Manifesto da plataforma aponta ao digest do config blob, que deve coincidir com o Image ID do engine para ligar os bytes. Quando houver index, primeiro resolver o filho linux/amd64.

A referência local informada tem sintaxe de digest, mas o mesmo texto dbe503 também foi informado como Image ID. Não afirmar que é automaticamente o digest publicado, nem declarar a referência inválida sem consultar metadados: falta distinguir .Config.Image, .Image, .RepoDigests e manifesto resolvido. Nome/texto igual não prova mesmo objeto ou camada da cadeia.

Cadeia necessária:

```text
run/revisão 6f64538... + digest de publicação registrado
    → manifesto/index por esse digest (linux/amd64)
    → config.digest = Image ID dbe503...
    → container observado pelo operador
```

Uma attestation verificável que vincule o artefato à revisão pode substituir a ligação por logs, mantendo a resolução do sujeito/manifesto para o config ID. Não suposta attestation automaticamente gerada ou disponível.

## Delta da candidata e contratos

Os **17 hashes** de fontes/testes/entidade do manifesto do relatório 15 foram rechecados: **zero divergências**. Nenhum delta contra HEAD em migrations, Program ou csproj. Delta LOCAL comprovado contra 6f64538:

- 9 fontes produtivos: agregador/contextos/logs e wrappers/hooks diagnósticos do manifesto 15.
- 7 arquivos de testes: validação de contagem/concorrência/retry/commit/rollback/materialização/logs.
- FormaPagamento: apenas Cor/Icone comentados, sem propriedades ativas ou ampliação do modelo.
- Histórico/auditorias: documentação separada; documentação ferroviária schedules fora do escopo preservada e excluída da allowlist.

Os corpos operacionais, payloads, codecs, contratos persistidos e regras contra HEAD permanecem como revisados e homologados nas etapas anteriores. Nenhuma funcionalidade foi modificada nesta etapa.

**Se** a cadeia comprovar que dbe503 corresponde ao checkout desse mesmo SHA sem alterações adicionais, o delta produtivo é exclusivamente a instrumentação e comentários autorizados. Enquanto não comprovada, o delta REAL contra produção não pode ser certificado. A equivalência das 23 migrations não prova igualdade de endpoints/matching/retry/fontes. Não reutilizar o diff de local03 como diff da imagem atual.

## Migrations e compatibilidade de retorno

Evidência fornecida fecha a lacuna de conjunto EF: **as mesmas 23 migrations da candidata estão aplicadas**, inclusive integridade circular e tarifas. Não há migration nova no delta diagnóstico; modelo/snapshot sincronizados e testes anteriores aprovados. Não requer preparar nem aplicar migration para essa candidata. Essa conclusão cobre inventário EF informado, não auditoria física de todas as colunas/índices nem equivalência dos binários.

Imagem de retorno recomendada para uma futura release diagnóstica: **a imagem que já está em produção, Image ID completo dbe503...**, preservada localmente e conferida antes de substituição. Ela já opera com o schema e configuração observados. Não selecionar 70f254..., 25162c..., ba2af67... build-102, 42d8b78... ou 2bbb601... rollback-3e40d92 só por disponibilidade: isso seria downgrade para binários cuja compatibilidade com tarifas, schema, integridade circular, CAS/codec/eventos e política de migration não foi verificada.

Requisitos para retorno à atual:

1. Resolver procedência atual/candidata e confirmar que o delta não altera contratos/decisões. Confirmar no binário atual a política de startup se houver dúvida; se comprovado HEAD, ela é Production fail-closed, sem migration automática.
2. Guardar a imagem atual exata localmente e verificar ID completo/arquitetura, sem depender de latest/registry. Registry digest só depois de resolver a cadeia; o caminho existente guarda uma tag local cujo ID anterior é conferido pelo health.
3. Congelar snapshot privado de base/overlay/env e manter limites/flags existentes; conferir o hash de composição pelo mesmo método informado. Guardar active.env/active.run/deploy.lock; não expor credenciais.
4. Recriar somente api sob o mesmo lock, --no-deps/--no-build/--pull never, e exigir imagem esperada/saúde. Sem duas APIs simultâneas ou recreação de PG/Redis.
5. Preservar schema, Outbox, journal/histórico, pendências de retry, versões/cursor/voltas/identidades/marcadores de integridade e timestamps Redis. Nenhum rollback de dados, migration reversa, limpeza de chaves/streams ou volumes.

Se o delta for apenas diagnóstico, os estados escritos durante a medição continuam no mesmo contrato da atual, favorecendo esse retorno. É condição de compatibilidade a certificar pelo vínculo de fontes; saúde da imagem anterior sozinha não cobre estados produzidos por outra candidata funcional. O script não desfaz writes e seu health não testa todos esses contratos. Evidências locais anteriores de codec/idempotência/rollback permanecem válidas no escopo testado, não são teste de implantação no homeserver.

## Gatilhos CI/CD

Workflow comprovado no run/local: pull_request main valida; push main valida e publica automaticamente GHCR (latest, timestamp-SHA e build-N). Deploy depende do push main, build aprovado, NOPONTO_DEPLOY_ENABLED=true, environment production e sua proteção. Run 114 registrou deploy success; isso prova execução histórica desse job, não valor atual de variável/proteções ou imagem hoje em serviço. Estado remoto atual dessas configurações não foi certificado.

Não há workflow_dispatch declarado nesse workflow. Não tratar merge/push como mera preparação de código: pode publicar e implantar automaticamente. Antes de promover candidata, obter o estado real dos controles e coordenar a aprovação em etapa separada; nenhum gatilho/flag foi modificado ou acionado. Revisados locks/health/retorno nos scripts existentes, sem executá-los.

## Contexto limpo e próxima evidência mínima

É possível preparar contexto controlado sem modificar a árvore compartilhada: numa etapa autorizada, exportar o HEAD COMPROVADO por git archive para diretório novo, aplicar a allowlist exata de 17 arquivos com hashes conferidos e documentação autorizada, manter assets rastreados necessários e não copiar .env/bin/obj/lab-output/ignorados. A documentação ferroviária modificada fica no workspace e não é aplicada nesse contexto. Não usar cópia recursiva da árvore suja. Não foi criado contexto nem build Docker aqui; uma exportação não transforma automaticamente essa base em produção comprovada.

**Menor evidência externa para fechar procedência:** resumo/log do job build 114 contendo commit completo e digest publicado, junto ao trecho de manifesto/config que liga esse digest ao Image ID dbe503. Alternativa: provenance/manifesto de release verificável com essa mesma ligação. Não são necessárias credenciais, logs completos, envs ou dump de imagem.

Leituras sugeridas para operador com sessão GitHub/registry JÁ autorizada, sem acionar jobs:

```text
gh run view 38020006164 --repo UVAEstudantes/noponto-backend --json headSha,number,event,conclusion,url
# Abrir resumo do job 114118945752 e fornecer somente commit/digest publicado.
# Se resumo ausente, obter o log desse job na sessão autorizada e recortar
# somente a saída de publicação/digest; não enviar logs completos ou segredos.
docker buildx imagetools inspect ghcr.io/guilhermedesales/noponto-api@sha256:DIGEST_PUBLICADO --raw
```

O último comando lê metadados de registry (não constrói/publica/puxa layers); se retornar index, repetir pelo digest filho linux/amd64 e fornecer somente mediaType/digests/platform/config.digest. Conferir .Image/.RepoDigests do container/imagem na coleta existente ou em futura leitura autorizada, sem dump de Config.Env. Não executar os placeholders literalmente. Esses comandos NÃO foram executados pelo agente com autenticação.

Próxima ação mínima: obter essa cadeia, comparar ao SHA/manifesto 15 e decidir se a candidata congelada realmente contém apenas o delta autorizado. Se corresponder, os deltas de tarifas/BRT/startup/timestamp antes tratados como desconhecidos passam a base produtiva e o gate de migrations está fechado; então registrar valor efetivo de MudancaOperacional e estado atual dos controles CI/CD, preparar contexto congelado e solicitar autorização separada de artefato/deploy. Se não corresponder, comparar a base comprovada sem inventar revisão ou promover o HEAD inteiro.

## Resultado desta etapa

Procedência Git de run 114: comprovada. Procedência Git da imagem dbe503: **não comprovada**. Migrations: compatíveis no conjunto informado de 23, sem alteração requerida. Principais limites/configuração: comprovados pelo operador, não defaults locais; flag de mudança operacional ainda desconhecida. Retorno recomendado: imagem atual exata, condicionado ao vínculo/contratos e preservação descritos. Coletor/performance reais continuam fora da validação local.

**BLOQUEADA por falta do vínculo digest → config ID → revisão**, sem presumir origem pelo horário. Só este relatório e acréscimo no histórico alterados. Não reexecutados build/testes: fontes continuam com hashes iguais e a tarefa foi de procedência/metadados; resultados 406 offline e homologação conectada permanecem nos relatórios anteriores. `git diff --check` sem erros, avisos LF/CRLF preexistentes. Sem modificações funcionais, migrations, SSH, serviços/infraestrutura, imagem Docker, publicação ou deploy.
