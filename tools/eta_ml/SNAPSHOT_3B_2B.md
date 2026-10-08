# ETA/ML 3B.2B — snapshot seletivo real local

Preparação local, sem execução de SQL, Docker, SSH, produção, snapshot real ou treino pelo agente. O operador informou encerramento da 3B.2A e validação do fix em produção; essa evidência não foi coletada pelo agente.

Este guia acompanha a ferramenta sem depender da regra que ignora novos arquivos em NoPonto/docs. Nenhuma regra de Git foi alterada. Antes de selecionar viagens, o operador deve conferir fechamento e escoamento dos workers/outbox; viagem fechada não garante que todos os dados assíncronos já estejam persistidos na fotografia.

## Marco pós-fix

- cutoff: `2026-10-07T21:00:57.997530Z`;
- backend: `70d7bcb8041e167df063237b4fdcafd4971b01d6`;
- image: `sha256:42d8b78b7b683234211f0d10edf8a79ddcf5c7f6557df9fd065f2409299f5094`;
- migration: `20261006180000_IntegridadeCircularDuravel`;
- contrato: `noponto-eta-gps-v1`;
- sampling: enabled=true, line_percentage=10, block_minutes=60, seed=NOPONTO_ML_V1.

Esses metadados são atestado do operador. O script confere a migration instalada, mas não verifica digest/commit da API pela conexão PostgreSQL. O cutoff anterior permanece documentado como coleta histórica; não misturar seus manifestos com esta coleta pós-fix.

## Arquitetura e dependências

`snapshot.py prepare` gera bundle v2 offline → operador revisa planos → `pg_dump --schema-only` das onze tabelas + uma sessão psql REPEATABLE READ/READ ONLY para dados e dependências → `seal` valida funções/triggers/dependências, gera functions.sql das definições originais e hasha os arquivos → transferência manual → `verify` offline → `restore` somente loopback, banco dedicado vazio, pre-data/dados/funções/post-data → contagens e comparação do catálogo/FKs/funções/triggers/dependências.

Nenhuma biblioteca nova: Python stdlib e clientes PostgreSQL 16 (`psql`, `pg_dump`, `pg_restore`). Não inicializa API nem reaplica migrations. A fixture mínima 3B.1 não é usada como schema real: o arquivo schema.dump preserva tipos, colunas, índices e constraints instalados das tabelas selecionadas, sem owners/ACLs/dados.

Fontes pesadas: TelemetriasVeiculoMl e HistoricoPassagens usam exclusivamente `ViagemId IN (lista explícita)`, sem filtrar suas linhas por timestamp e perder confirmações posteriores. EventosViagem não tem coluna ViagemId: usa `Payload->>'viagem_id' IN (mesma lista)` e janelas início/fim auditadas por viagem sobre TimestampEvento indexado. A janela é o ciclo completo fechado, inclui eventos órfãos de histórico existentes dentro dela e não usa último GPS como fim. Eventos fora das fronteiras são inconsistentes com o contrato e precisam ser investigados na auditoria antes da seleção; esta ferramenta não reconstrói journal inválido. Nenhum fallback para cópia completa ou descoberta global de IDs.

Estrutura completa escolhida: Modais, FontesEstruturais, Linhas, Sentidos, Paradas, PadroesOperacionais, PadroesVersoes e OcorrenciasParadasPadroes. É mais simples preservar todas as versões e pais que fechar IDs individualmente. Revisar tamanhos reais dessas oito fontes antes de exportar; não pressupor tamanho pequeno somente pelo nome. Se não forem pequenas, interromper e definir recorte de FK explícito em tarefa própria.

