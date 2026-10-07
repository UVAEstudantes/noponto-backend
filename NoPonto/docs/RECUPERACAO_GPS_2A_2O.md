# Etapa 2A.2O — recuperação operacional: reprodução e bloqueio arquitetural

## Resultado

Defeito reproduzido localmente; **não corrigido**. Interrompida a implementação funcional porque cumprir recuperação após restart/perda de Redis requer uma camada de ingestão recuperável e mudanças no contrato de confirmação. O pedido permite interromper essa parte quando exige mudança arquitetural substancial; não foi introduzido bypass de timestamp, fila em memória apresentada como durável, nem política improvisada de descarte. Implantação global continua bloqueada.

Resultados anteriores: 414 locais L, 116 reais M e 426 locais N. O usuário informou nova execução manual de **nove casos reais** da classe MudancaOperacionalPontaAPontaTests, todos aprovados. Não foram reexecutados aqui. Esses nove incluem um teste que comprova ausência de retry, não sua recuperação. O caso novo desta etapa é adicional e permanece pendente.

## Causa raiz e pontos de perda

O fluxo atual é **filtro → enriquecimento → aceite GPS Redis → viagem → commit PostgreSQL/outbox**, não aceite Redis antes do enriquecimento. Fontes são normalizadas, agrupadas pela observação mais recente e filtradas por idade/timestamp. O snapshot SPPO é memória com uma vaga; não é journal durável nem preserva todas as observações descartadas pela seleção do último GPS.

- `GpsPollingService.ProcessarCicloAsync`, linha 293: descarta GPS menor/igual ao anterior lido do cache antes de enriquecer. Uma posição aceita para mapa já pode impedir outra tentativa operacional.
- `GpsEnriquecimentoService.EnriquecerCoreAsync`, linha 82: estado de matching/histerese em memória; `MatchingOperacionalPlausivel` depende de histórico físico e é JsonIgnore. Redis contém DTO, não um ACK operacional nem uma prova persistida reutilizável sem validação.
- `GpsPollingService.ConfirmarPosicaoAsync`, linhas 1093–1101: CAS GPS antes da viagem; chama viagem somente quando GPS aceito. A repetição também pode ser rejeitada por esse segundo portão.
- `ViagemObservadaService.AtualizarInternoAsync`, linha 43: sem versão/fração não chama repositório; exceção vira InfrastructureFailure. O polling retorna o resultado do cache GPS, não o resultado de conclusão operacional.
- `GpsPollingService`, linhas 656–659: consumo do snapshot depende das falhas de cache GPS contabilizadas. Falha operacional após cache aceito não mantém snapshot como ingress recuperável.
- `ViagemOperacionalRepository.TentarAtualizarInternoAsync`, linhas 233–255: **Updated pode ser somente Redis quente**; checkpoint padrão 60s. Logo watermark gravado na presença de Created/Updated não prova que aquele timestamp foi duravelmente confirmado. `RejectedOlderOrEqual` compara contexto que também pode ser quente.
- Repositório, linhas 265–298: estado, integridade e eventos são atômicos na transação. Commit ocorrido continua válido mesmo se resposta/projeção falha depois. Nenhuma tabela contém a observação original pendente antes de um commit que falhou.
- `ViagemOutboxWorker`: materializa apenas eventos já commitados, com journal e idempotência; não recupera GPS ausente no outbox.

GPS/mapa permanecem disponíveis na falha PG. Telemetria de uma falha operacional não recebe associação confirmada pela fábrica ML; a observação pode continuar observacional. Isso evita certos labels incorretos, mas não recupera a evidência perdida. Observação posterior pode fazer catch-up, dependendo da regra; não garante reconstrução de candidato nem de toda passagem.

## O que os nove testes demonstram

Os cinco anteriores cobrem matching real por sentido, confirmação/cancelamento e flag. Dois novos ABB usam códigos diferentes com geometrias sobrepostas, individual/batch; ABCBA substitui candidato sem somar evidências diferentes e cancela no retorno A.

