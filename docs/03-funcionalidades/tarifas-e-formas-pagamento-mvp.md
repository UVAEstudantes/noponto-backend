# Tarifas e formas de pagamento — MVP

Implementação na branch `feat/tarifas-pagamentos-mvp`, a partir de `origin/main` e86f3d96c9bca2561f4f3950e13f18431088bd97. O adendo autoriza substituir integralmente o legado e descartar seus registros. Nenhuma operação produtiva faz parte do desenvolvimento.

## Arquitetura e auditoria

- Domain: `Tarifa` sem BaseEntity/vigência; `FormaPagamento`, `FormaPagamentoVinculo`; validação decimal e normalização NFC/lowercase invariant.
- Data: configuração EF centralizada, constraints e índices; `TarifasStore` com upsert PostgreSQL atômico e timestamps preservados quando nada muda.
- Application: `TarifaService`, contratos, cliente leve ArcGIS, plano determinístico e importador transacional.
- API: controllers finos, filtro específico para erros 400/404/409 e contratos Swagger. Sem autenticação nesta etapa.

O controller público antigo dependia de ITarifaService sem registro, e TarifaService estava excluído da compilação. ITarifaRepository tinha dois registros DI. Foram removidos os contratos antigos, repository/interface e controller/DTOs administrativos de tarifas. TarifaService e TarifasController foram substituídos. A exclusão específica do serviço foi removida do csproj; exclusões globais de Admin e de funcionalidades estruturais continuam porque cobrem outros componentes ainda adiados.

Linha/Modal mantêm suas navegações tarifárias, agora para a entidade definitiva. O detalhe de linha e repository excluídos foram adaptados ao novo contrato, eliminando referências aos DTOs/colunas antigos, sem reativá-los. O campo `TarifaAtual` do adaptador frontend existente continua como estava; o frontend deve usar `/tarifas/resolver`. Não foram alterados GPS, ETA, modais de apresentação, pipelines estruturais, startup policy, workflows, Compose ou Dockerfile.

## Banco e migration

Migration: `20261010002416_TarifasPagamentosMvp` (identificador EF em UTC; trabalho em 09/10/2026, America/Fortaleza).

| Tabela | Campos e integridade |
| --- | --- |
| Tarifas | Id UUID; ModalId OU LinhaId nullable/FK; Valor numeric(10,2) não negativo; Fonte MANUAL/ARCGIS_SPPO; CriadoEmUtc/AtualizadoEmUtc UTC. CHECK exclusivo e índice único parcial por escopo. |
| FormasPagamento | Id UUID; Nome/NomeNormalizado até 100 caracteres; CriadoEmUtc. UNIQUE NomeNormalizado; CHECK de tamanho, espaços, controles e NFC. |
| FormasPagamentoVinculos | Id UUID; FormaPagamentoId FK; ModalId OU LinhaId nullable/FK. CHECK exclusivo e índices únicos parciais por escopo+método. |

A normalização da chave é feita na aplicação com `ToLowerInvariant`; não usar `lower()` do PostgreSQL para gerar chaves, pois o resultado depende do locale. NFC preserva acentos: Jaé e Jae + acento combinante coincidem, Jaé e Jae não. Aparar e colapsar whitespace precede a criação; o primeiro nome de exibição não é atualizado por duplicatas. Constraints e UNIQUE protegem campos e concorrência. Escritas SQL externas devem fornecer a chave produzida pela mesma regra da aplicação.

**Operação destrutiva explícita:** Up obtém lock exclusivo (timeout 15 s), verifica FKs de entrada inesperadas e faz `DELETE FROM "Tarifas"`. Depois altera somente a tabela tarifária: remove Ativo/UpdatedAt/ValidoAte e o índice de vigência, renomeia campos e torna escopos nullable, acrescentando constraints e índices únicos. Não cria TarifasAtuais. FKs para Linhas/Modais são preservadas. Não altera migrations antigas nem manualmente __EFMigrationsHistory.

