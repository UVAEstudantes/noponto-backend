# Modelagem conceitual e lógica

**Análise:** 2026-10-06 · **Commits:** backend `53567bd`, frontend `c69a92e`, ML `08c493b` · **Verificação:** código, migrations e auditoria produtiva anterior; nenhuma consulta ou escrita nova em produção.

## Finalidade e estratégia de armazenamento

O modelo vigente separa quatro naturezas de dados. PostgreSQL/PostGIS é a autoridade durável da estrutura, viagens/checkpoints, eventos, passagens, schedules, telemetria e previsões. Redis contém posições quentes, índices de linha, estado causal, projeção quente de viagem e streams de entrega assíncrona. Memória do processo mantém snapshots, caches e trackers ferroviários que podem ser reconstruídos. Arquivos de coleta/builder são artefatos offline, não tabelas operacionais.

| Natureza | Exemplos | Armazenamento principal |
|---|---|---|
| estrutural | linha, sentido, padrão, versão, parada, ocorrência, identidades | PostgreSQL/PostGIS |
| operacional quente | posição ativa/recente, causalidade, contexto de viagem | Redis + memória; checkpoint de viagem no PostgreSQL |
| histórico | eventos, passagens, telemetria, previsões ETA | PostgreSQL, alimentado por streams/outbox |
| ferroviário | versões de grade, patterns, runs, stops | PostgreSQL; expected runs/tracker em memória |

## Modelo conceitual V2

```text
Modal 1 ── N Linha 1 ── N Sentido 1 ── N PadraoOperacional
                                                │ 1
                                                N PadraoVersao 1 ── N OcorrenciaParadaPadrao N ── 1 Parada
```

- **Modal** classifica a categoria cadastrada. Ônibus/BRT não devem ser inferidos apenas do registro modal: `Linha.TipoRota` também diferencia serviços.
- **Linha** é a oferta pública estável (`Codigo`, `Nome`, modal); possui sentidos e identidades externas.
- **Sentido** pertence obrigatoriamente a uma linha. Não contém por si só toda variante física.
- **PadraoOperacional** é a identidade estável de uma variante de serviço dentro do sentido. `Chave` é única por sentido; `VersaoAtualId` aponta para a versão publicada.
- **PadraoVersao** congela geometria, topologia, comprimento, hash, algoritmo e validação. Uma viagem fixa essa versão para não misturar geometrias durante sua execução.
- **OcorrenciaParadaPadrao** representa uma visita ordenada a uma parada na versão. A mesma `Parada` pode ocorrer várias vezes; por isso ETA/passagem usam ocorrência e volta, não apenas o local físico.
- **Parada** representa parada, plataforma ou estação por `TipoLocal`; ferrovia reutiliza a mesma entidade, sem hierarquia C# separada. `ParadaPaiId` permite agrupamento opcional.

## Modelo lógico e cardinalidades verificadas

| Origem | Relação | Destino | Cardinalidade/obrigatoriedade | FK e exclusão |
|---|---|---|---|---|
| Modal | possui | Linha | 1:N; linha obrigatória | `Linha.ModalId`, comportamento por convenção/migration |
| Linha | possui | Sentido | 1:N; sentido obrigatório | `Sentido.LinhaId` |
| Sentido | possui | PadraoOperacional | 1:N | FK, `Restrict` |
| PadraoOperacional | versiona | PadraoVersao | 1:N | FK, `Restrict` |
| PadraoOperacional | publica | PadraoVersao | 0..1 atual | FK composta `(Id,VersaoAtualId)` garante versão do próprio padrão; `Restrict` |
| PadraoVersao | contém | Ocorrência | 1:N | cascade ao remover versão |
| Ocorrência | referencia | Parada | N:1 | `Restrict` |
| FonteEstrutural | produz | ImportacaoEstrutural | 1:N | `Restrict` |
| Versão | deriva de | Importação | N:N com papel | associação composta; cascade da versão, restrict da importação |
| entidade interna | possui | identidade externa | 1:N | fonte + tipo + external id únicos; cascade do alvo, restrict da fonte |

O modelo não garante imutabilidade absoluta de `PadraoVersao` por trigger. A disciplina decorre de criar nova versão, hashes/unicidade e publicação; updates continuam tecnicamente possíveis por SQL/EF autorizado.

## Veículo, viagem e passagem

O identificador do veículo (`Ordem`) identifica o equipamento/serviço observado, não uma viagem. `ViagemId` representa uma execução operacional sobre linha, sentido, padrão e versão; um veículo pode criar várias viagens. Estados de domínio: `Ativa`, `PossivelFim`, `Finalizada`. A volta distingue ciclos em topologia circular.

Uma passagem é logicamente única por `(ViagemId, OcorrenciaParadaPadraoId, Volta)`. O timestamp pode ser interpolado entre observações GPS e essa qualidade não é persistida explicitamente. `EventosViagem` é journal idempotente; `HistoricoPassagens` é projeção relacional de eventos `PassagemParada`.

## Ferrovia no modelo conceitual

Uma `RailScheduleVersion` pertence a uma linha e contém patterns e runs. Pattern é sequência de estações; run é uma execução horária/calendário; stop é a visita programada de um run a uma estação. O pattern pode mapear opcionalmente a `PadraoVersao` V2. `ExpectedRun` não é tabela: é materializado em memória a partir de run + data de serviço + versão ativa.

## Integridade, entradas e saídas

Imports escrevem estrutura/versionamento; matching e APIs leem versões publicadas; GPS produz viagem/eventos; workers materializam histórico; schedule import escreve grade; runtime ferroviário lê grade; ETA fecha previsões com passagens. IDs externos nunca substituem GUIDs internos porque fontes podem colidir ou mudar; tabelas de identidade guardam fonte, tipo, origem do mapeamento, confiança e justificativa.

## Limitações, testes e pendências

Feriados ferroviários não são modelados; qualidade observada/interpolada da passagem não é coluna; imutabilidade de versão é convencional; algumas entidades antigas permanecem no histórico de migrations. Testes relevantes: `EstruturaFinalEtapas12Tests`, importações GTFS/trem, `ViagemOperacional*`, `RailScheduleImportTests` e integridade ETA. Não executados.

## Evidências e preparação de diagramas

Fontes: `estruturaTransporteV21.cs`, entidades base, `DbContext.OnModelCreating`, migrations V2/viagem/rail/ETA e repositórios. Para DER, usar visão geral acima e subdiagramas: estrutura, ferrovia, viagem/histórico e telemetria/ETA; não incluir objetos em memória como tabelas.

