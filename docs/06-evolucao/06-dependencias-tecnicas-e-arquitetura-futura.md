# NoPonto — dependências técnicas e arquitetura futura

**Finalidade:** mapear reutilização, novos componentes candidatos e limites operacionais sem convertê-los em arquitetura aprovada.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** arquitetura 02, fluxos 03, persistência 04, infraestrutura 05 e código frontend/backend. **Estado:** núcleo existente; componentes futuros conceituais.

## Mapa de dependências

```text
Estrutura V2 + schedules + geometrias
        ├─ realtime atual e UX de confiança
        └─ snapshot de rede + transferências/footpaths
                    └─ roteador multimodal
                         ├─ alternativas/rotas salvas
                         └─ Rotinas (também podem existir antes como intenção)
                              └─ monitoramento e alertas
ETA/telemetria ────────────────┘ como ajuste, não pré-requisito absoluto
identidade/privacidade ────────┘ necessária para sincronização, não para local
```

## Reutilização

APIs V2 oferecem identidade/versionamento; PostGIS localiza candidatos; schedules fornecem tempo ferroviário; GPS/ETA/evidências ajustam custos; Redis pode cachear resultados efêmeros; SignalR/polling atualizam viagem; MapLibre apresenta etapas; AsyncStorage sustenta protótipo local. Reutilização requer contratos próprios para rotas, sem acoplar grafo a DTOs de mapa.

## Componentes candidatos

| Componente | Responsabilidade | Alternativa |
|---|---|---|
| construtor de rede offline | validar estrutura, transferências e gerar snapshot | motor externo com import GTFS/OSM |
| serviço de roteamento | consulta temporal/multicritério sob demanda | biblioteca no monólito inicialmente |
| adaptador realtime | ajustar custo/freshness sem corromper schedule | pós-processar alternativas |
| catálogo tarifário | regras versionadas e explicáveis | omitir custo no MVP |
| serviço pessoal | favoritos/Rotinas/assinaturas | armazenamento somente local |
| avaliador de alertas | condições e deduplicação | alertas locais |

“Serviço” não implica microserviço. No host atual, módulo interno ou job offline é mais proporcional; separar processo só com necessidade medida.

## Execução e viabilidade

Host: i3-2120, 2 núcleos/4 threads, 3,7 GiB RAM, swap e stacks compartilhadas. Construção do grafo deve ocorrer offline/em janela controlada, com artefato versionado. Busca ocorre por solicitação e pode usar cache. Reavaliação periódica deve limitar-se a viagens/Rotinas ativas; alertas não devem varrer toda a rede por usuário. ML avançado não deve concorrer antes de orçamento e baseline.

Alternativas arquiteturais:

- dentro da API: menor operação, maior contenção/blast radius;
- worker/processo separado: isolamento, mais deploy/memória;
- serviço gerenciado/externo: capacidade/recursos maduros, custo, privacidade e dependência.

Não há benchmark que determine escolha. A ordem segura é protótipo offline → perfil → módulo sob demanda → separação se justificada.

## Decisões e requisitos

`REQUISITO_CANDIDATO ARCH-SNAPSHOT`: artefato de rede deve registrar fontes, versões, hash e data. `ARCH-ISOLAMENTO`: falha do roteador não interrompe realtime. `ARCH-ORCAMENTO`: jobs possuem limite, cancelamento e métricas. `ARCH-CACHE`: chave inclui versão e preferências; resultado stale é sinalizado.

Riscos: memória do grafo, rebuild concorrente, dados incompatíveis, Redis efêmero, endpoint caro sem rate limit, dados pessoais na mesma superfície pública. Segurança mínima, backup, auth e observabilidade da etapa 2.4B são dependências para qualquer backend pessoal.

## Decisões pendentes

Backend/local/motor externo; formato do snapshot; origem de caminhada; política de atualização; identidade; cache; estratégia de deploy e avaliação. Recomendação provisória: manter o MVP modular no backend e artefatos offline, mas aprovar somente após protótipo e medição.

Relacionados: [arquitetura geral](../02-arquitetura/01-arquitetura-geral.md), [infraestrutura](../05-infraestrutura/04-desempenho-otimizacoes-e-capacidade.md), [rotas](02-rotas-multimodais-e-algoritmos-candidatos.md) e [roadmap](07-roadmap-priorizacao-e-escopo-da-defesa.md).
