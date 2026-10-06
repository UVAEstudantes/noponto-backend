# Ferrovia — falhas, limites e validação

## Matriz de falhas

| Situação | Comportamento observado | Risco residual |
|---|---|---|
| 404 `NO_SERVICE` | cooldown específico | pode representar resposta contextual, não fim definitivo do serviço |
| 429 | pausa por `Retry-After` ou backoff | orçamento é por processo |
| timeout/5xx/payload inválido | falha, backoff e circuit breaker | lacuna temporária de evidência |
| resposta com linhas mistas | somente linha-alvo alimenta scanner | external id incorreto elimina hit válido |
| candidato temporal empatado | binding ambíguo, sem escolha forçada | trem pode não aparecer |
| padrão conflituoso/sem geometria | sem posição espacial | grade pode existir sem mapa |
| restart | tracker/binding/estimativas perdidos | bootstrap e desaparecimento temporário |
| evidência stale | fallback limitado e remoção | ausência visual deliberada |
| múltiplas instâncias | limites e estado não compartilhados | chamadas duplicadas e snapshots divergentes |
| feriado/alteração extraordinária | calendário comum é usado | schedule-only potencialmente incorreto |

## Casos-limite cobertos pelo desenho

- travessia de meia-noite por `dayOffset` e consulta à data de serviço anterior;
- corridas simultâneas com IDs distintos;
- short starts e padrões subset-compatible;
- parada repetida/pares ambíguos rejeitados para projeção;
- reversão somente no terminal e após confirmação crescente;
- distância monotônica e clamp na extensão da geometria;
- off-target incapaz de criar/matar pursuit da linha-alvo;
- schedule-only expirado após chegada mais tolerância;
- ETA crescente ou reset registrado como diagnóstico sem teleporte automático.

## Testes existentes

Há suítes para configuração, cliente/normalização, tracker, scheduler/scanner, gates de orçamento, motor e estimador, controller, importação estrutural e de grade, materialização, binding, publicação schedule-aware/schedule-first e vertical slice. No frontend há testes de geometria e rótulos de confiança. Nesta etapa eles foram inventariados, **não executados**, conforme a restrição de não disparar infraestrutura ou coletores.

Os testes comprovam regras isoladas e simulações; não provam qualidade atual do provedor, cobertura de todas as linhas, comportamento distribuído nem configuração efetivamente entregue ao app.

## Estado das linhas

- **Santa Cruz:** grade V1 construída, aprovada, importada e ativa em produção; runtime confirmado.
- **Japeri:** FULL concluída localmente e recuperada via checkpoint; builder, auditoria, importação e ativação não foram encontrados.
- **demais linhas:** não há nesta auditoria evidência suficiente para declarar grade operacional completa.

## Lacunas prioritárias

1. construir e auditar Japeri sem converter falhas da coleta em horários presumidos;
2. implantar coordenação distribuída de orçamento/estado ou garantir singleton formal;
3. substituir/acoplar claramente o perfil estático de Santa Cruz à grade ativa;
4. implementar calendário de feriados e exceções operacionais;
5. expor saúde/erro no frontend e estratégia de invalidação para geometria negativa;
6. validar flags e configuração realmente distribuídas do app;
7. medir acurácia contra observação independente antes de tratar posição como precisão operacional.

## Conclusão

O fluxo ferroviário vigente está reconstruído de forma suficiente para documentação e evolução: aquisição, grade, expected runs, scanner, binding, posição, publicação e mapa têm contratos identificáveis. As lacunas acima são limitações e próximos trabalhos, não impedem encerrar esta etapa documental.

**Veredito: `READY_FOR_STAGE_2_3C`.**

