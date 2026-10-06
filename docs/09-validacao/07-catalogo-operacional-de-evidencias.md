# Catálogo operacional de evidências

**Estados:** `EXISTENTE` possui artefato/observação; `REPRODUZIVEL` possui procedimento e ambiente seguro; `PENDENTE` não coletada; `BLOQUEADA` depende de ambiente/implementação/decisão.

| ID | OE/RF/RNF | Objetivo e procedimento | Ambiente/arquivo esperado | Validação/apresentação | Privacidade | Estado |
|---|---|---|---|---|---|---|
| EV-01 | OE-11 | diagramas Markdown, revisão estática | repo; `docs/07` | 26 blocos/inventário | nenhum segredo | EXISTENTE; visual pendente |
| EV-02 | OE-10 | descoberta `dotnet test --list-tests` | local; log sanitizado | 1.434 casos/exit 0 | remover paths se publicar | EXISTENTE/REPRODUZIVEL |
| EV-03 | RF-008/RNF-001 | filtro backend puro | local; resultado de test | contagens e stack trace | paths locais | EXISTENTE |
| EV-04 | RF-001–019 | suíte completa controlada | DB/Redis descartáveis; TRX/XML | pass/fail por requisito | não usar produção | BLOQUEADA |
| EV-05 | OE-07/RNF-013 | TypeScript/lint | frontend local | exit 0 e warning | nenhum | EXISTENTE/REPRODUZIVEL |
| EV-06 | RF-005/013/016 | jornada Android e capturas | dispositivo/build fixado; PNG/roteiro | checklist e screen recording | ocultar localização | PENDENTE |
| EV-07 | OE-04/RF-009 | corpus e experimento matching | laboratório PostGIS; CSV/JSON/hash | acurácia/cobertura/erros | pseudonimizar veículos | BLOQUEADA |
| EV-08 | OE-06/RF-015 | replay ferroviário offline | corpus/schedule fixados | binding, freshness e erro | remover IDs desnecessários | PENDENTE |
| EV-09 | OE-09/RF-023 | dataset e avaliação ETA | DB isolado/export; manifesto | split temporal e métricas | telemetria minimizada | BLOQUEADA |
| EV-10 | OE-01/02 | schema/migrations/DER | repo e catálogo read-only | constraints/cardinalidades | sem DSN | EXISTENTE |
| EV-11 | RNF-007 | desempenho controlado | host de teste; métricas brutas | percentis + carga/ambiente | sem host sensível | PENDENTE |
| EV-12 | RNF-008 | restart/recovery | ambiente descartável | estado antes/depois/RTO medido | sem credenciais | BLOQUEADA |
| EV-13 | RNF-006 | logs/contadores | janela controlada | agregação e timestamp | sanitizar IDs/hosts | PENDENTE |
| EV-14 | RNF-014/015 | interpretação/acessibilidade | dispositivo/participantes aprovados | tarefas/instrumento | consentimento/minimização | BLOQUEADA por decisão |
| EV-15 | deploy | workflow/Compose/fotografia anterior | repo/auditoria | versão/data/limitação | sem acesso SSH | EXISTENTE |

## Regras de coleta

Todo arquivo deve registrar commit, data/timezone, ambiente, comando/script, configuração não sensível, hash e responsável. O artefato bruto fica preservado; tabelas/figuras derivadas apontam para sua transformação. Resultado negativo não é descartado. Screenshot, log e payload não substituem protocolo.

## Pacote mínimo para a defesa

EV-01, EV-02/03, EV-05, EV-06, EV-07 ou EV-08 conforme escopo, EV-10, resultados de riscos críticos e uma matriz de limitações. ETA (EV-09) só entra como resultado se o escopo B e o protocolo forem aprovados.

Relacionados: [catálogo acadêmico](../08-tcc/04-catalogo-de-evidencias.md) e [relatório](09-relatorio-consolidado-de-validacao.md).
