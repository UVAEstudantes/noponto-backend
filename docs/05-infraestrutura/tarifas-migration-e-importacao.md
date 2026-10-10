# Operação controlada: novo schema de tarifas

Documento para execução futura pelo operador, **após autorização explícita**. Os comandos produtivos abaixo não foram executados durante o desenvolvimento. Não disparar/aprovar workflow, modificar secrets, configurar Development em produção nem usar comandos estruturais para aplicar migrations.

## Decisão de compatibilidade

A imagem anterior consulta Tarifa/ValidoDe/ValidoAte/CreatedAt/UpdatedAt/Ativo e o esquema antigo exigia LinhaId+ModalId. A nova migration remove/renomeia essas colunas e impõe modal OU linha. Mesmo que rotas de GPS possam continuar respondendo, não se pode garantir a API antiga com o novo schema. A política de startup consulta pendências EF, não detecta compatibilidade com schema mais novo.

**É necessária uma curta janela de manutenção:** parar a API antiga e quaisquer escritores do banco antes do backup final/migration; manter indisponível até a API nova estar validada. Não realizar rolling deploy. A indisponibilidade inclui GPS/polling/SignalR enquanto a API está parada; planejar aviso e validação de retomada. Não executar importação tarifária durante a janela: ela é posterior e separadamente autorizada.

O deploy existente tenta rollback somente da imagem/configuração. Isso **não recupera o schema nem os dados apagados**. Não autorizar seu gate enquanto a migration não tiver sido ensaiada, o plano de backup/restauração validado e a janela coordenada. Um rollback automático para imagem antiga após essa migration não comprova saúde; se ocorrer, parar a API e recuperar o banco antes de iniciar a imagem antiga. O smoke `/` isolado é insuficiente. Não foram alterados scripts/workflows para contornar esse limite.

## 1. Preparação e SQL local

Na revisão, confirmar exatamente 22 migrations aplicadas, última `20261006180000_IntegridadeCircularDuravel`, e ausência de drift. Nova: `20261010002416_TarifasPagamentosMvp`.

A factory de design-time exige NOPONTO_EF_CONNECTION e não inicia host/Redis/GPS. Geração de SQL/model check não precisa conectar ao banco: usar connection sintética local, nunca credenciais produtivas.

```bash
export NOPONTO_EF_CONNECTION='Host=localhost;Database=noponto_design;Username=design'
dotnet restore NoPonto/NoPonto.csproj
dotnet build NoPonto/NoPonto.csproj -c Release --no-restore
dotnet ef migrations has-pending-model-changes --project NoPonto --configuration Release --no-build
dotnet ef migrations script 20261006180000_IntegridadeCircularDuravel \
  20261010002416_TarifasPagamentosMvp --project NoPonto --configuration Release --no-build \
  --output tarifas-pagamentos-mvp.sql
sha256sum tarifas-pagamentos-mvp.sql
unset NOPONTO_EF_CONNECTION
```

Artefato gerado incluído: [tarifas-pagamentos-mvp.sql](tarifas-pagamentos-mvp.sql). Revisar SQL inteiro, comparando hash com o artefato entregue ao Debian. O SQL tem transação BEGIN/COMMIT, DELETE explícito somente em Tarifas, verificação de FKs de entrada, alterações tarifárias, duas tabelas novas e inclusão automática da migration no histórico EF. Não editar o histórico manualmente. Não repetir o SQL sobre schema já atualizado. Timeout de lock 15 s aborta se houver usuários ativos; não aumentar/remover sem revisar a janela.

Antes de produção, repetir o SQL e a recuperação em **PostgreSQL 16/PostGIS descartável** com backup do ambiente alvo, sem conexões aos serviços externos. Confirmar linhas/modais e demais contagens, antiga imagem+schema restaurado e nova imagem+schema migrado. Os testes de fixture cobrem schema/constraints, mas não substituem ensaio com o backup e os artefatos reais.

## 2. Contexto operacional (Debian)

Confirmar paths/nomes com o operador; são os usados no procedimento existente. Não imprimir `docker compose config` completo, env ou credenciais. `REAL_ENV_FILE`, `STATE_DIR`, `BACKUP_DIR`, `NEW_IMAGE`, `PREVIOUS_IMAGE`, `MODAL_ID`, `LINHA_ID` e IDs de métodos abaixo são parâmetros reais fornecidos pelo operador, não valores inventados. Imagens devem estar fixadas por digest/tag local preservada; nunca usar latest para recuperar.

