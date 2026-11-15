# Histórico

## 2026-11-15

- URLs de ITS, GTFS-RT ônibus/BRT e Expo Push configuráveis pelo .NET, com validação HTTP(S) e fallback para os endereços existentes. Trens RJ reutiliza `TremRealtime:BaseUrl`.
- Compose versionado encaminha as cinco URLs sem alterar fontes, limites, timeouts ou mecanismo de deploy. Nenhum `.env` foi editado.
- Validação: 43 testes xUnit direcionados em projeto temporário com os fontes reais; renderização do Compose e comparação dos valores anteriores aprovadas. Build completo e integrações externas não executados.

Históricos anteriores foram centralizados no repositório `noponto-docs` durante a limpeza documental.

- Promoção controlada das cinco URLs do `.env` para uma nova composição congelada: simulação sem escrita, aplicação explícita na mesma imagem, backup privado e rollback automático/manual. CI existente preservado; Radar aceita a URL já promovida sem permitir editá-la pelo overlay de Radar. Nenhum arquivo de produção ou `.env` foi alterado durante a implementação.
- Validação da promoção: 22 testes novos e 10 regressões de Radar/CI aprovados com fakes; preflight real de leitura aprovado, conferindo integridade dos arquivos protegidos e do container. Aplicação e rollback reais não executados.
