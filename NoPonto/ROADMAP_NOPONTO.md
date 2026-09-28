# Roadmap — NoPonto

> Roadmap público de evolução do NoPonto, com foco em construir primeiro uma base operacional sólida, entregar os quatro modais em tempo real e evoluir progressivamente o ETA, a inteligência histórica e os recursos multimodais.

## Visão do projeto

O NoPonto é um sistema de mobilidade urbana voltado para ônibus, BRT, trem e metrô. A prioridade do projeto é construir uma base técnica robusta para representar linhas, sentidos, itinerários, paradas e veículos em movimento, oferecendo posição em tempo real ou estimada, próxima parada/estação, ETA e informações operacionais úteis.

A evolução do projeto segue quatro princípios:

- primeiro, garantir consistência estrutural e operacional;
- depois, tornar os quatro modais funcionais no mapa;
- em seguida, melhorar ETA e inteligência usando dados históricos reais;
- por fim, expandir para notificações, rotas multimodais e modelos mais avançados.

---

# Etapa 1 — Fundação estrutural do backend

**Status:** ✅ Concluída

Objetivo: consolidar a estrutura de domínio que sustenta todas as funcionalidades futuras.

Principais entregas:

- estrutura `Modal → Linha → Sentido → PadraoOperacional → PadraoVersao → OcorrenciaParadaPadrao → Parada`;
- `PadraoVersao` imutável;
- ponteiro de versão atual por `VersaoAtualId`;
- importação GTFS Data.Rio;
- parser, planejamento, persistência e publicação GTFS;
- reconciliação entre GTFS e ArcGIS;
- suporte a variantes de itinerário;
- suporte a circularidade;
- suporte a paradas repetidas;
- validações e testes de integração.

## Etapa 1.1 — Importação GTFS Data.Rio

**Status:** ✅ Concluída

- parser GTFS;
- importação de linhas, sentidos, shapes e paradas;
- publicação da estrutura operacional;
- rerun idempotente;
- reconciliação com dados já existentes.

## Etapa 1.2 — Reconciliação ArcGIS / GTFS

**Status:** ✅ Concluída

- reconciliação estrutural entre fontes;
- associação de paradas;
- validação direcional;
- comparação sequencial;
- suporte entre padrões relacionados.

### Produção

Essa etapa já está madura para produção, mas o melhor ponto de deploy é em conjunto com as etapas operacionais seguintes.

---

# Etapa 2 — Matching GPS e estado operacional

**Status:** ✅ Concluída

Objetivo: saber com confiabilidade onde cada veículo está dentro da rota correta.

Principais entregas:

- matching PostGIS;
- score geométrico por distância e bearing;
- tolerância máxima de distância;
- histerese operacional;
- equivalência entre consultas individuais e batch;
- comportamento fail-closed para dados inválidos;
- matching formalmente fechado.

## Etapa 2.1 — Bearing e histórico causal

**Status:** ✅ Concluída

- preservação de bearing válido recebido da fonte;
- uso de histórico apenas quando necessário;
- correção de sobrescrita indevida do bearing.

## Etapa 2.2 — Viagem operacional e versão pinada

**Status:** ✅ Concluída

Durante uma viagem, o veículo permanece associado à `PadraoVersao` correta, mesmo que a versão atual do padrão seja alterada durante a execução.

## Etapa 2.3 — Próxima ocorrência

**Status:** ✅ Concluída

A próxima parada é representada por uma ocorrência específica dentro do padrão, e não apenas pela parada física.

Isso permite tratar corretamente:

- paradas repetidas;
- rotas lineares;
- rotas circulares;
- wrap de fim para início;
- versões históricas pinadas.

## Etapa 2.4 — Distância longitudinal

**Status:** ✅ Concluída

Implementação de `DistanciaRestanteRotaMetros`, calculada sobre a própria geometria da `PadraoVersao`.

Essa distância é a base correta para ETA, diferente da distância geodésica direta GPS → parada.

### Produção

A etapa 2 é adequada para produção e representa um dos principais pilares do sistema em tempo real.

