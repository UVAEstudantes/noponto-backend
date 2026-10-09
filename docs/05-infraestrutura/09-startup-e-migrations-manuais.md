# Startup e migrations manuais

## Política do startup normal

Antes desta etapa, `Program.cs` aplicava migrations EF incondicionalmente e depois executava `PosicaoVeiculoTsBootstrapper`. Agora `DatabaseStartupPolicy` separa essas responsabilidades, antes de `app.Run` e do início dos HostedServices:

| Ambiente efetivo | Política EF | Bootstrap Redis |
| --- | --- | --- |
| `Development` explícito (sem distinguir maiúsculas) | `MigrateAsync`, como no desenvolvimento anterior | Depois do sucesso |
| `Production`, `Staging`, `Testing`, ausente ou desconhecido | `GetPendingMigrationsAsync`; bloqueia se houver pendências ou falha | Somente após aprovação |

Não há flag que habilite migrations em produção. O ambiente padrão do host é Production; uma identificação inválida não cai em Development. As guardas existentes de isolamento de desenvolvimento continuam exigindo hosts locais permitidos. Quem configurar deliberadamente o ambiente efetivo como Development ainda selecionará o comportamento de desenvolvimento: conferir tanto `ASPNETCORE_ENVIRONMENT` quanto `DOTNET_ENVIRONMENT` em qualquer implantação futura.

A consulta de pendências compara migrations da assembly com o histórico EF, sem aplicar migrations ou criar schema. A implementação EF/Npgsql consultada usa leitura do histórico; histórico ausente resulta em todas as migrations conhecidas pendentes. Isso não certifica ausência de drift físico, compatibilidade com migrations mais novas que a assembly nem privilégios efetivos de escrita da credencial. Fontes: [EF Core 9](https://raw.githubusercontent.com/dotnet/efcore/v9.0.10/src/EFCore.Relational/Extensions/RelationalDatabaseFacadeExtensions.cs) e [Npgsql EF 9](https://raw.githubusercontent.com/npgsql/efcore.pg/v9.0.2/src/EFCore.PG/Migrations/Internal/NpgsqlHistoryRepository.cs).

Falhas de verificação encerram o startup com diagnóstico genérico, sem transportar mensagem ou exceção interna do provider. Pendências indicam necessidade de aplicação manual autorizada. O bootstrap mantém o código existente, Redis STRING/HASH, TTL, idempotência e `When.NotExists`; ele não roda se a política EF reprovar. Não foram alterados GPS, ETA, Redis, SignalR, HostedServices ou migrations.

## Auditoria e compatibilidade

O único `Migrate` automático do startup normal era o bloco de `Program.cs`. Não foram encontrados `EnsureCreated` nem DDL de inicialização de schema no fluxo normal. Permanecem três chamadas explícitas de `MigrateAsync` nos comandos administrativos `structural-import`, `trem-structural-import` e `rail-schedule-import`. Esses comandos são tratados antes do host web, podem modificar schema/dados e não são um procedimento de migration isolada. Não foram executados nem alterados.

Dockerfile e Compose foram inspecionados, sem alterações. O Compose produtivo seleciona Production; o override local seleciona Development. A versão build-102 continua sem qualquer mudança até uma implantação futura autorizada. A nova política é compatível se o banco tiver todas as migrations da nova assembly aplicadas e a credencial conseguir consultar o histórico EF. Não houve acesso ao banco para comprovar essa condição. Uma reprovação pode gerar ciclo de reinícios com a política de restart existente; exige diagnóstico e intervenção, não tentativa automática de corrigir schema.

## Procedimento manual futuro proposto (não implementado/executado)

Preferir SQL revisável, aplicado por processo administrativo separado da API. A [documentação EF](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) descreve geração e aplicação de scripts de migrations. Ainda não existe neste projeto uma factory de design time nem um comando exclusivo de migrations; não executar `dotnet ef` contra produção assumindo que o startup atual seja livre de efeitos colaterais.

1. Em etapa específica, preparar tooling separado (por exemplo, factory de design time com configuração explícita e sem bootstrap/HostedServices) para gerar SQL entre versões conhecidas, usando a mesma assembly e versões EF da release. A factory ou ferramenta não faz parte desta implementação.
2. Revisar SQL, migrations e compatibilidade com a API ativa/rollback; registrar origem, destino e hash do artefato. Não presumir que rollback de imagem reverta schema.
3. Ensaiar o script e a inicialização da release em PostgreSQL/PostGIS e Redis descartáveis, sem dados, conexões ou credenciais produtivas. Validar também o cenário sem pendências e o de falha de leitura.
4. Obter autorização operacional, backup verificado, janela e plano de recuperação antes de qualquer aplicação produtiva. Executar SQL por cliente administrativo separado, com credencial protegida fora do Git, logs e argumentos da linha de comando. Avaliar transações conforme o SQL efetivo.
5. Confirmar histórico e schema após a aplicação. Só então autorizar a inicialização/deploy da API; o startup continuará apenas verificando pendências.

Não mudar produção para Development para aplicar migrations. Não usar comandos de importação como substituto desse procedimento.

## Validação e limites

As regressões usam callbacks e proxies em memória; nenhuma conexão PostgreSQL/Redis é criada. Cobrem ambientes não desenvolvedores, pendências, falha de conexão/enumeração, diagnóstico sem informação sensível, desenvolvimento, falha de migration, falha de bootstrap e o bootstrap real sobre fixtures Redis STRING/HASH simuladas. A nova classe integra a seleção offline existente do CI.

Os testes não iniciam o host completo nem certificam estado/permissões do banco real. A execução no GitHub Actions após integração autorizada ainda deve confirmar a seleção ampliada no runner. Ensaios de integração com infraestrutura descartável e qualquer aplicação/deploy produtivo exigem etapas futuras; não foram executados nesta etapa.
