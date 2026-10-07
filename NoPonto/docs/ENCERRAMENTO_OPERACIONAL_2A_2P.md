# Etapa 2A.2P — prontidão operacional para o TCC

Revisão de 07/10/2026. **Recomendação: avançar para homologação controlada e desenvolvimento do histórico/ETA, sem abrir nova frente arquitetural.** Não encontrado nesta revisão defeito funcional novo que justifique correção antes da homologação. Capacidade em servidor compartilhado de aproximadamente 4 GiB ainda NÃO está demonstrada. Implantação exige medição mínima e atualização coordenada de schema, escritores e consumidores.

## Evidências e resultados

Resultados informados pelo usuário, não reexecutados nesta etapa: 444 testes locais O.1 aprovados; dois Redis/Lua reais aprovados; cinco reais específicos de recuperação aprovados; regressão real consolidada com 124 aprovados e zero falhas. Há sobreposição: não somar esses números como casos únicos. As aprovações reais fecham a pendência funcional registrada em O.1, não comprovam capacidade, latência ou consumo de memória. Sem logs individuais fornecidos, os resultados são registrados como informação do usuário, não validação independente de quais filtros compuseram os 124.

Consultados AGENTS, histórico, auditorias N/O/O.1, diffs e arquivos novos, polling/enriquecimento/retry, repositórios operacionais, integridade circular, migration e worker/materializador. Relatórios anteriores permanecem como evidência histórica; seus bloqueios de retry devem ser interpretados à luz da implementação e da política de melhor esforço posteriormente aprovadas. Nenhum código funcional ou limite foi alterado nesta etapa. Não foram repetidos testes: documentação não exige recompilar código que permaneceu intacto.

## Fluxo atual e referências

| Arquivo/método | Evidência principal |
|---|---|
| `GpsPollingService.ProcessarCicloAsync`, linha 173 | Retry elegível antes dos filtros GPS e antes do stopwatch de performance do ciclo |
| `GpsPollingService.ConfirmarPosicaoAsync`, linha 1093 | Claim por veículo, CAS GPS para mapa, consulta backlog, processamento ou registro de pendência; ML/shadow recebem apenas resultado compatível |
| `GpsEnriquecimentoService.RecalcularAsync`, linha 20 | Retry reenriquece predecessor e observação em instância isolada do serviço produtivo |
| `RetryOperacionalGpsService.RecuperarVeiculoAsync` | Prazo, autoridade durável, contexto, identidade física, prova recalculada, confirmação ou descarte/backoff |
| `PendenciaOperacionalGpsRepository` | Hash global, ZSET prazo/veículo, lease/token, scripts Lua e TTL |
| `ViagemOperacionalRepository.LerContextoAsync`, linha 37 | Circular consulta autoridade PG mesmo com projeção Redis; linear válido pode retornar do cache |
| `ViagemOperacionalRepository.TentarAtualizarInternoAsync`, linhas 230–325 | Proteção com flag false; política de persistência; lock consultivo e FOR UPDATE; estado/integridade/outbox atômicos; projeção Redis pós-commit |
| `ViagemOperacionalRepository.Integridade.BuscarProvaCircularAsync`, linha 8 | Consulta geométrica por versão, sem varrer histórico |
| `ViagemOutboxWorker.ClaimAsync/ProcessarLoteAsync` | Claim SKIP LOCKED com lease; journal/histórico e confirmação na mesma transação |
| `HistoricoEventoRepository.PersistirLoteAsync/EventoViagemValidator` | Somente PassagemParada materializa passagem; identidade validada, fim inferido preservado |

GPS aceito para mapa não significa operação concluída. Falha operacional cria pendência limitada, sem reverter mapa. Polling consome pendências mesmo sem replay da fonte. Prova efêmera não é persistida como verdade; é recalculada com predecessor físico. `Updated` quente não é ACK SQL; somente indicador pós-commit ou leitura durável reconhece conclusão. Contexto ultrapassado é descarte conservador, não sucesso inventado. Retry não republica ML/ETA e não fabrica passagem para preencher lacuna. Perdas aceitas podem reduzir coverage de labels; integridade da identidade continua mais importante que completar artificialmente o dataset.

