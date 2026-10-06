# 07 — Infraestrutura de produção

**Observação:** 2026-10-06 12:18 (America/Fortaleza) · **Ambiente:** servidor autorizado via SSH, somente leitura · **Estado:** fotografia pontual confirmada

## Host

Debian GNU/Linux 12 (bookworm), kernel `6.1.0-45-amd64`; Intel Core i3-2120, 2 núcleos/4 threads; 3,7 GiB RAM e 5 GiB swap. Raiz ext4 de 109 GiB com 73 GiB usados (71%); volume de backup ext4 de 293 GiB com 49 GiB usados. Docker Engine 29.4.3 e Compose 5.1.3.

## Stack NoPonto

| Container | Imagem | Estado | Porta publicada | Persistência/restart |
|---|---|---|---|---|
| `noponto_api` | `ghcr.io/guilhermedesales/noponto-api:latest` | ativo há 22h | 8080 | `unless-stopped`; Docker socket montado |
| `transporte_postgres` | `postgis/postgis:16-3.4` | ativo/healthy há 3d | 5432 | volume; `unless-stopped` |
| `transporte_redis` | `redis:7` | ativo/healthy há 3d | 6380→6379 | volume efêmero; on-failure; limite 640 MiB |
| `noponto_ml` | imagem GHCR `latest` | parado, exit 137, unhealthy | nenhuma ativa | modelo/encoder bind-mounted; sem restart |
| `noponto_v24_postgres` | PostGIS 16-3.4 | parado há 9d | 55440 quando ativo | legado aparente |

Na amostra leve, API usava ~307–327 MiB, PostgreSQL ~1,11 GiB e Redis ~115 MiB. CPU do PostgreSQL oscilou fortemente na segunda amostra; isso não constitui benchmark. Tráfego e I/O acumulados são altos.

API, banco e Redis compartilham `noponto_default`. A API expõe Swagger e respondeu `Hello World`; o Swagger confirmou consultas de linhas, itinerários, paradas, modais, padrões, tarifas, veículos, eventos e snapshot ferroviário.

## Implantação

Compose efetivo em `/opt/stacks/noponto/docker-compose.yml`; imagem API vem de GHCR com tag mutável `latest`. O repositório contém workflow de deploy. Não foi identificado mecanismo formal de rollback, pin por digest, limites de CPU/memória para API/Postgres ou healthcheck da API.

## Exposição e riscos operacionais

PostgreSQL, Redis e API estavam publicados em todas as interfaces (`0.0.0.0`/IPv6). O Docker socket do host é montado dentro da API, ampliando muito o impacto de comprometimento. O host tem RAM limitada, usa swap e o ML sofreu exit 137, compatível com pressão de memória, embora a causa exata não tenha sido provada. Disco raiz em 71% exige acompanhamento.

## NÃO VERIFICADO

Firewall/TLS/reverse proxy, backups restauráveis, alertas, rotação de logs, registry retention, processo de rollback, métricas históricas e rede externa. Outros containers (`auth-*`, Portainer, Dozzle) existem no host, mas não foram auditados fora da relação superficial.

## Referências

Comandos `date`, `/etc/os-release`, `uname`, `lscpu`, `free`, `df`, `docker version/compose/ps/inspect/stats`, `curl` e consultas PostgreSQL somente leitura; Compose do repositório.
