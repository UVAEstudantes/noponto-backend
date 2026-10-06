# Base acadêmica de resultados, limitações e evolução

**Finalidade:** fornecer estrutura de discussão sem preencher resultados inexistentes.

## Resultados a apresentar futuramente

1. cobertura funcional por RF/RNF, com evidência e versão;
2. integridade/importação da estrutura V2;
3. matching GPS por casos e distribuição de erro/duração;
4. cobertura, freshness e coerência ferroviária;
5. jornadas mobile em dispositivo;
6. disponibilidade/capacidade em janela definida;
7. ETA somente se o protocolo V2 for executado.

Modelo de tabela: `métrica | conjunto | baseline | resultado | intervalo | limitação`. Células permanecem `[RESULTADO PENDENTE]` até coleta reproduzível. Não converter logs pontuais em média nem teste unitário em eficácia do produto.

## Limitações confirmadas

Host de recursos limitados e único; Redis sem persistência; backup/restore e RPO/RTO não comprovados; portas/socket/auth requerem hardening; métricas não centralizadas; build mobile distribuído não confirmado; provider externo variável; ferrovia inferida; ETA V2 OFF; ground truth pode ser interpolado; POI/tarifa parciais; Rotas, Rotinas, notificações e metrô planejados.

## Discussão e ameaças à validade

Validade interna pode ser afetada por relógio, cache, configuração e seleção de amostras. Validade externa é limitada à cobertura geográfica/modal/provider estudada. Validade de constructo depende de ground truth e de métricas representarem experiência do passageiro. Reprodutibilidade requer commits, manifests, hashes e ambiente.

## Evolução

Primeiro estabilizar segurança, backup, CI, contratos, UX e observabilidade. ETA deve seguir ground truth→dataset→baseline→shadow→canary. Roteamento exige rede/transferências/footpaths antes de Rotinas/recomendação. Metrô e POIs são expansões, não entregas atuais.

Relacionados: [roadmap](../06-evolucao/07-roadmap-priorizacao-e-escopo-da-defesa.md), [riscos](../05-infraestrutura/08-operacao-riscos-e-plano-de-melhorias.md) e [plano](03-plano-de-testes-e-validacao.md).
