# 12 — Lacunas e perguntas

**Data:** 2026-10-06 · **Estado:** requer confirmação humana ou auditoria adicional dirigida

## Bloqueadores de afirmações definitivas

1. Por que tabelas `Linhas`, `Sentidos`, `Paradas`, padrões e identidades tinham estimativa zero em produção? Houve limpeza, migração incompleta ou estatística obsoleta?
2. O container ML deve estar ativo? O exit 137 decorreu de OOM, parada intencional ou healthcheck incorreto?
3. Quais flags GPS, ETA V2 e TremRealtime estão efetivamente habilitadas? Nomes foram verificados; valores não foram documentados por segurança.
4. Qual é a origem oficial de `extrair-trem/` e em qual repositório/commit deve ser preservado?
5. O worktree atual do painel administrativo representa desenvolvimento válido que deve ser commitado?
6. Onde TLS, reverse proxy e firewall são configurados? Banco e Redis devem ser acessíveis externamente?
7. Existe rotina testada de backup/restore e quais são RPO/RTO?
8. Quem pode acessar endpoints e Swagger? Qual modelo de autenticação está planejado?

## Inconsistências

- Código legado/admin existe, mas está removido da compilação.
- Compose declara integração ML, mas container está parado.
- API oferece estrutura, mas estatísticas das tabelas estruturais centrais estão zeradas.
- Redis possui volume, porém persistência RDB/AOF está desabilitada.
- Backend acumula outbox e telemetria em grande escala, sem evidência operacional de retenção suficiente.
- Frontend inclui `react-native-maps`, mas o caminho de mapa observado usa MapLibre/OSM em WebView.

## Informação ainda não verificada

Build mobile/EAS publicado; versões em lojas; uso real do painel admin; cobertura/testes passando; latência/disponibilidade; qualidade do ETA; precisão de matching; métricas ferroviárias; política LGPD; licenças/termos das fontes; custo operacional; estratégia formal de incidentes.

## Decisões necessárias

- Definir quais módulos excluídos serão removidos, reativados ou mantidos como legado.
- Escolher estado autoritativo do ETA para a apresentação do TCC.
- Aprovar contrato de confiança ferroviária mostrado ao passageiro.
- Delimitar roadmap obrigatório versus possibilidades futuras.
- Definir política de retenção para históricos, telemetria, shadows e outbox.

## Próxima auditoria recomendada

Após responder às questões 1–3, executar em ambiente isolado: build/testes, seed/importação controlada, fluxo GPS ponta a ponta, snapshot ferroviário e teste de fallback ML. Em produção, limitar-se a contagens/flags não sensíveis e métricas já expostas.

## Referências

Ver [03-inventario-funcionalidades.md](03-inventario-funcionalidades.md), [06-banco-e-persistencia.md](06-banco-e-persistencia.md), [07-infraestrutura-producao.md](07-infraestrutura-producao.md) e [10-seguranca-e-limitacoes.md](10-seguranca-e-limitacoes.md).
