# 10 — Segurança, confiabilidade e limitações

**Data:** 2026-10-06 · **Ambientes:** código e produção read-only · **Estado:** controles observados; não é pentest

## Controles existentes

- Segredos são parametrizados por variáveis de ambiente no Compose; valores não foram documentados.
- Middleware global padroniza erros; HttpClients usam timeout e os coletores possuem budget/backoff/single-flight.
- PostgreSQL e Redis têm healthchecks e volumes; reinício é configurado.
- Estado causal, TTL, retenção, outbox, streams bounded e backpressure tratam falhas/volume.
- CORS é configurável por ambiente.
- Validação temporal e gates ferroviários reduzem publicação de estado incoerente.

## Ausências/riscos observados

1. Não foram encontrados `AddAuthentication`, `UseAuthentication`, `UseAuthorization` ou atributos ativos equivalentes em `Program.cs`/Swagger. Os endpoints observados são públicos; controllers admin estão excluídos, mas autenticação continua necessária se retornarem.
2. PostgreSQL e Redis são publicados em todas as interfaces do host. O alcance real depende de firewall, **NÃO VERIFICADO**.
3. A API monta `/var/run/docker.sock`, equivalente a uma fronteira altamente privilegiada sobre o host.
4. Swagger está ativo em produção sem gate observado.
5. Imagens usam `latest`; não há pin por digest/SBOM/scan demonstrado.
6. Redis desabilita persistência e usa `noeviction`; indisponibilidade ou memória cheia afeta o runtime.
7. ML está parado; API depende de fallback e não deve prometer ETA por ML.
8. API/Postgres não têm limites de recursos no Compose; host possui apenas 3,7 GiB RAM.
9. Volumes/backups existem, mas restauração e RPO/RTO não foram comprovados.

## Confiabilidade dos dados

A ausência de linhas/paradas/padrões na estatística de produção, contrastando com milhões de eventos e telemetrias, ameaça matching e apresentação. Schedule ferroviário existe, mas qualidade depende do mapeamento das estações, observações do provider e flags runtime. Dados de terceiros podem ser atrasados, incompletos ou mudar contrato.

## Recomendações prioritárias

- Restringir portas de banco/Redis à rede interna e confirmar firewall/TLS.
- Remover o Docker socket da API ou interpor proxy mínimo e autorizado.
- Introduzir autenticação/autorização para superfícies administrativas e documentação operacional.
- Fixar imagens por versão/digest; healthcheck da API; política de rollback.
- Investigar tabelas estruturais e backlog da outbox.
- Definir retenção, backup restaurável, métricas e alertas.

## Referências

`Program.cs`; `ExceptionMiddleware.cs`; `docker-compose.yml`; serviços de retention/backpressure; observação Docker/Swagger. Nenhuma credencial foi registrada.
