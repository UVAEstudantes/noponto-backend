# Base acadêmica de arquitetura e desenvolvimento

**Finalidade:** oferecer texto preliminar verificável, não capítulo final.

## Arquitetura

O NoPonto foi estruturado como aplicação modular em camadas, implantada em um processo ASP.NET Core acompanhado por PostgreSQL/PostGIS e Redis. Controllers HTTP, hub SignalR e serviços hospedados compartilham o mesmo processo; portanto a solução não constitui uma arquitetura de microsserviços. Essa escolha reduz complexidade de implantação e favorece composição interna, embora faça endpoints e processamento assíncrono competirem pelos mesmos recursos.

PostgreSQL atua como autoridade durável para estrutura, schedules, viagens e dados históricos. PostGIS aproxima o cálculo geoespacial dos dados e permite selecionar/projetar posições sobre percursos indexados. Redis mantém estado operacional com TTL, CAS e streams; sua configuração sem RDB/AOF torna o conteúdo reconstruível/efêmero e impede tratá-lo como fonte durável.

## Estrutura e rodoviário

O modelo V2 separa a identidade estável do padrão operacional de suas versões geométricas. Ocorrências ordenadas representam visitas a paradas, inclusive repetidas, permitindo que uma viagem fixe a versão utilizada. No fluxo rodoviário, providers são normalizados, observações passam por validação temporal e matching PostGIS, e uma fração longitudinal sustenta próxima ocorrência, progresso e viagem. Lua implementa aceite atômico dentro do Redis, sem criar transação distribuída com o banco.

## Ferrovia e frontend

Na ferrovia, schedules versionados materializam viagens esperadas. Probes limitados obtêm evidências; binding e tracker em memória associam-nas a runs; estimadores interpolam tempo e posição sobre geometria publicada. Assim, a posição ferroviária é inferida e acompanhada de origem, qualidade e freshness, não GPS contínuo.

O frontend Expo/React Native consulta estrutura e snapshots, usa SignalR no rodoviário e polling ferroviário. O mapa MapLibre executa em WebView e recebe comandos incrementais de estrutura, realtime e usuário. AsyncStorage conserva preferências locais. TanStack Query está instalado, porém ausente dos fluxos vigentes.

## ETA e desenvolvimento

O cliente ETA legado é fail-open, mas seu serviço está desligado. A fundação V2 registra identidade da viagem/ocorrência, baseline, previsão e ground truth; flags produtivas permanecem OFF. Um modelo supervisionado só poderá ser discutido como resultado após dataset reproduzível e avaliação temporal.

O processo foi incremental e evidenciado por migrations, testes, auditorias, commits e deploys, sem base para alegar Scrum formal. Diagramas: [containers](../07-diagramas/02-c4-containers.md), [V2](../07-diagramas/06-der-estrutura-v2.md), [GPS](../07-diagramas/10-matching-estado-causal-e-viagem.md) e [rail](../07-diagramas/13-ferrovia-predicao-estados-e-publicacao.md).