```bash
set -euo pipefail
umask 077
: "${REAL_ENV_FILE:?arquivo env produtivo confirmado}"
: "${STATE_DIR:?diretório privado de estado confirmado}"
: "${BACKUP_DIR:?diretório privado de backup confirmado}"
: "${NEW_IMAGE:?digest da candidata aprovado}"
: "${PREVIOUS_IMAGE:?referência preservada da imagem anterior}"
mkdir -p "$BACKUP_DIR"
compose() {
  env -u GTFSRT_API_IMAGE docker compose --project-name noponto \
    --project-directory /opt/stacks/noponto \
    --env-file "$REAL_ENV_FILE" --env-file "$STATE_DIR/active.env" \
    -f /opt/stacks/noponto/docker-compose.yml \
    -f /home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml "$@"
}
compose config --quiet
docker inspect --format '{{.Image}}' noponto_api > "$BACKUP_DIR/previous-image-id.txt"
```

Se active.env ainda não existir, usar o conjunto de env atualmente confirmado pelo operador, sem criar marcador fictício. Preservar configurações e imagem anteriores de modo privado, conforme .deploy/README.md, inclusive bytes da imagem no daemon ou registry. Preservar os próprios dados/cache operacional que o plano de recuperação do projeto exigir. Conferir espaço livre suficiente para backup, restore de ensaio e recuperação.

Consulta somente leitura para inspeção (confirmar container PostgreSQL `transporte_postgres`):

```bash
docker exec -i transporte_postgres sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT count(*) AS tarifas_legadas FROM "Tarifas";
SELECT count(*) AS linhas FROM "Linhas";
SELECT count(*) AS modais FROM "Modais";
SELECT conrelid::regclass AS tabela_origem, conname, pg_get_constraintdef(oid)
FROM pg_constraint WHERE contype = 'f'
AND (conrelid = '"Tarifas"'::regclass OR confrelid = '"Tarifas"'::regclass);
SELECT schemaname, viewname FROM pg_views
WHERE definition ILIKE '%Tarifas%';
SQL
```

Guardar contagens de todas as tabelas relevantes fora de tarifas, schema-only dump e dependências. Nenhuma FK de entrada é esperada: se houver, interromper e revisar. Conferir locale/encoding UTF8, PostGIS, permissões da role, 22 migrations e drift com a baseline. Não prosseguir se existir qualquer migration pendente diversa ou schema inesperado.

## 3. Backup e verificação de recuperação

Autorizar a janela, impedir outras promoções/escritores e parar a API. Para evitar disputa com o deploy, durante backup e migration segurar o mesmo lock em uma sessão administrativa:

```bash
exec 9>"$STATE_DIR/deploy.lock"
flock -n 9
compose stop api
# O operador deve confirmar que nenhum outro serviço/processo escreve neste banco.
BACKUP="$BACKUP_DIR/noponto-pre-tarifas-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker exec transporte_postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom' > "$BACKUP"
test -s "$BACKUP"
sha256sum "$BACKUP" > "$BACKUP.sha256"
sha256sum -c "$BACKUP.sha256"
docker exec transporte_postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --schema-only' > "$BACKUP.schema.sql"
```

Backup consistente por pg_dump e freeze de escritores evitam perda de novas escritas na recuperação. Arquivos são privados: contêm dados, nunca versionar/transportar em logs. Roles/extensões/configuração de cluster devem estar cobertas pelo backup operacional existente e pelas permissões necessárias à restauração.

Verificação de recuperação **antes de aplicar em produção**, em container descartável. Esta etapa restaura o conteúdo em banco isolado, sem expor porta e sem iniciar a API com serviços reais:

```bash
RESTORE_CONTAINER="noponto-tarifas-restore-$(date -u +%Y%m%dT%H%M%SZ)"
docker run -d --name "$RESTORE_CONTAINER" --network none \
  -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=noponto_tarifas_restore postgis/postgis:16-3.4
# Aguardar init completo; pg_isready TCP retorna 0 apenas após o startup final:
docker exec "$RESTORE_CONTAINER" pg_isready -h 127.0.0.1 -U postgres -d noponto_tarifas_restore
# Só continuar após readiness confirmada.
docker exec -i "$RESTORE_CONTAINER" pg_restore --exit-on-error --no-owner --no-privileges \
  -U postgres -d noponto_tarifas_restore < "$BACKUP"
docker exec "$RESTORE_CONTAINER" psql -X -v ON_ERROR_STOP=1 -U postgres -d noponto_tarifas_restore \
  -c 'SELECT count(*) FROM "Tarifas"; SELECT count(*) FROM "Linhas"; SELECT count(*) FROM "Modais"; SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";'
```

