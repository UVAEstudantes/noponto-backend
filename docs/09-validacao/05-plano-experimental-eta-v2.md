# Plano experimental — ETA V2

**Finalidade:** preparar uma avaliação justa mantendo ETA V2 desligado.

## Testes e estado

`EtaV2FoundationTests` cobre elegibilidade, baseline, distâncias/velocidades/clamps, canary e estados básicos. `EtaV2HardeningTests` cobre channel, batch, retry, shutdown e manutenção. `EtaV2PostgresIntegrationTests` exige `ETA_V2_TEST_CONNECTION` local e algumas rotinas opt-in.

No filtro local, Hardening participou de 140 casos: um teste (`Worker_FlushesAtMaximumDelay`) falhou e passou na repetição isolada. O resultado indica provável sensibilidade a scheduling e precisa ser estabilizado antes de usar a suíte como evidência. PostgreSQL não foi executado. Flags produtivas permaneceram OFF.

## Dataset mínimo

Cada exemplo deve conter viagem, PadraoVersao, ocorrência-alvo, volta, instante da previsão, instante da passagem, método/qualidade do ground truth, horizonte, features causais, provider, preditor/versão e estado. Excluir ou estratificar labels interpolados; nunca usar observações futuras nas features.

## Protocolo

1. manifesto de schema, commits, flags, intervalo, timezone e hashes;
2. critérios de elegibilidade antes de olhar erro;
3. split temporal, preferencialmente com separação de viagens/dias;
4. baseline longitudinal congelado;
5. modelo candidato treinado apenas no treino;
6. tuning em validação e uma avaliação final no teste;
7. comparar no mesmo subconjunto elegível e por horizontes;
8. reportar cobertura, erro, viés, falhas e custo;
9. shadow/canary somente após aprovação operacional futura.

## Métricas

MAE, RMSE, MedAE, viés, P90/P95 absoluto, cobertura, erro por horizonte/linha/faixa horária e taxa realizada/expirada/invalidada. RMSE evidencia outliers; MAE/MedAE auxiliam interpretação. Nenhuma métrica isolada prova utilidade ao passageiro.

## Tabelas vazias

| Preditor/versão | Split/hash | N elegível | cobertura | MAE | RMSE | MedAE | P90 | viés |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| baseline `[PENDENTE]` |  |  |  |  |  |  |  |  |
| candidato `[PENDENTE]` |  |  |  |  |  |  |  |  |

| Horizonte | N | cobertura | MAE baseline | MAE candidato | diferença | IC/teste aprovado |
|---|---:|---:|---:|---:|---:|---|
| `[PENDENTE]` |  |  |  |  |  |  |

## Bloqueios

Qualidade do ground truth não persistida explicitamente; dataset ainda não consolidado; integração PostgreSQL sem ambiente isolado; teste temporal instável; shadow/canary OFF. Relacionados: [ETA V2](../03-funcionalidades/16-eta-v2-baseline-shadow-e-canary.md) e [datasets](../03-funcionalidades/17-datasets-treinamento-e-validacao.md).
