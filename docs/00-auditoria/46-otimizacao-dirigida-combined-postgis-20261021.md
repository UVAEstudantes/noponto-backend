# CombinedSetBased: comprimento somente das versões vencedoras

Branch `perf/combined-setbased-directed`, criada de `origin/main` após fetch, base `b4ff287daad464c65bed1ce351b7bd3009408119`. Árvore inicial limpa; patches 38–45 preservados. **Patch funcional pequeno, ganho local medido e equivalência verificada. Build independente da solução ainda bloqueado pela ferramenta.** Produção #140 e seus números vieram do usuário; produção não foi acessada.

## Causa reproduzida

Na carga alta, a SQL original levou **9.143ms no servidor**. O nó final Sort teve startup de 9.125ms e total de 9.127ms: quase todo o trabalho dos filhos termina antes da primeira linha. Isso explica a concentração observada em ExecuteReader sem atribuir os segundos ao custo da ordenação ou à conexão. Npgsql ExecuteReader também inclui protocolo/rede; não medimos novamente o homeserver.

O contexto GLOBAL materializado calculava `ST_Length(Geometria::geography)` de **5.560 candidatos por ciclo**, embora só 3.000 versões fossem vencedoras nos respectivos chunks. Comprimento não participa de distância, bearing, score, ranking ou desempate. O plano e um contador determinístico de chamadas PostGIS reproduziram esse trabalho descartado. **Essa é uma parcela evitável, não a explicação de todos os 9,62s produtivos.**

O patch remove o comprimento de `contexto_global`, mantém `global_vencedor` materializado e cria `global_comprimentos`, materializado, com EXISTS sobre as versões vencedoras. Cada versão é calculada uma vez por chunk, inclusive quando muitos inputs a compartilham. O resultado GLOBAL faz join por UUID para obter o mesmo comprimento; o wrap circular usa esse mesmo valor. ANTERIOR, OPERACIONAL e as expressões de próxima parada permanecem iguais.

Um primeiro candidato calculava por input vencedor. Foi descartado: repete trabalho em carga concentrada. Outro experimento com LATERAL em ANTERIOR não demonstrou a melhor oportunidade. Os experimentos estão preservados no laboratório, sem entrar no patch funcional. Não alterados paralelismo, conexão, tamanho de chunk, índices ou configurações.

## Fixture e alcance

Foi criado **um único container novo no Docker Desktop local**, imagem já disponível `postgis/postgis:16-3.4`, PostgreSQL 16.4 / PostGIS 3.4.3, 2 CPUs, 2GiB de memória, dados em tmpfs de 2GiB, porta efêmera somente em 127.0.0.1. `work_mem=4MB`, JIT on, sem JIT efetivamente gerado nos planos. Nenhuma configuração de desempenho foi alterada; search_path apenas seleciona o schema da fixture nas sessões. Não foi criado volume persistente ou rede adicional.

Antes de DDL: conferidos ID, nome, label aleatória, mounts, binding, banco vazio e marcador exclusivo. Nenhuma fixture foi apontada para banco preexistente. O container foi removido ao final, os manifests privados com senha descartável foram apagados e os **19 IDs de containers anteriores continuaram presentes**. Uma conferência intermediária comparou IDs curtos com completos; foi corrigida a normalização, sem nova remoção. O bootstrap permanente passou pelo parser PowerShell; a criação desta rodada foi feita em passos equivalentes, não por uma segunda execução dele.

Catálogo local GTFS SHA256 `a99f925460e7628b6eeecb9952430542c06b3e2800afa8ba7f9765fd2e6f26f1`: 464 linhas BUS (tipos 700/200), mais uma sintética; 885 padrões, 1.770 versões, 667.662 vértices, mediana 364 e máximo 1.046 vértices, 74 versões circulares, 106.988 ocorrências. Um trip representativo por rota/direção/shape, frações reconstruídas por LineLocatePoint e versões anteriores clonadas. Incluídos empate exato por UUID, wrap circular e parada repetida por ocorrência. Um dump local antigo foi inspecionado, mas não importado.

**Não é replay operacional:** posições reconstruídas sobre geometrias estáticas, mistura controlada de ramos, índices relevantes reconstruídos das definições locais; sem catálogo/estatísticas/hardware atuais de produção. A preparação inicial foi corrigida para os tipos GTFS estendidos e tabelas pertencentes às extensões da imagem, antes das medições finais.

## Sete pares alternados aquecidos

