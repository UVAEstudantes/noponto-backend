# Catálogo, convenções e rastreabilidade dos diagramas

**Objetivo:** indexar as figuras da baseline 2026-10-06 e impedir confusão entre estado atual, memória, persistência e planejamento. **Nível:** catálogo. **Baseline:** backend `53567bd`, frontend `c69a92e`, ML `08c493b`.

## Convenções

- linha contínua: fluxo implementado; tracejada: opcional, desligado ou planejado, conforme rótulo;
- cilindro: persistência; Redis é marcado **efêmero**; componentes `in-memory` não são tabelas;
- workers aparecem dentro da API, nunca como containers;
- `[OFF]` significa código/configuração presentes, não operação produtiva;
- `GPS` aplica-se ao rodoviário; ferrovia usa schedule e evidências, não GPS contínuo;
- cardinalidades dos DERs vêm de entidades/migrations; referências armazenadas em JSON são rotuladas lógicas.

| Documento | Visão | Evidência principal | Uso sugerido |
|---|---|---|---|
| 01–05 | C4, componentes e implantação | Program, Compose, frontend | análise/projeto |
| 06–08 | DERs | DbContext e migrations | modelagem de dados |
| 09–15 | fluxos GPS, rail e ETA | services/workers | implementação |
| 16 | CI/CD e recuperação | workflows/infra | implantação/operação |

Cada documento descreve participantes, relações, fonte, limitações e uso acadêmico. Mermaid foi escolhido por ser textual/versionável. Os desenhos são abstrações: não substituem schema, contrato ou plano de execução.

## Rastreabilidade

As figuras apontam para [arquitetura geral](../02-arquitetura/01-arquitetura-geral.md), [funcionalidades](../03-funcionalidades/01-realtime-onibus-brt-fluxo-completo.md), [dados](../04-dados/01-modelagem-conceitual-e-logica.md) e [infraestrutura](../05-infraestrutura/01-servidor-ambientes-e-topologia.md) como fontes normativas. Relatórios 00–13 são históricos; reconciliações 14/15 prevalecem.

**Limitações:** layouts podem variar entre renderizadores; métricas, TLS/firewall e funcionalidades futuras não são inferidos. **Uso na monografia:** selecionar apenas as figuras necessárias e adaptar legenda/numeração ao padrão institucional.
