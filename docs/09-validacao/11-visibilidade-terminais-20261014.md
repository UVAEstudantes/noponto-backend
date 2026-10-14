# Presença BUS/BRT nos terminais — implementação local

## Defeito demonstrado e correção

Teste RED executado no filtro real `filtrarVeiculosPorPadroesVisiveis`: veículo sintético parado, GPS atual, linha explicitamente selecionada e versão nula → retornava zero marcadores, esperado um. Após a correção, GREEN. Isso comprova uma condição local, **não a causa do desaparecimento observado em produção**.

O caminho de remoção era: filtro em `services/veiculosMapa.ts`, filtro adicional linha/sentido em `app/index.tsx` e, por fim, `mapScript.ts` removendo cada marcador ausente de `visibleKeys`. A tela Linhas também exigia uma versão/sentido selecionados antes de construir o payload do mapa. As adaptações ficam exclusivamente atrás de `EXPO_PUBLIC_ROAD_PRESENCE_ENABLED=true` (ausente/false mantém o comportamento anterior).

No modo novo, permite presença da linha/código consultados com identidade estrutural temporariamente ausente ou versão ainda não carregada. Não inventa LinhaId, SentidoId, versão ou execução. Código da linha deve coincidir; LinhaId explícito divergente é recusado. Sentido explícito incompatível e versão já conhecida fora da seleção continuam filtrados. Sem geometria confiável, mostra posição GPS bruta e sentido indeterminado; não encaixa na primeira geometria da lista nem executa dead reckoning.

## Estados de apresentação

| Estado | Evidência usada |
|---|---|
| Em viagem | Identidade estrutural coerente com geometria LINEAR carregada, fração válida, velocidade >=3 km/h. Não certifica execução nem ETA. |
| No terminal — fim do percurso | Próximo da última ocorrência real por ordem, fração compatível com o trecho final e duas observações GPS distintas e consistentes. Não pressupõe fração terminal=1. |
| No terminal — aguardando saída | Chegada anteriormente confirmada nesta sessão na mesma linha, novo sentido/versão estruturais consistentes em duas observações, proximidade da primeira ocorrência e velocidade exatamente zero. Não cria nova viagem. |
| Estado indeterminado | Sem estrutura confiável, confirmação insuficiente, velocidade zero fora do terminal, sentido isolado, geometria circular ou sequência inválida. |
| GPS desatualizado | Mais de 60s desde o GPS, até o limite finito de 180s. Exibe idade e marcador atenuado. |

Raio visual de entrada 50m, saída 100m: margem adicional de 50m alinhada à tolerância GPS já existente, somente para evitar alternância na borda. Confirmado por fixture de jitter e recuperação após intervalo sem GPS fresco. São parâmetros de apresentação a homologar no dispositivo, não mudanças no matching/cruzamentos. Confirmações exigem timestamps GPS crescentes; re-render/tick não fabrica segunda observação. Referência de chegada expira em 180s e não é transferida para outra linha.

Não classifica aguardando saída só porque a velocidade é zero, nem se o aplicativo começou a acompanhar o veículo já parado no início. Sem ter observado chegada e novo sentido suficientes, mantém indeterminado. A classificação terminal é conservadora para LINEAR; CIRCULAR permanece indeterminado.

## Presença, tempo e ETA independentes

Retém a última presença por até 180s, inclusive omissão transitória em snapshot, sem renovar TimestampGps. Usa 60s de frescor visual para acomodar a cadência BRT de aproximadamente 58s registrada anteriormente. **Não alterou TTL ativo40s/recente180s/linha180s, parser ou deduplicação.** Coordenadas inválidas, timestamp futuro ou retenção vencida não são mostrados. Relógio da UI reavalia a cada segundo em foreground; ao retomar, usa idade real. Evidências em memória limitadas a 5.000 veículos, sem AsyncStorage ou schema. Mudança observada de linha substitui o marcador antigo do mesmo veículo, evitando duplicação.

O parser GTFS-RT aceita velocidade zero; exige identidade, coordenadas e timestamp válidos. Timestamps antigos/iguais não renovam o GPS aceito. O reader oficial da API usa ativo e, na ausência, recente/SemSinal. Não exige versão ou execução Finalizada/Ativa para presença. Quando ambos expiram, a API não fornece posição; o frontend só pode reter a última que já recebeu até o prazo finito, não inventar nova observação.

