# 04 — Redis, SignalR e publicação

**Finalidade:** definir aceite operacional, snapshots, TTL, REST, broadcast e desaparecimento.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Verificação:** scripts Lua, repositórios, hub e consumidores inspecionados; Redis de produção previamente confirmado ativo. Testes não executados nesta etapa.

## 1. Quando uma posição é aceita

Aceite operacional é exclusivamente `PosicaoVeiculoCacheStatus.Accepted`, devolvido quando `PosicaoVeiculoCacheRepository.TentarAtualizarAsync` consegue:

1. lock `veiculo:{ordem}:gps-lock` com token, TTL 15 s, até 60 tentativas espaçadas 25 ms;
2. serializar o DTO;
3. executar o Lua com ownership intacto;
4. validar argumentos/tipos/ACLs e constatar `novo_timestamp > atual`;
5. gravar integralmente timestamp, ativo e recente e liberar o lock.

Chaves:

| Chave | Conteúdo | TTL default |
|---|---|---:|
| `veiculo:{ordem}:ts` | Unix ms monotônico | `max(ativo,recente)` = 180 s |
| `veiculo:{ordem}:ativo` | hash compatível com `IDistributedCache`, JSON | 40 s |
| `veiculo:{ordem}:recente` | mesmo JSON | 180 s |
| `veiculo:{ordem}:gps-lock` | token fenced | 15 s |
| `linha:{codigo}:veiculos` | CSV de ordens | 180 s |
| `veiculo:{ordem}:posicao-causal` | hash C versionado | 300 s |
| `veiculo:{ordem}:viagem` | projeção quente da viagem | política própria do repositório |

O script faz preflight antes dos writes; Redis Lua impede interleaving, mas não promete rollback diante de falha catastrófica de processo/alocação. Timeout com resposta perdida é tratado como infraestrutura ambígua e não dispara compensação.

Timestamp igual/menor é rejeição normal sem mudar as três chaves. Lock perdido, tipo inesperado, estado incoerente, ACL ou exceção viram falha de infraestrutura. Somente `Accepted` aciona viagem, ETA V2 e telemetria.

## 2. Ordem real das fases

`matching → ETA legado → preparação causal C → CAS posição B → [viagem → ETA V2 → telemetria] por veículo aceito → persistência C/shadow → índice de linha → SignalR`.

Detalhes importantes:

- ETA legado ocorre antes do aceite e seus campos entram no JSON se houver previsão.
- viagem é chamada dentro do processamento do resultado aceito; falha não muda o status B.
- telemetria e ETA V2 são posteriores à viagem e fail-open.
- estado causal é preparado antes, mas seu CAS acontece após o lote B e verifica o timestamp B corrente.
- índice por linha e SignalR não fazem parte do Lua de aceite.
- histórico/outbox é consequência da viagem e/ou workers assíncronos, não da mesma transação Redis.

## 3. Índices e snapshots

Para cada linha com aceitos, o ciclo lê seu índice anterior e mantém ordens cujo `:recente` ainda existe; depois grava o conjunto mesclado com TTL 180 s. Troca de linha conhecida em `_linhaPorVeiculo` remove a ordem do índice antigo. Esse mapa é memória local; restart perde a lembrança de trocas anteriores.

Para uma linha assinada:

- aceitos deste ciclo entram como `Ativo`;
- ordens indexadas que não vieram no ciclo são lidas em `:recente` e entram como `SemSinal`;
- sem `:recente`, são omitidas;
- payload corrompido é ignorado.

`VeiculosLinhaRuntimeReader` aplica a mesma política no REST: tenta ativo e depois recente. `/veiculos/{ordem}` devolve um item; `/veiculos/linha/{codigo}` devolve contagens e posições, ou 404 se o índice não possui ordens. Portanto REST e SignalR derivam das mesmas chaves, mas são leituras separadas e podem observar instantes distintos.

## 4. SignalR

`GpsHub` expõe:

- `InscreverseLinha(codigoLinha)` → normaliza uppercase, adiciona conexão ao grupo `linha:{codigo}` e registra demanda;
- `CancelarLinha(codigoLinha)` → remove do grupo e recalcula linhas ativas;
- `OnDisconnectedAsync` → remove estado da conexão.

`GpsPollingService` lê `GpsHub.LinhasComAssinantes`. Por default, só essas linhas são enriquecidas e publicadas; posições de outras linhas ainda podem ser aceitas, com histórico de velocidade, mas sem estrutura V2. O evento `PosicaoAtualizada` contém `FrontendLegacyPosicaoSignalRDto[]`, alias de saída compatível que preserva IDs V2 e campos de posição.

