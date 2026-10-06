# NoPonto — segurança e superfície de exposição

**Finalidade:** analisar controles e riscos observáveis sem teste ofensivo.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** código/Compose/workflows e fotografia produtiva; firewall, TLS e alcance externo permanecem `NÃO VERIFICADO`.

## API

Não foram encontrados `AddAuthentication`, `UseAuthentication`, `UseAuthorization` nem proteção equivalente no pipeline vigente. Controllers compilados devem ser considerados públicos conforme sua rota. Controllers administrativos antigos estão excluídos da compilação observada, mas Swagger mantém documentos `v1` e `admin`; Swagger/UI é habilitado sem condição de ambiente. Endpoints mutáveis compilados precisam inventário específico e autorização antes de exposição futura.

CORS usa origins configuradas; se a lista estiver vazia, aceita qualquer origem com headers/métodos/credenciais. CORS não substitui autenticação. Middleware global trata erros, opções são validadas e HttpClients possuem timeouts/budgets, mas rate limiting HTTP não foi identificado. HTTPS/reverse proxy/HSTS não foram comprovados.

## Docker socket

O Compose monta `/var/run/docker.sock` na API e `Program.cs` registra cliente Unix para ele. Comprometimento da API pode permitir operações privilegiadas sobre containers/host; o mount é risco alto mesmo sem endpoint público comprovado que o utilize. A dependência funcional atual não foi demonstrada. Futuro: remover após inventário ou interpor proxy allow-list sem capacidade ampla.

## PostgreSQL, Redis e rede

As portas estavam publicadas em todas as interfaces; isso não prova alcance pela internet, mas expõe aos segmentos alcançáveis do host. PostgreSQL usa credencial; Redis não mostra senha/TLS no Compose. API conecta por rede Docker, portanto a publicação dos datastores não é necessária para o fluxo interno demonstrado. Firewall, ACL Redis, `pg_hba`, TLS e privilégio do usuário da aplicação são `NÃO VERIFICADO`.

Prioridade: confirmar alcance e restringir banco/Redis à rede interna ou loopback quando houver necessidade administrativa; usar autenticação/TLS adequados ao limite de confiança. Nenhuma mudança foi aplicada.

## Segredos e cadeia de fornecimento

Compose referencia variáveis de `.env`; Actions usa Secrets para GHCR, Tailscale, SSH e Expo. Valores não foram lidos. Variáveis de container podem ser acessíveis a operadores e a processos privilegiados; rotacionar e conceder mínimo. Não imprimir `.env`, connection strings ou inspect bruto é requisito operacional.

Dockerfile usa bases .NET 9 por tag; Compose usa `latest` para API e tags de linha (`redis:7`, `postgis:16-3.4`). Workflows usam actions por tags e EAS `latest`. Lockfiles ajudam aplicações, mas não foi comprovado scanner de vulnerabilidades, Dependabot, SBOM, assinatura, provenance ou pin por digest. Build reproduzível é parcial.

## Matriz resumida

| Achado | Controle existente | Estado/risco |
|---|---|---|
| API sem auth observável | controllers admin excluídos; validação de entrada por framework/DTO | alto se houver mutação/dado sensível |
| Docker socket na API | acesso Unix local ao container | alto, blast radius de host |
| datastores publicados | credencial PostgreSQL; rede Docker | alto até confirmar firewall; Redis sem auth observável |
| Swagger em produção | documentação útil | médio; amplia enumeração |
| CORS fallback aberto | origins podem ser configuradas | médio; não é barreira de segurança |
| tag mutável | tags versionadas também publicadas | médio para integridade/rastreabilidade |
| TLS/firewall | nenhuma evidência suficiente | `NÃO VERIFICADO`, decisão prioritária |

## Limitações e melhorias

Não houve scan, exploração, leitura de segredo nem mudança. Recomenda-se modelar ameaças; autenticação/autorização por política; separar endpoints administrativos; limitar Swagger; rate limiting; hardening de portas/socket; usuário DB mínimo; gestão/rotação de secrets; imagem por digest, SBOM e atualização monitorada.

Fontes: `Program.cs`, Compose, workflows, controllers compilados e [auditoria de segurança](../00-auditoria/10-seguranca-e-limitacoes.md). Relacionados: [containers](02-containers-redes-volumes-e-dependencias.md) e [riscos](08-operacao-riscos-e-plano-de-melhorias.md).
