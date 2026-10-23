# Edição administrativa e publicação

## PUT /formas-pagamento/{id}

Reutiliza `FormaPagamentoRequest`, a normalização, validação e tradução de conflitos do cadastro. Exemplo de corpo:

```json
{"nome":"Vale Transporte","icone":"wallet","cor":"#12aBcD"}
```

Resposta 200 com `id`, `nome`, `icone`, `cor`. Nome obrigatório; atualização completa: ícone/cor omitidos ou null removem a personalização. Para manter a aparência, reenviar seus valores atuais. Retorna 400 para nome/ícone/cor ou GUID inválidos, 404 para ID inexistente ou removido durante a escrita, 409 para nome normalizado já utilizado. Exclui o próprio ID da verificação de duplicidade. O índice único existente também garante 409 nas corridas. Atualiza apenas Nome, NomeNormalizado, Icone e Cor; não altera ID, CriadoEmUtc, tarifas ou vínculos. GETs/POST anteriores já aceitam/retornam a aparência e foram preservados.

Não há autorização administrativa reutilizável ativa. O novo PUT exige o header `X-Formas-Pagamento-Admin-Key`, comparado em tempo constante após SHA-256 com `FORMAS_PAGAMENTO_ADMIN_SECRET` da configuração padrão do ASP.NET (variável de ambiente no servidor). Sem segredo configurado de 32 a 256 caracteres, ou com valor inteiramente branco, retorna 503. Credencial ausente, errada ou headers duplicados retornam 401, antes do model binding e sem revelar a existência do ID.

Na implantação futura autorizada, provisionar pelo gerenciador de segredos um valor aleatório de pelo menos 256 bits (32 bytes em base64, por exemplo). Somente ferramenta de operador confiável deve enviar o header, via HTTPS. Nunca colocar o segredo em mobile, frontend, URL/query, appsettings/Compose versionado, logs, histórico, PR ou comandos compartilhados. Não registrar o header em proxy/APM. Rotacionar no servidor e na ferramenta de operador. Não há segredo padrão, login ou sessão administrativa. Nenhum segredo foi provisionado nesta tarefa; sem ele, o PUT permanece bloqueado e o restante da API continua funcionando.

A proteção cobre o novo PUT. GETs, POST e vínculos existentes conservam seu acesso anterior; proteção global das demais escritas não faz parte desta mudança.

## Primeiro schema, depois merge/deploy

**Não fazer merge nem deploy até confirmação explícita do operador de que o schema foi atualizado.** A PR deve permanecer em rascunho, sem auto-merge. O workflow existente executa CI na PR; build/publicação/deploy são condicionados a push na main, portanto a confirmação deve preceder o merge. Nenhum workflow foi modificado.

A única migration permitida nesta publicação é `20261023014903_FormaPagamentoAparencia`, sobre `20261010002416_TarifasPagamentosMvp`. O [SQL incremental gerado pelo EF](formas-pagamento-aparencia.sql) está inalterado: dois ADD COLUMN nullable sem defaults/backfill e um INSERT no histórico. Não altera tarifas, vínculos, índices, FKs ou outras tabelas de negócio. Não usar `dotnet ef database update`: poderia aplicar migrations históricas pendentes. Não executar Down nem migrations anteriores.

Procedimento **não executado**, para operador autorizado numa máquina administrativa com clientes PostgreSQL compatíveis com o servidor. Usar serviço libpq `noponto-prod` previamente configurado com host/porta/database conferidos, TLS verificável (`sslmode=verify-full`, CA e hostname corretos) e autenticação fora do repositório/linha de comando. Adaptar apenas o nome do serviço ao ambiente real. Executar no checkout revisado, sem outra operação de schema concorrente, e parar em qualquer erro.

```bash
set -euo pipefail
umask 077
export PGSERVICE=noponto-prod
backup_dir="$HOME/noponto-backups/formas-pagamento-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$backup_dir"

# 1. Somente leitura: conferir banco/operador, baseline exata, colunas e tipos anteriores.
psql -X --dbname="service=$PGSERVICE" -v ON_ERROR_STOP=1 \
  -f NoPonto/docs/formas-pagamento-aparencia-preflight.sql

# 2. Backup integral antes do DDL; respeitar a política de retenção do operador.
pg_dump --dbname="service=$PGSERVICE" --format=custom --file="$backup_dir/antes.dump"
test -s "$backup_dir/antes.dump"
pg_restore --list "$backup_dir/antes.dump" > "$backup_dir/antes.toc"
sha256sum "$backup_dir/antes.dump" > "$backup_dir/antes.sha256"
```

O catálogo listado não prova restauração. Validar o backup em ambiente isolado conforme o procedimento de recuperação do operador. **Interromper aqui até o operador conferir o banco, o backup e autorizar o DDL.** A próxima etapa é separada e não deve ser executada automaticamente após o backup.

