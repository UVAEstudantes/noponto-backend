# Histórico

## 2026-11-15

- URLs de ITS, GTFS-RT ônibus/BRT e Expo Push configuráveis pelo .NET, com validação HTTP(S) e fallback para os endereços existentes. Trens RJ reutiliza `TremRealtime:BaseUrl`.
- Compose versionado encaminha as cinco URLs sem alterar fontes, limites, timeouts ou mecanismo de deploy. Nenhum `.env` foi editado.
- Validação: 43 testes xUnit direcionados em projeto temporário com os fontes reais; renderização do Compose e comparação dos valores anteriores aprovadas. Build completo e integrações externas não executados.

Históricos anteriores foram centralizados no repositório `noponto-docs` durante a limpeza documental.

- Promoção controlada das cinco URLs do `.env` para uma nova composição congelada: simulação sem escrita, aplicação explícita na mesma imagem, backup privado e rollback automático/manual. CI existente preservado; Radar aceita a URL já promovida sem permitir editá-la pelo overlay de Radar. Nenhum arquivo de produção ou `.env` foi alterado durante a implementação.
- Validação da promoção: 22 testes novos e 10 regressões de Radar/CI aprovados com fakes; preflight real de leitura aprovado, conferindo integridade dos arquivos protegidos e do container. Aplicação e rollback reais não executados.

## 2026-11-15 � Etapa 1: cobertura can�nica da 898

- 898 permanece no fluxo operacional: capabilities can�nicas deixam de anunci�-la; snapshot direto e publica��o can�nica retornam enabled=false, sem ampliar identidades A2-... nem fazer fallback por lista vazia. Revis�es e remo��es das linhas cobertas s�o preservadas.
- Regress�o: CanonicalCoverageExcludes898WithoutChangingAuthoritativeEmpty838 e su�tes UnifiedCohorts/CanonicalPhysical/UnifiedLocation. Sem configura��o, dados ou deploy de produ��o. Relat�rio detalhado em noponto-docs.

## 2026-11-15 � Etapa 1: validade f�sica expl�cita de ETA

- Eventos f�sicos/h�bridos publicam PredictionValidUntilUtc ancorado no TimestampGps + PhysicalEta.MaxAgeSeconds; refresh e substitui��o h�brida preservam o instante. Campo opcional aditivo, sem mudar algoritmos, programa��o ou ferrovia.
- RadarSelector remoto limita conte�do tamb�m pela expira��o publicada. Testes focados de ETA f�sico, endpoint h�brido e Radar; sem produ��o/deploy. Depend�ncia: frontend novo deve consumir o campo antes de alterar a configura��o temporal.

- Revisão de integração da cobertura: teste direto do publicador canônico comprova 898 desabilitada, 838 habilitada e IDs A2-... não admitidos. 63 testes focados de integração/cobertura/ETA aprovados na rodada final.

- Radar remoto preserva ScheduledAt independente quando o ETA físico do mesmo evento expira, alterando o rótulo para ScheduleOnly. 39 testes RadarSelector aprovados, incluindo o cenário programado e os três tipos físicos/híbridos.

## 2026-11-15 - Organizacao dos diretorios e branches

- Branch da Etapa 1 retomada no diretorio oficial; commits preservados. Politica permanente contra worktrees e copias adicionais registrada em AGENTS.md. Nenhum codigo funcional ou ambiente de producao alterado.
- Verificacao: HEAD anterior conferido, arquivos locais preservados e remocao dos tres worktrees da Etapa 1 pelo Git.


## 2026-11-16 — Radar 2: diagnóstico limitado da geração de eventos

- Testes offline reproduzem: perda temporária de padrão/progresso omite o evento; distância ausente mantém evento com ETA/validade nulos e qualidade previsao_indisponivel, permitindo alternância 4 → 14 → 4. Ausência de GPS novo apenas envelhece evidência; fallback ativo/recente preserva captura válida. Não foi comprovada a causa do episódio Android nem um defeito corrigível sem dados adicionais.
- Doze testes direcionados aprovados em PhysicalEtaSelectedStopTests (três novos casos) e PhysicalEtaIntermittenceTests; sem Redis/Postgres/produção. Nenhuma alteração funcional. Diagnóstico detalhado centralizado em noponto-docs/00-auditoria/2026-11-16/radar2-diagnostico-eventos.md. Alterações anteriores deste histórico preservadas e excluídas do commit.
