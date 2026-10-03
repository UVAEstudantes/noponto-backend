# Grade ferroviária e próxima etapa operacional

A grade persistida é um snapshot temporal observado. Ela não altera `Linha`, `Sentido`,
`PadraoOperacional`, `PadraoVersao` ou `OcorrenciaParadaPadrao`. Seus patterns são
assinaturas de atendimento e apenas referenciam uma `PadraoVersao` quando o mapeamento
estrutural é seguro.

O calendário V1 distingue `WEEKDAY`, `SATURDAY` e `SUNDAY`. Feriados, grades especiais
e alterações temporárias não são inferidos por esta versão.

## Evolução futura (não implementada)

```text
RailScheduledRun
    ↓ instanciação para uma data real de serviço
ExpectedRun (estado operacional/efêmero)
    ↓ evidência realtime suficiente associada a TrainCode
RailRun
```

Um `RailScheduledRun` isolado nunca deve publicar trem no mapa. `TrainCode` não pertence
à grade e somente poderá ser associado na futura camada realtime. O scanner atual não
consulta nem depende destas tabelas.