Inspecionados DbContext, ModelSnapshot e migrations: Linhas/Paradas dependem de Modais; Sentidos depende de Linha/FonteEstrutural; Paradas tem pai recursivo; versões dependem de padrões, e padrão referencia sua versão atual por FK composta. Histórico depende de parada/sentido/versão/ocorrência; telemetria depende de versão/ocorrência. Itinerarios/ParadasItinerario foram removidos pela migration RemoverLegadoEstruturalBusBrt; não transportados. FKs reais diferentes do esperado fazem a exportação/seal falhar, sem remover constraints ou copiar pais desconhecidos automaticamente. ViagensOperacionais, outbox e demais fontes não são requeridos pelo exporter; evidência externa de qualidade continua no pedido auditado.

O ciclo padrão↔versão e a hierarquia de paradas são resolvidos pela criação normal do banco: pre-data cria tabelas/tipos, dados completos são inseridos, post-data cria/valida FKs e índices originais. Não há DROP CONSTRAINT, disable triggers ou session_replication_role. Banco com linhas/tabelas de aplicação existentes é recusado; falha em qualquer fase deixa cópia incompleta, nunca pronta. Preparar outro banco descartável vazio para repetir, sem limpeza automática.

Cada CSV tem uma coluna `row`, contendo JSONB da linha inteira, não apenas features. Geometrias de Paradas.Localizacao/PadroesVersoes.Geometria são explicitamente codificadas como hex EWKB (SRID/dimensões/coordenadas), evitando conversão GeoJSON irreversível na entrada geometry; `jsonb_populate_record` recupera tipos usando o schema original. Colunas geradas/identity e colunas espaciais novas não revisadas são recusadas. A conferência conectada desse round-trip permanece pendente.

## Pedido obrigatório

Copiar snapshot.request.example.json para arquivo privado local e preencher snapshot_end UTC fixo e trips auditadas. O template vazio é deliberadamente inválido. Formato de cada trip:

```json
{
  "viagem_id": "UUID_CANONICO_REAL_AUDITADO",
  "inicio": "INSTANTE_UTC_REAL_DO_PAYLOAD_INICIO",
  "fim": "INSTANTE_UTC_REAL_DO_PAYLOAD_FIM",
  "codigo_linha": "CODIGO_REAL",
  "referencia_auditoria": "REFERENCIA_DA_EVIDENCIA_DA_EXECUCAO_COMPLETA"
}
```

Exige cutoff <= início < fim < snapshot_end, UUID não vazio/único, código e referência não vazios. Seal confere fronteiras canônicas/schema2/código/timestamps no journal copiado; mantém tolerância de até 9 ticks entre timestamptz e payload. Isso não autentica a evidência nem atribui automaticamente AuditadaSemProtecao. Não incluir viagem aberta, parcialmente pré-cutoff ou qualidade não comprovada. Snapshot_end é limite exclusivo para selecionar viagens completas; snapshot_at/MVCC descrevem o instante posterior real de leitura. Incluem-se todas as linhas persistidas daquelas viagens visíveis nesse instante.

## Comandos futuros — execução manual

Os comandos abaixo não foram executados pelo agente. Trabalhar somente com arquivos/serviços confiáveis e diretórios novos. Manter bundle/request/logs fora do Git; `tools/eta_ml/outputs/` já é ignorado. Não registrar credenciais/IDs reais no histórico. Não expor senha em comandos: libpq service e passfile são configurados externamente pelo operador; `--no-password` impede prompt inesperado.

Na raiz do checkout (PC ou servidor), depois de preencher `tools/eta_ml/outputs/snapshot.request.real.json`:

```sh
python3 tools/eta_ml/snapshot.py prepare tools/eta_ml/outputs/snapshot.request.real.json tools/eta_ml/outputs/snapshot-3b2b
```

Servidor: disponibilizar os arquivos gerados ao operador e entrar no diretório do bundle. Serviço libpq deve apontar ao banco/schema corretos, com usuário SELECT. Nenhum host remoto está embutido na ferramenta. Primeiro planos somente, sem ANALYZE:

```sh
cd tools/eta_ml/outputs/snapshot-3b2b
PGSERVICE=noponto_auditoria psql -X --no-password -v ON_ERROR_STOP=1 -f plans.sql
```

