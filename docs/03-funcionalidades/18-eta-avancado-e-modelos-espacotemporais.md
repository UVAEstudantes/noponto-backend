# ETA avançado e modelos espaço-temporais

## Finalidade e estado

Este documento registra possibilidades de pesquisa, não uma arquitetura aprovada. Hoje não há feature operacional de interação entre veículos, GNN ou DCRNN no ETA. O próximo avanço só é justificável após dataset V2 correto e baseline comparável.

## Evolução incremental

1. baseline longitudinal V0;
2. baseline tabular com histórico causal e estado operacional;
3. gradient boosting supervisionado com validação temporal;
4. modelo sequencial por veículo/viagem, se a densidade temporal permitir;
5. modelo espaço-temporal ou grafo, somente se cobertura e custo justificarem.

Gradient boosting é candidato natural para dados tabulares heterogêneos, missing values e inferência barata, mas não há resultado V2 demonstrando superioridade. RNN/TCN/Transformers podem representar sequência, porém exigem janelas consistentes, máscaras e controle de ausência. Complexidade não garante menor erro.

## Interação entre veículos

Telemetria contém viagem, linha, sentido, padrão, fração, timestamp e velocidade, permitindo em tese alinhar veículos no mesmo trecho. Features causais possíveis:

- distância longitudinal ao veículo à frente/atrás;
- headway temporal e espacial;
- velocidade/estado coletivo em janela passada;
- densidade de veículos por segmento;
- agrupamento e propagação de desaceleração;
- relações entre linhas que compartilham geometria.

Elas ainda não são produzidas pelo preditor. Desafios: relógios diferentes, observações desaparecidas, matching incorreto, padrões parcialmente compartilhados, sentidos opostos, voltas, mudança de viagem e relações que mudam a cada instante. Join por código de linha sem geometria/sentido criaria vizinhos falsos.

## Formulação de grafo

Um grafo viário estático poderia usar nós como segmentos direcionados ou ocorrências de parada e arestas como continuidade/topologia compartilhada. Sinais temporais por nó incluiriam velocidades agregadas, densidade e atraso, todos calculados apenas até `t₀`. Um grafo dinâmico de veículos é diferente: nós são veículos/viagens e arestas representam proximidade longitudinal naquele instante.

Essa distinção importa: a malha muda lentamente por versão estrutural; relações entre veículos mudam em segundos. Misturar ambos sem versionamento e máscaras de tempo cria leakage e inconsistência dimensional.

## DCRNN como referência

DCRNN combina convolução por difusão em grafo dirigido com recorrência temporal. Em termos conceituais, a difusão modela influência ao longo do fluxo e a recorrência modela evolução da série. Para o NoPonto, nós poderiam representar segmentos/paradas e a direção da rota definir transições; sinais seriam velocidade/tempo de percurso por janela.

Requisitos ainda não satisfeitos de forma demonstrada:

- grafo direcionado versionado e estável;
- séries densas/sincronizadas por nó;
- tratamento de missing/mudança estrutural;
- ground truth e horizonte claramente definidos;
- volume suficiente por trecho e período;
- comparação com baselines tabulares/sequenciais;
- infraestrutura de treino e inferência adequada.

DCRNN é relevante academicamente, mas foi concebido para sensores fixos densos; telemetria móvel, irregular e com identidade operacional incerta pode favorecer agregação por segmento ou modelos mais simples. Sua superioridade precisa de experimento, não pode ser presumida.

## Múltiplos veículos e multi-horizon

Um modelo pode prever ETA para várias ocorrências/horizontes, mas cada saída precisa de máscara de ocorrência alcançável e label correspondente. Outra estratégia prevê velocidade/tempo por segmento e soma distribuições; erros correlacionados e dwell tornam a soma não trivial. Previsão probabilística (quantis) pode expressar incerteza melhor que um número único, desde que calibrada por horizonte e cobertura.

## Viabilidade computacional

Treinamento deve ocorrer offline, fora do servidor produtivo restrito. Inferência online precisa limitar CPU, memória, bundle e latência por ciclo GPS. Alternativas:

- modelo tabular por evento, barato e fácil de fallback;
- inferência em lote dos veículos do ciclo;
- features agregadas pré-computadas/cacheadas;
- atualização menos frequente que o GPS quando contexto muda devagar;
- modelo pesado offline gerando artefato leve/distilado;
- serviço separado somente após medir custo e disponibilidade.

Não há medição que permita estimar consumo de GNN/DCRNN. Frequência, tamanho do grafo, hidden state, batch e framework alteram drasticamente o custo.

## Relação com ferrovia

Métodos temporais, avaliação, quantis e versionamento podem ser compartilhados. Dados não: ferrovia usa grade e ETA de provider, rodoviário usa GPS/matching. Um único modelo multimodal só seria defensável com targets e incertezas harmonizados e ganho demonstrado; hoje seria prematuro.

## Segurança, privacidade e minimização

Interação entre veículos não requer identificar passageiros. Datasets devem usar identificadores operacionais mínimos, retenção definida, acesso controlado e agregação quando possível. Futuras rotinas pessoais/favoritos não devem ser unidas à telemetria de veículos sem finalidade, base legal e minimização específicas.

## Limitações, testes e pendências

Antes de modelos avançados: adaptar exportador V2, persistir qualidade do ground truth, medir cobertura, estabelecer baseline, produzir holdout temporal e observar drift. Evidências atuais: `TelemetriasVeiculoMl`, estrutura V2, viagens/passagens e scripts offline de posição; nenhuma classe de inferência avançada foi localizada.

Não existem testes de GNN/DCRNN ou interação de veículos no runtime. Testes futuros precisam cobrir construção causal do grafo, máscaras de missing, isolamento temporal, mudança de versão estrutural, custo de inferência e comparação no mesmo holdout do baseline.

## Referências

`TelemetriaMl.cs`, `telemetriaVeiculoMl.cs`, `ViagemOperacional.cs`, `EtaV2Shadow.cs`; no repositório ML, `posicao_corrigida.py`, `validacao_temporal.py` e respectivos testes. DCRNN/GNN aparecem aqui como referências conceituais de pesquisa, não dependências do projeto.