Radar continua consumindo exclusivamente `/paradas/{id}/eventos` e seus vínculos estruturais. Nenhum marcador é convertido em evento/ETA pelo modo novo. O backend físico mantém suas validações causais, recusa zero velocidade e volta a prever quando há par GPS com movimento válido. A flag visual **não ativa** PhysicalEta nem muda o fallback legado do backend quando PhysicalEta está OFF; homologar o ETA físico separadamente no backend isolado. ML/Shadow permanecem fora da previsão pública.

## Validação e limites

- **RED real:** falha inicial `0 !== 1` no filtro atual, antes da correção.
- **24 testes frontend aprovados:** 12 novos de presença + 12 regressões de Favoritos/SignalR/Radar multimodal, com transporte simulado. Cobrem os cenários solicitados, prazo de retenção, escopo seguro, confirmação temporal, jitter, carregamento estrutural pendente e troca de linha sem duplicação. Inclui construção do JavaScript real da WebView e verificação sintática; não é teste de renderização no dispositivo.
- `node --test src/services/terminalPresence.test.cjs src/services/favoritosEtapaB.test.cjs`; `node node_modules/typescript/bin/tsc --noEmit --pretty false`: aprovados após ajustes locais. Primeira checagem TypeScript encontrou acesso opcional a prior, corrigido; não é falha de regra operacional.
- **110 testes C# aprovados** no filtro TerminalPresenceContractTests, PhysicalEtaMvpTests, PhysicalEtaSelectedStopTests, EventosParadaProjectionTests e GtfsRealtimeGpsTests. Inclui HTTP real em Kestrel loopback efêmero, controller existente e cache **em memória**: posição parada/sem matching continua presente, também pela via recente/SemSinal, sem ETA. Fixture de movimento causal restaura previsão física. TRX em `D:/repositorio_github/NoPonto/terminal-presence-validation/terminal-presence-final.trx`.
- Nenhum Redis/PostgreSQL, Docker, fonte operacional, banco real, homeserver, produção, treino ou nova janela diagnóstica acessados nesta tarefa. Não executados build mobile, teste visual em dispositivo ou homologação real dos limiares.

Para confirmar a hipótese real, basta uma resposta já disponível do runtime e o contexto estrutural visível na mesma ocasião: o veículo estava presente? Timestamp progredia? Versão era nula/não carregada/fora do filtro? Não afirmar que o provedor continua transmitindo no terminal nem que o novo sentido estará disponível antes de movimento. Essas lacunas não foram preenchidas por heurística operacional.

## Arquivos e Git

Frontend adaptado: `app/index.tsx`, `app/linhas.tsx`, `src/hooks/useMobilidadeRio.ts`, `src/services/veiculosMapa.ts`, `src/types/veiculoMapa.ts`, `src/components/mapOSM/types.ts`, `src/components/mapOSM/webview/mapScript.ts`; todos estavam limpos no início. Novos: `roadPresence.ts` e `terminalPresence.test.cjs`. Histórico frontend somente por acréscimo.

Backend: novo `TerminalPresenceContractTests.cs`, este relatório e acréscimo em HISTORICO.md. **Nenhuma implementação backend/ETA/GPS foi editada.** Os 18 arquivos rastreados já sujos e os arquivos novos anteriores foram tratados como legítimos; o frontend tinha nove rastreados sujos e dois novos de startup, não modificados nesta tarefa. Inventário SHA inicial de ambos: `D:/repositorio_github/NoPonto/terminal-presence-before.json`. Sem troca de branch/worktree, commit/push/merge, CI/CD ou configuração produtiva.

Conferência final de preservação: 59 arquivos preexistentes sujos/untracked mantidos byte a byte por SHA-256 nos dois repositórios; única exceção permitida nesse conjunto foi o acréscimo ao histórico backend. Histórico frontend também apenas acrescido. Nenhuma alteração inesperada, nada staged e diff --check aprovado. Resultado em D:/repositorio_github/NoPonto/terminal-presence-validation/preservation.json.