Revisar uso do índice parcial (ViagemId,TimestampGps) em GPS, índice por ViagemId no histórico e TimestampEvento nas janelas do journal. Se houver scan global de fonte pesada, NÃO executar export.sh; não criar índice nem aumentar timeout por hipótese. Lista muito grande pode gerar planos ruins; reduzir explicitamente o pedido de viagens auditadas e preparar novo bundle. Scans estruturais pequenos são esperados.

Depois da revisão, ainda dentro do bundle:

```sh
PGSERVICE=noponto_auditoria sh export.sh
```

Esse script só faz pg_dump schema-only e leituras READ ONLY via psql; grava arquivos do cliente. A sessão dos dados usa timeout60s por statement, lock_timeout2s, work_mem8MB. Só selar após exit0 e recibo completed.csv. Schema dump e sessão de dados são operações separadas: não alterar schema/publicar migrations durante ambas. A comparação do catálogo após restore detecta divergência de colunas/FKs; não promete congelamento automático de todo DDL/índices entre conexões. Snapshot longo mantém MVCC e pode afetar vacuum, mesmo READ ONLY; manter pedido pequeno e interromper ao atingir timeout.

Retornar à raiz do checkout para selar e empacotar:

```sh
python3 tools/eta_ml/snapshot.py seal tools/eta_ml/outputs/snapshot-3b2b
tar -C tools/eta_ml/outputs -czf tools/eta_ml/outputs/snapshot-3b2b.tar.gz snapshot-3b2b
```

Transferir manualmente o pacote ao PC por canal autorizado fora desta ferramenta; não há SSH/upload automático. Extrair em diretório local privado, conservando todos os arquivos. Antes de restore:

```powershell
python tools/eta_ml/snapshot.py verify tools/eta_ml/outputs/snapshot-3b2b
```

O operador prepara previamente PostgreSQL16/PostGIS3.4 local descartável, com banco NOVO eta_snapshot_3b2b, sem túnel/encaminhamento de produção. Porta55439 abaixo é exemplo de loopback, não serviço criado/testado pelo agente. Identidade libpq via PGUSER/passfile externos. Somente nesse ambiente local:

```powershell
createdb --host=127.0.0.1 --port=55439 --no-password eta_snapshot_3b2b
psql -X --host=127.0.0.1 --port=55439 --dbname=eta_snapshot_3b2b --no-password -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION postgis;'
python tools/eta_ml/snapshot.py restore tools/eta_ml/outputs/snapshot-3b2b --database eta_snapshot_3b2b --port 55439
```

Restore fixa host127.0.0.1, aceita apenas nome eta_snapshot_*, exige banco vazio/PostGIS instalado, verifica hashes antes de conectar, conta cada tabela após insert e compara colunas/FKs após post-data. Processos têm timeout de300s por fase (60s para catálogo); volume grande pode exceder e deixar banco incompleto. Não foi executado teste conectado potencialmente longo nesta entrega.

Manifesto: cutoff/release/image/migration/contract/sampling, snapshot_end, source_context (snapshot_at/MVCC/versões PostgreSQL/PostGIS), lista ordenada de viagens/evidências, contagens por fonte e SHA256 dos CSVs/catalog/context/recibo/schema/scripts/request. Manifesto JSON e hashes são determinísticos para os mesmos arquivos e pedido; arquivos pg_dump reais podem variar entre execuções por metadados do archive e schema/MVCC. Não prometida igualdade binária de dois snapshots diferentes. Hash não autentica origem; preservar a confiança no canal/arquivo schema.dump, que contém DDL executável local.

## Perfis oficiais — bloqueio do exporter resolvido

O bloqueio originalmente encontrado foi resolvido por tools/collection_profiles.json, catálogo compartilhado incorporado ao binário do exporter e lido por Python pelo caminho do módulo. Ele permite somente as tuplas completas historical-pre-fix e official-post-fix; cutoff/commit/image/migration/contract/sampling misturados falham. Snapshot usa o perfil atual do mesmo catálogo. Ao disponibilizar utilitários Python, preservar tools/collection_profiles.json e os módulos collection_profiles.py/snapshot.py/snapshot_schema.py/snapshot_functions.reviewed.json em tools/eta_ml; não copiar somente snapshot.py isolado.

