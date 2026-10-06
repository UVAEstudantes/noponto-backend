# DER — viagens, histórico, telemetria e ETA

**Objetivo:** mostrar persistência operacional e identidades de avaliação. **Nível:** físico resumido, com referências lógicas marcadas. **Baseline:** 2026-10-06.

```mermaid
erDiagram
  VIAGEM_OPERACIONAL ||--o{ OUTBOX_VIAGENS : gera
  VIAGEM_OPERACIONAL ||--o{ EVENTOS_VIAGEM : referencia_logica
  VIAGEM_OPERACIONAL ||--o{ HISTORICO_PASSAGENS : referencia_logica
  VIAGEM_OPERACIONAL ||--o{ TELEMETRIAS_VEICULO_ML : contextualiza
  VIAGEM_OPERACIONAL ||--o{ PREVISOES_ETA_V2 : agrupa
  PADRAO_VERSAO ||--o{ TELEMETRIAS_VEICULO_ML : fixa
  PADRAO_VERSAO ||--o{ PREVISOES_ETA_V2 : fixa
  OCORRENCIA_PARADA_PADRAO ||--o{ HISTORICO_PASSAGENS : passagem
  OCORRENCIA_PARADA_PADRAO ||--o{ PREVISOES_ETA_V2 : alvo

  VIAGEM_OPERACIONAL { string OrdemVeiculo PK jsonb Estado bigint Versao datetime AtualizadoEmUtc }
  OUTBOX_VIAGENS { uuid Id PK string Tipo jsonb Payload string Status datetime CriadoEmUtc }
  EVENTOS_VIAGEM { uuid Id PK uuid ViagemId string Tipo datetime OcorridoEmUtc }
  HISTORICO_PASSAGENS { uuid Id PK uuid ViagemId uuid OcorrenciaId int Volta datetime TimestampPassagem }
  TELEMETRIAS_VEICULO_ML { uuid Id PK uuid ViagemId uuid PadraoVersaoId datetime TimestampGps string Provider }
  PREVISOES_ETA_V2 { uuid Id PK uuid ViagemId uuid OcorrenciaAlvoId int Volta string Preditor string Versao string Estado datetime PrevistoPara datetime RealizadoEm double ErroSegundos }
```

`ViagensOperacionais.Estado` guarda agregado JSON; algumas relações acima são identidades dentro de payload/colunas e não FKs em todos os casos. A previsão usa viagem + versão + ocorrência + volta para evitar fechar contra visita errada. Ground truth pode ser interpolado; sua qualidade não tem campo explícito.

**Fonte:** migrations de viagem/outbox, telemetria e ETA; [dados operacionais](../04-dados/05-viagens-historico-telemetria-e-eta.md). **Limitações:** conferir nome/constraint na migration antes de publicar figura física final. **Uso:** persistência, outbox e avaliação ETA.
