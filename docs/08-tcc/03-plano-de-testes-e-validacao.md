# Plano de testes e validação

**Finalidade:** definir avaliação reproduzível sem executar produção ou fabricar resultados. **Baseline:** 2026-10-06.

## Protocolo comum

Registrar hipótese, commit/imagem, configuração não sensível, ambiente, dataset/janela, procedimento, seed, métricas, exclusões, resultado bruto, análise e limitações. Separar unidade, integração controlada, dispositivo e observação produtiva. Aprovar metas antes de observar resultados.

| Eixo | Casos/procedimento | Métricas/aceite a definir |
|---|---|---|
| estrutura | PK/FK/unique/check, versões, ocorrências repetidas, geometrias/SRID e import idempotente | violações, cobertura, hash/reprodutibilidade |
| GPS | válido/inválido, duplicado, atraso, salto, circularidade, bearing, match/sem match | precisão/aceite, erro espacial, duração p50/p95/p99 |
| causal/viagem | concorrência, timestamp igual, terminal, retorno e nova viagem | regressões, passagens idempotentes, estados corretos |
| ferrovia | calendário, binding único/ambíguo, delay, freshness, provider down | cobertura, binding correto, idade, erro temporal/espacial quando referência existir |
| ETA V2 | qualidade do ground truth, baseline, split temporal por viagem/data | MAE, RMSE, MedAE, viés, P90/P95 e cobertura |
| frontend | busca, sentido, mapa, parada, push, polling, reconnect, vazio/offline/stale | sucesso da tarefa, consistência e interpretação |
| infraestrutura | health, latência, CPU/RAM/disco, restart controlado | disponibilidade/recuperação e orçamento aprovado |

ETA deve evitar leakage: treino/validação/teste separados temporalmente e, quando necessário, por viagem/linha. Comparar modelo apenas ao mesmo conjunto elegível do baseline; informar cobertura junto ao erro. ETA V2 está OFF e não possui resultado a reportar.

## Ambientes e segurança

Integrações destrutivas/falhas devem rodar em banco/Redis descartáveis, nunca na produção. Teste em dispositivo registra SO/app/build. Observação produtiva é leve e sanitizada. Backup/restore exige ambiente isolado e autorização futura.

## Análise

Relatar distribuição, tamanho da amostra e intervalos/incerteza adequados; não somente média. Casos rejeitados fazem parte do resultado. Ameaças: provider variável, relógio, seleção de linhas, ground truth interpolado, hardware e cache aquecido.

**Pendências:** escopo A/B/C, metas, datasets, execução consolidada, autorização para participantes. Referências: [rastreabilidade](02-rastreabilidade-de-objetivos-e-requisitos.md), [ETA](../03-funcionalidades/17-datasets-treinamento-e-validacao.md) e [infra](../05-infraestrutura/05-logs-metricas-e-observabilidade.md).