audit.example.json/options.example.json/config.real.example.json agora são pós-fix; exemplos historical preservam o marco anterior. Config real deve ter collection_profile=official-post-fix, com início >= cutoff atual e cortes por viagem completa; dataset manifest deve corresponder integralmente ao mesmo perfil. Configs legadas sem ID continuam apenas no contexto histórico/fixture antiga. A fixture sintética continua separada de produção, sem inventar release real; manifest real sem coleta/auditoria não é aceito.

Após snapshot/restauração/auditoria do operador, compilar o exporter offline atualizado e configurar ETA_ML_LOCAL_CONNECTION externamente, SOMENTE para o banco local descartável sem túnel. Preencher audit.real.json com as mesmas viagens, fronteiras e referências do snapshot e SnapshotReference correspondente; opções devem ter limites inteiros adequados ao volume real. Comando futuro, não executado:

```powershell
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --no-restore --verbosity quiet
dotnet run --project tools/EtaMl.Export/EtaMl.Export.csproj --no-build -- tools/EtaMl.Export/outputs/audit.real.json tools/EtaMl.Export/outputs/options.real.json NoPonto/ETA_ML_CANDIDATOS_3A.sql tools/EtaMl.Export/outputs/dataset-post-fix
```

O exporter mantém localhost/127.0.0.1/::1, REPEATABLE READ/READ ONLY e AuditadaSemProtecao. Não usar o manifesto do snapshot diretamente como audit do exporter: os formatos têm responsabilidades distintas. Ainda pendem exportação/transferência/restore conectado reais, review de planos/schema/contagens e evidência por viagem; para treino, CSV válido e gate de volume/representatividade. Nenhuma dessas etapas foi automaticamente aprovada pelo reconhecimento do perfil.

## Validação desta entrega

### Correção de dependências de triggers — contrato v2

Evidência fornecida pelo operador: primeiro snapshot real com 16 viagens/11 tabelas/775 telemetrias/188 passagens/220 eventos passou prepare/export/seal/verify e pre-data/importação local (onze count_guard=1), mas post-data falhou por funções ausentes. pg_dump seletivo inclui triggers sem fechar automaticamente suas dependências de funções. A tentativa parcial em eta_snapshot_3b2b_real01 não deve ser reutilizada. O agente não acessou o bundle real nem bancos.

Novos bundles usam noponto-snapshot-3b2b-v2. Exportação inclui functions.csv (pg_get_functiondef, assinatura/schema/corpo/linguagem/propriedades), triggers.csv (definição, função e estado de habilitação) e dependencies.csv. Descoberta consulta TODOS os triggers não internos das onze tabelas. Um percurso recursivo de pg_depend inclui objetos pertencentes às tabelas (índices, defaults, constraints, tipos de linha, triggers e sequências), as funções desses triggers e referências externas. Dependências pg_catalog, schema public, linguagem plpgsql e membros da extensão postgis instalada são suportadas; referências externas adicionais falham explicitamente. O mesmo inventário deve corresponder no restore. Isso não é um dump global de funções/tabelas.

SQL PL/pgSQL armazenado como texto não registra todas as referências do corpo em pg_depend. Por isso a política é conservadora: snapshot_functions.reviewed.json contém somente os dois corpos revisados contra a migration EstruturaFinalEtapas1e2, com regressão que exige correspondência. Permite somente public.BloquearMutacaoOcorrenciaPublicada() e public.BloquearMutacaoVersaoPublicada(), RETURNS trigger, LANGUAGE plpgsql, SECURITY INVOKER, propriedades padrão e sem configuração adicional. Seus corpos referenciam PadroesOperacionais, já transportada, e usam os tipos/operadores existentes no schema/PostGIS. Outra função, corpo alterado, função externa em default/índice/constraint, tipo externo ou trigger novo requer revisão explícita e bloqueia seal; não é copiado nem ignorado automaticamente. Não presume que produção só tenha essas duas dependências: descobre todas e recusa as não suportadas.