Comparar resultados com as contagens/22 migrations do backup; verificar dados e permissões exigidas pela imagem anterior em ambiente de homologação isolado. Listar arquivo (`pg_restore --list`) sozinho **não comprova restauração**. Aplicar o SQL no clone e verificar constraints, schema, nova imagem, queries e recuperação. Se qualquer verificação falhar, não migrar produção; ainda se pode reabrir a antiga API sobre o banco intocado. Remover somente o container/volume de ensaio identificado após conservar os resultados privados:

```bash
docker rm -f -v "$RESTORE_CONTAINER"
```

## 4. Migration manual autorizada

Somente depois do backup restaurado, ensaio validado e autorização para descartar tarifas antigas. API/escritores permanecem parados. Copiar o SQL revisado ao Debian, verificar hash e executar:

```bash
sha256sum tarifas-pagamentos-mvp.sql
docker exec -i transporte_postgres sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' < tarifas-pagamentos-mvp.sql

docker exec -i transporte_postgres sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT count(*) FROM "Tarifas";
SELECT count(*) FROM "FormasPagamento";
SELECT count(*) FROM "FormasPagamentoVinculos";
SELECT count(*) FROM "Linhas";
SELECT count(*) FROM "Modais";
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid IN ('"Tarifas"'::regclass, '"FormasPagamento"'::regclass, '"FormasPagamentoVinculos"'::regclass);
SQL
```

Esperado: 23 migrations, nova última; Tarifas/catalogo/vínculos vazios; contagens estruturais preservadas; FKs/CHECKs/UNIQUE corretos. Migration falhando deve reverter a transação inteira. Conferir schema/histórico antes de decidir reiniciar a antiga API: não presumir rollback por exit code apenas. Não iniciar imagem anterior se o schema já mudou.

## 5. Implantação e validação funcional

Liberar lock administrativo **antes** de aprovar o job de deploy existente, que usa o mesmo lock. Manter janela/escritores suspensos e nenhum outro deploy autorizado. Confirmar que a candidata corresponde exatamente ao SQL aplicado e roda Production. Não iniciar a API antiga entre migration e promoção.

```bash
flock -u 9
exec 9>&-
```

O operador aprova o environment GitHub `production` da candidata já publicada por fluxo autorizado. Não disparar novo push/merge ou aprovação automaticamente. A API nova apenas verifica pendências; não aplica migrations produtivas. Se o job falhar e trocar para imagem antiga, tratar como incidente de compatibilidade e seguir recuperação abaixo; `/` 200 não encerra o incidente.

Após deploy aprovado e antes de encerrar janela, conferir imagem efetiva, ausência de reinícios/erros de schema e os endpoints com IDs reais:

```bash
: "${MODAL_ID:?modal persistido real}"
: "${LINHA_ID:?linha persistida desse modal}"
API=http://127.0.0.1:8080
test "$(docker inspect --format '{{.Image}}' noponto_api)" = "$(docker image inspect --format '{{.Id}}' "$NEW_IMAGE")"
docker inspect --format '{{.State.Status}} restarts={{.RestartCount}} oom={{.State.OOMKilled}}' noponto_api
curl --fail-with-body "$API/"
curl --fail-with-body "$API/tarifas/resolver?linhaId=$LINHA_ID"
curl --fail-with-body "$API/tarifas/resolver?modalId=$MODAL_ID&linhaId=$LINHA_ID"
curl --fail-with-body "$API/formas-pagamento"
```

Confirmar tarifa null e catálogo vazio no primeiro deploy. IDs inexistentes retornam 404, modal incompatível retorna 400. Testes de escrita produtiva dependem de valores/métodos reais aprovados pelo operador: PUT tarifa MANUAL, criar método, repetir nome com case/espaços (409), vincular modal/linha, conferir deduplicação, remover tarifa específica e conferir fallback. Esses comandos criam dados: não usar valores fictícios em produção. Conferir GPS/ETA/SignalR existentes sem alterar sua configuração. Só reabrir tráfego/escritores após validação funcional e decisão do operador.

## 6. Importação posterior, separada

Após atualização validada, preservar backup e gerar um segundo backup pré-apply se já houver novas escritas. O comando usa a imagem/variáveis do serviço e não inicia host/polling/Redis/SignalR. Não chamar structural-import, que tem comportamento de migrations próprio.

```bash
compose run --rm --no-deps --pull never api tarifas-import-arcgis --dry-run \
  > "$BACKUP_DIR/tarifas-arcgis-dry-run.json"
```