## Custo potencial em 4 GiB

| Caminho | Custo adicional/limite | Avaliação |
|---|---|---|
| GPS normal | Até duas leituras Redis por posição aceita: claim vazio e ZCARD backlog; consulta limitada de prazos por ciclo | Sem escrita de fila por GPS normal e sem novo commit PG só para ACK |
| Cadastro raro | Hash + dois índices, EXPIRE; payload físico atual/predecessor; lease separado | Limite 3/veículo, 2.000 global; retenção lógica 180s/física420s |
| Retry | Até 4 claims com item por ciclo/instância, até 4 tentativas/item; backoff20s exponencial; timeout cooperativo3s; lease60s | Até cerca de12s de trabalho cooperativo por ciclo, além do Redis e dependências que ignorem cancelamento; não é limite global entre instâncias |
| Reenriquecimento | Duas observações no serviço espacial produtivo, leituras de contexto/autoridade, possível checkpoint SQL | Não varre histórico; custo depende da geometria e consultas direcionadas/global |
| Circular | Leitura PG mesmo com cache; com âncora nova a extensão muda e pode exigir commit por observação | Principal ponto de atenção: WAL, tuplas mortas, locks e pool; não reduzir persistência sem política nova aprovada |
| Outbox | Claim e materialização por lote, lease2min, máximo100 itens/lote | Backlog/erros e limpeza existentes precisam observação; não apagar pendentes para liberar espaço |

Memória do retry é limitada em itens, não bytes. Estimativa paramétrica: se payload medido tiver 2–8 KiB, 2.000 itens significam aproximadamente4–16 MiB SOMENTE de JSON, antes de índices, strings e overhead Redis. Isso é cenário aritmético, não medição nem teto total. Payloads variáveis impedem declarar máximo absoluto. Usar MEMORY USAGE das chaves e INFO memory para custo real; outros caches/backend/PG competem pelos mesmos4GiB.

Padrões atuais (`GpsPollingOptions`): intervalo20s, paralelismo de enriquecimento20 e de viagem20, checkpoint linear60s. Veículos normais continuam processados com concorrência limitada. O orçamento de retry é compartilhado dentro da instância; claims vazios devolvem orçamento, exceções consomem conservadoramente. Lock por veículo não é lock global, mas pool/CPU são compartilhados. Transação usa advisory hash por veículo, FOR UPDATE, CAS durável e comparação da extensão. Eventuais colisões do hash serializam veículos distintos, sem autorizar commit incorreto.

Âncoras circulares atualizadas geram UPDATE do JSONB/versão e WAL mesmo sem passagem. N veículos circulares observados a cada T segundos podem resultar em ordem de N/T commits por segundo, além de contexto/matching/ocorrências/outbox. Fórmula de dimensionamento, não throughput medido. Prova geométrica executa operação sobre uma versão e seu custo depende dos vértices. Quatro GiB não bastam, isoladamente, para afirmar que esses padrões são excessivos ou seguros. **Manter limites; medir antes de ajustar.**

Oportunidades desejáveis: contabilizar retry inicial no tempo global; resumir contagem/latência de retry; medir antes de reduzir paralelismo em configuração de homologação. Não retirar autoridade circular, diluir commits de proteção ou enfraquecer idempotência para reduzir custo. Há lacuna de observabilidade concreta: stopwatch e contadores normais do GPS começam depois do retry inicial; `total_ms` e contagem PG do ciclo não representam todo o trabalho de recuperação.

## Medição local mínima — somente execução manual futura

