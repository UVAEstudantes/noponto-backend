# Sprint integrada — 14/11/2026

## A. Resultado e complexidade
Implementadas mudanças localizadas nos dois repositórios, sem commits, push, deploy, migrations, infraestrutura, clones ou worktrees novos. Entrega com validação automatizada parcial e validação visual pendente; não representa homologação do APK.

Classificação: baixa para confirmação, tutorial, estrela independente e borda da seta; média-baixa para catálogo compartilhado, fila Radar, foco/AppState, localização inicial, persistência de itinerários, compressão, contratos de paradas, painel de linhas e protótipo de Rotinas. Nenhuma alteração estrutural de alta complexidade foi implementada.

## B. Checklist completo
| Solicitação | Estado / evidência e limite |
|---|---|
| P1 modais | Implementado: Promise compartilhada na origem, cache de cinco minutos, vazio/rejeição recuperáveis; testes de concorrência e recuperação. Resolver de filtros também expira. |
| P2 Radar | Implementado e testado: reconciliações ainda na fila compartilham trabalho; execução iniciada admite novo evento. Mutação intermediária exige nova reconciliação. Sem agrupar mutações. |
| P3 mapas ocultos | Implementado, pendente manual: foco da aba e AppState retiram somente códigos do consumidor unificado do mapa; serviço de donos SignalR preservado. Sem desligar Radar. |
| P4 compressão | Parcial: gzip nativo Fastest para application/json em GET /paradas e /padroes-versoes, inclusive HTTPS; sem cache público e sem WebSocket. Snapshots ferroviários não incluídos. Sem limite mínimo de bytes/buffering; respostas pequenas dessas rotas também podem comprimir. CPU, negociação e bytes precisam de medição local. |
| P5 cache | Implementado e testado no helper: persistência por versão, até 12 entradas de 200 mil caracteres, recuperação de corrupção/falha de armazenamento. Evicção por chave, não LRU. Sentidos/padrões/detalhes não receberam cache persistente. Validação de leitura exige ID da versão, ocorrências e coordenadas. |
| P6 cancelamento | Parcial: nova consulta de linhas da parada usa AbortController ao fechar/desmontar/trocar parada; chamadas compartilhadas de catálogo não são canceladas. Não houve revisão exaustiva de todas as consultas do app. |
| Localização inicial | Implementado, pendente aparelho: marcador oculto até posição válida; última posição de até 60s e precisão exigida de 100m; consulta atual assíncrona com permissão concedida. Centralização tardia forçada removida; botão explícito preservado. Teste de marcador e gesto aprovado. |
| Confirmação de exclusão | Implementado, pendente manual: confirmação nativa nos dois caminhos de remoção da seleção; guarda contra múltiplos diálogos, cancelar sem mutação. |
| Tutorial | Implementado: remover durante lineRemove avança a lineTab em vez de voltar à busca. Persistência existente não é apagada. Teste novo da transição aprovado; fluxo completo precisa de validação manual. |
| Paradas | Parcial: dois novos contratos, filtros adicionais e compatibilidade preservada; 22 testes de paradas/contratos aprovados. Novos filtros de ID/nome da linha ainda precisam de casos específicos de tradução SQL e combinações. Busca textual conserva comportamento existente, sem normalização nova de acentos. |
| Heads-up | Implementado, pendente físico: duas faixas essenciais, padding/margem/fontes compactos, informação secundária fica no expandido. Sem build Kotlin/Android. |
| Borda da seta | Implementado e testado no renderer: script preenchia a borda externa com a cor da linha. Agora só define --direction-color interno; CSS branco externo é preservado. Visual em dispositivo pendente. |
| Rotinas | Implementado, pendente visual: aba existente, exemplos explicitamente fictícios, vazio, criar/editar/remover, dias, horário validado, voz e ativação local. Sem persistência, cálculo de rota, agenda ou notificações reais. |
| Linhas/favoritos | Implementado, pendente tela estreita: estrela circular independente de 48px; seletor de modais mantém rolagem e lógica existente. |
| Linhas atendentes na parada | Implementado, pendente integração visual: painel com código/nome/sentido, scroll máximo de 220px, loading/vazio/erro/retry; sucesso reutilizado durante montagem. Modal não é traduzido para nome; contrato compacto fornece modalId. |

