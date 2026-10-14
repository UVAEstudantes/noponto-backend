# ETA físico para a parada consultada — 14/10/2026

Implementado no endpoint existente `GET /paradas/{id}/eventos`, mantendo PhysicalEta OFF por padrão. Não depende de PassagemParada, cursor ou encerramento da viagem.

## Caminho funcional

Reutiliza a captura causal do MVP, o DTO no cache, a seleção de veículos por linha/versão e o calculador puro longitudinal. A razão entre a distância e o ETA **marcado como físico na captura** recupera exatamente a velocidade então validada, sem usar um ETA legado/ML ou recomputar a média no instante da consulta. Regressão, falta de predecessor ou salto já impedem esse marcador no enriquecimento; zero velocidade, SemSinal, GPS antigo/futuro e ausência de identidade continuam recusados.

`RoadEtaDistances` resolve em lote a ocorrência consultada e a próxima ocorrência do matching, ambas na mesma versão. Confere parada, ordem, linha/código, sentido, padrão, topologia, alvo adiante e sequência não regressiva entre próxima e destino. Também verifica que a próxima ainda é a primeira ocorrência adiante e que sua distância coincide com a capturada (tolerância numérica de 1 cm). Divergência não autoriza nova inferência.

Distância usada, igual à query atual de matching:

`ST_Length(ST_LineSubstring(Geometria,0,fraçãoDestino)::geography) - ST_Length(ST_LineSubstring(Geometria,0,fraçãoGPS)::geography)`.

Não usa linha reta ou fração × comprimento para a previsão física. Distância não resolvida fica nula, junto com o ETA. A aproximação antiga fica restrita ao caminho legado OFF. A distância vem do GPS capturado; EstimatedAt = TimestampGps + distância / velocidade, e SecondsUntilEvent desconta a idade da observação.

Máximo de 1.000 candidatos por consulta, em lotes de 200 (até cinco roundtrips). Demais candidatos ficam sem ETA. Cada lote: transação READ ONLY, statement_timeout 500ms, lock_timeout 100ms e cancelamento total de 1s, inclusive abertura de conexão. Falha/timeout retorna previsão indisponível; cancelamento do cliente é propagado. Sem consultas por veículo, sem novas tabelas, migrations, persistência ou chamadas ao ML.

## Testes e exemplo sintético

`PhysicalEtaSelectedStopTests`: 15 casos novos. Primeira, segunda e terceira ocorrências futuras; alvo anterior; versão/sentido/linha/padrão incompatíveis; GPS antigo; velocidade zero; regressão; ausência de predecessor; ETA legado; 401 veículos em lotes 200/200/1; falha do resolver; OFF sem consulta; tempo decorrido; HTTP local real no controller existente para terceira parada e dois veículos.

Fixtures: velocidade causal 36 km/h, distâncias estruturais fornecidas pelo resolver fake de 1.000/2.400/4.100m → ETA 100/240/410s. Na terceira parada, HTTP após cinco segundos retorna duas previsões de **405s**, com o ID da ocorrência consultada. Esses valores não são aproximações por fração × comprimento nem evidência de precisão real.

**265 testes C# aprovados, zero falhos/ignorados** após a última alteração de código; cinco warnings preexistentes. TRX `D:/repositorio_github/NoPonto/eta-selected-stop-validation/selected-stop-release.trx`. Filtro explícito das classes de ETA físico, eventos, enriquecimento, cadência/fontes, parser GTFS-RT, cliente ETA e calculador EtaV2. Não inclui testes que conectem ao Redis/PostgreSQL existentes. HTTP usa apenas Kestrel loopback efêmero, dados sintéticos e resolver fake; não inicializa Program.cs/polling/migrations.

O teste SQL reproduzível está em `tools/eta_ml/test_selected_stop_postgis.py`: exige Docker Desktop local, imagem já disponível, container novo exclusivo sem rede/portas, PostgreSQL inteiramente em tmpfs, label de propriedade e remoção ao terminar. Não usa bancos locais existentes. O teste extrai a SQL literal do serviço e cria somente tabelas/linhas sintéticas nesse container; não executa migrations da aplicação. A validação SQL e seu resultado são registrados no complemento abaixo.