Usar ambiente descartável conhecido, com PostGIS16/3.4.3 e Redis7 já disponíveis e o mesmo binário/schema revisados. Não usar fixture de teste como benchmark: startup/migrations/limpeza alteram custos. Usar geometria e observações sintéticas com timestamps atuais, cadência/quantidade documentadas, sem dados reais. Não alterar produção nem instalar observabilidade. Não fornecido comando que inicie container ou aplique migration.

Rodadas: aquecer2min; coletar10min com mesma sequência, primeiro flag false, depois true somente em homologação; grupos linear, circular, candidato/cancelamento/proteção; repetir com uma falha transitória de teste já existente. Começar10 veículos, depois50 e finalmente o volume alvo declarado para o TCC. Flag e falha são configuradas manualmente só no harness/ambiente isolado, nunca na produção. Se não houver fonte/harness com replay contínuo, registrar essa preparação como pendência; as sequências pequenas dos testes não substituem carga sustentada.

Métricas existentes: log `Performance GPS` com total/start-to-start, etapas matching/viagem/Redis, contadores PG e tamanho batch; logs de pendência criada/tentativa/encerramento e erro limitado por minuto; `Outbox batch`/cleanup; `docker stats`; `redis-cli INFO`, `MEMORY USAGE`; vistas estatísticas PG. Não presumir endpoint Prometheus nem pg_stat_statements instalado.

Após conferir nomes/IDs dos containers EXCLUSIVAMENTE de teste (sem imprimir variáveis/credenciais):

```powershell
$backendTeste = 'SUBSTITUIR_BACKEND_DESCARTAVEL'
$postgresTeste = 'SUBSTITUIR_POSTGRES_DESCARTAVEL'
$redisTeste = 'SUBSTITUIR_REDIS_DESCARTAVEL'
$usuarioTeste = 'SUBSTITUIR_USUARIO_TESTE'
$bancoTeste = 'SUBSTITUIR_BANCO_TESTE'
docker inspect --format '{{.Name}} {{.Config.Image}}' $backendTeste $postgresTeste $redisTeste
# Amostrar a cada5s por10min; não iniciar nada:
1..120 | ForEach-Object {
    Get-Date -Format o
    docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.BlockIO}}' $backendTeste $postgresTeste $redisTeste
    Start-Sleep -Seconds 5
} | Set-Content recursos-teste.txt
docker logs --timestamps --since 12m $backendTeste 2>&1 | Set-Content gps-teste.log
```

MemUsage e CPU containers não são RSS individual por processo; em Windows Docker Desktop também medir VM/host. Não extrapolar diretamente para Linux4GiB. Se backend roda fora de container, usar processo conhecido com Get-Process (WorkingSet64, PrivateMemorySize64 e diferença de CPU acumulada por intervalo), mantendo PG/Redis medidos por container.

```powershell
$valores = @(Get-Content gps-teste.log | ForEach-Object {
    if ($_ -match 'Performance GPS:.*?\btotal_ms=(\d+)') { [double]$Matches[1] }
} | Sort-Object)
if ($valores.Count -eq 0) { throw 'Sem amostras Performance GPS; verificar logging já configurado.' }
[pscustomobject]@{
    Amostras=$valores.Count
    P50ms=$valores[[int][math]::Ceiling($valores.Count*0.50)-1]
    P95ms=$valores[[int][math]::Ceiling($valores.Count*0.95)-1]
}
# Calcular também percentis de start_to_start_ms com o mesmo procedimento.
# Esse valor inclui atraso planejado; não equivale à duração pura do trabalho.
docker exec $redisTeste redis-cli INFO commandstats
docker exec $redisTeste redis-cli INFO stats
docker exec $redisTeste redis-cli INFO memory
docker exec $redisTeste redis-cli HLEN noponto:gps:retry:dados
docker exec $redisTeste redis-cli ZCARD noponto:gps:retry:prazos
docker exec $redisTeste redis-cli MEMORY USAGE noponto:gps:retry:dados
docker exec $redisTeste redis-cli MEMORY USAGE noponto:gps:retry:prazos
docker exec $redisTeste redis-cli --scan --pattern 'noponto:gps:retry:veiculo:*'
```

