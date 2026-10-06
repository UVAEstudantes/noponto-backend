# NoPonto — critérios de aceite, avaliação e riscos

**Finalidade:** transformar evolução em hipóteses verificáveis e registrar riscos acadêmicos, técnicos e pessoais.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** requisitos RF/RNF existentes, testes, telemetria, observabilidade e documentos desta etapa. **Estado:** plano; nenhuma avaliação nova foi executada.

## Requisitos candidatos

Esta tabela complementa, sem renumerar, os requisitos de 2.1.

| ID | Problema/critério de aceite | Dependência | Prioridade/estado/fonte |
|---|---|---|---|
| `REQUISITO_CANDIDATO ROTA-01` | retornar etapas somente com direção, calendário e transferência válidos | snapshot/footpaths | candidato; planejado; doc 02 |
| `ROTA-02` | distinguir sem rota, sem serviço e dados indisponíveis | contratos/UX | candidato; planejado |
| `ROTA-03` | indicar fonte/idade/incerteza e não misturar versões | V2/realtime | núcleo do roteador; planejado |
| `PERS-01` | salvar/apagar dado pessoal e operar opt-in | storage/identidade | posterior; planejado |
| `NOTIF-01` | deduplicar, respeitar quiet hours e suprimir stale | eventos/background | posterior; planejado |
| `UX-01` | funções essenciais têm alternativa textual e não dependem só de cor | frontend | candidata TCC; parcial |
| `MODAL-01` | integração nova preserva proveniência e cobertura | provider/estrutura | expansão; planejado |

## Avaliação técnica

- contratos/schema: compatibilidade e versionamento;
- estrutura: conectividade, sentido, ocorrência repetida e geometria;
- GPS: aceite, matching e erro contra amostra de referência;
- ferrovia: cobertura de expected runs, freshness, binding e estado correto;
- ETA: MAE/MedAE/percentis por horizonte/linha, sempre com dataset temporal reproduzível;
- performance: latência p50/p95/p99, custo por lote, memória/CPU e backlog, com ambiente/carga registrados;
- resiliência: API/Redis/provider indisponível, reconnect e reconstrução.

Métrica só é válida com commit, flags, janela, amostra, exclusões e incerteza. Não há resultados novos nesta etapa.

## Avaliação funcional e de usabilidade

Roteiros: buscar linha; escolher sentido; visualizar veículo/parada; interpretar trem estimado; reconhecer dado stale; recuperar de offline. Medir sucesso, erro, tempo e compreensão, sem transformar tempo menor automaticamente em melhor UX.

Estudo com participantes pode usar tarefas, observação e questionário validado, mas ainda não ocorreu. Definir público, amostra, consentimento, tratamento de dados e exigências da instituição/comitê aplicável antes da coleta.

Para roteador futuro: conjunto de casos de referência, validade de conexão, duração calculada, tempo computacional, cobertura de alternativas, transferências, tarifa e comportamento sob atraso. Comparar algoritmo simples e candidato avançado no mesmo snapshot.

## Privacidade e proteção

Rotinas podem revelar residência, trabalho, religião/saúde por destino e padrões horários. Requisitos: minimização; finalidade; base jurídica analisada; permissão de localização; opt-in de notificações; retenção/exclusão; controle de acesso; segredo protegido; segurança local e em trânsito. Não unir identidade/Rotina à telemetria veicular sem desenho e finalidade explícitos. Este documento não substitui análise jurídica.

## Matriz de riscos

| Risco | Tipo/impacto | Mitigação/evidência exigida |
|---|---|---|
| escopo excessivo | acadêmico, alto | núcleo + um experimento; aprovação do orientador |
| rota impossível | funcional/segurança, alto | transferências validadas e casos dourados |
| falsa precisão | funcional, alto | origem/idade/confiança e estados stale |
| dados pessoais de Rotinas | privacidade, alto | local/opt-in/minimização/auth antes de sync |
| provider incompleto | dados, médio/alto | cobertura/proveniência e degradação |
| host saturado | operacional, alto | offline build, consulta sob demanda e métricas |
| resultado não reproduzível | acadêmico, alto | manifests, commits, hashes e split temporal |
| avaliação humana irregular | ético/institucional, alto | verificar autorização antes da coleta |

## Evidências e critérios de conclusão

Aceite documental exige matriz atualizada por release. Aceite de produto exige testes automatizados + roteiro em dispositivo + evidência de ambiente. Aceite acadêmico exige protocolo, dataset/artefato reproduzível, resultados e limitações. Nenhuma meta quantitativa foi aprovada aqui.

**Pendências:** escolher escopo; definir métricas/metas com orientador; decidir estudo com usuários; aprovar fontes pedonais/tarifárias; política pessoal; preparar casos de referência. Relacionados: [requisitos existentes](../01-projeto/04-requisitos-e-funcionalidades.md), [observabilidade](../05-infraestrutura/05-logs-metricas-e-observabilidade.md), [ETA](../03-funcionalidades/17-datasets-treinamento-e-validacao.md) e [roadmap](07-roadmap-priorizacao-e-escopo-da-defesa.md).
