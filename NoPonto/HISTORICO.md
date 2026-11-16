# Hist√≥rico

## 2026-11-15

- URLs de ITS, GTFS-RT √¥nibus/BRT e Expo Push configur√°veis pelo .NET, com valida√ß√£o HTTP(S) e fallback para os endere√ßos existentes. Trens RJ reutiliza `TremRealtime:BaseUrl`.
- Compose versionado encaminha as cinco URLs sem alterar fontes, limites, timeouts ou mecanismo de deploy. Nenhum `.env` foi editado.
- Valida√ß√£o: 43 testes xUnit direcionados em projeto tempor√°rio com os fontes reais; renderiza√ß√£o do Compose e compara√ß√£o dos valores anteriores aprovadas. Build completo e integra√ß√µes externas n√£o executados.

Hist√≥ricos anteriores foram centralizados no reposit√≥rio `noponto-docs` durante a limpeza documental.

- Promo√ß√£o controlada das cinco URLs do `.env` para uma nova composi√ß√£o congelada: simula√ß√£o sem escrita, aplica√ß√£o expl√≠cita na mesma imagem, backup privado e rollback autom√°tico/manual. CI existente preservado; Radar aceita a URL j√° promovida sem permitir edit√°-la pelo overlay de Radar. Nenhum arquivo de produ√ß√£o ou `.env` foi alterado durante a implementa√ß√£o.
- Valida√ß√£o da promo√ß√£o: 22 testes novos e 10 regress√µes de Radar/CI aprovados com fakes; preflight real de leitura aprovado, conferindo integridade dos arquivos protegidos e do container. Aplica√ß√£o e rollback reais n√£o executados.

## 2026-11-15 ó Etapa 1: cobertura canÙnica da 898

- 898 permanece no fluxo operacional: capabilities canÙnicas deixam de anunci·-la; snapshot direto e publicaÁ„o canÙnica retornam enabled=false, sem ampliar identidades A2-... nem fazer fallback por lista vazia. Revisıes e remoÁıes das linhas cobertas s„o preservadas.
- Regress„o: CanonicalCoverageExcludes898WithoutChangingAuthoritativeEmpty838 e suÌtes UnifiedCohorts/CanonicalPhysical/UnifiedLocation. Sem configuraÁ„o, dados ou deploy de produÁ„o. RelatÛrio detalhado em noponto-docs.

## 2026-11-15 ó Etapa 1: validade fÌsica explÌcita de ETA

- Eventos fÌsicos/hÌbridos publicam PredictionValidUntilUtc ancorado no TimestampGps + PhysicalEta.MaxAgeSeconds; refresh e substituiÁ„o hÌbrida preservam o instante. Campo opcional aditivo, sem mudar algoritmos, programaÁ„o ou ferrovia.
- RadarSelector remoto limita conte˙do tambÈm pela expiraÁ„o publicada. Testes focados de ETA fÌsico, endpoint hÌbrido e Radar; sem produÁ„o/deploy. DependÍncia: frontend novo deve consumir o campo antes de alterar a configuraÁ„o temporal.

- Revis√£o de integra√ß√£o da cobertura: teste direto do publicador can√¥nico comprova 898 desabilitada, 838 habilitada e IDs A2-... n√£o admitidos. 63 testes focados de integra√ß√£o/cobertura/ETA aprovados na rodada final.

- Radar remoto preserva ScheduledAt independente quando o ETA f√≠sico do mesmo evento expira, alterando o r√≥tulo para ScheduleOnly. 39 testes RadarSelector aprovados, incluindo o cen√°rio programado e os tr√™s tipos f√≠sicos/h√≠bridos.
