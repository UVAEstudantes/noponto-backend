# NoPonto — estado atual e roadmap

**Finalidade:** registrar o ponto de partida da Etapa 2.1, as migrações em curso, prioridades e horizontes de evolução.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** backend/produção verificados; frontend verificado em código; roadmap sujeito a decisão.

## Síntese do estado atual

O NoPonto possui um núcleo técnico operacional para estrutura de transporte, realtime rodoviário e representação ferroviária. A arquitetura vigente é a V2: linhas e sentidos se ligam a padrões operacionais versionados e ocorrências ordenadas de parada. A produção possui estrutura V2 populada, API, PostGIS e Redis ativos.

O aplicativo já usa os contratos V2 para montar percursos e recebe veículos rodoviários por SignalR. A ferrovia usa polling de snapshots estimados. O build efetivamente distribuído não foi confirmado, portanto a experiência completa ainda precisa de validação em dispositivo.

## Estado por domínio

| Domínio | Estado | Evidência | Principal pendência |
|---|---|---|---|
| Estrutura V2 | operacional | endpoints, migrations e contagens reais | documentar/importar de forma reproduzível |
| GPS ônibus/BRT | operacional | polling, providers, matching e flags ativos | avaliação representativa de qualidade |
| Redis/SignalR | operacional | snapshots e hub no fluxo | teste ponta a ponta e capacidade |
| Frontend estrutural/mapa | implementado | services/hooks/MapLibre | validar build/dispositivo |
| Ferrovia schedule-first | operacional | flags, schedules e endpoint | métricas de confiança/precisão |
| ETA antigo | legado ativo degradado | cliente chamado; serviço intencionalmente off | remover após substituição segura |
| ETA V2 | em migração | pipeline compilado; flags OFF; zero previsões | shadow, ground truth e avaliação |
| POIs | parcial | frontend chama; backend atual não expõe endpoints | reimplementar V2 ou retirar UI |
| Tarifas | parcial | endpoint/componente existem | dados e integração funcional |
| Metrô | planejado | visão/roadmap | fonte e abordagem |
| Rotinas/rotas | planejado | placeholder e visão funcional | requisitos, grafo e privacidade |
| Painel admin | fora do escopo | decisão do desenvolvedor | nenhuma nesta fase |

## Migrações em andamento

### ETA/ML V2

O modelo antigo usa features/target incompatíveis com a semântica pretendida de tempo restante até a próxima ocorrência. O caminho de transição é:

```text
cliente/modelo antigo (legado fail-open)
  → telemetria e viagem V2
  → baseline longitudinal versionado em shadow
  → previsão + chegada observada + erro
  → validação temporal e critérios de promoção
  → modelo supervisionado V2, se demonstrar benefício
```

O pipeline de dados existe, mas shadow/canary estão desligados. O próximo marco técnico coerente é validar a fundação ETA V2 sem alterar o que o passageiro vê.

### Contratos de compatibilidade

O frontend usa estrutura V2, mas ainda consome detalhe de linha por uma fachada legacy construída sobre V2 e recebe um alias de DTO SignalR. A migração deve versionar contratos antes de remover essas superfícies.

### Limpeza de legado/configuração

Controllers, entidades, jobs e importadores antigos estão excluídos da compilação. Variáveis antigas permanecem no Compose. A limpeza depende de mapear scripts externos, migrations históricas e consumidores; não é requisito funcional do passageiro nem deve antecipar a estabilização dos contratos.

## Prioridades sugeridas até a defesa

1. Fixar o escopo avaliado com o orientador.
2. Preparar build mobile reproduzível e roteiro de demonstração.
3. Executar testes em ambiente isolado e publicar resultados por commit.
4. Validar fluxos GPS e ferroviário ponta a ponta com amostra documentada.
5. Decidir POIs e corrigir a inconsistência frontend/backend.
6. Habilitar ETA V2 em shadow somente se fizer parte da entrega aprovada.
7. Produzir arquitetura, banco, fluxos, diagramas e avaliação nas próximas etapas documentais.
8. Tratar segurança operacional prioritária independentemente do escopo acadêmico: rede interna, Docker socket e autenticação de operações mutáveis.

## Horizontes do roadmap

### Horizonte A — núcleo demonstrável

- catálogo V2;
- mapa com linhas, sentidos, percursos e paradas;
- realtime de ônibus/BRT;
- trem schedule-first com posição estimada;
- apresentação da origem/qualidade;
- persistência/cache e testes reproduzíveis.

### Horizonte B — candidatos para a defesa

- ETA V2 shadow avaliado;
- movimento visual contínuo estabilizado;
- integração mínima de metrô;
- POIs resolvidos;
- validação de usabilidade ou técnica definida.

Todos dependem de confirmação do desenvolvedor/orientador.

### Horizonte C — evolução posterior

- Rotinas e favoritos integrados;
- grafo e planejamento multimodal;
- comparação dinâmica de alternativas;
- tarifas de viagem completa;
- notificações contextuais;
- ML supervisionado V2 e modelos espaço-temporais;
- expansão geográfica/modal.

## Visão funcional de Rotinas

Uma Rotina representa uma intenção de deslocamento, não apenas uma linha favorita. Poderá conter nome, origem, destino, dias, horários e alternativas multimodais. Durante a viagem, poderá organizar etapas, estimar chegada ao destino, acompanhar conexões e indicar impacto de atrasos. A seleção entre ônibus+trem, BRT+trem ou BRT+metrô deverá considerar critérios explicitamente definidos e explicáveis.

Ainda não estão decididos o algoritmo de roteamento, a forma de armazenar dados pessoais, os providers necessários, o modelo de tarifa ou os critérios de recomendação. Esses pontos permanecem requisitos futuros.

## Dependências e riscos

- providers podem atrasar, mudar contrato ou ficar indisponíveis;
- a estrutura importada é pré-condição para matching e visualização;
- inferência ferroviária depende de schedules e evidências suficientes;
- ML não compensa inconsistência semântica de dados;
- funcionalidades pessoais futuras exigem autenticação, privacidade e retenção;
- escopo excessivo dos quatro modais pode comprometer validação do núcleo;
- compatibilidades não podem ser removidas sem coordenar backend e frontend.

## Inconsistências conhecidas

- roadmap histórico ainda mostra trem como planejado, embora o runtime esteja ativo;
- frontend contém chamadas POI sem endpoint ativo;
- arquivo `favoritos.tsx` apresenta Rotinas, mas apenas como texto futuro;
- README do frontend descreve versões/estrutura antigas;
- ETA antigo permanece no call path mesmo com o serviço desligado;
- tag Docker `latest` não identifica commit por metadata.

## Limitações

O roadmap não é cronograma. Nenhum item candidato tem prazo ou critério final aprovado neste documento. Custos, capacidade da equipe e data de defesa não foram informados.

## Evidências

Ver [objetivos e escopo](03-objetivos-e-escopo.md), [requisitos](04-requisitos-e-funcionalidades.md), auditorias [14](../00-auditoria/14-evolucao-arquitetural-e-legado.md) e [15](../00-auditoria/15-reconciliacao-producao-e-contexto.md).

## Pendências

Converter o Horizonte B em backlog aprovado, priorizado e associado a critérios de aceite.