## C. Principais arquivos
Backend: EstruturaParadasController.cs (novas rotas); EstruturaLeituraV2Repository.cs (filtros e modo compacto sem query de vínculos); EstruturaLeituraDtos.cs (parâmetros opcionais); EstruturaLeituraV2ControllerTests.cs (contratos HTTP); RadarTests.cs/RadarEngineTests.cs (assinaturas das fixtures); Program.cs (compressão).
Frontend: app/favoritos.tsx (Rotinas); app/index.tsx (posição inicial); mapOSM.tsx (foco/AppState); mapScript.ts (posição e seta); linhasContainer.tsx (confirmação/estrela); TutorialProvider.tsx (remoção no tutorial); paradaSheet.tsx e linhasDaParada.tsx (painel); mobilidadeRio.ts/catalogoCompartilhado.ts/searchModalFilter.ts (catálogo); estruturaV2.ts/itinerarioPersistido.ts (persistência); radarRemotoCore.ts (fila); layout heads-up e RadarNativeNotificationModule.kt. Testes localizados e HISTORICO.md em ambos.

## D. Branches e preservação
Backend: origin/main 2700bf691a6c9971e79303079afb5511fcc5aba4 → feat/otimizacoes-beta.
Frontend: origin/chore/principal 4854478b35ab0659fd4af68e2ee45e1fe57b8329 → feat/otimizacoes-beta.
Nenhum commit criado. Workspaces mantêm alterações não commitadas. Artefatos não rastreados preexistentes preservados.
Backend inicialmente recusou checkout por histórico modificado e auditorias não rastreadas. Com autorização explícita, cópias exatas dos três arquivos foram guardadas em .git/preservado-otimizacoes-beta. Branch criada da main remota; entrada local de histórico conciliada por acréscimo. Auditorias da main mantidas; originais disponíveis no backup. A branch local antiga encontrada é perf/otimizacao; a auditoria SignalR não existe nessa referência e foi consultada na main atual. Nenhum merge da branch antiga.

## E. Performance
Redução de leituras simultâneas do catálogo e de reconciliações pendentes equivalentes. Itinerários podem ser reutilizados após reinício. Mapas de abas ocultas não demandam grupos unificados exclusivos. Sem percentuais de economia: medição de rede/CPU real não executada. Sem mudança de GPS global, fallback, DTOs SignalR, polling ferroviário, ngrok ou zrok.

## F. Contratos principais
A: GET /paradas/consulta?paradaId=<uuid>&nome=Terminal&page=1&pageSize=50
Parâmetros opcionais paradaId/nome combináveis. Retorno: {"items":[{"parada":{"id":"uuid","codigo":"P001","nome":"Exemplo","latitude":-22,"longitude":-43},"linhas":[{"id":"uuid","codigo":"838","nome":"Exemplo","modalId":"uuid","sentidos":[{"id":"uuid","linhaId":"uuid","nome":"Destino"}]}]}],"page":1,"pageSize":50,"total":1}.
Nome com múltiplas correspondências retorna todas paginadas; ID inexistente retorna items vazio, total 0. Nenhuma escolha arbitrária.

B: GET /paradas/busca?linhaId=<uuid>&codigoLinha=838&nomeLinha=Terminal&modalId=<uuid>&sentidoId=<uuid>&nome=Centro&paradaId=<uuid>&page=1&pageSize=50
Todos os filtros opcionais, combináveis; filtros de linha/sentido/modal respeitam o mesmo vínculo operacional atual.
Retorno: {"items":[{"id":"uuid","codigo":"P001","nome":"Exemplo","latitude":-22,"longitude":-43}],"page":1,"pageSize":50,"total":1}.
Sem linhas, modais ou geometrias no JSON. page>=1, pageSize 1..100, offset limitado a Int32; inválidos retornam 400. Ordem nome/ID. Correspondência textual ignora caixa; código de linha exato. Acentuação mantém a semântica do banco existente: não foi instalado unaccent nem alterado schema.

