# Captura de frescor GTFS e fases de matching — 28/10/2026 UTC

Base funcional:89dfdbc. Mudança desta etapa restrita ao capturador, testes e documentação; sem alteração C#, supervisor, workflow, TTL, cadência, CAS ou matching. Sem SSH, deploy ou janela. Os668 testes C# anteriores não foram repetidos; CI Release da PR permanece obrigatória antes de considerar merge.

## Famílias arquivadas

- `gtfs_freshness`: fonteGTFSRT_BUS/BRT, acquisitionDownloaded/LocalCache/NotModified/Failed, sucesso/falha fechados, duração, reuso, contagem bruta, idade física média no body original e na leitura, tempo de retenção do body e quantidade bruta até60s. Mensagens antigas sem os novos agregados e outros logsGTFS são ignorados; mensagem nova incompleta marca lacuna. Campos de idade assinados permitem observar futuro/clockskew sem renovar GPS. `(null)` é preservado. Feedbruto não significa veículos aceitos/publicados ou de um modal já validado.
- `matching_phases`: operaçãoGlobal/Combined/Directed/Stage, motorNaoAplicavel/Legacy/SetBased, outcomeCompleted/Degraded/Cancelled/Failed, entradas/comandos/fallbacks/linhas/falhas dechunk, temposprepare/connection/execute_reader/read/materialize/fallback/other/wall. Duracões negativas, não finitas e enums desconhecidos são rejeitados. ExecuteReader inclui rede/servidor; não chamá-lo de CPU ouSQLpuro. Stage e operações podem se sobrepor: não somar suas durações como etapas independentes.

Schemas são allowlists: nenhum URL, cursor, token, identidade individual, coordenada ou payload bruto é arquivado, inclusive em erro. As duas famílias recebem container e timestamp existentes, mas não inventam relação com geração/ITS. `acquisition`GTFS é uma classificação, não GUIDITS; não cria uma aquisição fantasma nem muda a associação de páginas/resumosITS. `link_basis=unproven` permanece explícito para essas famílias. A correlação de geração continua sendo fornecida separadamente por GPSflow.

## Proteções e compatibilidade

Mesma captura/arquivo exclusivo, checksums, fila/backpressure, deduplicação, reconexão por container, heartbeat e fechamento já existentes. Limites mantidos:1000s/64MiBentrada/8MiBarquivo/12000linhas; sem aumento de orçamento, nova chamadaHTTP ou escritorGPS. Novos registros usam os mesmos limites; esgotamento continua explícito/truncado/incompleto. A prova futura precisa verificar volume real: estes testes não garantem que qualquer volume caiba no orçamento.

Supervisor permanece byte a byte inalterado nesta etapa. Guardas continuam no digest153; não foram adiantadas para uma imagem futura inexistente nem desativadas. Antes de usar o capturador com outra imagem, fixar o digest publicado aprovado e conferir novos hashes em diretório privado novo. Nenhum supervisor foi armado.

## Validação local

- `python -B -m unittest discover -s tools/gps -p test_its_log_capture.py -q`:39 aprovados, seis testes novos com subcasos.
- `python -B tools/gps/test_its_window.py -q`:95 aprovados. Usado o runner direto existente; discovery é incompatível com osunittest.main existentes desse arquivo e não foi alterado nesta tarefa.
- `test_gtfs_real_console_trailer_preserves_physical_age_without_private_fields`: formato real com sufixo textual, preservação das idades10/71s e descarte de campos privados.
- `test_new_families_archive_dedup_and_do_not_create_fake_its_acquisitions`: persistência/verificação, deduplicação, contador de famílias e isolamento da associaçãoITS.
- `test_gtfs_legacy_and_non_acquisition_messages_are_ignored`: compatibilidade com logs antigos153.
- `test_new_schemas_reject_unknown_enums_missing_metrics_nan_and_negative_duration`: schema fechado/missing fields/dados inválidos sem vazamento.
- `test_gtfs_empty_failure_brt_and_signed_ages_remain_diagnostic`: BRT, falha503/vazio/null/futuro como diagnóstico.
- `test_new_families_obey_existing_output_budget`: truncamento explícito no limite reduzido permitido do teste, sem aumentar defaults.

Não repetida investigação ou benchmarkPostGIS. A próxima imagem poderá distinguir idade recebida vs idade por retençãoGTFS e custo por fase de matching, junto de GPSflow/CAS/REST/SignalR existentes. Ainda exige dados reais para concluir frescor produtivo; esta PR não autoriza merge/deploy/janela.

## Hashes canônicos UTF-8/LF do conjunto operacional

- `its_window.py`: `304bca05641acb139dbcd4ec2e95f6e2aa82975a17a2bd1b776d62570ce5fd76`
- `its_log_capture.py`: `0f71435193c0e0e2fb23c08c1fdfbceecf63ff4074eb16601ffbc8c9fcd40eda`
- `probe_http.py`: `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248`
- `its.primary.override.yml`: `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1`
- `gtfs.rollback.override.yml`: `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900`