Não existe conversão confiável automática de registros antigos que exigiam linha E modal e tinham vigência. Os registros são descartados, sem reaproveitamento silencioso. Tarifas desconhecidas ficam sem registro. Down também apaga tarifas novas, remove catálogo/vínculos e restaura apenas a estrutura antiga; **não recupera dados**. Para recuperar valores antigos, restaurar backup. DELETE não usa CASCADE; dependências de entrada não previstas abortam antes da exclusão. Dependências de views/colunas podem bloquear DDL, revertendo a transação; revisar no ambiente alvo.

Excluir uma linha/modal pode excluir seus próprios registros/vínculos por FK cascade. Remover tarifa/vínculo pelos endpoints preserva linha/modal/catálogo; FK do catálogo é Restrict.

## Contratos HTTP

| Método e rota | Resultado |
| --- | --- |
| GET /tarifas/resolver?modalId=UUID&linhaId=UUID | 200: resolução abaixo; modal obrigatório apenas quando não há linha. |
| PUT /tarifas/modais/{modalId} | Body `{ "valor": 5.00 }`; 204; fonte MANUAL. |
| PUT /tarifas/linhas/{linhaId} | Mesmo body; 204; tarifa específica. |
| DELETE /tarifas/modais/{modalId} | 204; remove apenas o valor padrão. |
| DELETE /tarifas/linhas/{linhaId} | 204; volta ao fallback do modal. |
| GET /formas-pagamento | 200: catálogo ordenado por chave normalizada e ID. |
| POST /formas-pagamento | Body `{ "nome": "RioCard" }`; 201 com `{ "id": "UUID", "nome": "RioCard" }`. |
| PUT /modais/{modalId}/formas-pagamento/{formaPagamentoId} | 204; vínculo idempotente. |
| DELETE /modais/{modalId}/formas-pagamento/{formaPagamentoId} | 204; preserva catálogo. |
| PUT /linhas/{linhaId}/formas-pagamento/{formaPagamentoId} | 204; método adicional. |
| DELETE /linhas/{linhaId}/formas-pagamento/{formaPagamentoId} | 204; não exclui método herdado. |

IDs inexistentes: 404; falta de escopo/inconsistência linha-modal/valor inválido: 400; nome duplicado após normalização, inclusive concorrente: 409. Corpo ausente, UUID inválido ou valor não decimal: 400 via ApiController. Valor aceito: 0 a 99999999.99, no máximo duas casas (sem arredondar na aplicação). Zero é permitido explicitamente, mas não significa desconhecido. PUTs com valor e fonte já iguais preservam Id e timestamps; atualizações mudam somente valor/fonte/AtualizadoEmUtc. DELETE repetido em escopo existente retorna 204.

Exemplo de resolução:

```json
{
  "modalId": "11111111-1111-1111-1111-111111111111",
  "linhaId": "22222222-2222-2222-2222-222222222222",
  "tarifa": { "valor": 5.00, "origem": "MODAL", "fonte": "MANUAL", "moeda": "BRL" },
  "formasPagamento": [{ "id": "33333333-3333-3333-3333-333333333333", "nome": "Jaé" }]
}
```

Se não houver tarifa: `"tarifa": { "valor": null, "origem": null, "fonte": null, "moeda": "BRL" }`, com HTTP 200. A linha tem prioridade; métodos são a união modal+linha, deduplicados pelo ID e ordenados de modo determinístico. Não há exclusão de herdados.

O ID virtual BRT usado pelo adaptador frontend não é um modal persistido: esse ID retorna 404 aqui. Usar linhaId real, que identifica seu modal. Nenhuma nova linha/modal é criada pelo recurso.

## ArcGIS

