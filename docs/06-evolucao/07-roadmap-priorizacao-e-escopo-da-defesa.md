# NoPonto — roadmap, priorização e escopo da defesa

**Finalidade:** apoiar uma decisão realista de escopo sem assumi-la pelo desenvolvedor/orientador.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** requisitos 2.1, estado técnico 2.2–2.4 e documentos desta etapa. **Estado:** matriz de decisão, não cronograma ou compromisso.

## Núcleo, candidatos e evolução

| Item | Estado/valor | Dependências/esforço/risco | Demonstração e decisão |
|---|---|---|---|
| estrutura V2 | operacional; alto valor técnico | estabilização; baixo adicional | demonstrável; núcleo forte |
| ônibus/BRT realtime | operacional backend/UI implementada | teste E2E; médio | núcleo forte |
| ferrovia inferida | operacional, diferencial acadêmico | medir cobertura/confiança; médio | núcleo forte se bem comunicado |
| app mobile | implementado não validado integralmente | build/roteiro/UX; médio | necessário para demonstração |
| ETA V2 | migração/off | ground truth→dataset→baseline→avaliação→shadow; alto | candidato somente com janela |
| metrô | planejado | provider/estrutura; alto | posterior ou integração estática delimitada |
| POIs/tarifas | parciais | contratos/dados/regras; médio/alto | decisão: concluir, reduzir ou retirar |
| roteador multimodal | planejado; alto valor | rede/footpaths/algoritmo; alto | protótipo candidato, não obrigação |
| Rotinas/notificações | placeholder/planejadas | rotas, identidade, privacidade; alto | posterior |
| segurança/observabilidade | lacunas confirmadas | operacional; médio | mínimo obrigatório de estabilidade |
| avaliação técnica | instrumentação existente parcial | protocolo e execução; médio | essencial ao TCC |
| avaliação com usuários | não realizada | desenho/autorizações; alto | decisão acadêmica |

## Fases por dependência

1. **Estabilização:** backup/restore; portas/socket; auth de mutações; snapshots vazios; reinscrição SignalR; recuperação; CI/testes; observabilidade mínima.
2. **Consolidação do núcleo:** E2E, qualidade GPS, cobertura ferroviária, contratos, build e comunicação de idade/confiança.
3. **ETA:** qualidade do ground truth → dataset reproduzível → baseline → avaliação → shadow → canary → eventual promoção.
4. **Multimodal:** conexões/footpaths → snapshot → roteador inicial → alternativas → tempo/realtime → tarifa → replanejamento.
5. **Personalização:** favoritos → rotas salvas → Rotinas → identidade opcional → alertas.
6. **Expansão:** metrô, grades, POIs e modelos avançados.

Não há datas arbitrárias. Uma fase pode ser adiada sem invalidar o núcleo.

## Opções de escopo da defesa

| Opção | Conteúdo | Vantagem | Risco |
|---|---|---|---|
| A — núcleo consolidado | V2 + ônibus/BRT + ferrovia + app + avaliação | já possui evidência, defensável | exige qualidade/UX e testes, menos novidade algorítmica |
| B — núcleo + ETA shadow | A + baseline/avaliação ETA V2 | experimento quantitativo forte | dados/tempo e risco metodológico |
| C — núcleo + roteador mínimo | A + protótipo unimodal/multimodal delimitado | contribuição algorítmica | grande dependência de dados/conexões |

Recomendação provisória: A como piso; escolher **um** eixo experimental (B ou C) somente após prova de viabilidade. Não é decisão aprovada.

## Matriz de decisões do desenvolvedor

| Decisão | Alternativas | Recomendação provisória/dependência |
|---|---|---|
| eixo da defesa | núcleo, ETA, roteador | núcleo + um eixo medível |
| metrô | excluir, estático, operacional | não prometer sem provider |
| primeira rota | estática, time-dependent, motor externo | protótipo pequeno comparativo |
| cálculo | app, backend, externo | backend/offline inicialmente |
| caminhada | linha reta, OSM/motor, curada | não usar reta como transferência válida |
| POIs | concluir, reduzir, adiar | decidir antes do roteiro da UI |
| Rotinas/auth | local, conta, híbrido | local mínimo antes de conta |
| ETA V2 | fora, experimento, entrega | só após ground truth/dataset |
| avaliação humana | incluir ou técnica apenas | alinhar exigências institucionais |

## Critérios de passagem

Cada candidato precisa: responsável, fonte de dados, critério verificável, roteiro demonstrável, custo compatível, estado de falha e prazo real definido fora deste documento. “Implementado” não basta sem evidência por commit/build.

Riscos: dispersar quatro modais, rotas, ML e Rotinas; negligenciar segurança; produzir demo sem avaliação; depender de provider instável. Pendências centrais: data da defesa, disponibilidade, orientação acadêmica, população de teste e infraestrutura de avaliação.

Relacionados: [estado anterior](../01-projeto/05-estado-atual-e-roadmap.md), [riscos operacionais](../05-infraestrutura/08-operacao-riscos-e-plano-de-melhorias.md), [rotas](02-rotas-multimodais-e-algoritmos-candidatos.md) e [avaliação](08-criterios-de-aceite-avaliacao-e-riscos.md).