Seal gera functions.sql exclusivamente das definições ORIGINAIS exportadas, após validar identidade, wrapper e corpo. O arquivo revisado é uma política de aceitação, nunca usado para fabricar funções ausentes. Manifesto incorpora evidências e SHA256 dos três CSVs, functions.sql e bundle.version.json, além dos hashes anteriores. Verify confere a geração determinística sem escrever nada. Seal recusa manifesto existente antes de qualquer escrita. Bundles v1 continuam verificáveis pelo contrato original, sem modificar seus arquivos, mas restore v1 é recusado ANTES de conectar: preparar/exportar/selar um NOVO bundle v2. Não adicionar arquivos nem re-selar o snapshot antigo.

Restore exige PostgreSQL 16/PostGIS 3.4 e banco novo sem tabelas/funções de aplicação. Ordem: pre-data → dados → functions.sql transacional/UTF8 → post-data → validação de constraints → comparação de colunas/FKs/funções/triggers/dependências com origem. ON_ERROR_STOP, check=True e timeouts interrompem imediatamente qualquer falha, inclusive na fase de funções; post-data só inicia após sucesso dessa fase. Nenhum trigger/FK/constraint é desabilitado ou removido. As regras de imutabilidade preservam corpos, UPDATE/DELETE BEFORE ROW, vínculo às funções e enabled=O. Owners/ACLs continuam deliberadamente fora do transporte para o banco descartável; SECURITY INVOKER permanece. Hashes verificam integridade, não autenticam a origem do DDL executável.

Comandos FUTUROS, exclusivamente pelo operador, com novo diretório e novo banco (sem senhas na linha):

```sh
# PC: preencher request privado com as viagens já auditadas; utilitários atualizados.
python3 tools/eta_ml/snapshot.py prepare tools/eta_ml/outputs/snapshot.request.real.json tools/eta_ml/outputs/snapshot-3b2b-v2
# Servidor: dentro do NOVO bundle; revisar planos antes da exportação read-only.
PGSERVICE=noponto_auditoria psql -X --no-password -v ON_ERROR_STOP=1 -f plans.sql
PGSERVICE=noponto_auditoria sh export.sh
# Na raiz do checkout, após exportação e transferência manual conforme guia:
python3 tools/eta_ml/snapshot.py seal tools/eta_ml/outputs/snapshot-3b2b-v2
python3 tools/eta_ml/snapshot.py verify tools/eta_ml/outputs/snapshot-3b2b-v2
```

```powershell
# PC: SOMENTE PostgreSQL/PostGIS descartável em loopback, novo banco.
createdb --host=127.0.0.1 --port=55439 --no-password eta_snapshot_3b2b_real02
psql -X --host=127.0.0.1 --port=55439 --dbname=eta_snapshot_3b2b_real02 --no-password -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION postgis;'
python tools/eta_ml/snapshot.py restore tools/eta_ml/outputs/snapshot-3b2b-v2 --database eta_snapshot_3b2b_real02 --port 55439
```

Se a versão de PostGIS local disponível não for 3.4, interromper; não relaxar guardas. Se nova falha ocorrer, conservar logs e criar outro banco vazio para repetir, sem limpeza automática. pg_dump e psql ainda são sessões distintas: congelar alterações de DDL durante exportação; as comparações locais detectam divergências cobertas, sem prometer um snapshot atômico de DDL. Execução automática real permanece pendente do operador. Testes do agente são offline, com subprocessos mockados; não provam execução conectada das funções. Durações por fase limitadas a 300s/60s; os comandos conectados acima NÃO foram executados pelo agente.

