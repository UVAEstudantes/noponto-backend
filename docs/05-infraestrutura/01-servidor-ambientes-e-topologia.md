# NoPonto — servidor, ambientes e topologia

**Finalidade:** registrar onde o NoPonto executa e separar topologia confirmada, configuração declarada e pontos não verificados.  
**Data:** 2026-10-06 (America/Fortaleza).  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** repositórios inspecionados; produção baseada na fotografia SSH read-only de 2026-10-06 12:18 já registrada na auditoria. Uma nova autenticação não foi automatizada; mudanças posteriores são `NÃO VERIFICADO`.

## Ambientes e fontes

Desenvolvimento ocorre em Windows, com Docker Desktop desligado e fora da referência operacional. CI usa runners Ubuntu do GitHub Actions e o serviço EAS. Produção é um host Debian executando Docker Compose. Fontes: `docs/00-auditoria/07`, `10` e `15`; arquitetura 02/06–07; Dockerfile, Compose e workflows; código dos workers e integrações.

## Host confirmado

| Item | Fotografia pontual | Limitação |
|---|---|---|
| SO/kernel | Debian 12 bookworm; Linux `6.1.0-45-amd64`; systemd inferido da distribuição | serviços systemd não foram enumerados nesta etapa |
| CPU | Intel Core i3-2120, x86-64, 2 núcleos/4 threads | não equivale a quota de container |
| Memória | 3,7 GiB RAM; 5 GiB swap | uso médio/pico não medido |
| Disco raiz | ext4, 109 GiB, 73 GiB usados (71%) | ponto no tempo, sem taxa de crescimento |
| Volume secundário | ext4, 293 GiB, 49 GiB usados; descrito como área de backup | presença não comprova backup íntegro/restaurável |
| Docker | Engine 29.4.3; Compose 5.1.3 | versões observadas naquela fotografia |
| Uptime/inodes | consultados na auditoria, sem série histórica publicada | tendência e exaustão: `NÃO VERIFICADO` |

O host também participa do compartilhamento de internet entre uma interface Wi-Fi USB e o PC por Ethernet. A existência dessa dependência foi fornecida pelo desenvolvedor; interfaces, encaminhamento, NAT, DHCP, DNS e firewall atuais não foram reinspecionados. Uma indisponibilidade ou mudança de rede no host pode afetar simultaneamente produção e conectividade do ambiente de desenvolvimento.

## Topologia lógica

```text
dispositivo móvel -- HTTP/SignalR --> host:API
API -- PostgreSQL --> transporte_postgres
API -- RESP --> transporte_redis
API -- HTTP/HTTPS --> providers GPS, ferroviário, ArcGIS e mapas
API -. HTTP legado indisponível .-> noponto_ml
GitHub Actions -- Tailscale/SSH --> host; GHCR --> Docker Engine
EAS --> APK preview --> distribuição interna não comprovada
```

API, PostgreSQL e Redis estavam na rede Docker `noponto_default`. O host também contém stacks `auth-*` e ferramentas compartilhadas Portainer/Dozzle, que não integram o domínio funcional do NoPonto. Não foi confirmado reverse proxy nem terminação TLS; a auditoria observou a API diretamente na porta publicada.

## Recursos, limites e riscos

A API, seus BackgroundServices, SignalR e endpoints HTTP concorrem dentro de um processo. PostgreSQL e API não possuem limites de CPU/memória declarados no Compose; Redis tem reserva/limite próprios. Em 3,7 GiB de RAM, picos de banco, GC, workers e outras stacks podem provocar swap, latência ou OOM. O `exit 137` do ML é compatível com término por pressão de memória, mas não prova sua causa.

Riscos principais: ponto único de falha físico; raiz já em 71%; dependência da internet e energia do host; contenção com containers externos; ausência de alta disponibilidade comprovada. Melhorias possíveis: orçamento por componente, acompanhamento de disco/RAM/swap, documentação da rede sem identificadores sensíveis e plano de migração/recuperação.

## Evidência, medições e pendências

Comandos originais de baixo impacto incluíram `uname`, `/etc/os-release`, `lscpu`, `free`, `df`, `lsblk`, `docker ps/inspect/stats` e `docker system df`. São medições pontuais, não benchmark. Permanecem `NÃO VERIFICADO`: topologia de firewall/TLS, acessibilidade pública, inodes atuais, limites físicos detalhados do armazenamento, UPS, monitoramento histórico e inventário da rede compartilhada.

Relacionados: [implantação](../02-arquitetura/07-arquitetura-de-implantacao.md), [containers](02-containers-redes-volumes-e-dependencias.md), [capacidade](04-desempenho-otimizacoes-e-capacidade.md) e [resiliência](07-resiliencia-backup-e-recuperacao.md).