Revisar totais/itens: conflitos, inválidos, manuais preservadas, serviços sem linha/contexto e alterações previstas. Confirmar que tarifas padrão não foram inventadas. Depois de autorização **específica para apply**:

```bash
compose run --rm --no-deps --pull never api tarifas-import-arcgis --apply \
  > "$BACKUP_DIR/tarifas-arcgis-apply.json"
docker exec -i transporte_postgres sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT "Fonte", count(*) FROM "Tarifas" GROUP BY "Fonte";
SELECT count(*) FROM "Tarifas" WHERE ("ModalId" IS NULL) = ("LinhaId" IS NULL);
SELECT "LinhaId", count(*) FROM "Tarifas" WHERE "LinhaId" IS NOT NULL GROUP BY "LinhaId" HAVING count(*) > 1;
SELECT "ModalId", count(*) FROM "Tarifas" WHERE "ModalId" IS NOT NULL GROUP BY "ModalId" HAVING count(*) > 1;
SQL
curl --fail-with-body "$API/tarifas/resolver?linhaId=$LINHA_ID"
compose run --rm --no-deps --pull never api tarifas-import-arcgis --dry-run \
  > "$BACKUP_DIR/tarifas-arcgis-rechecagem.json"
```

Esperado: nenhuma duplicata/escopo inválido; valores por linha ARCGIS_SPPO; MANUAL intactos; repetição da mesma fonte inalterada. A fonte pode mudar entre download/dry-run/apply: revisar relatório do apply, não prometer snapshot externo estável. Timeout de lock/falha de fonte aborta; não aumentar permissões nem esconder falhas. Catálogo/formas não são preenchidos pelo ArcGIS.

## 7. Recuperação compatível com schema e imagem

1. Se a migration abortou e o schema/histórico continuaram antigos, manter banco e reiniciar a imagem antiga confirmada. Sem restauração desnecessária.
2. Se schema novo foi confirmado e a nova API falhou, parar qualquer imagem e escritores. **Trocar só a imagem não basta.** Preservar diagnóstico e, se já houve escritas novas, backup pós-falha privado antes da recuperação. O operador decide perda/reconciliação das escritas posteriores ao backup.
3. Restaurar backup pré-migration em **nova database de recuperação**, sem apagar a database falha. Executar somente após autorização operacional e confirmação de espaço/role/nome distintos. Usar nome fornecido pelo operador:

```bash
: "${RECOVERY_DB:?nova database de recuperação explicitamente autorizada}"
# Confirmar manualmente que o nome é novo, não existe, e difere da database original.
docker exec transporte_postgres sh -c 'exec createdb -U "$POSTGRES_USER" "$1"' sh "$RECOVERY_DB"
docker exec -i transporte_postgres sh -c 'exec pg_restore --exit-on-error --no-owner --no-privileges -U "$POSTGRES_USER" -d "$1"' sh "$RECOVERY_DB" < "$BACKUP"
docker exec -i transporte_postgres sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$1"' sh "$RECOVERY_DB" <<'SQL'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT count(*) FROM "Tarifas";
SELECT count(*) FROM "Linhas";
SELECT count(*) FROM "Modais";
SQL
```

4. Verificar 22 migrations/colunas legadas/tarifas antigas e demais contagens/ownership/permissões. Atualizar de forma privada e autorizada a configuração efetiva do serviço para `POSTGRES_DB=$RECOVERY_DB` e a referência persistente de imagem anterior (`GTFSRT_API_IMAGE=$PREVIOUS_IMAGE`), seguindo `.deploy/README.md` e coordenando active.env/active.run com o operador. Não imprimir conteúdo de env, não iniciar outro banco/Redis/serviço pela composição e não editar __EFMigrationsHistory. A mudança da database alvo exige autorização de infraestrutura; nenhuma foi feita nesta tarefa.
5. Sob o mesmo lock e após configuração/pares schema-imagem conferidos, recriar somente API com a imagem anterior preservada:

```bash
compose config --quiet
compose up -d --no-deps --no-build --pull never api
curl --fail-with-body http://127.0.0.1:8080/
```

6. Validar também funcionalidades que acessam dados, contagens e ausência de reinícios, retomada dos pipelines e contratos da imagem anterior. Reabrir somente após decisão do operador. Não usar Down como recuperação de dados: ele apaga também novos pagamentos/tarifas e restaura apenas estrutura. Não remover a database falha até a reconciliação e autorização para sua retenção/limpeza.

A imagem anterior pode ter política de startup diferente (build-102 aplicava migrations): ensaiar o artefato exato e seu comportamento sobre o backup antes da janela. Este procedimento não autoriza essa imagem a modificar schemas por conta própria.