Preservados GET /paradas (enriquecido/paginação antiga), /paradas/{id}, /paradas/{id}/linhas, /linhas/{id}/paradas e /paradas/{id}/eventos. Painel usa /paradas/{id}/linhas: array linhaId/codigo/nome/modalId/sentidoId/sentido, 404 inexistente e [] sem vínculo. Exemplos acima são ilustrativos.

## G. Validações
Backend: dotnet test NoPonto/NoPonto.csproj --no-restore --filter "FullyQualifiedName~ParadasConsultaTests|FullyQualifiedName~EstruturaLeituraV2ControllerTests" --verbosity quiet: 22 aprovados, zero falhas/ignorados. Compilação com avisos preexistentes. Não inicia bootstrap produtivo.
Frontend: seleção final de 51 testes passou, zero falhas/ignorados, incluindo regressões novas de tutorial e localização; Radar também rechecado 15/15. TypeScript sem erros nas execuções concluídas. ESLint dos novos componentes/helper/Rotinas aprovado. git diff --check aprovado.
Tentativas ampliadas de tutorialUI/carregamentoInicial falharam em fixtures antigas que não resolvem GatewayStatus/tutorialMapContract. Não afirmamos suíte completa aprovada. Uma invocação de tsc fora da pasta NoPonto não executou o compilador do projeto; checagens posteriores usaram node_modules/typescript/bin/tsc dentro do projeto.

Comandos leves no frontend/NoPonto:
node node_modules/typescript/bin/tsc --noEmit
node scripts/test-radar.cjs src/services/catalogoCompartilhado.test.ts src/services/itinerarioPersistido.test.ts src/services/searchModalFilter.test.ts src/services/radarRemotoCore.test.ts src/components/mapOSM/webview/userLocation.test.cjs src/services/apresentacaoIndicadores.test.cjs src/components/tutorial/tutorialSteps.test.cjs

Manual (sem comandos automáticos de build):
1. Abrir com permissão concedida/negada/GPS indisponível; marcador não aparece no centro padrão. Arrastar antes do GPS e confirmar que a câmera não volta; botão centralizar continua funcionando.
2. Cancelar/confirmar remoção e tocar repetidamente; remover todas, adicionar e reiniciar app com tutorial concluído.
3. Alternar Home/Linhas/background e conferir demanda por dono, retomada e Radar/push/voz intactos.
4. Em tela estreita/temas claro e escuro, conferir estrela, modais, painel da parada e Rotinas; criar inválida/válida, editar, alternar e remover até lista vazia.
5. Reiniciar app e conferir itinerário sem novo download para versão já persistida; mudar versão e confirmar leitura nova.
6. Após build Android manual: Xiaomi e outro Android, fonte padrão/ampliada, heads-up com 1/2/3 linhas, 2 veículos da mesma linha e texto longo. Conferir chegada essencial e expandido completo; borda branca nas setas sobre mapa escuro.
7. Backend local de testes: consultar contratos com ID, nome, linha/código, múltiplos sentidos e paginação; negociar Accept-Encoding:gzip em resposta grande elegível e comparar bytes/CPU, sem tráfego pelo túnel.

## H. Pendências e limites
Não se classificou artificialmente trabalho restante como alta complexidade. A sprint possui implementação ampla, mas validação incompleta: fixtures amplas precisam ser atualizadas, novos filtros precisam de testes SQL adicionais, componentes interativos precisam de testes de interação/inspeção visual. Menor próximo passo é completar esses testes e homologar no aparelho; não requer redesenhar arquitetura.
Sem implementação das otimizações complexas excluídas pelo pedido. Sem motor de rotas, agendamento, workers ou integração fictícia de notificações. Sem testes físicos, carga, serviços reais, Docker, Metro, Expo ou build Android automático. Não houve inspeção visual por screenshots; acabamento de Rotinas ainda exige conferência no app.