Chunks sequenciais de 100, uma conexão, sem mudanças de ambiente entre variantes. Aquecimento de ambas, depois AB/BA alternado, sete pares por cenário. p95 por nearest rank (máximo com n=7). Todas as 42 rodadas medidas compararam **todos os campos e a ordem de cada linha**, exatamente, sem ordenar resultados nem tolerância numérica. Linhas de ramos não são veículos distintos.

| Carga | Inputs / comandos | GLOBAL / ANTERIOR / OPERACIONAL | p50 baseline → candidato | p95 baseline → candidato |
| --- | --- | --- | --- | --- |
| Baixa | 2.800 / 28 | 2.660 / 40 / 40 = 2.740 | 6,199s → 5,046s (−18,6%) | 6,768s → 5,277s |
| Alta | 3.000 / 30 | 3.000 / 2.400 / 500 = 5.900 | 9,832s → 8,942s (−9,1%) | 10,261s → 9,115s |
| Concentrada, linha 838 | 3.000 / 30 | 3.000 / 2.400 / 500 = 5.900 | 3,929s → 3,582s | 4,329s → 3,877s |

Candidato mais rápido em 7/7 pares baixa, 7/7 alta, 6/7 concentrada. A concentrada preserva o compartilhamento: 60 comprimentos de contexto passam a 30 de vencedores por ciclo. Seu ganho agregado varia e não deve ser atribuído integralmente à redução de apenas 30 cálculos; uma passagem isolada de wall-clock foi mais lenta. Não há garantia estatística de p95 com sete amostras, nem ganho produtivo comprovado.

Wall-clock do harness inclui serialização, envio e fetchall pelo psycopg2; **não é a fase ExecuteReader do Npgsql**. A comparação usa a SQL produtiva extraída do const, com JSON parametrizado idêntico. Parser/DTOs Npgsql são verificados separadamente pelos testes PostGIS.

## Planos e custo dos ramos

`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` parametrizado para todos os chunks de cada variante/carga, mais probes dos três ramos isolados no primeiro chunk. Uma passagem de planos por variante, separada dos sete pares de wall-clock; não tratar essa passagem como mediana.

| Plano completo | Planning baseline → candidato | Execution baseline → candidato | Comprimentos GLOBAL baseline → candidato |
| --- | --- | --- | --- |
| Baixa | 107,460 → 106,811ms | 5.533,384 → 4.474,425ms | 5.188 → 2.660 |
| Alta | 105,877 → 104,156ms | 9.142,536 → 7.879,060ms | 5.560 → 3.000 |
| Concentrada | 131,957 → 121,266ms | 3.336,710 → 3.072,221ms | 60 → 30 |

Carga alta, tempos inclusivos por ramo no plano completo:

| Ramo | Baseline | Candidato | Observação |
| --- | --- | --- | --- |
| GLOBAL | 5.974,670ms | 4.530,393ms | Inclui contexto/ranking e próxima parada. |
| ANTERIOR | 3.070,009ms | 3.244,608ms | SQL intacta; variação desta passagem não é otimização. |
| OPERACIONAL | 54,976ms | 61,243ms | GLOBAL já produzido pelo consumidor anterior. |

CTEs têm tempos inclusivos e execução sob demanda: **não somar CTEs com seus consumidores**. Na alta, `contexto_global` caiu de 2.603,413 para 466,139ms; o novo `global_comprimentos` custou 931,560ms. A redução conjunta de aproximadamente 1,206s acompanha a redução de 1,263s no servidor. Próxima parada permaneceu em aproximadamente 2,9s, com 5.400 loops; não foi otimizada.

Buffers alta: 653.601 → 649.166 hits, **zero reads e zero temp read/write** nas três cargas. Permanecem os scans das pequenas tabelas de catálogo, 7.960 loops de index scan em versões, 5.400 bitmap scans em ocorrências e aproximadamente 170 mil buscas por PK em paradas. Não apareceu I/O físico ou spill no perfil aquecido; EXPLAIN não mede aquisição de conexão. Predicados espaciais e suas barreiras permanecem no GLOBAL, ANTERIOR e OPERACIONAL; não foi proposta alteração de índice.

O novo semijoin/join acrescenta scans de CTE e comparações de UUID; na alta, o planner escolheu nested loop com 297 mil rejeições de join. É custo adicional medido e inferior à economia espacial nesta carga de chunks de 100. Não extrapolar desempenho a chunks maiores. Loops/linhas removidas são médias arredondadas por nó no PostgreSQL; a contagem exata de resultados vem da consulta normal.

Probes isolados da alta (100 inputs; Execution baseline → candidato): GLOBAL 218,772 → 176,825ms; ANTERIOR 143,290 → 155,842ms; OPERACIONAL 215,133 → 146,860ms. OPERACIONAL isolado também precisa construir GLOBAL; não somar esses tempos ao plano completo. JSONs preservam Planning, Execution, loops, buffers, scans, joins e filtros espaciais.