---

# Etapa 3 — Fontes GPS e observação estrutural

**Status:** ✅ Concluída

Objetivo: desacoplar o pipeline GPS das fontes concretas e permitir comparação entre provedores.

Principais entregas:

- abstração de fontes GPS;
- adapters por provedor;
- normalização de contratos;
- integração Data.Rio;
- suporte a Zirix/SPPO/BRT;
- separação entre origem, provedor e dado normalizado.

## Etapa 3.1 — Data.Rio GPS em shadow

**Status:** ✅ Concluída

A fonte Data.Rio pode ser observada e comparada sem substituir automaticamente o fluxo operacional existente.

## Etapa 3.2 — Hints estruturais GTFS

**Status:** ✅ Concluída

Observação de:

- `route_id`;
- `direction_id`;
- `shape_id`;
- `trip_id`.

Os hints são usados para diagnóstico e auditoria, sem alterar ainda o matching operacional.

## Etapa 3.3 — Auditoria shadow

**Status:** ✅ Concluída

- agregadores de métricas;
- comparação de concordância e conflito;
- diagnósticos de ausência e inconsistência;
- runners de auditoria.

### Produção

Bom ponto para observação real, especialmente para comparar comportamento de fontes sem risco operacional.

---

# Etapa 4 — Checkpoint e estabilização do Git

**Status:** ✅ Concluída

Objetivo: consolidar a evolução estrutural em commits separados e auditáveis.

Principais checkpoints:

- GTFS/Data.Rio;
- matching, versão pinada e distância longitudinal;
- abstração de fontes e hints estruturais;
- higiene do repositório.

---

# Etapa 5 — ETA V2 para ônibus e BRT

**Status:** 🟡 Em andamento

Objetivo: substituir gradualmente o ETA legado por uma base semanticamente correta, observável e treinável com dados limpos.

## Etapa 5.1 — Fundação de dados ETA V2

**Status:** 🟡 Em implementação

Principais objetivos:

- registrar eventos de previsão;
- associar previsão a veículo, viagem, versão, ocorrência e volta;
- persistir distância longitudinal;
- registrar baseline versionado;
- fechar ground truth a partir de passagem real;
- tratar expiração e invalidação;
- aplicar sampling para controlar volume;
- garantir fail-open em relação ao GPS operacional;
- incluir feature flag e métricas.

## Etapa 5.2 — Baseline ETA inicial

**Status:** ⬜ Próxima

Primeira abordagem:

```text
ETA = DistanciaRestanteRotaMetros / VelocidadeAtual
```

Com regras explícitas para:

- velocidade mínima confiável;
- distância inválida;
- veículo parado;
- ausência de previsão.

## Etapa 5.3 — ETA shadow em produção

**Status:** ⬜ Próxima

O ETA V2 será calculado e armazenado sem alterar imediatamente o ETA exibido ao usuário.

### Ponto ideal de produção #1

Esse é o primeiro grande deploy recomendado.

Validar em produção:

- crescimento da tabela;
- volume de writes;
- latência do pipeline;
- impacto no PostgreSQL;
- impacto no polling;
- quantidade de previsões pendentes;
- quantidade de previsões realizadas;
- cobertura;
- circularidade real;
- comportamento de viagens reais;
- veículos parados;
- expirações;
- erros de correlação.

### Janela de validação sugerida

```text
30 minutos → infraestrutura e exceptions
2 horas     → volume, latência e pendências
24 horas    → qualidade semântica e ground truth
```

---

# Etapa 6 — Movimento contínuo de ônibus e BRT no frontend

**Status:** ⬜ Planejada

Objetivo: evitar o efeito visual de veículo parado entre atualizações da API.

O frontend poderá interpolar o movimento usando:

- última posição observada;
- posição projetada na rota;
- bearing;
- velocidade;
- tempo desde a última atualização;
- geometria do itinerário.

Resultado esperado:

```text
posição observada
→ interpolação pela rota
→ nova posição observada
→ correção suave
```

