# NoPonto — Diário ETA/ML histórico: comparação para o TCC

**Abertura:** 17/10/2026  
**Documento diretor:** `PLANO_ETA_ML_SHADOW_NOPONTO_2026-10-17.md`  
**Escopo (redefinido em 17/10):** **C0 ground truth real → C1 ExtraTrees treinado offline com dados reais → C2 comparação pareada para o TCC**. S0 captura Shadow/S1 ML Shadow são bônus condicionais, não dependências do TCC.

## Status por gate (prioridade atual)

| Gate | Estado em 17/10 | Prova necessária | Próxima ação |
|---|---|---|---|
| **C0 — ground truth auditado** | **NÃO DEMONSTRADO** no último bundle; 758 candidatas preliminares não são labels certificados. | Coorte real, viagens completas, chegada única, identidade/relógio e motivos de exclusão. | Auditoria Codex D0 + amostra verificável D1. |
| **C1 — ExtraTrees real offline** | **NÃO DEMONSTRADO**; código existe mas não foi localizado artefato real certificado. | Dataset real auditado, separação temporal por viagem, fit TRAIN, manifesto/hash e TEST intocado. | Decisão A/B/C D2; treino offline D4 se cabível. |
| **C2 — comparação TCC** | **PENDENTE**, objetivo principal. | ETA fixo reconstruído, ML no mesmo TEST, tabela pareada, métricas/gráficos, denominadores e scripts. | D5–D7 se C0/C1 permitirem. |
| **S0 — captura Shadow** | **IMPLEMENTADO NO CÓDIGO, NÃO COMPROVADO PRODUTIVO**. | Janela autorizada e recursos, JSONL v2 privado, logs/perdas e nenhum efeito no ETA público. | Só em paralelo, se não atrapalhar C0–C2. |
| **S1 — ML em Shadow produção** | **NÃO DEMONSTRADO**. | Modelo real qualificado, `/ready` com digest, benchmark servidor, isolamento e autorização. | Bônus, após critérios produtivos. |

## Histórico imutável de decisões

### 2026-10-17 — D-001 — Abrir frente com caminho crítico

- **Contexto:** usuário solicita retomar ETA/ML após numerosas investigações, com limite de uma semana para engenharia e sem aguardar semanas/meses de coleta antes de voltar a desenvolver.
- **Base:** acervo `noponto-docs.rar`, documentação consolidada até 16/10; leitura dos relatórios 4C.9, integração real Shadow e diagnóstico causal.
- **Decisão:** não reconstruir pipeline ou Shadow existentes. Separar P0/P1/P2 e desenvolver captura/dados/serviço em paralelo. Modelo qualificado exige dados reais auditados; não chamar sintético, legado ou candidato bruto de modelo qualificado.
- **Achados conhecidos:** integração atual implementada OFF; bundle conhecido 775 GPS/758 candidatas preliminares/16 viagens/zero certificadas; 14 dias e 10k observações ainda exigidos; GPS 4C.9 segue causalmente inconclusivo; servidor limitado teve histórico de exit137 compatível com pressão de memória.
- **Não executado:** nenhuma leitura de produção, mutação de repositório, teste runtime, treinamento, deploy, alteração de flags ou criação de modelo real.
- **Próxima evidência decisiva:** auditoria Codex read-only do estado vigente + inventário de coorte/procedência.
- **Status:** plano aberto; G0...G7 pendentes conforme tabela. **Não** declarar Shadow ativo.

### 2026-10-17 — D-002 — Repriorização explícita para TCC

- **Decisão do usuário:** a entrega desejada em primeiro lugar é uma comparação **entre ETA fixo e ML histórico** que possa ser documentada no TCC. Shadow produtivo e gráficos de previsões em tempo real são desejáveis, **mas não obrigatórios** para o resultado científico.
- **Decisão técnica:** mudar caminho crítico para C0 → C1 → C2, deixando S0/S1 como trilha paralela condicionada. Comparação offline em `tGPS` é válida como experimento, desde que declare que o ETA operacional foi **reconstruído**, não necessariamente mostrado ao passageiro.
- **Advertência:** estudo exploratório pequeno só com dados reais auditados, viagens distintas em TRAIN/TEST, sem fabricar rótulos ou dar a um modelo piloto recibo de qualificação de produção. Se não existirem labels, nenhuma métrica real pode ser produzida ainda.
- **Próxima ação:** solicitar auditoria Codex read-only do caminho real até `comparison.csv`; verificar existência de coortes/labels auditados e código de reconstrução operacional antes de tocar no Shadow.
- **Não executado:** nada em produção, nenhum treino, nenhum teste runtime; documentos e prioridades apenas.

## Template de registro diário

Copiar para cada experimento ou decisão relevante:

```markdown
### AAAA-MM-DD — D-XXX — Título
- **Objetivo/gate:**
- **Ambiente/branch/HEAD:**
- **Hipótese falsificável:**
- **Ação executada (e autorização quando produtiva):**
- **Teste/comando e isolamento:**
- **Resultado (contagens, tempos, motivo, perdas):**
- **Evidência/artefato + SHA256/caminho privado (sem segredos):**
- **O que fica comprovado / o que permanece NÃO VERIFICADO:**
- **Decisão GO/NO-GO:**
- **Próximo teste mínimo / responsável / revisão:**
```

## Registro curto de bloqueios (máximo 3 ativos)

| ID | Bloqueio | Menor evidência que muda decisão | Limite de investigação |
|---|---|---|---|
| B-01 | Dados/labels/identidade e corte pós-GTFS-RT ainda não auditados. | Inventário de coortes distintas + 1 viagem provada integralmente + dry-run real e motivos de rejeição. | Decisão A/B até D2. |
| B-02 | Sem ExtraTrees real auditado para TEST independente. | Dataset/split reais + inferência ExtraTrees offline e `qualify_real --dry-run` quando aplicável. | Revisão D4; modelo exploratório é offline, sem promoção. |
| B-03 | ETA operacional comparável ainda não reconstruído/pareado na mesma coorte. | Versão/política de `EventosParadaService`, relógio `tGPS` e TEST comum gerando primeira linha pareada. | D3–D5; Shadow não corrige este problema. |

**GPS 4C.9:** subinvestigação vinculada a B-01; investigar apenas um caso delimitado quando a evidência existir. Não substituir B-01 por uma auditoria ampla de todo o GPS.

## Inventário de artefatos a preservar

- Relatório Codex D0 (somente leitura, estados com arquivo:linha).
- Resultado dos testes de regressão e eventual fixture descartável.
- Manifesto de coortes, perfil, versão e política de seleção; hashes de exportação/splits.
- Tabela `comparison.csv` e `metrics.json` do TEST, script de replay e gráficos com denominadores; sem PII na versão pública.
- Arquivos JSONL v2 privados e seus sumários de perdas, **se Shadow for utilizado**; sem dados pessoais na versão pública.
- Manifestos/digests de modelo e baselines treinados, testes `/ready` e benchmark real.
- Relatório pareado para TCC com contagens, relógios e decisões explícitas de exclusão.
