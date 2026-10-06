# DER — estrutura de transporte V2

**Objetivo:** explicar identidade estável, versão e ocorrências. **Nível:** lógico/físico resumido. **Baseline:** migrations até a baseline.

```mermaid
erDiagram
  MODAL ||--o{ LINHA : possui
  LINHA ||--o{ SENTIDO : possui
  SENTIDO ||--o{ PADRAO_OPERACIONAL : organiza
  PADRAO_OPERACIONAL ||--o{ PADRAO_VERSAO : versiona
  PADRAO_VERSAO ||--o{ OCORRENCIA_PARADA_PADRAO : ordena
  PARADA ||--o{ OCORRENCIA_PARADA_PADRAO : referencia
  FONTE_ESTRUTURAL ||--o{ IDENTIDADE_EXTERNA : declara
  IDENTIDADE_EXTERNA }o--|| LINHA : identifica
  IMPORTACAO_ESTRUTURAL ||--o{ PADRAO_VERSAO : produz

  MODAL { uuid Id PK }
  LINHA { uuid Id PK uuid ModalId FK string Codigo }
  SENTIDO { uuid Id PK uuid LinhaId FK }
  PADRAO_OPERACIONAL { uuid Id PK uuid SentidoId FK }
  PADRAO_VERSAO { uuid Id PK uuid PadraoOperacionalId FK geometry Geometria int Numero bool Publicada }
  OCORRENCIA_PARADA_PADRAO { uuid Id PK uuid PadraoVersaoId FK uuid ParadaId FK int Ordem double Fracao }
  PARADA { uuid Id PK geometry Localizacao }
```

Uma parada física pode ocorrer várias vezes no padrão; por isso ocorrência, ordem e versão são indispensáveis. `PadraoOperacional` é identidade estável e `PadraoVersao` preserva mudanças de geometria. Proveniência/importação foi simplificada; algumas identidades externas são polimórficas e não formam FK física única como sugeriria um DER ingênuo.

**Fonte:** DbContext, migrations e [estrutura V2](../04-dados/03-estrutura-v2-identidades-e-versionamento.md). **Limitações:** campos secundários omitidos. **Uso:** modelagem e justificativa de versionamento.
