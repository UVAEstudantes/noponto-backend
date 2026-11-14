# Refinamento visual, UX e estabilização — 14/11/2026

Continuação de `feat/otimizacoes-beta` nos dois repositórios. Mantidos localização inicial, persistência do tutorial, separação de favoritos e contratos de paradas já aprovados. Nenhuma branch, clone ou worktree criado; nenhum commit/push/merge/PR/deploy.

## Implementado e arquivos

- `Confirmacao.tsx`: diálogo reutilizável com título, descrição, ícone, variantes padrão/destrutiva/aviso, rótulos, callbacks e processamento. Modal nativo transparente com fade, card central temático, SafeArea e conteúdo rolável, botões empilhados para fonte ampliada. Back/backdrop/cancelar não mutam dados; processamento bloqueia cancelamento e confirmação duplicada. Falha da ação permite tentar novamente. Não altera Alerts informativos.
- `linhasContainer.tsx`: aplica confirmação nas remoções da lista/mapa e personalização. Retirados banner intermediário e animação de saída anterior à confirmação. Card permanece original até a mutação confirmada. Favoritos dourado, visualmente exclusivo dos modais; botão desativa para um modal disponível coerente. Seletor sem ScrollView, uma faixa quando cabe e grade responsiva quando necessário, toque de46px e texto adaptável.
- `app/favoritos.tsx`: Rotinas com cabeçalho compacto, apresentação neutra, CTA amarelo, aviso discreto, cards com status e trajetos, exemplos locais, estado vazio e fluxo criar/editar/remover/voz/dias/horário. Edição mantém a ordem da lista. Modal “Como funcionam” organiza Planeje/Salve/Acompanhe/Receba avisos e distingue funções planejadas. Sem motor de rotas, backend, agenda ou notificações reais.
- `linhasDaParada.tsx` / `paradaSheet.tsx`: botão circular no cabeçalho junto ao sino; lista abre em Modal independente que bloqueia o mapa, sem deslocar a sheet. Card tem título/parada, chips de código, nome/sentido/modal disponível, loading/erro/retry/vazio, scroll interno e fechamento por Back/botão/backdrop. Chave por parada, AbortController e guarda de resposta obsoleta; resultado reaproveitado durante montagem. Catálogo de modais compartilhado preservado, sem cancelar a chamada dos demais consumidores.
- `mapStyles.ts` / `mapScript.ts`: seta de14×13px com SVG real, preenchimento da linha, contorno branco1.4px e junções arredondadas. Removida geometria CSS de triângulos sobrepostos. Mantidos direção, rotação, gap e estilos de marcador; centro horizontal ajustado para20px.
- `radar_notification_heads_up.xml`: revisão das duas linhas essenciais. Badge passa a uma linha, maxWidth88dp e ellipsize; ETA recebe ellipsize. Mantidos cores/Kotlin/layout expandido. Não se impôs altura fixa incompatível com fonte ampliada.
- `tutorialUI.test.cjs` / `carregamentoInicial.test.cjs`: fixtures atualizadas para GatewayFooter, tutorial recolhível, texto atual das ações, wrapper View da WebView, dependências relativas, AppState/foco e contrato Radar atual. ETA passado sozinho não comprova passagem; consulta vencida continua descartada. Nenhum teste excluído.
- Backend `EstruturaLeituraV2Repository.cs`: expressão adicional de filtros extraída literalmente para teste; contratos e regras preservados. `ParadasConsultaTests.cs` acrescenta ID, parada sem vínculo/inexistente, ID/nome da linha, mesmo vínculo, paginação/deduplicação e SQL Npgsql sem conexão.
- Backend `CompressaoHttpPublica.cs` / `Program.cs`: configuração existente centralizada para exercitar a mesma cadeia em testes. GETs públicos de paradas/itinerários, JSON, gzip Fastest e HTTPS preservados. `CompressaoHttpPublicaTests.cs` testa HTTP loopback: cliente sem gzip, gzip aceito/recusado(q=0), Vary, decompression, JSON pequeno, texto e sessão privada fora da compressão.

## Causas corrigidas

O desaparecimento visual vinha de `removeOpacity` e `removeScale`: eram animados para zero antes do callback que abria o Alert. Cancelar não desmontava/remontava o card e a opacidade permanecia zero. Agora a solicitação apenas abre confirmação; não há reconstrução de seleção ou rerender artificial ao cancelar.

A falsa seleção de modal vinha do índice forçado a zero quando o ID de Favoritos não aparecia na lista de modais. O seletor agora recebe seleção nula nesse modo e aplica destaque somente aos botões realmente selecionados.

