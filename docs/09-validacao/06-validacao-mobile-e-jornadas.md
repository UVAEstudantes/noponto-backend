# Validação mobile e jornadas

**Finalidade:** registrar validações locais e preparar roteiro Android sem alegar teste em dispositivo.

## Resultado estático

Ambiente frontend `c69a92e`, Node 20.20.2, pnpm 10.17.1 e dependências já instaladas:

- `pnpm exec tsc --noEmit`: aprovado, exit 0;
- `pnpm lint`: aprovado com 0 erros e 1 warning (`mapControls.tsx:13`, `useRef` não usado);
- 10 arquivos `*.test.ts` inventariados, cobrindo estrutura V2, geometria rail, eventos, mapa, confiança, histórico/busca, placeholder animado, veículos e apresentação rail;
- sem script/runner de testes no `package.json`: testes não executados;
- nenhum EAS build, bundle, app ou dispositivo iniciado.

## Contratos e lacunas

HTTP e SignalR são adaptados em services/hooks próprios; TanStack Query não participa. Erros ainda podem resultar em listas vazias. Reconexão SignalR automática existe, mas reinscrição de grupos precisa E2E. Polling rail, MapLibre/WebView e AsyncStorage possuem unidades auxiliares, mas integração em runtime/dispositivo não está comprovada.

## Roteiro Android futuro

| Etapa | Ação | Resultado esperado/evidência |
|---|---|---|
| instalação | instalar build identificado e abrir | versão/commit e startup sem erro |
| busca | buscar código/nome e limpar filtro | opções coerentes; loading/error distinguíveis |
| linha | selecionar linha, sentido e padrão | geometria/paradas corretas |
| rodoviário | observar snapshot e push | sem duplicação; idade/status coerentes |
| ferrovia | selecionar ramal | posição marcada como inferida e confiança/idade |
| parada | abrir sheet/eventos e focar veículo | dados contextualizados ou vazio explicado |
| persistência | alterar tema/mapa/linhas e reiniciar | valores válidos restaurados |
| conexão | desligar rede e recuperar | erro/stale; reconnect e reinscrição verificadas |
| sem dados | provider/snapshot vazio controlado | mapa remove/degrada sem veículo fantasma |
| acessibilidade | TalkBack, tamanho de fonte, contraste e toque | foco/labels/ordem; informação não só por cor |
| background | alternar app e retornar | polling/bateria/reconexão coerentes |

Capturas devem ocultar localização/identificadores pessoais e registrar dispositivo, Android, build, rede, data e cenário. Não realizar o roteiro contra produção sem autorização e plano de dados.

## Critérios de prontidão mobile

Runner local definido; lint sem warning acordado; contrato versionado; build reproduzível; roteiro aprovado em pelo menos dispositivo-alvo definido; estados empty/error/stale distintos; reconnect testado. Relacionados: [experiência](../06-evolucao/01-experiencia-atual-e-jornadas-do-passageiro.md) e [frontend diagram](../07-diagramas/15-frontend-webview-e-realtime.md).
