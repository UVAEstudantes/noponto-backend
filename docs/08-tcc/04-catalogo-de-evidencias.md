# Catálogo de evidências acadêmicas

**Finalidade:** planejar coleta e apresentação sem fabricar provas. **Baseline:** 2026-10-06.

| Evidência/objetivo | Obtenção e validação | Estado/dependência | Apresentação/risco |
|---|---|---|---|
| telas de busca/mapa/parada/rail | build identificado e captura em dispositivo | pendente | figura com commit; remover localização pessoal |
| diagramas 07 | Mermaid + revisão contra código | existente, validação sintática limitada | figuras de arquitetura |
| schema/DER | migrations/catalog read-only | existente | recorte, nunca credenciais |
| payloads V2/SignalR/rail | request controlado e sanitização | contratos existentes; amostra pendente | quadro JSON curto; IDs pseudonimizados |
| matching | dataset de referência + output | testes/harness existem; consolidar | tabela caso/esperado/obtido |
| inferência ferroviária | schedule, evidência, binding e snapshot | logs/código; amostra pendente | timeline com origem/confiança |
| telemetria/ETA | export reproduzível e manifesto | ETA V2 OFF; pendente | tabelas/gráficos somente após experimento |
| performance | métricas do protocolo controlado | pendente | distribuição, ambiente e carga |
| testes | comando, commit, stdout/resultado | classes existem; execução pendente | tabela pass/fail/skipped |
| falhas/recovery | cenários isolados | pendente | matriz antes/depois, sem afetar produção |
| implantação | Compose/workflow + fotografia sanitizada | parcial existente | diagrama e tabela temporal |
| logs/métricas | janela curta e filtros | sinais existem; coleta pendente | excerto sanitizado/agregado |

## Cadeia de custódia

Guardar artefato bruto fora da monografia quando necessário, calcular hash, registrar data/timezone, versão, query/script e transformação. Captura não substitui métrica; log isolado não prova disponibilidade. Dados operacionais devem minimizar identificadores e remover hosts, tokens, coordenadas sensíveis e strings de conexão.

## Checklist de validação

Cada evidência responde a uma pergunta, tem origem verificável, pode ser reproduzida ou auditada, declara limitações e aponta requisito/objetivo. Resultados negativos permanecem no conjunto.

Relacionados: [plano de testes](03-plano-de-testes-e-validacao.md), [diagramas](../07-diagramas/00-catalogo-convencoes-e-rastreabilidade.md) e [checklist da defesa](09-decisoes-pendentes-e-checklist-da-defesa.md).
