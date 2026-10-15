# Laboratório ETA/ML para TCC — somente sintético

O executor conecta o ETA físico C#, o adapter Shadow existente e a FastAPI real
em loopback. Reutiliza `synthetic.py`, `pipeline.py`, ExtraTrees e o baseline
histórico do repositório ML. Não registra DI, não inicia a API operacional e não
altera o caminho público. Modelos sintéticos continuam proibidos pelo worker
Shadow normal; seus testes verificam essa recusa.

Execução no PowerShell, a partir do backend, com as dependências dos projetos
já disponíveis. Escolha sempre um diretório de saída **novo**:

```powershell
python tools/eta_ml/tcc/experiment.py `
  --ml-root D:/repositorio_github/NoPonto/ml `
  --shadow-root D:/repositorio_github/NoPonto/noponto-backend-eta-shadow-gps-current `
  --output D:/repositorio_github/NoPonto/tcc-shadow-nova-execucao
```

O comando gera as fixtures, treina duas vezes, compara todas as previsões,
abre uma FastAPI efêmera em `127.0.0.1`, faz inferência HTTP em lotes de 200 e
executa 56 testes C# selecionados. O `Lab.targets` é importado **somente** nesse
comando MSBuild explícito, vinculando os fontes Shadow da outra worktree sem
copiá-los ou alterar projetos. O output .NET fica no diretório do experimento.
Nenhum PostgreSQL/Redis é iniciado ou utilizado; o entrypoint operacional não
é executado. Não há mudança de flag persistida.

Testes adicionais (os paths não precisam apontar para dados operacionais):

```powershell
$env:PYTHONPATH='D:/repositorio_github/NoPonto/ml/tools/eta_ml;D:/repositorio_github/NoPonto/ml'
python -m unittest discover -s tools/eta_ml/tcc -p test_experiment.py -v
Push-Location ../ml
python -m unittest tests.test_eta_history_service tests.test_qualify_real tests.test_offline_cycle tests.test_inference_contract tests.test_validation_policy tests.test_migration_3c tools.eta_ml.test_pipeline
Pop-Location
dotnet test ../noponto-backend-eta-shadow-gps-current/NoPonto/NoPonto.csproj --no-restore --filter FullyQualifiedName~HistoricalEtaGtfsPollingTests
```

## Metodologia e artefatos

- `experiment.json`: seed, versões Python/scikit-learn/numpy/scipy, revisões,
  paths sujos, hashes dos fontes, dados, modelo e resultados; gates reais não
  satisfeitos. Não é um certificado de auditoria de dados reais.
- `model/` e `repeat-model/`: artefatos do treinador existente, manifests e
  métricas originais. Somente pickle gerado pelo próprio laboratório é carregado.
- `predictions.jsonl`: observação, viagem, volta, modal, linha, sentido, padrão,
  versão, ocorrência, parada, timestamp GPS, instante causal da previsão,
  registro em relógio real, distância, horizonte físico, três previsões,
  digest do ExtraTrees, versão dos baselines e duração HTTP do lote.
- `synthetic-labels.jsonl`: identidade completa separada, chegada e label
  **sintéticos**. Associação por igualdade integral; não há associação por
  proximidade temporal. Alterar qualquer identidade ou repetir previsão/label
  é recusado pelo comparador.
- `comparison.json`: TRAIN/VALIDATION/TEST separados; MAE, MedAE, P90, RMSE,
  segundos/minutos, percentuais em 60/120s, cobertura e contagens. Amostra comum
  distinta da cobertura individual. Recortes por linha/modal/horizonte exigem
  pelo menos 30 observações e três viagens; o horizonte usa a previsão física,
  nunca a chegada futura. Divergência entre previsões fica em seção separada
  dos erros contra labels.
- `csharp-bridge.json`: prova integrada de uma observação com `PhysicalEta.Apply`,
  `HistoricalEtaAdapter.Capture/Payload` e ExtraTrees via HTTP. Inclui também o
  baseline histórico calculado pelo bundle TRAIN, na mesma ocorrência.
- `test-results/tcc.trx`, `dotnet.log`, `fastapi.log`: evidências de execução.

O gerador fixo produz 1.920 observações/240 viagens/5 dias/4 linhas. TRAIN:
08–10/10 (144 viagens), VALIDATION: 11/10 (48), TEST: 12/10 (48). O loader
existente exige fronteiras completas, viagens disjuntas, causalidade do label,
hash do dataset, allowlist e ausência de duplicatas. Imputação, categorias,
medianas e fallback histórico são ajustados somente em TRAIN. ExtraTrees:
64 árvores, profundidade 18, folhas limitadas e seed 20261007, um job.

A matriz tem 16 features: modal, linha ID/código, sentido, padrão, versão,
ocorrência, parada, topologia, frações GPS/destino, distância longitudinal,
velocidade instantânea/média causal, hora e dia da semana. IDs de viagem,
observação e timestamps/labels de chegada não entram na matriz.

O comparador Python aplica a fórmula física distância/velocidade causal e a
política de instantânea válida 1–160 km/h; não usa fallback TRAIN. As fixtures
assumem matching causal válido: a prova de predecessor, regressão, idade e
estrutura é exercitada separadamente no código C# real. Não trata a mera
presença de uma linha no CSV como aprovação operacional do matching.

## Limites e segurança

A entrada é exclusivamente o gerador sintético existente, não há opção de
qualificar automaticamente um CSV real. O pipeline limita 200 mil linhas;
o gerador desta demonstração é fixo em 1.920. HTTP: no máximo 200 itens/lote,
10s/request, prontidão limitada, servidor encerrado em `finally`. Testes .NET:
240s. Uma execução por diretório novo, sem retenção em daemon ou fila ilimitada.
Os artefatos permanecem locais para revisão; não existe expurgo automático ou
coleta contínua. Reserve espaço para dois modelos e o output .NET. Não exponha
o servidor/modelo demonstrativo fora do loopback.

As distâncias do CSV e do bridge são **fixtures**, sem resolução geométrica
real nesta execução. O gerador não simula um sampling produtivo e calcula
labels independentes por observação; não demonstra consistência de uma chegada
física única por viagem/ocorrência. As métricas medem somente esta demonstração.
Não representam precisão no Rio nem aprovação de dados GTFS-RT.

## Captura real ainda OFF: contrato e pendências

O Shadow atual captura **a próxima ocorrência operacional**. O físico atual
também prevê outras paradas consultadas; ligar os dois sem conferir o alvo
compararia destinos diferentes. Além disso, os resolvers geométricos existentes
usam substring versus diferença de prefixos geográficos: é necessário consumir
a mesma distância longitudinal resolvida e validada antes de homologar uma
comparação real. Nenhuma dessas regras foi alterada aqui.

O sink histórico em memória é limitado/temporário, não oferece retenção após
reinício. A outra worktree já possui exportador JSONL opcional e limitado,
OFF por padrão, e `tools/eta_ml/evaluate_shadow.py`. Seu campo `HistoricalEta`
significa a saída do modelo histórico (ExtraTrees), **não** o baseline de
medianas. Não há captura produtiva simultânea dos três métodos nesta entrega.

Para dados reais, reutilizar esse exportador/evaluator, com diretório privado
explicitamente configurado e limites, manifest/hash e auditoria externa de cada
chegada. Exigir mesma observação, viagem confiável, volta, modal, linha, sentido,
padrão, versão, ocorrência, parada e timestamp GPS; chegada posterior à previsão,
previsão válida/não expirada e ausência de duplicatas/conflitos. Excluir
identidade ambígua, regressão, GPS antigo, destino incompatível, chegada não
auditada e regimes operacionais não qualificados. Não usar PassagemParada como
label confiável por sua existência. Uma mesma chegada auditada deve ser única
e consistente por execução/volta/ocorrência, com proveniência verificável.

Antes de captura real: alinhar alvo e distância nos adapters sem alterar o ETA
público; incluir o baseline TRAIN no registro comparativo offline; disponibilizar
dataset real auditado e modelo qualificado pelos gates existentes; homologar
fila, falhas, exportação/retenção e cobertura no regime corrigido de passagens.
`PhysicalEta`, ML/Shadow e demais flags mantêm seus defaults atuais OFF.