Essa etapa aumenta muito a percepção de tempo real mesmo sem aumentar agressivamente a frequência de polling.

---

# Etapa 7 — Trem

**Status:** ⬜ Planejada

Objetivo: representar trens mesmo quando a fonte não fornece GPS contínuo.

## Etapa 7.1 — Estrutura ferroviária

- linha;
- sentido;
- estações ordenadas;
- trechos entre estações;
- tempos esperados de deslocamento;
- posição relativa ao longo da linha.

## Etapa 7.2 — Estado operacional do trem

Estados possíveis:

```text
EM_ESTACAO
ENTRE_ESTACOES
APROXIMANDO
ATRASADO
INDETERMINADO
```

## Etapa 7.3 — Posição estimada

Quando não houver GPS, a posição pode ser inferida a partir de:

- estação anterior;
- próxima estação;
- ETA informado pela fonte;
- tempo esperado entre estações;
- progresso temporal no trecho.

## Etapa 7.4 — ETA ferroviário inicial

Primeira versão baseada em:

- informação fornecida pela API;
- tempo entre estações;
- atraso atual;
- histórico quando disponível.

Não é necessário começar com ML complexo.

---

# Etapa 8 — Metrô

**Status:** ⬜ Planejada

Objetivo: reutilizar a mesma base ferroviária sempre que possível.

Principais componentes:

- linha;
- sentido;
- estações;
- trechos;
- posição observada ou inferida;
- ETA;
- atraso;
- estado operacional.

A estratégia será semelhante à de trem, adaptada às fontes disponíveis.

---

# Etapa 9 — Tempo real unificado dos quatro modais

**Status:** ⬜ Grande marco funcional

Objetivo: oferecer ao frontend um contrato comum para ônibus, BRT, trem e metrô.

Exemplo conceitual:

```text
VeiculoTempoReal
- modal
- linha
- sentido
- posição
- tipo da posição: observada ou estimada
- confiança
- próxima parada/estação
- ETA
- atraso
- timestamp
- fonte
```

Para ônibus/BRT:

```text
posição = observada por GPS
```

Para trem/metrô:

```text
posição = observada ou inferida
```

### Ponto ideal de produção #2

Esse é o principal marco de produto do TCC.

Meta:

> Os quatro modais aparecem no mapa e possuem movimento contínuo, linha, sentido, próxima parada/estação e uma estimativa temporal utilizável.

A partir desse ponto, o NoPonto já pode ser considerado funcional como aplicação multimodal em tempo real.

---

# Etapa 10 — ETA unificado dos quatro modais

**Status:** ⬜ Planejada

Objetivo: expor uma experiência uniforme mesmo usando algoritmos diferentes por modal.

## Ônibus e BRT

Base inicial:

```text
distância longitudinal
+ velocidade atual
+ histórico futuro
```

## Trem e metrô

Base inicial:

```text
posição estimada
+ tempos entre estações
+ atraso observado
+ histórico futuro
```

Externamente, o frontend recebe apenas uma estimativa coerente para o próximo ponto da viagem.

### Ponto ideal de produção #3

Após validar os quatro modais e a qualidade das estimativas, o ETA unificado pode substituir gradualmente comportamentos legados.

Sempre com feature flag e rollback simples.

---

# Etapa 11 — Dataset histórico operacional

**Status:** ⬜ Contínua após ETA V2

Objetivo: acumular dados limpos para evoluções futuras.

Dados úteis:

- tempo entre paradas;
- tempo parado;
- velocidade por trecho;
- horário;
- dia da semana;
- linha;
- sentido;
- trecho;
- headway;
- atraso;
- posição;
- ETA previsto;
- ETA real;
- erro da previsão.

Esse dataset passa a ser um ativo central do projeto.

---

# Etapa 12 — Tabela de horários observada

**Status:** ⬜ Planejada

Objetivo: construir uma grade estatística da operação real.

Exemplo:

```text
Linha X
Terça-feira
07:00–08:00

Parada A → B: mediana 3m20s
Parada B → C: mediana 4m10s
Parada C → D: mediana 2m55s
```

