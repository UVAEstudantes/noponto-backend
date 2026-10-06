# 06 — Banco e persistência

**Data:** 2026-10-06 · **Ambientes:** modelo EF/migrations e PostgreSQL de produção read-only · **Estado:** verificado

## Tecnologias e divisão de responsabilidade

PostgreSQL 16/PostGIS 3.4 mantém estrutura durável, geometria, schedules, viagens, histórico, outbox, telemetria e previsões. Redis 7 mantém snapshots, índices por linha, estado causal, streams e dados operacionais com TTL. Redis está configurado sem AOF/RDB no Compose e com política `noeviction`: é deliberadamente efêmero, mas saturação pode causar falhas de escrita.

## Entidades principais

| Conceito | Estrutura real e relações |
|---|---|
| Modal/Linha/Sentido | `Modal` → `Linha` → `Sentido`; identidades externas separam IDs de providers |
| Padrão operacional | identidade estável de uma variante por sentido; chave única por sentido |
| Padrão versão | geometria LineString 4326, hash e número; múltiplas versões por padrão, uma atual |
| Parada/estação | `Parada` atende ambos os conceitos; tipo/modal e identidades distinguem uso |
| Ocorrência de parada | associação ordenada parada–versão, com posição no traçado; admite repetição conceitual controlada pela ordem |
| Importação estrutural | fonte, hash, algoritmo, relatório JSONB e vínculos com versões |
| Veículo/posição | leitura operacional e posição; runtime principal é Redis, histórico segue tabelas específicas |
| Viagem operacional | estado durável observado; eventos e outbox dão continuidade/integração |
| Schedule ferroviário | versão → pattern → scheduled run → scheduled stop |
| ETA/ML | `PrevisoesEtaV2`, `TelemetriasVeiculoMl`, shadows de correção |

## Integridade e geoespacial

O `DbContext` define PKs, FKs com comportamentos `Restrict`/`Cascade`, unicidade de identidades externas, versão/hash, runs e stop sequence. Geometrias usam SRID 4326 e índices GIST. JSONB armazena relatórios/metadata, evitando colunas rígidas para dados de proveniência.

## Produção observada

As 21 migrations do repositório, até `20261003211227_RailScheduleFoundation`, constam no histórico. Foram observadas 34 tabelas de domínio, além das tabelas das extensões. Estimativas relevantes: `TelemetriasVeiculoMl` 19.320.492; `EventosViagem` 5.432.526; `HistoricoPassagens` 5.229.456; `PositionCorrectionShadowOrigins` 4.959.175; `OutboxViagens` 4.026.467; `ViagensOperacionais` 4.544. Schedule: 1 versão, 23 patterns, 246 runs, 7.433 stops.

Todas as tabelas estruturais rodoviárias centrais (`Linhas`, `Sentidos`, `Paradas`, padrões e identidades) apareceram com `n_live_tup=0`. Isso é uma divergência crítica: pode representar truncamento recente, estatística desatualizada ou arquitetura de compatibilidade/cache. Não foi feita contagem exata nem investigação mutável.

## Riscos e pendências

- Outbox com milhões de registros sugere verificar política de consumo/limpeza.
- Telemetria e históricos requerem política documentada de retenção/backup.
- Confirmar backup/restore dos volumes e RPO/RTO.
- Executar contagens exatas e planos de consulta apenas em janela aprovada.

## Referências

`4-Data/Context/DbContext.cs:20-437`; entidades em `3-Domain/Entities`; migrations; repositórios Redis/Npgsql; consulta read-only de catálogo e `pg_stat_user_tables` em produção.