Cada envio representa um **snapshot completo da linha naquele ciclo**, combinando ativos e recentes. Não é delta por veículo. O frontend substitui todos os veículos das linhas contidas no evento.

Não foi encontrado push inicial dentro de `InscreverseLinha`; o cliente espera o próximo ciclo. Também não há backplane Redis para SignalR. `_linhasPorConexao`, `_linhasAtivas`, padrão/velocidade e handoff são locais ao processo. Em múltiplas APIs, uma instância pode coletar sem conhecer assinantes conectados a outra, e grupos não são compartilhados sem infraestrutura adicional.

## 5. Ciclo de vida e desaparecimento

1. primeira leitura aceita cria `:ts`, `:ativo` e `:recente`;
2. atualizações estritamente novas renovam os TTLs;
3. após ~40 s sem aceite, `:ativo` expira; `:recente` mantém a posição até ~180 s e ela aparece `SemSinal`;
4. após ~180 s, payload recente e controle expiram; o merge subsequente deixa de conservar a ordem;
5. o índice de linha também expira se não for renovado.

Limite crítico: broadcast só é adicionado quando `veiculosDaLinha.Count > 0`. Uma linha assinada sem qualquer ativo/recente não recebe `[]`. Como o frontend precisa de snapshot da linha para remover seus veículos, o último marcador pode permanecer indefinidamente se nenhum evento posterior daquela linha chegar. Assim, expiração Redis está implementada; reconciliação visual completa do snapshot vazio **não está comprovada**.

Troca de linha remove o veículo do índice anterior somente quando o processo a detecta em memória. O evento da nova linha não substitui automaticamente a antiga no frontend, pois snapshots são particionados por código; a linha antiga precisaria de novo snapshot para remoção.

## 6. Reconexão

O cliente usa `withAutomaticReconnect()`. Entretanto, `gpsHub.ts` apenas registra logs em `onreconnected`; não reinvoca `InscreverseLinha`. Grupos pertencem à conexão SignalR, portanto reinscrição automática não foi comprovada. Também não existe fallback REST automático no hook atual.

## 7. Falhas

- Redis indisponível na leitura inicial pode abortar o ciclo pelo tratamento externo; no CAS vira `InfrastructureFailure` e nada avança.
- falha ao gravar índice ocorre depois do aceite: posição/viagem podem existir sem publicação daquele ciclo.
- falha SignalR ocorre depois do aceite e dos índices; não há fila/replay por cliente.
- payload recente inválido é isolado e omitido.
- falha causal C não revoga B; falha de viagem/telemetria/ETA também não.
- resposta perdida do Lua pode ter aplicado B, mas o processo não anuncia sucesso sem confirmação; retry igual será rejeitado.

## 8. Testes e complexidade

`PosicaoVeiculoCacheRepositoryTests` cobre novo/antigo/igual, concorrência, locks, fencing, tipos, TTL 40/180, resposta perdida e zero writes em preflight. `VeiculosLinhaRuntimeReaderTests` cobre ativo/recente. `EstadoCausalPosicaoRedisTests`, `ViagemObservadaRepositoryTests` e integrações de viagem cobrem os outros CAS. Não foi localizada suíte backend específica que conecte `GpsHub` real a um cliente; a reconciliação frontend tem testes unitários.

O lock pode esperar nominalmente até `60 × 25 ms` mais operações (~1,5 s, sem contar scheduling). Merge de linha é proporcional ao número de ordens indexadas e faz múltiplas leituras Redis; o multiplexer as pipelineia. Não há medição atual de cardinalidade/latência produtiva nesta etapa.

## 9. Diagrama futuro, referências e pendências

**Sequência Redis/SignalR:** Polling → lock → Lua B → viagem → C → índice → leitura ativos/recentes → HubContext/grupo → app.  
**Participantes:** `PosicaoVeiculoCacheRepository`, `PosicaoVeiculoPayloadWriter`, `CorrecaoTemporalPosicaoCoordinator`, `ViagemObservadaService`, `VeiculosLinhaRuntimeReader`, `GpsHub`, `GpsPollingService`.

Referências: `PosicaoVeiculoPayloadWriter.cs`; `PosicaoVeiculoCacheRepository.cs`; `GpsPollingService.cs:452-640`; `EstadoCausalPosicaoRepository.cs`; `GpsHub.cs`; `VeiculosController.cs`; `VeiculosLinhaRuntimeReader.cs`.

Pendências prioritárias: publicar snapshot vazio com identidade de linha, armazenar/reaplicar inscrições após reconnect, testar troca de linha e decidir backplane/coordenação para múltiplas instâncias.
