# Aparência de formas de pagamento

Base: `origin/main` f6c4a07; branch `feat/formas-pagamento-aparencia`, independente do laboratório ITS. Release #142 fora do escopo.

`POST /formas-pagamento` continua aceitando apenas `nome`. Agora aceita também `icone` e `cor`, opcionais e nullable. Os dois GETs (`/formas-pagamento` e `/tarifas/resolver`) e a resposta do cadastro incluem esses campos em camelCase. Valores ausentes ficam null, inclusive nos registros anteriores. Não há preenchimento automático nem alteração dos nomes, IDs, vínculos ou tarifas.

Ícones válidos (comparação exata): `credit-card`, `wallet`, `banknote`, `landmark`, `smartphone`, `qr-code`. Cor: exatamente `#RRGGBB`, com dígitos ASCII e letras em qualquer caixa. Strings vazias, espaços e valores fora do catálogo retornam 400; use null para ausência. A validação fica no serviço, inclusive para chamadas internas. A chave normalizada e o índice único existentes continuam impondo 409 para duplicatas, sem atualizar a aparência do cadastro original.

## Registros existentes

`PUT /formas-pagamento/{id}` permite editar nome, ícone e cor, com o mesmo acesso do POST existente, sem segredo ou header específico. Autenticação/autorização ficará para uma etapa futura. Ver [contrato e procedimento de publicação](formas-pagamento-publicacao.md). Nome obrigatório; ícone/cor omitidos ou null removem a personalização. Reenviar valores atuais para conservá-los. Retorna o DTO atualizado em 200, 400 para dados inválidos, 404 para ID inexistente e 409 para nome duplicado normalizado. Preserva identidade, criação, vínculos e tarifas.

O SQL abaixo é uma alternativa restrita de operador, documentada antes do PUT, por ID e com parâmetros. Não foi executado; preferir o PUT após implantação autorizada:

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
PREPARE configurar_aparencia(uuid, text, text) AS
UPDATE "FormasPagamento"
SET "Icone" = $2, "Cor" = $3
WHERE "Id" = $1
  AND ($2 IS NULL OR $2 IN ('credit-card', 'wallet', 'banknote', 'landmark', 'smartphone', 'qr-code'))
  AND ($3 IS NULL OR (length($3) = 7 AND $3 ~ '^#[0-9A-Fa-f]{6}$'))
RETURNING "Id", "Nome", "Icone", "Cor";
-- Substituir o UUID pelo ID conferido; os dois valores são explícitos, NULL limpa o campo.
EXECUTE configurar_aparencia('00000000-0000-0000-0000-000000000000', 'wallet', '#AABBCC');
DEALLOCATE configurar_aparencia;
-- Conferir exatamente uma linha e os valores retornados. Zero linhas indica ID/valores inválidos.
ROLLBACK;
-- Somente em operação autorizada: substituir o ROLLBACK por COMMIT após a conferência.
```

Não recriar nem excluir métodos para configurar aparência: isso comprometeria identidades/vínculos. O SQL altera somente as duas propriedades. O exemplo não constitui ferramenta administrativa disponível ao público. Escritas SQL externas devem respeitar a mesma validação; esta migration não adiciona constraints de catálogo ou cor.

## Migration e verificação

`20261023014903_FormaPagamentoAparencia` é incremental sobre `20261010002416_TarifasPagamentosMvp`. O [SQL gerado pelo EF](formas-pagamento-aparencia.sql) contém somente duas colunas nullable em `FormasPagamento`, além do registro padrão em `__EFMigrationsHistory`. Sem defaults, backfill, alteração de tarifas, índices, FKs ou vínculos. O snapshot foi gerado pelo tooling EF 9.0.10 e comparado ao modelo. A migração histórica anterior de tarifas não deve ser confundida com esta: revisar a faixa incremental, nunca aplicar todo o histórico sem avaliar o estado do banco.

Aplicar o schema antes de executar o backend novo: consultas EF passam a referenciar as colunas. O backend antigo pode conviver com as colunas adicionais. O `Down` remove somente as duas colunas e perde a aparência configurada; preferir rollback do binário mantendo o schema aditivo. O DDL ainda exige lock de tabela, a avaliar na implantação futura.

Em 2026-10-23, o usuário confirmou a aplicação bem-sucedida da migration no PostgreSQL produtivo e seu registro em `__EFMigrationsHistory`. Não reaplicar; merge e deploy seguem sem autorização automática.

Evidências automatizadas:

| Requisito | Evidência |
| --- | --- |
| Catálogo de ícones e cor opcional | `CatalogoAceitaSomenteIconesDoFrontend`, `CorOpcionalOuHexadecimal` |
| Entrada inválida produz 400 sem consulta/escrita | `CadastroInvalidoRetorna400AntesDeAcessarBanco` |
| Cliente antigo, null e JSON camelCase | `ClienteAntigoENulosMantemContratoJson` |
| EF nullable, comprimentos, snapshot sem drift, Up/Down e SQL restritos | `ModeloSnapshotEMigrationSomenteDuasColunasNullable` |
| Preservação de registros, IDs, chave de nome, timestamps, vínculo e tarifa ao migrar | asserts em `TarifasPostgresFixture.InitializeAsync` após seed no schema anterior |
| POST e ambos GETs, persistência, deduplicação modal/linha e 409 sem sobrescrever aparência | `HttpRealSwaggerValidacaoETratamentoDe409` |

Os casos PostgreSQL exigem banco **novo e descartável**, loopback, prefixo `noponto_tarifas_`, fornecido explicitamente pela variável de teste existente; não utilizar banco real. Eles foram adicionados, mas não executados nesta tarefa por falta de banco descartável autorizado. A suíte direcionada offline valida contratos/regras/modelo/SQL, sem provar execução do DDL e persistência em PostgreSQL.

Verificação anterior (`42f4ad9`): 75/75 testes direcionados e 910/910 da seleção offline oficial. Os testes adicionais do PUT e a publicação estão documentados no procedimento acima. Nenhuma migration aplicada, infraestrutura alterada, implantação, merge ou alteração da release #142.