Guards stdlib cobrem seleção/cutoff/release/contrato, filtro de cada fonte pesada, ausência de DML no SQL da origem, dump schema-only, ausência de host embutido na exportação, restore loopback antes de qualquer conexão, fases/FKs/contagens, geometria EWKB/precisão temporal, fronteiras faltantes/linhas alheias/FKs desconhecidas, determinismo e alteração de hash. Restore é mockado, sem psql/Docker/rede. Guardas anteriores da auditoria/SQL3A também executadas. Resultados finais registrados no HISTORICO.md; execução real do snapshot, custo/plano e round-trip conectado ainda dependem do operador.


### Comparação determinística e validação do banco existente

Nova evidência MANUAL do operador em eta_snapshot_3b2b_real02 (PostgreSQL16/PostGIS3.4 descartável): onze tabelas com count_guard=1, duas funções originais, post-data sem erros,38constraints/nenhuma não validada, dois triggers habilitados e ligados às funções corretas. Catalog(11) e functions(2) idênticos; triggers(2) e dependencies(4) idênticos ao fixar search_path=pg_catalog. Agente não acessou banco nem bundle real. A etapa3B.2B permanece aberta até validar geometrias e conteúdo restaurado.

pg_get_triggerdef depende da visibilidade no search_path: public visível pode omitir a qualificação da função. Isso altera representação sem alterar o vínculo. O checkout já continha SET search_path=pg_catalog para funções/triggers/dependências; a evidência não determina qual cópia/comando foi usado pelo operador. Agora compare_restored centraliza as quatro consultas, com BEGIN READ ONLY, SET LOCAL de contexto/timeout60s/lock2s e COMMIT na MESMA sessão/argumento -c, usado pelo restore e por check-restored. Não depende de contexto herdado de outra sessão. Comparação continua integral, sem remover public., ignorar definições ou comparar somente contagens.

Contextos preservados para compatibilidade com v2 selado: catalog.csv usa public,pg_catalog (public visível, tipos e FKs como no export anterior); functions/triggers/dependencies usam somente pg_catalog (referências públicas qualificadas). Novos prepare explicitam também o contexto do catálogo antes de exportá-lo. A política de corpos/propriedades e as consultas de inventário permanecem iguais. Nenhum formato/hash/manifeste precisa mudar; NÃO reexportar, re-selar nem editar arquivos do bundle existente.

Validar no PC, manualmente, usando o caminho REAL do bundle v2 já selado (o diretório abaixo é exemplo), credenciais/passfile externos e o banco local já restaurado:

```powershell
python tools/eta_ml/snapshot.py verify tools/eta_ml/outputs/snapshot-3b2b-v2
python tools/eta_ml/snapshot.py check-restored tools/eta_ml/outputs/snapshot-3b2b-v2 --database eta_snapshot_3b2b_real02 --port 55439
```

Check-restored verifica integridade/versionamento OFFLINE antes de conectar, aceita somente loopback/banco eta_snapshot_*, executa quatro consultas de METADADOS com timeout60s e compara colunas/FKs/funções/triggers/dependências com as evidências originais. Não executa pg_restore, importação, criação, alteração ou limpeza; não exige banco vazio. Saída0 confirma somente essa comparação; não reconta tabelas nem certifica geometrias/conteúdo. Falha devolve erro e interrompe consultas seguintes. NÃO rodar restore novamente no banco real02: restore continua exigindo banco vazio. Não usar check-restored com túnel ou encaminhamento de produção.

Regressões offline simulam public/"$user",public/pg_catalog/outro schema herdados, a desqualificação de função se o contexto faltar e rejeição de alterações em função vinculada/tabela/nome/habilitação/eventos/BEFORE-AFTER/ROW-STATEMENT/definição. Também conferem apenas psql READ ONLY, host fixo, bundle byte a byte intacto e bloqueio antes de conexão em host inválido/corrupção. Comandos conectados não executados pelo agente. Geometrias e conteúdo permanecem pendentes da validação final do operador.
