# NoPonto — containers, redes, volumes e dependências

**Finalidade:** descrever o Compose declarado e a fotografia produtiva sem confundir presença, saúde e uso funcional.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Verificação:** Compose/Dockerfile atuais e produção read-only registrada em 2026-10-06 12:18.

## Inventário

| Container | Imagem/configuração | Estado observado | Rede, porta e persistência | Classificação |
|---|---|---|---|---|
| `noponto_api` | GHCR `noponto-api:latest`; .NET 9 | ativo | `8080:8080`; `noponto_default`; socket Docker montado; sem volume de dados | atual |
| `transporte_postgres` | `postgis/postgis:16-3.4` | ativo/healthy | host `5432`→container `5432`; volume externo `noponto_postgres_data` | atual/durável |
| `transporte_redis` | `redis:7` | ativo/healthy | host `6380`→`6379`; volume externo `noponto_redis_data_v2` | atual/efêmero |
| `noponto_ml` | imagem GHCR `latest` | parado, estado persistido unhealthy/exit 137 | modelo/encoder bind-mounted; sem porta ativa observada | legado desligado intencionalmente |
| `noponto_v24_postgres` | PostGIS 16-3.4 | parado | publicaria `55440`; volume não inventariado | legado provável, não remover |
| `auth-*` | outra stack | existência observada | fora da rede funcional confirmada | fora do escopo |
| Portainer/Dozzle | ferramentas compartilhadas | existência observada | acesso ao Docker conforme configuração própria | infraestrutura compartilhada |

## API e ciclo de execução

O Dockerfile multi-stage restaura/publica com SDK .NET 9 bookworm-slim e executa `dotnet NoPonto.dll` no runtime ASP.NET 9, ouvindo em `0.0.0.0:8080`. O Compose espera PostgreSQL e Redis `healthy`, mas isso só ordena a inicialização; não prova migrations, schema, provider ou fluxo funcional. Não há healthcheck da API no Compose.

No mesmo processo convivem controllers, Swagger, SignalR e workers GPS, ferrovia, viagem/outbox, telemetria, ETA V2 e retenção. Benefícios: implantação simples, DI compartilhada e menor overhead. Custos: uma falha ou pressão de GC afeta toda a aplicação; loops competem pelo pool PostgreSQL, conexão Redis, CPU e memória; não há escala independente.

`restart: unless-stopped` aplica-se à API e ao PostgreSQL. Redis usa `on-failure:3`, portanto uma parada limpa ou falhas repetidas além do limite não têm a mesma garantia de reinício. Não existem quotas declaradas para API/PostgreSQL. Redis declara reserva de 384 MiB, limite de 640 MiB e `pids_limit: 128`.

## PostgreSQL/PostGIS

O volume externo é persistente além do ciclo do container, mas não é backup. O healthcheck usa `pg_isready`. A API resolve o serviço pela rede Docker e Npgsql; a porta também estava publicada em todas as interfaces do host. TLS, privilégios mínimos, `pg_hba.conf`, parâmetros de WAL/checkpoint/autovacuum e acesso efetivo além do host são `NÃO VERIFICADO`.

## Redis e consequência da efemeridade

O comando efetivo declara `--save ""`, `--appendonly no`, `--maxmemory 512mb` por padrão e `--maxmemory-policy noeviction`. Logo, o volume `/data` não torna o dataset recuperável: não há RDB nem AOF configurado. Restart/perda do processo elimina snapshots, TTLs, locks, índices por linha, estado causal, streams, consumer groups e PEL/pending.

Projeções quentes podem ser reconstruídas por novas coletas, mas eventos ainda não persistidos no PostgreSQL podem ser perdidos. Um stream oferece ordenação/ack durante a vida da instância, não durabilidade após restart nesta configuração. `noeviction` evita remoção silenciosa de chaves, mas passa a rejeitar escritas ao atingir a memória; isso pode interromper realtime, stream e locks antes de o container exceder 640 MiB. Na amostra, Redis usava cerca de 115 MiB; `INFO`/DBSIZE/PENDING atuais não foram recolhidos nesta etapa.

## Portas, DNS, redes e mounts

Bindings observados eram API `0.0.0.0:8080`, PostgreSQL `0.0.0.0:5432` e Redis `0.0.0.0:6380`. Isso amplia a superfície local/Tailscale, mas isoladamente não prova exposição à internet; firewall e roteamento são `NÃO VERIFICADO`. Internamente, a API usa DNS Compose (`postgres`, `redis`, destino legado `noponto_ml`). Não foi confirmado reverse proxy.

O mount `/var/run/docker.sock` dá ao processo API uma via de controle equivalente a alto privilégio no host. O código registra um `HttpClient` para o socket, o que comprova dependência compilada, embora o caso de uso operacional atual não tenha sido demonstrado. Remoção exige primeiro mapear consumidores; alternativa futura é um proxy de API Docker restrito ou eliminar a função.

## Evidências, limitações e pendências

Fontes: `NoPonto/docker-compose.yml`, override local, Dockerfile, `Program.cs` e auditorias 07/15. Não foram executados restart, inspect bruto de ambiente, logs extensos nem comandos de escrita. Pendente: digest atual, limites de log, driver de logging, utilização de volumes, configuração Redis via `CONFIG GET` sanitizado, conexões, healthcheck da API e dependências do container legado.

Relacionados: [topologia](01-servidor-ambientes-e-topologia.md), [segurança](06-seguranca-e-superficie-de-exposicao.md) e [recuperação](07-resiliencia-backup-e-recuperacao.md).