O recorte da interface de linhas atendentes vinha de renderizar a lista no fluxo da sheet. O conteúdo agora pertence a uma janela Modal independente; o botão permanece no cabeçalho.

## Validações

- Frontend: seleção consolidada113/113aprovados,0falhas/ignorados; teste responsivo adicional2/2aprovados. Inclui Confirmacao, tutorialUI, carregamentoInicial, catálogo/cache versionado, Radar remoto, usuário no mapa, indicadores, unifiedNetwork e unifiedLocationContract.
- Evidências novas: `cancelar e Android Back não alteram lista; confirmar última linha ocorre uma única vez`; `processamento bloqueia múltiplos toques e cancelamento; nova abertura permite repetir`; `solicitar remoção do card não anima opacidade nem modifica seleção`; testes de exclusividade/fallback do seletor; harness WebView verifica retirada e retomada de demanda pelo foco.
- Backend:25/25aprovados em ParadasConsultaTests, EstruturaLeituraV2ControllerTests e CompressaoHttpPublicaTests. Após ampliar Accept-Encoding:q=0 e JSON pequeno, compressão revalidada1/1. Avisos preexistentes de build/analisadores.
- XML heads-up: parsing e limites maxLines/ellipsize essenciais verificados por self-check Python.
- SQL foi traduzido com ToQueryString pelo provider Npgsql/PostGIS; não houve conexão PostgreSQL nem alteração de dados.
- HTTP testado em loopback com esquema HTTPS simulado no HttpContext; não é homologação de TLS, proxy, túnel ou consumo real.
- TypeScript/lint e diff: resultados finais acrescentados abaixo após conclusão dos processos locais.

## Limites e escolhas

Respostas JSON pequenas elegíveis também comprimem. Não adicionado buffering nem middleware de limiar por tamanho: não há Content-Length confiável nas respostas JSON transmitidas e bufferizar mudaria memória/latência e tratamento de erros. Teste confirma o comportamento nativo; CPU e banda reais continuam sem medição. Sem WebSocket/MessagePack/infra.

Não havia Metro/preview nas portas locais usuais8081/8082/19000/19006. Não iniciado servidor ou build para fabricar validação visual. Acabamento revisado no código, temas, hierarquia, toque/scroll, SafeArea e tamanhos; não homologado por screenshot ou aparelho. Nada garante comportamento específico do SystemUI Xiaomi sem teste físico. Transições de janela Modal sobre a personalização também precisam desse teste.

Workspaces mantêm alterações anteriores e as desta rodada sem commit. Backend começou sem alterações rastreadas; frontend já continha a sprint anterior não commitada. Artefatos não rastreados preexistentes foram preservados. Branch em ambos: feat/otimizacoes-beta.

## Checklist manual Android

1. Linha no mapa: long-press/lixeira → Cancelar/Back/backdrop. Conferir card, geometria e seleção intactos imediatamente; confirmar, tocar repetidamente, repetir e remover a última linha. Repetir dentro da personalização.
2. Favoritos → nenhum modal marcado; escolher modal → estrela desmarcada; desativar estrela → filtro coerente. Conferir telas estreitas, modo escuro e fonte ampliada, sem cortes/scroll horizontal.
3. Rotinas: criar inválida/válida, dias/voz/horário, editar sem mudar ordem, ativar, excluir até vazio; abrir explicação e fechar por Back. Conferir barra inferior e teclado.
4. Parada: abrir linhas pelo botão junto ao sino. Conferir que a sheet não se move; modal cobre WebView/barra, scroll/vazio/retry, fechar mantém contexto, trocar parada não mostra dados antigos. Conferir dois sentidos da mesma linha e modal disponível.
5. Setas: ônibus/BRT/trem, cores claras/escuras, quatro direções e zooms. Conferir contorno fino, centralização e rotação estável.
6. Após build manual: heads-up1/2/3linhas,2veículos da mesma linha, texto longo, fonte padrão/ampliada, Xiaomi e outro Android. Informação principal inteira; detalhes completos no expandido.
7. Alternar abas/background/retorno com Radar ativo: inscrições legítimas, retomada, GPS, voz e push. Nenhum build Android, Metro, carga ou serviço real executado nesta rodada.

## Resultado final das checagens

TypeScript concluído sem erros. ESLint focado não concluiu: processos sem diagnóstico por vários minutos foram encerrados explicitamente; não declarado aprovado. Frontend113testes consolidados+2responsivos aprovados; backend25testes+rechecagem1decompressão aprovados. Diff final verificado. Nenhum preview ou teste físico.