Fonte reutilizada do ArcGisSppoSnapshotClient: [FeatureServer SPPO](https://pgeo3.rio.rj.gov.br/arcgis/rest/services/Hosted/Rede_%C3%94nibus_SPPO_visualiza%C3%A7%C3%A3o/FeatureServer/1?f=pjson). Verificação real em 09/10/2026: maxRecordCount=1000, 963 features sem geometria; tipos regular/brt/frescao e preços como 5.00, 24.85, 21.65, 16.6, 15, além de campos vazios. São evidências da fonte naquele instante, não tabelas fixas da aplicação.

O cliente consulta apenas servico,tipo_rota,tarifas,fid, ordena por fid e respeita páginas de 1000. Repetição de fid, página vazia com limite excedido, erro ArcGIS/HTTP ou payload inválido abortam antes de escrever. O download inteiro precede o apply.

Parsing estrito de um único decimal positivo/zero (ponto ou vírgula; prefixo R$ opcional; até duas casas). Recusa sinais negativos, milhares, múltiplos preços, exponentes, unidades desconhecidas, valores fora de numeric(10,2). O relatório conta features recebidas/válidas; importados/atualizados/inalterados/ignorados/conflitantes contam grupos de serviço+tipo. No dry-run esses três primeiros são **ações previstas**, não escritas realizadas. Itens informam código, tipo, linha, valor e motivo, inclusive inválidos/linhas ausentes/ambíguas/manuais preservadas/conflitos.

Agrupamento por código aparado/uppercase invariant e tipo aparado/lowercase invariant. Não remove zeros à esquerda. O match exige código e tipo exatos e modal Ônibus/Onibus para regular/frescao; brt exige tipo brt e modal Ônibus/Onibus ou BRT persistido. Não associa ônibus a BRT por código. Caso a base não contenha BRT/frescão, esses grupos ficam LINHA_NAO_ENCONTRADA. Não inventa modalidade/contexto pelo código e não altera os identificadores estruturais.

Mais de um preço no grupo: CONFLITO_PRECOS, sem escolha arbitrária. Uma feature inválida no grupo impede extrapolar o preço às outras variantes (VALOR_INVALIDO_OU_INCOMPLETO). Essa escolha conservadora pode deixar valores desconhecidos até revisão. Mais de uma linha compatível também impede escrita. Não cria linhas, padrão de modal, média, moda ou registros de pagamento. MANUAL nunca é sobrescrita; ARCGIS_SPPO pode ser atualizada; dados ausentes não apagam tarifas.

Apply é uma transação, com lock SHARE ROW EXCLUSIVE em Tarifas e timeout de 15 s para serializar importações e bloquear gravações tarifárias durante o plano/aplicação. O upsert ainda protege MANUAL atomicamente. Dry-run usa transação REPEATABLE READ READ ONLY. Falha durante apply reverte todas as gravações. Chamadas HTTP não seguram locks de banco.

Comando: `dotnet NoPonto.dll tarifas-import-arcgis --dry-run` ou `--apply`. Exige exatamente uma flag. Retornos: 0 sucesso; 2 sintaxe; 3 migrations pendentes; 4 falha de configuração/fonte/banco. Reutiliza POSTGRES_* e isolamento de ambiente existentes. Só cria DI PostgreSQL/HttpClient/importador; não cria host, HostedServices, Redis, GPS ou SignalR. Não chama Migrate/EnsureCreated nem inicia importação no startup.

## Testes reproduzíveis

```powershell
dotnet restore NoPonto/NoPonto.csproj
dotnet build NoPonto/NoPonto.csproj -c Release --no-restore
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TarifasRegrasTests
```

PostgreSQL/PostGIS **descartável**, sem volume persistente, porta loopback e nome exclusivo; a fixture exige banco vazio e recusa host não local/database sem prefixo noponto_tarifas_. A opção trust é somente para essa fixture temporária isolada; não usar em ambientes reais.

```powershell
docker run -d --name noponto-tarifas-mvp-test -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=noponto_tarifas_tests -p 127.0.0.1:55439:5432 postgis/postgis:16-3.4
# Aguarde a inicialização completa, incluindo PostGIS, e confirme TCP (não apenas socket temporário):
docker exec noponto-tarifas-mvp-test pg_isready -h 127.0.0.1 -U postgres -d noponto_tarifas_tests
$env:TARIFAS_TEST_CONNECTION='Host=127.0.0.1;Port=55439;Database=noponto_tarifas_tests;Username=postgres'
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TarifasPostgresTests
docker rm -f -v noponto-tarifas-mvp-test
```

A fixture aplica as 22 migrations originais, cadastra tarifa legada/sentinela de linha-modal, testa bloqueio por FK inesperada e aplica a nova. Testes verificam schema sem drift, constraints, concorrência real, fallback, catálogo, união, fonte manual, rollback do apply, dry-run e Down/Up. Um host HTTP mínimo, sem Program.cs/HostedServices, também verifica DI, Swagger, validação de body, respostas 400/404/409 reais e requisições HTTP concorrentes para cadastro, tarifas e vínculos. Não reutilizar o banco em outra execução: remover e criar uma fixture nova. Regras/HTTP fake entraram na allowlist CI offline; PostgreSQL continua fora dessa seleção. Resultados efetivos e limitações estão no HISTORICO.md.

## Limites

Não há vigência/histórico avançado, descontos, integração tarifária, bilhetagem, trajetos, cache, worker periódico ou autenticação. Nenhum método é atribuído automaticamente à importação ArcGIS. Aceitação de PIX em algumas estações ferroviárias não é generalizada a linhas/modais; expansão futura deve acrescentar escopo ParadaId e regras próprias. Endpoints de escrita estão acessíveis conforme a política atual da API; a segurança permanece para etapa posterior solicitada.

O contrato tarifário legado foi removido deliberadamente. Clientes que usavam seus endpoints/DTOs devem migrar. A imagem antiga depende de colunas removidas e de ambos os escopos obrigatórios: não pode ser considerada funcional durante a transição. Ver procedimento operacional abaixo.

Procedimento completo de implantação e recuperação: [tarifas-migration-e-importacao.md](../05-infraestrutura/tarifas-migration-e-importacao.md).

## Arquivos alterados

- `NoPonto/3-Domain/Entities/tarifa.cs`, `Entities/FormaPagamento.cs`, `3-Domain/Tarifas/RegrasTarifarias.cs`.
- `NoPonto/4-Data/Context/DbContext.cs`, `Context/TransporteDesignTimeFactory.cs`, `4-Data/Tarifas/TarifasModelConfiguration.cs`, `TarifasStore.cs`.
- `NoPonto/2-Application/Services/TarifaService.cs`, `Services/Tarifas/ArcGisTarifasClient.cs`, `ArcGisTarifasImportador.cs`, `TarifasImportCommand.cs`, `DTOs/Tarifas/TarifasContratos.cs`.
- `NoPonto/1-API/Controllers/TarifasController.cs`, `FormasPagamentoController.cs`, `Program.cs`, `NoPonto.csproj`.
- `NoPonto/2-Application/DTOs/Linhas/LinhaDetalhesDTO.cs`, `4-Data/Repositories/LinhaRepository.cs`: somente dependências tarifárias dos componentes excluídos, sem ativação.
- `NoPonto/Migrations/20261010002416_TarifasPagamentosMvp.cs`, respectivo `.Designer.cs`, `TransporteDbContextModelSnapshot.cs`.
- `NoPonto/5-Testes/TarifasRegrasTests.cs`, `TarifasPostgresTests.cs`, `.ci/offline-tests.json`.
- Este documento, `docs/05-infraestrutura/tarifas-migration-e-importacao.md`, `tarifas-pagamentos-mvp.sql` e `NoPonto/HISTORICO.md` (apenas acréscimo).
- Removidos: controller AdminTarifas; DTOs AdminTarifaCreate/Update/ListItem; DTOs TarifaCriar/Consulta/Resumo; ITarifaService, ITarifaRepository e TarifaRepository antigos.
