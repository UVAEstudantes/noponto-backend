# NoPonto — rotinas, favoritos e personalização

**Finalidade:** separar conceitos pessoais e planejar sua evolução sem inventar persistência ou conta.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** placeholder `favoritos.tsx`, AsyncStorage, histórico de busca, RF-018/019/024–027 e documentos de privacidade. **Estado:** preferências/histórico locais implementados; Rotinas planejadas.

## Conceitos e estado

Favorito é um atalho salvo; histórico é consulta passada; rota salva é um itinerário/versionamento escolhido; Rotina é uma intenção recorrente com contexto temporal e alternativas; recomendação é resultado calculado. Hoje existem linhas selecionadas persistidas, histórico de pesquisa, modal, tema, mapa e lateralidade em AsyncStorage. Não há favorito formal, conta, sincronização, CRUD de Rotina nem recomendação. A aba “Rotinas” apenas comunica visão futura.

| Função | Implementação | Disponibilidade | Prioridade |
|---|---|---|---|
| preferências/histórico locais | implementada não validada integralmente | código | núcleo auxiliar |
| linhas/paradas/estações favoritas | planejada | não implantada | candidata simples |
| Rotina e alternativas | planejada | não implantada | evolução/decisão |
| sincronização/recomendação | planejada | não implantada | posterior |

## Modelo conceitual de Rotina

MVP candidato: nome, origem, destino, dias, horário de saída **ou** chegada, ativo/pausado e preferências mínimas. Alternativas são referências a resultados versionados ou regras de consulta, não uma lista eterna de veículos. Evoluções: calendário excepcional, acessibilidade, custo, limite de caminhada, notificação, viagem pontual e múltiplas janelas.

Antes da saída, a rotina pode recalcular alternativas e informar recomendação atual; durante o deslocamento, pode mostrar etapas e próxima conexão. Isso não prova embarque do passageiro. Progresso precisa ação explícita, localização consentida ou inferência comunicada como tal. Rota favorita e rota recomendada devem aparecer separadas.

## Persistência e identidade

Três alternativas permanecem abertas:

1. somente local: simples e privado por padrão, sem sincronização/recuperação;
2. conta própria/terceiro: sincroniza, mas exige auth, autorização, recuperação e operação;
3. híbrida: dados locais com sincronização opt-in, maior complexidade de conflito.

Não há decisão aprovada. RNF-009/011 deve anteceder backend pessoal. Dados sensíveis incluem casa/trabalho, horários, localização e histórico. Aplicar minimização, finalidade, opt-in de localização/notificação, retenção curta quando possível, exclusão, exportação/controle, criptografia apropriada e separação da telemetria operacional.

## Requisitos candidatos

- `REQUISITO_CANDIDATO PERS-FAVORITO`: salvar/remover entidade local e restaurar após reinício sem duplicidade.
- `PERS-ROTINA`: criar/editar/pausar/excluir Rotina com validação temporal.
- `PERS-ALTERNATIVA`: distinguir rota salva de recomendação recalculada e registrar versão/frescura.
- `PERS-PRIVACIDADE`: informar finalidade, permitir apagar e não sincronizar sem consentimento/conta.
- `PERS-PROGRESSO`: nunca afirmar embarque sem evidência; indicar etapa manual/inferida.

Critérios incluem migração de schema AsyncStorage, comportamento offline, timezone/horário de verão, rotina sem rota e conflito de sincronização. Testes com usuário ainda não ocorreram.

## Dependências, riscos e pendências

Favoritos locais independem do roteador; Rotina origem/destino pode existir antes dele como intenção, mas alternativas/recomendação dependem de rotas. Alertas dependem de eventos confiáveis. Riscos: inferir hábitos, notificar local sensível, dados órfãos, rota salva obsoleta e escopo excessivo.

Recomendação provisória: se entrar no curto prazo, iniciar com favoritos locais ou Rotina local mínima, sem conta/recomendação automática; decisão cabe ao desenvolvedor/orientador. Pendências: identidade, armazenamento, sincronização, fonte de endereço e papel na defesa.

Relacionados: [rotas](02-rotas-multimodais-e-algoritmos-candidatos.md), [notificações](05-notificacoes-ux-e-acessibilidade.md), [segurança](../05-infraestrutura/06-seguranca-e-superficie-de-exposicao.md) e [roadmap](07-roadmap-priorizacao-e-escopo-da-defesa.md).