## RED/GREEN e integridade

- Novo `Combined_CalculaComprimentoSomenteAposRanking_SemMudarDtoOuOrdem`, dois casos: com/sem vencedor. **RED 0/2**, esperado zero chamadas em geometrias rejeitadas, observado duas. **GREEN 2/2** dentro dos 123 testes PostGIS. Contador test-only em schema aleatório delega à função PostGIS real; não há assert de duração. Também exige uma chamada por versão vencedora compartilhada, DTO equivalente, ordem original dos inputs, ausência de fallback e status inelegível preservado.
- **123/123 PostGIS**, incluindo diferenciais, perfis normal/invertido, empate/score, circular/paradas repetidas, versões pinadas, operacional, fallback/falha SQL e cancelamento. As tolerâncias existentes não foram ampliadas; a comparação SQL grande usa igualdade exata.
- **411/411 direcionados** e **883/883 offline oficiais**, zero falhas/ignorados; `.ci/offline_tests.py verify` aprovou todas as classes. Seleções têm sobreposição, não somar como testes únicos.
- Guard rejeitou label divergente antes de conectar; seed recusou tabelas já existentes antes de DDL, sem alterar sua contagem; mutação ASC no ORDER BY ficou RED no primeiro chunk do comparador. Parser PowerShell e AST Python válidos.
- Projeto compilou em Release pelos testes, com os cinco avisos preexistentes. `dotnet build NoPonto.sln --no-restore --no-incremental -c Release` **não iniciou**: rejeição automática `approval required by policy, but AskForApproval is set to Never`. Não houve contorno; build completo não está aprovado.

Único fonte funcional alterado: `GpsItinerarioRepository.CombinedSetBased.cs`. Novos teste e ferramentas somente de laboratório, evidências e acréscimo em HISTORICO. Manifesto inicial compara todos os arquivos rastreados: demais fontes, parser/LerRota, SQLs dos outros motores, orquestração, CAS, TTL, deduplicação, timestamps físicos, estado recente, PhysicalETA, viagens, SignalR, ACK/backpressure, opções e limites permanecem intactos. Cada chunk continua uma consulta/snapshot transacional; sem novas tentativas ou catch-up histórico. Histórico anterior e relatórios 38–45, inclusive planos 41/42, preservados.

## Reproduzir e revisar

Evidências: [validação e amostras](46-combined-postgis-validation-20261021.json), [planos JSON completos comprimidos](46-combined-postgis-plans-20261021.json.gz). TRX, fontes dos candidatos e planos legíveis: `NoPonto/lab-output/combined-directed-20261021/`. Nenhuma credencial ou identidade de veículo foi copiada.

Exemplo futuro, a partir da raiz, com Python/psycopg2, Docker Desktop local e imagem já presentes; o script recusa saída existente e nunca recebe conexão de banco operacional:

```powershell
$baseline = Join-Path $env:TEMP 'combined46-baseline.cs'
$source = git show b4ff287daad464c65bed1ce351b7bd3009408119:NoPonto/4-Data/Repositories/GpsItinerarioRepository.CombinedSetBased.cs
[IO.File]::WriteAllText($baseline, ($source -join "`n"), [Text.UTF8Encoding]::new($false))
# Compile o projeto Release/testes antes do benchmark; o runner não inicia a aplicação.
./tools/performance/Invoke-CombinedPostgisLab.ps1 -GtfsFile NoPonto/lab-output/gtfs.zip -OutputDirectory NoPonto/lab-output/combined46-new-run -BaselineSource $baseline -Compare -RunDifferential
```

O bootstrap cria um novo container, verifica propriedade/vazio, executa seed/EXPLAIN/pares e, em finally, remove apenas o próprio ID. Não executado novamente nesta entrega para respeitar um único container. Não usar runners antigos com conexão arbitrária ou o teste representativo dependente de Redis operacional.

**Pronto para revisão técnica do patch; build completo pendente antes de futura implantação manual.** Permanecem riscos de distribuição/catálogo/planner/hardware diferentes e custo espacial remanescente. A homologação futura deve comparar os mesmos contadores de inputs/comandos/linhas/fallback, p50/p95 de ExecuteReader/matching/ciclo e idade GPS física, sem aumentar timeouts ou atribuir melhora local à produção. Nenhum acesso/alteração produtiva, banco real, infraestrutura, servidor, configuração, commit, push, PR, merge ou deploy. Os planos 41/42 continuam sendo planos, sem execução.
