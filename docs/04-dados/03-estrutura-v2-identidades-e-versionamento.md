# Estrutura V2, identidades e versionamento

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Estado:** implementação e schema local examinados; produção confirmada previamente populada.

## Finalidade

A estrutura V2 desacopla identidade operacional estável de uma geometria mutável. Linha/sentido/padrão são identidades; `PadraoVersao` é uma fotografia espacial; ocorrências ligam visitas ordenadas a locais. Isso permite atualizar feeds sem reinterpretar uma viagem em andamento.

## Entidades, entradas e publicação

Importadores GTFS/Data.Rio e trem produzem um plano validado. `FonteEstrutural` identifica a origem; `ImportacaoEstrutural` registra status, horários, versão, hash, algoritmo, URI opcional e relatório JSONB. Uma importação concluída com mesma fonte/hash/algoritmo não pode duplicar-se pelo índice unique parcial.

Para cada variante, `PadraoOperacional` mantém chave estável e várias versões. Uma versão armazena `geometry(LineString,4326)`, comprimento métrico, topologia linear/circular, hash, método, confiança, algoritmo, validação, relatório e timestamps. `VersaoAtualId` publica uma delas; a FK composta impede apontar para versão de outro padrão.

`PadraoVersaoImportacao` registra por qual importação e papel (`MEMBERSHIP`, `GEOMETRIA`, `PARADAS`, `METADADOS`) a versão foi formada. A estrutura documenta proveniência por aspecto, não apenas “último feed”.

## Ocorrências e circularidade

Cada ocorrência guarda ordem positiva, sequência externa opcional, fração `[0,1]`, distância acumulada e desvio da linha em metros. Unique `(PadraoVersaoId,Ordem)` preserva sequência, mas não proíbe repetir `ParadaId`: repetição é necessária em loops. A volta pertence à viagem/passagem, não à ocorrência estrutural.

Distância restante é obtida ao longo da geometria/ocorrências, não por linha reta. Em topologia circular, cálculo pode somar trecho até o fim e do início ao alvo.

## Identidades externas

Há tabelas separadas para linha, sentido, parada e padrão. Cada registro associa GUID interno a fonte/tipo/external id, origem `FONTE|MANUAL`, confiança opcional `[0,1]` e justificativa. Unique por `(fonte,tipo,external id)` assegura que uma identidade externa resolva para no máximo um alvo daquele domínio. IDs externos não são PKs porque escopo, estabilidade e colisões dependem do fornecedor.

## Overrides

`OverrideOcorrenciaPadrao` expressa inclusão, exclusão ou movimento com justificativa/autor e ciclo ativo/obsoleto. O check exige ordem positiva para incluir/mover e nula para excluir. Ele é entrada de reconciliação, não mutação silenciosa de ocorrências publicadas.

## Transações e consistência

Importadores estruturais usam transações para criar/reutilizar entidades, versões, ocorrências, identidades e publicar ponteiro atual apenas após validação. As constraints impedem muitos estados inválidos; não há transação distribuída com Redis. Viagens já pinadas continuam na versão antiga quando nova publicação ocorre.

## Consultas e índices

- unique sentido+chave resolve padrão estável;
- unique padrão+número/hash implementa versionamento/idempotência;
- GiST na geometria suporta candidatos espaciais;
- versão+ordem/fração suporta próxima ocorrência;
- índices de identidade traduzem payload externo sem varredura.

## Estado produtivo datado

A auditoria de 06/10/2026 confirmou 502 linhas, 951 sentidos, 7.798 paradas, 980 padrões/versões e 54.873 ocorrências. São contagens pontuais. Confirmou também todas as migrations até `RailScheduleFoundation`; não houve nova inspeção operacional nesta etapa.

## Legado e compatibilidade

`Itinerario`/`ParadaItinerario` não são o modelo conceitual vigente. Fachadas de compatibilidade podem traduzir V2 para contratos antigos. Nomes `itinerarioId` em clientes não provam uso da tabela antiga. Remoção física exige inventário separado.

## Limitações, testes, referências e pendências

Imutabilidade não é trigger; confiança é escalar sem taxonomia completa; qualidade de importação permanece parcialmente em JSONB; ausência de regras de validade temporal explícita além da publicação. Evidências: `estruturaTransporteV21.cs`, `DbContext.ConfigurarEstruturaTransporteV21`, migrations V2, `GtfsDatarioPlanPersister`, structural import services e testes `EstruturaFinalEtapas12Tests`/importação. Testes não executados.

