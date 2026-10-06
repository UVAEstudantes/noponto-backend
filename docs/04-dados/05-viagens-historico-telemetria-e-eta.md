# Viagens, histórico, telemetria e ETA

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Verificação:** entidades, SQL direto, Lua, workers e migrations; ETA V2 permanece desabilitado.

## Viagem operacional

`ViagemOperacionalState` combina observação pinada (`ViagemId`, veículo, versão, timestamps, fração/cursor/volta) com linha, sentido, estado, confirmações de terminal e candidato à próxima viagem. Estados: `Ativa`, `PossivelFim`, `Finalizada`. A viagem não é o veículo: o mesmo `Ordem` inicia outra instância após término e evidência de movimento compatível.

PostgreSQL é autoridade durável. O repositório consulta primeiro `veiculo:{ordem}:viagem`; se ausente/inválido, lê a tabela durável e reprojeta Redis. Checkpoints ocorrem em eventos/transições e, no máximo, pelo intervalo configurado (60 s; ≤0 persiste toda posição). Atualizações quentes intermediárias podem ficar apenas na projeção Redis.

## Concorrência e outbox

Na persistência durável, `ViagemOperacionalRepository` abre transação `ReadCommitted`, adquire `pg_advisory_xact_lock(hashtextextended(ordem,0))`, relê o estado com lock, valida versão/timestamp, grava estado e eventos/outbox antes do commit. Isso serializa por veículo dentro do PostgreSQL, não globalmente.

O outbox evita perder eventos quando o checkpoint durável é confirmado: estado e evento pendente compartilham transação. Worker publica/persiste depois, com retry e retenção. A garantia é entrega pelo menos uma vez com consumidores idempotentes, não exactly-once. EventId e uniques absorvem repetição; conflito de mesmo ID com payload diferente vira erro/DLQ.

## Passagem e histórico

Transição sobre ocorrências gera `PassagemParada` identificado por viagem+ocorrência+volta. `TimestampPassagem` interpola entre frações somente sob condições causais e intervalo ≤180 s; caso contrário usa timestamp GPS atual. O banco não armazena flag “interpolada”.

`HistoricoEventoRepository` grava `EventosViagem` e `HistoricoPassagens` na mesma transação e valida ocorrência contra versão, padrão, parada, ordem, fração, sentido, linha e código. Unique parcial `(ViagemId,Ocorrencia,Volta)` fornece idempotência estrutural. ACK do stream ocorre após commit.

## Telemetria ML

`TelemetriasVeiculoMl` recebe somente observação aceita selecionada pelo sampling. `ObservacaoId` é SHA-256 de modal/provedor/veículo/timestamp GPS e possui unique index. Campos incluem coordenada recebida/projetada, velocidade/bearing, timestamps, linha/sentido, viagem, padrão/ocorrência/volta, fração/comprimento, distância e média causal; contexto estrutural é nullable para preservar observações parcialmente enriquecidas.

Fluxo: `TryWrite` em channel bounded → publisher em lote → Redis Stream → consumer group → `TelemetriaMlRepository` com inserção idempotente. Channel cheio descarta sem bloquear GPS. Redis/worker têm pending, retry, DLQ e trim seguro; a tabela PostgreSQL não possui política de retenção identificada equivalente ao stream.

## Previsões ETA V2

Uma linha `PrevisoesEtaV2` fixa veículo, viagem, linha, sentido, padrão, versão, ocorrência, ordem, volta, `TimestampGps`, `TimestampPrevisao`, fração, metros restantes, km/h, bearing, modal/provedor, preditor/versão, ETA ou motivo e estado.

Checks exigem contexto válido; ETA nulo se e somente se há motivo; e ground truth somente para `REALIZADA`. Estados permitidos:

- `PENDENTE`: aguardando passagem;
- `REALIZADA`: passagem, ETA real e erros preenchidos;
- `EXPIRADA`: excedeu janela;
- `INVALIDADA`: nova viagem do mesmo veículo invalidou contexto anterior.

Persistência usa advisory locks por veículo e target; impede amostra duplicada dentro do sampling. Fechamento ocorre dentro da mesma transação de histórico e exige veículo, viagem, linha, sentido, padrão, versão, ocorrência, volta e previsão anterior à passagem. Passagem duplicada não refecha porque só `PENDENTE` é atualizada.

## Falhas e consistência

- Redis GPS aceito e checkpoint PostgreSQL falha: posição pública pode existir sem avanço durável; próxima tentativa/recovery reconcilia, sem atomicidade distribuída.
- viagem falha após posição aceita: GPS permanece; ETA/telemetria dependem do bloco posterior.
- channel cheio: evento perdido e métrica, sem replay.
- worker reinicia: pending stream pode ser reclamado; itens apenas em channel morrem com processo.
- versão nova publicada: viagem continua com versão pinada; matching divergente precisa projeção compatível.

## Timeout ETA legado — reconciliação documental

`Program.cs:295-300` configura `GpsEtaClient` com **3 segundos**. Documentos 2.3A já registram 3 s; `14-eta-legado-e-migracao-v2.md` mencionou 5 s incorretamente. O valor vigente comprovado é 3 s; o histórico não foi editado nesta etapa.

## Limitações, testes e referências

Não há distinção persistida da qualidade da passagem; históricos crescem sem particionamento identificado; outbox/stream não oferecem exactly-once; ETA V2 está OFF. Referências: `ViagemOperacional*`, `HistoricoEventoRepository`, workers/outbox, `TelemetriaMl*`, `EtaV2*`, migrations e testes correspondentes. Não executados.

