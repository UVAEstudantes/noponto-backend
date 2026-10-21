# Validação da preparação — 21/10/2026

Base: branch `audit/gps-bus-brt-pipeline`, HEAD inicial `63da9a9d3cc6fa48e476c0e2eddb13784d75bf40`, status Git inicialmente vazio. SDK local observado: **9.0.306**. Nenhum recurso Docker foi iniciado para validar esta entrega. Não há resultados de desempenho.

## Verificações executadas

1. Leitura de `AGENTS.md`, `NoPonto/HISTORICO.md`, instruções ancestrais/diretórios (nenhuma outra encontrada), fixtures, código/configuração e documentação citada no README. `git branch --show-current`, `git rev-parse HEAD`, `git status --short`, `git ls-files` e buscas `rg` para inventário. Parte de `NoPonto/lab-output/d8-run-*` recusou leitura; esses artefatos ignorados não são dependências do kit.
2. Sintaxe via `System.Management.Automation.Language.Parser.ParseFile` para Lab.ps1 e Report.ps1: zero erros. O executável alias PowerShell 7 foi inicialmente bloqueado pelo sandbox; repetido com autorização do ambiente, aprovado. Não foi contornada a restrição.
3. Compilações Release com dependências já restauradas; nenhum restore, startup de API ou teste C# executado. Primeira compilação direta: **0 erros, 6 avisos, 38,15 s**. Avisos: os cinco preexistentes (CS8981 ×2, CS7022, xUnit2031 ×2), mais Grpc.Tools sobre saída esperada no diretório intermediário novo. Última compilação incremental do fonte final: **0 erros, 5 avisos preexistentes, 30,61 s**. Não houve alteração do gerador/protobuf para esconder o aviso de primeiro build.

Comando da primeira e da última compilação direta (caminho específico de verificação, não iniciar cenários nele):

```powershell
dotnet build NoPonto/NoPonto.csproj -c Release --no-restore -p:CustomAfterMicrosoftCommonTargets=D:/repositorio_github/NoPonto/noponto-backend/tools/performance/PerformanceLab.targets -p:OutputPath=D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/perf-kit-validation-20261021/build/ -p:IntermediateOutputPath=D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/perf-kit-validation-20261021/obj/ --verbosity quiet
```

4. Verificação do próprio `Prepare`, sem Docker, em dois diretórios novos:

```powershell
pwsh -NoProfile -File tools/performance/Lab.ps1 -Action Prepare -LabDirectory D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/perf-643dfc778bde46b9b84f2c7e8b884127
pwsh -NoProfile -File tools/performance/Lab.ps1 -Action Prepare -LabDirectory D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/perf-69835a1e4f94462aa7845eea1c342c78
```

Primeira tentativa: compilação **aprovada** (0 erros/6 avisos, 31,14 s), mas launcher saiu com erro ao interpretar o retorno de tarefas de cópia assíncrona como parte do exit code. Corrigida a supressão desses valores auxiliares. Segunda tentativa: `Prepare` **aprovado**, exit 0, compilação 0 erros/6 avisos, 22,18 s. Ambos retêm stdout/stderr em seus diretórios; nenhum container/rede/volume foi criado. Essas árvores de validação não devem ser usadas para executar os cenários após o commit/alterações finais; `Prepare` manual criará outro UUID com a proveniência atual.

5. Checks offline: primeira versão **16 aprovados**; depois da regressão do launcher, **19 aprovados** em duas rodadas; versão final com casos operacionais/consistência **22 aprovados**, exit 0. Só executam `dotnet --version`, processamento de arquivos sintéticos e rejeições de caminhos anteriores a qualquer chamada Docker.

```powershell
pwsh -NoProfile -File tools/performance/Verify.ps1
```

| Requirement | Evidence |
|---|---|
| Sintaxe dos três scripts | `syntax/Lab.ps1`, `syntax/Report.ps1`, `syntax/Verify.ps1` |
| Exit code e logs corretos | `child process returns scalar exit code without async output contamination`, `child stdout retained separately`, `bounded native argument dispatch` |
| Caminho relativo/fora da raiz recusado | Três `unsafe path rejected` |
| Relatório completo reconhecido | `complete evidence accepted` |
| Percentis corretos e throughput ponderado pelo tempo | `nearest-rank percentiles`, `throughput uses total time` |
| Dados ausentes não viram zero | `missing REST is unavailable, never zero`, `absent container metrics unavailable` |
| Cenário operacional sem contadores GPS inventados | `operational-only report accepts the expected sample count`, `operational-only report does not invent GPS counters` |
| Definição de cenário deve coincidir com o manifesto | `mismatched scenario definition rejected` |
| Sem testes, ciclos faltando, interrompido e sem TRX não aprovam | Quatro `incomplete evidence rejected` |
| Quantidade operacional inesperada recusa completude e agrupamento preserva flag/etapa | `unexpected operations rejected and flag grouping measured` |

Artefatos finais desses checks: `NoPonto/lab-output/perf-kit-verification-036b56ce69854802aac11bf311716d76/`. São **sintéticos**, não relatórios de desempenho. Os checks não comprovam guards/cleanup contra Docker real.

6. Descoberta filtrada VSTest, em duas rodadas, sem construção da fixture ou execução do caso; o único teste listado foi `NoPonto.Tests.GpsPipelineDisposableIntegrationTests.PerformanceLab_Manual`, exit 0. Comando:

```powershell
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore -p:OutputPath=D:/repositorio_github/NoPonto/noponto-backend/NoPonto/lab-output/perf-kit-validation-20261021/build/ --list-tests --filter FullyQualifiedName=NoPonto.Tests.GpsPipelineDisposableIntegrationTests.PerformanceLab_Manual --verbosity quiet
```

7. `git diff --check`, conferência do escopo final/staging e confirmação de `MudancaOperacionalHabilitada=false` em `GpsPoolingOptions.cs`. Apenas `tools/performance/*` e acréscimo ao histórico são destinados ao commit. Arquivos runtime/configuração/Compose/CI e documentação anterior preservados.

## Explicitamente não executado

- `Start`, `Run`, `Interrupt` e `Stop` com Docker real; portanto o isolamento/teardown implementado exige sua primeira validação manual. Nenhuma prova de preservação de containers reais é alegada nesta etapa.
- Os cenários **A, B, C, D**; nenhum teste de carga, benchmark, matriz integrada, simulação prolongada ou testemunho NaturalExpiry de 60 s.
- Migrations/seed/consultas em PostgreSQL, Redis ou PostGIS, mesmo descartáveis, durante esta preparação; só compilação e descoberta.
- Inicialização completa da API ou background services; GPS externo, homeserver, produção, cloud/serviço pago.
- Deploy, CI/CD, push, merge ou PR. O único registro Git autorizado é o commit local específico do kit.

Os critérios no README são propostas para os relatórios futuros. Não há medições reais de p50/p95/p99, throughput, CPU/RAM, backlog ou recuperação disponíveis nesta entrega.