Essa estrutura pode ser derivada da coleta histórica ou comparada futuramente com horários públicos oficiais.

Benefícios:

- melhorar ETA;
- detectar atrasos;
- preencher lacunas de dados;
- servir como fallback;
- permitir análise programado vs observado.

---

# Etapa 13 — ETA histórico melhorado

**Status:** ⬜ Planejada

Antes de modelos sofisticados, explorar estatística histórica.

Possibilidades:

```text
ETA = tempo esperado dos trechos restantes
    + dwell esperado nas paradas
```

ou

```text
velocidade histórica por trecho + horário + dia
```

Esse estágio pode gerar ganhos grandes com complexidade relativamente baixa.

---

# Etapa 14 — ML V2 supervisionado

**Status:** ⬜ Futuro

Objetivo: treinar um modelo novo sobre dados semanticamente corretos.

Features candidatas:

- distância restante;
- velocidade atual;
- velocidade histórica;
- hora;
- dia da semana;
- linha;
- sentido;
- trecho;
- parada;
- dwell;
- estado da viagem;
- headway;
- atraso observado.

Validação obrigatória:

- holdout temporal;
- MAE;
- mediana;
- P90;
- P95;
- coverage;
- métricas por horizonte;
- métricas por linha e sentido.

### Ponto ideal de produção #4

O ML só deve substituir o baseline quando provar ganho consistente em dados reais.

Primeiro entra em shadow, depois pode ser promovido.

---

# Etapa 15 — Contexto de veículos à frente e atrás

**Status:** ⬜ Futuro

Objetivo: usar contexto coletivo da operação.

Possíveis sinais:

- veículo à frente;
- veículo atrás;
- distância entre veículos;
- velocidade média do corredor;
- headway;
- comboio;
- congestionamento;
- anomalias de fluxo.

Esse estágio permite que o ETA deixe de tratar cada veículo como isolado.

---

# Etapa 16 — Detecção de atraso de linha

**Status:** ⬜ Futuro

Objetivo: identificar quando a operação está significativamente pior que o comportamento esperado.

Exemplos:

```text
Trecho normalmente: 8 min
Trecho atual: 15 min
```

ou

```text
Headway normal: 8 min
Headway atual: 22 min
```

Possíveis resultados:

- atraso por trecho;
- atraso por linha;
- aumento de headway;
- veículo parado fora do padrão;
- propagação de atraso.

---

# Etapa 17 — Notificações úteis ao usuário

**Status:** ⬜ Futuro

Objetivo: transformar inteligência operacional em informação acionável.

Exemplos:

```text
Sua linha está com aproximadamente 12 minutos de atraso.
```

```text
O intervalo da linha aumentou de 8 para 21 minutos.
```

```text
O próximo veículo está a 3 paradas.
```

```text
Há lentidão significativa entre X e Y.
```

```text
A linha ferroviária está operando acima do atraso habitual.
```

---

# Etapa 18 — Sistema de rotas multimodal

**Status:** ⬜ Futuro importante

Objetivo: planejar deslocamentos combinando os quatro modais.

Exemplo:

```text
origem
→ caminhada
→ ônibus
→ metrô
→ trem
→ caminhada
→ destino
```

Possíveis pesos do roteamento:

- tempo total;
- tarifa;
- quantidade de transferências;
- caminhada;
- tempo de espera;
- confiabilidade;
- atraso atual;
- acessibilidade futura.

---

# Etapa 19 — Modelos espaciais e temporais avançados

**Status:** ⬜ Longo prazo

Somente depois de acumular dados suficientes.

Possíveis abordagens:

- modelos de sequência;
- grafos espaço-temporais;
- DCRNN;
- GNNs;
- modelos que representam propagação de congestionamento e atraso pela rede.

Esse estágio não é requisito para tornar o produto funcional.

---

# Etapa 20 — Hardening para produto público

**Status:** ⬜ Futuro

Antes de tratar o sistema como produto público real, fortalecer:

