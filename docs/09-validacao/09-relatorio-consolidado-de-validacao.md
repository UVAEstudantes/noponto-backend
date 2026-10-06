# Relatório consolidado de validação

**Data:** 2026-10-06. **Commit documental/executado:** `2d57ea8`; **baseline funcional:** `53567bd`; frontend `c69a92e`.

## O que foi comprovado

- 17 documentos, 26 Mermaid: estrutura sintática estática válida e semântica coerente na revisão; visual não renderizado.
- Backend: 1.434 casos descobertos; build/discovery concluídos sem erro fatal.
- Filtro inicial: 203 total, 150 aprovados, 53 falhos; 43 são bloqueio explícito de PostGIS e 10 falhas de fixture/DI em estado causal.
- Filtro refinado: 140 total, 139 aprovados e 1 falho; o teste ETA falho passou isoladamente, sinal de instabilidade temporal.
- Frontend: TypeScript aprovado; lint com zero erros/um warning; 10 arquivos de teste sem runner configurado.
- Nenhuma produção, provider, Docker, DB ou Redis foi acessado/alterado.

## O que não foi comprovado

Suíte backend completa; PostGIS/Redis/streams/outbox integrados; SignalR E2E/reinscrição; app Android; renderização Mermaid; precisão GPS/matching/rail/ETA; desempenho; backup/restore; segurança pública; ETA V2 operacional.

## Inconsistências e diagnósticos

1. `EstadoCausalPosicaoTests` não acompanha dependências atuais do construtor de `GpsPollingService` e falha antes da asserção.
2. `OcorrenciaParadaTests` foi inicialmente classificado como local, mas o fixture corretamente exige PostGIS; reclassificado como `BLOQUEADO_POR_AMBIENTE`.
3. `Worker_FlushesAtMaximumDelay` é sensível ao timing: falhou no conjunto e passou isolado.
4. Frontend contém testes fonte, mas não fornece script/runner para executá-los.
5. O lint aponta import `useRef` não usado; não afeta typecheck, mas impede um relatório sem warnings.

## Riscos para a defesa

Apresentar testes “existentes” como aprovados; usar animação rail como precisão; usar produção como laboratório; afirmar ETA V2; depender de provider ao vivo; não possuir build mobile reproduzível; ocultar falhas/blocked; publicar dados de acesso ou localização.

## Próximos passos controlados

1. provisionar PostGIS/Redis descartáveis, documentando destinos;
2. categorizar testes e separar unidades/integrações/benchmarks;
3. reparar fixtures e timing em tarefa de código autorizada;
4. configurar runner frontend e validar Android;
5. instalar/aprovar renderizador por processo separado ou usar ambiente institucional;
6. executar um eixo experimental com corpus versionado;
7. tratar backlog crítico antes de exposição pública.

## Veredito

`NEEDS_TEST_ENVIRONMENT`

A documentação e os protocolos permitem validação controlada, mas PostgreSQL/PostGIS, Redis e jornada mobile ainda não possuem ambiente isolado comprovado. Falhas reais de fixture e instabilidade temporal também precisam tratamento antes de considerar o núcleo integralmente verde.
