# NoPonto — notificações, UX e acessibilidade

**Finalidade:** planejar alertas confiáveis e uma interface que comunique origem, idade e incerteza.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** UI atual, contratos realtime, ETA/ferrovia, RNF-014/015 e documentação mobile. **Estado:** alguns estados/labels implementados; notificações planejadas.

## Comunicação da informação

O app deve diferenciar: GPS recebido; posição projetada sobre rota; posição ferroviária inferida; schedule; ETA; sem sinal; stale. Ícone, texto e timestamp devem complementar cor. “Ao vivo” só cabe a evidência recente; confiança não pode ser substituída por animação visual suave.

| Estado | Mensagem necessária | Ação possível |
|---|---|---|
| loading | componente sendo consultado | aguardar/cancelar |
| empty | consulta válida sem resultados | mudar filtro |
| error/offline | comunicação falhou | tentar novamente/ver cache marcado |
| stale | último dado ultrapassou limiar | mostrar idade, não mover como atual |
| sem cobertura | fonte/modal não cobre região | explicar limite |
| sem previsão | veículo existe, ETA não | manter posição sem ETA inventado |
| geometria/versão incompatível | não projetar | omitir marcador e registrar diagnóstico |

Hoje esses estados aparecem de forma desigual e erros podem virar listas vazias. Há tema/lateralidade e alguns `accessibilityRole/Label`, mas não existe auditoria completa nem conformidade WCAG declarada.

## Notificações futuras

Eventos já aproveitáveis: posição/próxima ocorrência, eventos de viagem, passagem, ETA V2 futuramente, schedule/evidência ferroviária e freshness. “Serviço interrompido”, “melhor alternativa” ou “perderá conexão” requer novas fontes/regras e roteador.

Alternativas:

- local: app agenda/avalia; preserva simplicidade, mas background pode limitar atualização;
- push: backend mantém assinaturas e dispara serviço externo; melhora alcance, aumenta identidade/operação;
- híbrida: push para mudança remota e local para lembretes conhecidos; maior coordenação.

Nenhum fornecedor ou fila está aprovado. Requisitos: opt-in granular, quiet hours, idempotência/deduplicação, freshness, cooldown, cancelamento, timezone, mudança da viagem e deep link seguro. Dado stale deve suprimir ou rebaixar o alerta.

## Acessibilidade e desempenho mobile

Critérios candidatos: contraste medido; alvo de toque adequado; leitor de tela; ordem/foco; texto alternativo ao mapa; não depender de cor; linguagem objetiva; dynamic text; redução de movimento. Testar Android real e, se alvo, iOS; não declarar WCAG sem avaliação.

Em rede lenta/offline, carregar estrutura antes do realtime, limitar payload, reutilizar cache versionado e interromper polling quando não útil. Animação local reduz rede, mas pode mascarar stale; background deve economizar bateria e respeitar políticas do SO. Não há métricas de tráfego/bateria.

## Requisitos e validação

- `REQUISITO_CANDIDATO UX-CONFIANCA`: toda posição/previsão expõe natureza e idade.
- `UX-ESTADOS`: loading/empty/error/stale são visualmente e semanticamente distintos.
- `A11Y-MAPA`: informação essencial possui alternativa textual navegável.
- `NOTIF-OPTIN`: nenhuma assinatura pessoal é criada sem escolha reversível.
- `NOTIF-IDEMPOTENCIA`: mesmo evento lógico não gera duplicatas na janela definida.

Validar por testes de componentes/contratos, leitor de tela, contraste, perda de rede, provider stale, background e roteiro com participantes se aprovado institucionalmente. Não há pesquisa de usuário realizada.

Riscos: spam, falsa precisão, vazamento na tela bloqueada, consumo de bateria e dependência de background. Pendências: público-alvo, plataforma, política de permissão, serviço push, limiares e protocolo de usabilidade.

Relacionados: [experiência](01-experiencia-atual-e-jornadas-do-passageiro.md), [ETA](../03-funcionalidades/19-eta-limitacoes-testes-e-roadmap.md), [Rotinas](03-rotinas-favoritos-e-personalizacao.md) e [avaliação](08-criterios-de-aceite-avaliacao-e-riscos.md).