- autenticação;
- autorização;
- proteção de endpoints administrativos;
- rate limiting;
- recuperação de falhas;
- Redis/backplane quando necessário;
- segurança operacional;
- concorrência;
- observabilidade;
- backups;
- retenção de dados;
- health checks;
- deploy e rollback.

---

# Marcos principais de produção

## Deploy A — Base operacional + ETA V2 shadow

Inclui:

```text
estrutura V2
matching V2
viagem pinada
próxima ocorrência
DistanciaRestanteRotaMetros
fontes GPS abstratas
hints shadow
ETA V2 shadow
ground truth
persistência de previsões
```

Objetivo: começar a coletar dados limpos reais e encontrar problemas de infraestrutura cedo.

---

## Deploy B — Quatro modais em tempo real

Inclui:

```text
ônibus
BRT
trem
metrô
posição observada ou estimada
movimento contínuo
próxima parada/estação
ETA básico
```

Esse é o principal marco funcional do TCC.

---

## Deploy C — ETA operacional unificado

Inclui:

- ETA V2 promovido para ônibus/BRT;
- ETA ferroviário;
- contrato comum entre modais;
- comparação com comportamento legado;
- feature flags para rollback.

---

## Deploy D — Inteligência histórica

Inclui:

- tabela de tempos observados;
- histórico por trecho;
- atraso;
- headway;
- baselines mais fortes.

---

## Deploy E — ML V2

Inclui:

- modelo supervisionado treinado no dataset V2;
- validação temporal;
- comparação contra baselines;
- entrada primeiro em shadow;
- promoção somente após ganho consistente.

---

# Visão por eras

## Era 1 — Base operacional

**Status:** ✅ praticamente concluída

```text
estrutura V2
matching
viagem
ocorrência
distância longitudinal
fontes
GTFS
reconciliação
```

## Era 2 — App funcional multimodal

**Status:** 🟡 em andamento

```text
ETA ônibus/BRT
movimento contínuo
trem
metrô
posição estimada
tempo real dos 4 modais
ETA básico dos 4 modais
```

## Era 3 — Inteligência operacional

**Status:** ⬜ futura

```text
histórico
tabela de horários observada
ETA histórico
ML
headway
veículos à frente
atraso
```

## Era 4 — Produto avançado

**Status:** ⬜ futura

```text
notificações
planejamento multimodal
previsão de rede
GNN / DCRNN
novos recursos derivados dos dados
```

---

# Meta funcional do TCC

O marco principal não depende de terminar toda a parte de Machine Learning.

A meta funcional é:

> **Ônibus, BRT, trem e metrô representados em um único sistema, com posição observada ou estimada, movimento contínuo no mapa, linha, sentido, itinerário, próxima parada/estação e ETA utilizável.**

A partir desse ponto, o NoPonto já possui uma base sólida e funcional. As etapas seguintes passam a ser melhorias progressivas de inteligência, confiabilidade e experiência do usuário.

---

# Estado atual

```text
ETAPA 1   ✅ Fundação estrutural
ETAPA 2   ✅ Matching e estado operacional
ETAPA 3   ✅ Fontes GPS e hints
ETAPA 4   ✅ Checkpoint / estabilização
ETAPA 5   🟡 ETA V2 para ônibus/BRT
            ↑
         ESTADO ATUAL

ETAPA 6   ⬜ Movimento contínuo
ETAPA 7   ⬜ Trem
ETAPA 8   ⬜ Metrô
ETAPA 9   ⬜ Tempo real unificado dos 4 modais
ETAPA 10  ⬜ ETA unificado
ETAPA 11+ ⬜ Histórico, inteligência, ML e evolução do produto
```

---

## Princípio de evolução

O NoPonto não depende de um único modelo ou algoritmo para ser útil.

A estratégia é evoluir em camadas:

```text
funcionar
→ medir
→ comparar
→ melhorar
→ automatizar
→ prever
```

Cada nova etapa deve entrar apenas quando a anterior já produz dados confiáveis o suficiente para justificar a próxima.