O nono, `FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampNaoReprocessaCandidato` (teste linha 168), induz falha antes do commit, verifica estado anterior preservado, repete GPS e exige **mesma contagem de chamadas**. GPS posterior produz primeira evidência. Verde significa que a perda foi caracterizada corretamente. Sua expectativa não foi alterada para sugerir correção.

Após restart com cache GPS preservado, filtro continua impedindo repetição; memória de matching se perde. Após perda do Redis, pode haver nova tentativa se a fonte reenviar GPS ainda elegível, mas histórico físico/matching precisa reconstrução e a observação pode ter expirado. Não há promessa de replay das fontes estabelecida pelo código inspecionado. Remover projeção operacional Redis não remove necessariamente o watermark do cache GPS: são chaves/contratos diferentes.

## Contrato necessário, ainda não implementado

1. Aceite de mapa é independente de ACK operacional. Não desfazer visualização por rollback PG.
2. Identidade estável da observação deve usar modal/provedor/veículo/timestamp, como ObservacaoId existente; payload divergente na mesma chave deve ser conflito, não sobrescrita silenciosa.
3. ACK operacional deve identificar commit durável ou descarte terminal explicitamente justificado. Created/Updated atuais são insuficientes. Retry não pode considerar qualquer rejeição de timestamp como confirmação daquela observação específica.
4. Observação pendente recuperável exige dado bruto, vínculo com predecessor físico e metadados suficientes para recalcular matching/prova, sem confiar em boolean serializado. Reconsultar contexto/CAS atual; não repetir snapshot operacional antigo.
5. Serializar consumo por veículo com ordenação, lease/CAS e confirmação junto à transação de estado/integridade/outbox. Duplicado após commit não gera segundo evento. ACK perdido deve poder ser reconhecido sem exigir replay de efeitos.
6. Mais recente não pode fazer antiga retroceder. Processar anterior primeiro quando disponível; se já houve avanço ou lacuna, decidir descarte/proteção/baseline de forma explícita, com motivo. Essa política para lacunas não foi aprovada genericamente para viagens lineares.
7. Retenção/capacidade/backoff devem ser limitados e observáveis, sem bloquear mapa indefinidamente. Expiração de pendência não equivale a sucesso e não permite fabricar passagens.
8. Sem fonte com replay ou cópia durável anterior à falha, recuperação após perda do processo+Redis não é garantida. PostgreSQL indisponível também impede aceite numa fila PostgreSQL; esse intervalo precisa garantia condicionada ou outro journal previamente disponível.

## Alternativas e decisão necessária

| Alternativa | Vantagem | Limite / motivo para não implementar isoladamente |
|---|---|---|
| Processar viagem antes de mapa | Evita alguns aceites GPS antes da operação | Portão anterior continua; compromete disponibilidade se acoplado; não guarda GPS em falha/restart. |
| Watermark operacional separado | Evita interpretar timestamp do mapa como ACK | Updated não é sempre durável; não guarda predecessor/prova nem ordena lacunas. Exige contrato de confirmação. |
| Pendência limitada em memória | Retry rápido dentro do processo | Perde em restart; não coordena instâncias nem recupera Redis perdido. Não satisfaz o conjunto de invariantes. |
| Pendência Redis | Compartilha retries entre instâncias | Não sobrevive à perda de Redis prevista; requer lease, retenção e prova recuperável. Não pode ser nova autoridade operacional. |
| Ingress PostgreSQL aditivo | Retém observação independentemente do mapa; possível ACK atômico | Nova tabela/consumer, captura antes de descarte, confirmação durável, política de gaps/expiração e desempenho. Não aceita duravelmente durante indisponibilidade do próprio PG. |

Recomendação: etapa dedicada de ingress operacional PostgreSQL, preservando PostgreSQL como autoridade, com aceite de mapa independente e garantia condicionada ao aceite durável do ingress. Definir comportamento quando ingress indisponível, orçamento de armazenamento/retention e lacunas antes de implementar. Não basta acrescentar uma tabela: fontes/polling selecionam somente último GPS, enriquecimento usa predecessor do mapa e commits quentes não fornecem ACK durável. Alterar esses três contratos conjuntamente é mudança arquitetural substancial. Não preparada migration sem desenho definido; nenhum contrato persistido foi modificado.