## Frontend, limites e habilitação

Radar já usa `/paradas/{id}/eventos`, prefere `estimatedAt` e usa `secondsUntilEvent` como alternativa (`src/services/radarParada.ts:69–76`). Pode continuar mostrando minutos a partir dos mesmos campos, sem novo endpoint ou alteração obrigatória do aplicativo. Source=PhysicalCausal, Quality=estimativa_fisica; ausência usa previsao_indisponivel/gps_desatualizado.

Previsão continua sendo velocidade constante: não modela permanência nas paradas intermediárias, trânsito ou semáforos. Não afirmar precisão real. Para parada repetida, seleciona apenas a primeira ocorrência futura compatível; não inventa volta seguinte ou wrap. Timestamp velho, veículo parado, mudança não confirmada ou geometria inconsistente ficam sem estimativa. Não classifica aguardando_saida sem evidência de partida.

Habilitação futura **somente em ambiente isolado comprovado**: PhysicalEta__Enabled=true e PhysicalEta__MaxAgeSeconds=30. Default permanece false, sem mudança de appsettings/Compose. Manter ML__ETA__Enabled=false, EtaV2__Enabled=false, EtaV2__ShadowEnabled=false e HistoricalEtaShadow OFF; nenhum ML participa desta previsão. O ambiente completo precisa de estrutura e runtime de teste isolados; não iniciar a API com configurações locais operacionais preexistentes.

Arquivos já sujos antes e adaptados agora: EventosParadaService.cs (orquestração e projeção), PhysicalEta.cs (velocidade capturada), Program.cs (DI), HISTORICO.md (somente acréscimo). Novos desta tarefa: RoadEtaDistances.cs, PhysicalEtaSelectedStopTests.cs, test_selected_stop_postgis.py e este documento. GpsPollingService.cs e demais trabalhos GPS/MVP foram preservados. Inventário SHA inicial: `D:/repositorio_github/NoPonto/eta-selected-stop-before.json`.

Complemento de validação SQL: **7 cenários aprovados** em PostGIS real descartável (um lote com três destinos e seis rejeições: alvo passado, sentido/versão divergentes, próxima ocorrência incorreta, ordem divergente e distância de referência incompatível). Geometria sintética não uniforme: distâncias 22147.41631008513 / 87093.03861763242 / 149050.235546101 m. Para o segundo alvo, fração × comprimento daria 85599.62651791252 m, diferença de cerca de 1.493m. Não há dados de usuários/produção nem prova de precisão de ETA.

Isolamento comprovado antes do SQL: endpoint Docker Desktop local npipe, container exclusivo noponto-eta-stop-test-3915fe943ab4, network none, zero portas, PGDATA em tmpfs sem volumes persistentes. Teste final terminou com código 0 e limpeza própria; não utilizou Redis/PostgreSQL locais preexistentes. Houve tentativas anteriores bloqueadas por timeout de CLI, validação de montagem e prontidão do servidor temporário do entrypoint. Corrigidos somente o executor de teste (aceitar tmpfs declarado, transporte SQL por -c e aguardar PID1 postgres definitivo); recursos exclusivos anteriores removidos. Nenhuma dessas tentativas é contada como teste aprovado.

A integração SQL extrai a consulta literal do serviço e usa arrays sintéticos equivalentes aos parâmetros. A demonstração HTTP usa resolver fake; não é uma execução única ponta a ponta de EF/Npgsql/Redis/HTTP. Nenhuma migration da aplicação foi executada.

Conferência final: 41 arquivos preexistentes fora dos quatro adaptados nesta tarefa permaneceram byte a byte iguais por SHA-256; nenhuma alteração inesperada (preservation.json no diretório de validação). git diff --check aprovado, nada staged e nenhum container de teste remanescente. Branch fix/gps preservada.