```bash
set -euo pipefail
export PGSERVICE=noponto-prod

# 3. Repetir preflight imediatamente antes: divergência ou schema já atualizado exige parar.
psql -X --dbname="service=$PGSERVICE" -v ON_ERROR_STOP=1 \
  -f NoPonto/docs/formas-pagamento-aparencia-preflight.sql

# 4. Após backup validado e autorização: APENAS esta migration incremental.
PGOPTIONS='-c search_path=pg_catalog,public -c lock_timeout=5s -c statement_timeout=60s' \
psql -X --dbname="service=$PGSERVICE" -v ON_ERROR_STOP=1 \
  -f NoPonto/docs/formas-pagamento-aparencia.sql

# 5. Pós-verificação somente leitura.
psql -X --dbname="service=$PGSERVICE" -v ON_ERROR_STOP=1 <<'SQL'
SELECT "MigrationId", "ProductVersion" FROM public."__EFMigrationsHistory"
WHERE "MigrationId" = '20261023014903_FormaPagamentoAparencia';
SELECT column_name, data_type, character_maximum_length, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'FormasPagamento' AND column_name IN ('Icone','Cor')
ORDER BY column_name;
SQL
```

Exigir uma linha de histórico com EF `9.0.10`, Cor varchar(7), Icone varchar(20), ambas nullable e sem default. Guardar saída e identificação do backup em canal administrativo seguro. Se a migration já consta no histórico, verificar o schema e não reaplicar. Estado parcial, baseline diferente ou falha/timeout de lock exigem revisão. O SQL é transacional; ON_ERROR_STOP encerra a conexão em erro e reverte a transação. Após perda de conexão, verificar histórico/schema antes de repetir: o COMMIT pode já ter concluído.

Somente após essa confirmação e CI aprovado, autorizar separadamente merge/deploy pelo processo existente e provisionamento do segredo. O backend antigo tolera as colunas novas; no rollback do binário, manter o schema aditivo (Down perde aparência). Após a implantação, conferir GETs e realizar a edição com ferramenta administrativa. Sem alterar configurações operacionais, Compose, GPS ou a release atual nesta tarefa.

## Evidências e limites

| Requisito | Teste |
| --- | --- |
| Catálogo, cor e compatibilidade JSON anterior | `FormaPagamentoAparenciaTests`, `TarifasRegrasTests` |
| 400 antes de I/O | `PutRecusaDadosInvalidosAntesDeConsultarBanco` |
| Normalização, mesmo ID, DTO, null e UPDATE restrito | `PutNormalizaNomeRetornaDtoELimitaUpdateAoCadastro` |
| 404/409 e remoção concorrente | `PutDistingueAusenciaDuplicataERemocaoConcorrente` |
| Corrida de índice único | `PutTraduzUnicidadeConcorrentePara409` |
| Sem configuração, ausente/errado/repetido e segredo válido | `SemSegredoForteEdicaoFicaIndisponivel`, `SegredoAusenteOuIncorretoRetorna401`, `SegredoCorretoAutorizaSemAceitarHeadersDuplicados` |
| Pipeline HTTP PUT, autorização antes do body, 200/400/401/404/409, limpeza com null | `PutHttpProtegeAntesDoBodyEEntregaContratoAtualizado` |
| EF nullable, snapshot sem drift e SQL restrito a duas colunas | `ModeloSnapshotEMigrationSomenteDuasColunasNullable` |
| PostgreSQL: preservação na migration e POST/GETs/PUT persistidos | `TarifasPostgresFixture.InitializeAsync`, `HttpRealSwaggerValidacaoETratamentoDe409` |

Os testes offline de PUT usam MVC e o provider Npgsql reais, simulando apenas a fronteira ADO.NET com interceptores. Verificam tradução SQL/erros sem conectar ao banco; não provam execução no PostgreSQL. Os testes reais exigem banco novo descartável autorizado, loopback, prefixo `noponto_tarifas_` e `TARIFAS_TEST_CONNECTION`. Não executados por falta desse ambiente; não usar banco real para testes. SQL/preflight revisados, sem ensaio PostgreSQL ou execução produtiva nesta tarefa.

Validação local: 94 aprovados, 0 falhas, 0 ignorados nos direcionados; 929 aprovados, 0 falhas, 0 ignorados na seleção offline, TRX verificado. Testes Python do seletor/verificador: 6 aprovados, 4 ignorados por exigirem POSIX não root. Compilação do projeto pelos testes aprovada com cinco avisos anteriores. Comandos PowerShell executados:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~NoPonto.Tests.FormaPagamentoEdicaoTests|FullyQualifiedName~NoPonto.Tests.FormaPagamentoAparenciaTests|FullyQualifiedName~NoPonto.Tests.TarifasRegrasTests' --logger 'trx;LogFileName=put-final.trx' --results-directory artifacts/formas-pagamento-put
$filter = py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=offline.trx' --results-directory artifacts/formas-pagamento-put
py -3.13 -B .ci/offline_tests.py verify artifacts/formas-pagamento-put/offline.trx
```