## Reprodução e matriz de validação

Adicionados dois casos locais `RecuperacaoPendente_GpsRepetidoDepoisFalha_DeveRetentarOperacao` em ViagemObservadaServiceTests: mesma instância/nova instância, cache controlado aceitando primeiro e rejeitando repetição, falha operacional controlada. Exercitam método produtivo ConfirmarPosicaoAsync; esperam duas chamadas, obtêm uma. **Dois vermelhos esperados**, não mocks de uma correção e não prova SQL. Nova instância testa ausência de estado local recuperável, não desligamento real do processo.

Preparado teste real `RecuperacaoPendente_MesmoGpsDepoisRollback_DeveRecuperarCandidatoEConfirmar`, reutilizando fixture/harness/hook e matching PostGIS reais: exige retry, candidato no timestamp original, confirmação posterior, fim único e zero passagem artificial. Falha desativada em finally. Não executado; esperado vermelho no código atual. Teste antigo de caracterização preservado.

| Cenário | Evidência atual / pendência |
|---|---|
| Falha antes do commit + mesmo GPS | Reprodução local vermelha; caracterização SQL informada verde; novo contrato SQL preparado. |
| Commit ocorreu, resposta falhou | Atomicidade/outbox existentes preservados; testar end-to-end com BeforeDurableProjectionAsync e ACK futuro, não classificar falha de resposta como rollback. |
| Posterior antes da recuperação | Repositório rejeita antigo via timestamp; falta política de gap/ordenação do ingress. Não implementado teste que invente essa política. |
| Concorrência/duplicações | Suítes duráveis/operacionais/outbox existentes; futuro teste de lease/ACK ingress adicional necessário. |
| Restart/perda Redis | Local nova instância reproduz portão; suítes duráveis recuperam viagem, mas não GPS pendente. Futuros testes precisam ingress e predecessor persistidos. |
| Circular protegida em falha | IntegridadeCircularPostgresTests existentes cobrem rollback/proteção/recovery; futuro replay ingress deve manter extensão e labels suprimidos. |
| Ausência de dados incorretos | Regressões locais ML/ETA/circular executadas; futura primeira passagem após replay deve validar identidade/volta/payload/outbox. |

Essa matriz distingue cobertura existente de cobertura ainda não implementável sem contrato; não afirmar que oito cenários do novo retry foram aprovados.

## Operação, custo e próximos passos

Não houve correção funcional nesta etapa, portanto não há novo custo de CPU/RAM/IO/WAL em produção. Um ingress PG acrescentaria escrita por observação, índices e limpeza, além de processamento atrasado: medir carga compartilhada de 4 GiB, backlog, WAL, pool e latência antes de rollout. Não reter trilha sem limite nem consultar histórico inteiro por GPS.

Prioridades: aprovar contrato de aceite durável condicionado/gaps/retention; implementar ingress+ACK com transação existente; tornar vermelhos verdes sem alterar critérios de confirmação; ampliar testes pós-commit/concurrency/restart/Redis/circular; executar reais manualmente; medir carga e resolver compatibilidade de escritores/consumers já documentada em N. Flag desligada não resolve retry nem permite ignorar marker circular. Models/predictors e ETA público permanecem intactos.

Comandos para casos específicos (primeiro local foi executado; SQL abaixo NÃO executado):

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~RecuperacaoPendente_GpsRepetidoDepoisFalha' --verbosity quiet
# Somente depois de verificar conexões para PostgreSQL/PostGIS/Redis descartáveis:
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~RecuperacaoPendente_MesmoGpsDepoisRollback' --verbosity normal
```

Esses filtros retornam falha esperada até solução aprovada. Rodadas amplas com ViagemObservadaServiceTests também incluem os dois vermelhos. Não esconder esses testes com Skip nem declarar conjunto todo verde. Compilação e resultados finais registrados em HISTORICO.md. Arquivos desta etapa: dois arquivos de testes, este relatório e histórico; alterações anteriores preservadas. Sem Docker/serviços reais/SSH/produção/migrations/deploy/commit/ativação/infraestrutura.
