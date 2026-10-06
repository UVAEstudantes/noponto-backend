# Plano experimental — GPS e matching

**Finalidade:** transformar cenários existentes em avaliação quantitativa reproduzível, sem usar produção como laboratório.

## Cobertura atual

| Cenário | Evidência/teste | Resultado nesta etapa | Lacuna |
|---|---|---|---|
| coordenada inválida | GpsLeituraValidatorTests | aprovado no filtro | ampliar limites geográficos |
| timestamp ausente/epoch/futuro | GpsLeituraValidatorTests | aprovado | relógio/provider real |
| duplicata/fora de ordem | EstadoCausal/CAS | parte não executada; fixture com falhas | Redis isolado e E2E |
| salto geográfico | correção temporal/validator | unidades aprovadas | limiares representativos |
| bearing/cardinais/wrap | ArcGisParadasReconciliadorUnitTests | existente, não executado aqui | relatório consolidado |
| matching ambíguo/sentidos próximos | CompetidorDirecional/Matching diferencial | existentes | PostGIS isolado/corpus |
| versão pinada/circularidade | GpsPinnedVersion/CircularNextOccurrence | existentes | PostGIS isolado |
| paradas repetidas/próxima ocorrência | OcorrenciaParadaTests | bloqueado por PostGIS | ambiente descartável |
| CAS/TTL/expiração | PosicaoVeiculoCache/Redis tests | não executado | Redis descartável |
| viagem operacional | ViagemOperacionalRegra | filtro aprovado | integração DB/outbox |
| SignalR/reconexão | Hub/frontend | cobertura parcial | E2E e reinscrição |

## Ground truth

Construir corpus autorizado e minimizado com observação GPS, linha/sentido/padrão verdadeiros e timestamps. A verdade pode vir de operação rotulada, viagens acompanhadas com protocolo ou datasets oficiais compatíveis; nunca do próprio matcher avaliado. Registrar anotador, evidência, incerteza e versão estrutural. Casos devem incluir corredores paralelos, terminais, bifurcações, circularidade, GPS ruim, perda de sinal e horários variados.

## Amostragem e procedimento

1. congelar commit, configuração e snapshot V2 por hash;
2. separar desenvolvimento e teste por viagens/dias para evitar duplicação;
3. estratificar por modal, linha, sentido, trecho e ambiguidade;
4. executar baseline e candidato sobre as mesmas entradas, sem consulta futura;
5. registrar aceito, rejeitado e motivo;
6. revisar divergências cegamente quando possível;
7. repetir desempenho em máquina/estado declarados, sem produção.

## Métricas

- acurácia de linha, padrão e sentido;
- cobertura = matches emitidos / observações elegíveis;
- precisão condicional entre matches emitidos;
- matriz de confusão e rejeições por motivo;
- erro lateral em metros contra segmento verdadeiro;
- erro longitudinal ao longo do padrão;
- acerto da próxima ocorrência/volta;
- latência p50/p90/p95/p99, tamanho de lote e round-trips;
- estabilidade: regressões, troca indevida de padrão e duplicação de passagem.

Não somar cobertura baixa e alta precisão como “ótimo” sem expor ambos.

## Tabelas vazias

| Corpus/hash | N elegível | cobertura | padrão correto | sentido correto | erro lateral P50/P95 | latência P50/P95 | rejeições |
|---|---:|---:|---:|---:|---:|---:|---:|
| `[PENDENTE]` |  |  |  |  |  |  |  |

| Caso ambíguo | Esperado | Obtido | Confiança | Motivo | Revisão |
|---|---|---|---|---|---|
| `[PENDENTE]` |  |  |  |  |  |

## Critério de prontidão

Ambiente isolado, corpus versionado, anotação auditável, métricas implementadas e limites aprovados antes da execução. Relacionados: [matching](../03-funcionalidades/03-matching-v2-e-viagem-operacional.md) e [testes](02-inventario-de-testes-e-execucoes.md).
