# Histórico

## 2026-11-15

- URLs de ITS, GTFS-RT ônibus/BRT e Expo Push configuráveis pelo .NET, com validação HTTP(S) e fallback para os endereços existentes. Trens RJ reutiliza `TremRealtime:BaseUrl`.
- Compose versionado encaminha as cinco URLs sem alterar fontes, limites, timeouts ou mecanismo de deploy. Nenhum `.env` foi editado.
- Validação: 43 testes xUnit direcionados em projeto temporário com os fontes reais; renderização do Compose e comparação dos valores anteriores aprovadas. Build completo e integrações externas não executados.

Históricos anteriores foram centralizados no repositório `noponto-docs` durante a limpeza documental.
