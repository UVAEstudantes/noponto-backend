# NoPonto — expansão modal, POIs e tarifas

**Finalidade:** comparar maturidade dos modais e avaliar informações complementares necessárias à evolução.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Fontes:** fluxos 03, modelo 04, controllers/DTOs/services, frontend `linhas.tsx`, componentes POI/tarifa e migrations. **Estado:** código inspecionado; providers novos não consultados.

## Maturidade modal

| Modal | Estrutura/operação | Acompanhamento | Lacuna para rotas |
|---|---|---|---|
| ônibus | V2 + GPS/matching/SignalR operacional | posição observada/projetada | timetable/headway, transferências e ETA validado |
| BRT | V2 + fonte GPS própria no pipeline rodoviário | posição observada/projetada | integração física/tarifária e cobertura |
| trem | schedule versionado + evidências + posição inferida | snapshot HTTP/polling | cobertura, calendário/feriado e confiança |
| metrô | planejado | nenhum provider operacional comprovado | todas as fontes estruturais/operacionais |

Os modais não são equivalentes. Um contrato comum deve preservar origem e qualidade, não apagar diferenças. Para metrô são mínimos: linhas, estações, sentidos, geometria, calendários/headways, estado operacional, previsão quando disponível, tarifa e proveniência. Encontrar/avaliar provider é pendência futura; integração mínima estática seria distinta de realtime.

## POIs

O legado modela `Poi` geográfico e associação `PoiParada` com distância, além de importação Overpass/OSM e matching. A tela de Linhas ainda chama POIs por itinerário/parada e possui painéis. Controllers, services/repositories POI e worker Overpass estão excluídos da compilação; os calls degradam para vazio. Portanto POI é **parcial**, não funcionalidade operacional.

Valor possível: orientar desembarque, acesso e destino, enriquecer busca e contexto de parada. Não substitui rede de caminhada nem comprova acessibilidade. Caminhos:

1. retomar em V2: maior valor, exige contrato, atualização, qualidade/licença e operação;
2. reduzir a categorias/destinos selecionados: menor custo e cobertura explícita;
3. adiar pós-defesa: reduz risco e remove/oculta UI inconsistente após decisão própria.

## Tarifas

Há entidade/migration com valor, vigência, fonte e relação com linha/modal; DTOs, repository, controller e componente existem. Porém `TarifaService` está excluído no `.csproj`, enquanto `TarifasController` permanece no código; DI/execução e dados produtivos não estão comprovados. O fluxo é classificado **parcial / presença no código, produção não confirmada**.

Tarifa nominal não equivale ao custo total: integração, janela, benefício, meio de pagamento, vigência e regras oficiais alteram o resultado. Roteamento precisaria motor de regras versionado e fonte oficial; somar embarques é apenas estimativa inadequada como regra geral.

## Requisitos, alternativas e validação

- `REQUISITO_CANDIDATO MODAL-PROVENIENCIA`: todo estado modal informa fonte, atualização e natureza observada/inferida/programada.
- `POI-CONTRATO`: UI só anuncia POI quando endpoint ativo e cobertura conhecida.
- `TARIFA-VIGENCIA`: custo usa regra oficial versionada na data/hora e explica limitações.
- `METRO-FONTE`: nenhuma integração é chamada operacional antes de validar licenciamento, cobertura e atualização.

Validar modais por estrutura, direção, geometria, freshness e estados de falha; POIs por precisão/amostra/licença; tarifas por casos oficiais e datas-limite. Riscos: fonte instável, dado desatualizado, falsa integração tarifária e ampliar o escopo da defesa.

**Decisões pendentes:** metrô na defesa; destino dos POIs; fonte/dono da tarifa; expansão de grades ferroviárias. Recomendação provisória: consolidar os três modais atuais e tratar metrô/POI/tarifa integrada como incrementos independentes.

Relacionados: [modelagem](../04-dados/01-modelagem-conceitual-e-logica.md), [ferrovia](../03-funcionalidades/07-realtime-ferroviario-fluxo-completo.md), [experiência](01-experiencia-atual-e-jornadas-do-passageiro.md) e [rotas](02-rotas-multimodais-e-algoritmos-candidatos.md).