Executar INFO antes/depois e usar deltas; comandos do observador também entram nas contagens. `commandstats` inclui comandos executados pelos scripts: não somar cegamente EVAL e subcomandos como viagens de rede. Pendências: HLEN; estrutura física: MEMORY USAGE também de cada chave veiculo/lease descoberta, sem ler payloads. Valores ausentes podem significar nenhuma pendência ou expiração. Correlacionar ID nos logs de criação/encerramento para latência de recuperação e motivo final; perda Redis/evicção não oferece conclusão individual. Não usar KEYS, MONITOR, FLUSH ou reset de estatísticas.

SQL de leitura para snapshots antes/depois, executado APENAS no banco descartável identificado. Search_path deve apontar ao schema do backend/harness; fixture usa schema exclusivo, não assumir public. Substituir explicitamente após conferir identidade:

```powershell
$consulta = @'
BEGIN READ ONLY;
SELECT current_database(), current_schema(), inet_server_addr(), inet_server_port();
SELECT pg_current_wal_lsn(), wal_records, wal_fpi, wal_bytes FROM pg_stat_wal;
SELECT datname, xact_commit, xact_rollback, tup_inserted, tup_updated,
       blks_read, blks_hit, deadlocks FROM pg_stat_database WHERE datname=current_database();
SELECT relname,n_tup_ins,n_tup_upd,n_dead_tup,pg_total_relation_size(relid) AS bytes
FROM pg_stat_user_tables WHERE relname IN ('ViagensOperacionais','OutboxViagens','HistoricoPassagens');
SELECT count(*) AS pendentes, min("CriadoEmUtc") AS mais_antigo,
       max("Tentativas") AS max_tentativas
FROM "OutboxViagens" WHERE "ProcessadoEmUtc" IS NULL;
SELECT wait_event_type,wait_event,count(*) FROM pg_stat_activity
WHERE datname=current_database() GROUP BY wait_event_type,wait_event;
COMMIT;
'@
$consulta | docker exec -i $postgresTeste psql -X -v ON_ERROR_STOP=1 -U $usuarioTeste -d $bancoTeste
```

WAL é global ao cluster, não atribuível só ao backend; teste isolado permite aproximação. Usar diferença de wal_bytes/tempo, tuplas atualizadas e crescimento de relações; não confundir WAL gerado com espaço retido em pg_wal. Vistas têm atualização assíncrona; esperar amostras estabilizarem, sem resets. Não ler texto de queries/payloads ou imprimir credenciais. Se autenticação exige senha, usar mecanismo local previamente aprovado, sem incluí-la em comandos ou relatório.

Critérios propostos para gate de capacidade do TCC (ainda não medidos): quantidade/cadência alvo definida; p95 de trabalho completo menor que intervalo configurado e sem crescimento sustentado start-to-start; CPU sem saturação contínua; pelo menos20% de margem de RAM da máquina sem swap/OOM; backlog outbox não crescente e drenado em até2min depois da rodada; falha transitória recuperada dentro de180s ou descarte explicitamente explicado; zero conflito de identidade/duplicata inesperado. Medir também disco disponível e crescimento diário extrapolado do WAL/histórico para o volume alvo. Se falhar gate, primeiro ajustar escopo/cadência/paralelismo de homologação medidos, sem alterar integridade. Os testes124/444 não fornecem esses resultados.

## Implantação e recuperação coordenadas — checklist, não executado

1. Identificar todos os escritores e consumidores, schema atual, binário e configuração; preservar flag de mudança desligada. Ter backup/snapshot verificado e artefato compatível de retorno; não copiar credenciais para logs.
2. Janela coordenada: parar ingresso/escritores antigos; drenar consumidores antigos do outbox enquanto só existem payloads antigos, depois interrompê-los. Preservar journal, outbox, histórico, projeções e fila Redis; não limpar volumes/chaves.
3. Aplicar manualmente a migration revisada `20261006180000_IntegridadeCircularDuravel` antes do novo binário, conforme processo autorizado. Adiciona JSONB NULL e CHECK objeto/NULL, sem backfill. ALTER toma lock: janela é necessária. Confirmar schema e compatibilidade; nenhum comando de migration executado nesta etapa.
4. Atualizar todos os escritores e consumidores para versão que compreenda extensão e `motivo_fim=PerdaContinuidadeCircular`. Não coexistir escritor antigo com novo. Consumers antigos podem perder motivo e gerar divergência em reentrega.
5. Iniciar consumidores compatíveis e novo backend com flag false. Circularidade já protegida continua protegida mesmo assim; false não dispensa coluna e não permite binário antigo. Validar leitura dos snapshots antigos NULL, outbox, mapa, identidade ML e saúde antes de discutir ativação em etapa separada.
6. Medir gate de capacidade; só avançar volume após cumprir critérios. Não reivindicar labels garantidos: joins ETA precisam viagem/versão/ocorrência/volta, passagem posterior e ausência de intervalo ambíguo, conforme auditoria N.

Falha de backend/Redis: interromper escritor se integridade estiver em dúvida; PG contém estado/integridade/outbox autoritativos. Restart com Redis preservado pode retomar pendências ainda elegíveis; TTL pode expirar durante janela. Com Redis perdido, hidratar operação pela autoridade e coletar nova evidência física; pendências perdidas não são reconstituídas. Não fabricar passagem nem reparar histórico automaticamente. Falha PG: mapa pode continuar; retry limitado expira e diagnóstico disponível não implica conclusão.

Falha pós-commit/resposta perdida: preservar PG/outbox e reconciliar pela leitura durável; não replay manual indiscriminado. Consumer falhou: lease2min libera trabalho e materialização idempotente permite reentrega; observar UltimoErro/backlog, sem marcar processado à força.

Rollback real: parar escritores/consumers, voltar SOMENTE a artefato compatível com extensão e novo motivo, preservando schema/dados. `Down` da migration lança exceção deliberadamente. Binário pré-integridade NÃO é rollback seguro após começar a escrever extensão. Desligar flag não elimina proteção nem desfaz eventos. Se nenhum artefato compatível existir, manter escritores parados e corrigir para frente; backup restaurado é recuperação de desastre com perda/reconciliação explícita, não rollback rotineiro. Não apagar marker para fazer binário antigo funcionar.

## Classificação final

**A — gates obrigatórios:** schema antes do binário, parada/atualização coordenada dos escritores/consumers e retorno compatível; medição mínima no volume alvo antes de declarar capacidade4GiB. Não há bloqueador funcional novo demonstrado. Limitações aprovadas de melhor esforço não impedem homologação.

**B — desejáveis:** contabilização completa do tempo/queries de retry no log do ciclo; resumo leve de backlog/latência; aferir custos de geometrias e paralelismo; ensaio de carga reproduzível e curto. Não implementados nesta etapa porque não foi encontrada correção funcional indispensável e se pode medir com ferramentas existentes, explicitando lacunas.

**C — pós-TCC:** ingress SQL durável, replay forte após perda total, auditoria completa de perdas, coordenação forte de falhas simultâneas, otimizações espaciais/armazenamento avançadas e qualificação/reparação de datasets externos. Não novos bloqueadores.

Alterações efetivas desta etapa limitadas a este relatório e entrada acrescentada em HISTORICO. Sem testes/serviços externos, Docker, produção/SSH, migrations, infraestrutura, flags, código funcional/ETA/ML, histórico real, dependências, commit ou deploy alterados. **Encerrar estabilização funcional, avançar homologação e liberar desenvolvimento ETA; implantação segue condicionada aos gates operacionais acima.**
