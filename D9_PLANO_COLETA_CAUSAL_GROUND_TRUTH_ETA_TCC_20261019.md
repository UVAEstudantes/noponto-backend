# D8 encerrada; D9 — coleta prospectiva e comparação científica

Data: 19/10/2026. Escopo: documentação e planejamento. Nenhuma captura real,
ativação de flag, serviço, treinamento ou implementação D9 foi executada.

**Estado em 19/10/2026: desenvolvimento ETA/ML temporariamente pausado por decisão
do usuário.** D0–D8 e este plano D9 ficam preservados na branch
`chore/eta-ml-d0-d8-checkpoint`. A prioridade passa à auditoria do fluxo GPS de
ônibus/BRT, em escopo separado. As tarefas propostas abaixo não estão em execução;
a retomada do desenvolvimento ML depende de nova orientação. A pausa não altera
configurações, funcionalidades operacionais ou os critérios científicos existentes.

## 1. Encerramento D8

**PASS de integração sintética**, sem certificação científica. Sexta e única
execução desta rodada: `d8-run-257a2c6bc48a479ab33bbcedb98c5e88`, em
`NoPonto/lab-output/`, branch `chore/eta-ml-d0-d8-checkpoint`, checkpoint local
`31cca6026e6974f27b9fc86ae94c6d54759993e3`. Artefatos permanecem privados e fora do Git.

| Evidência da execução | Resultado |
|---|---|
| Código de saída preservado | 0 |
| `d8-prepare.trx` | Exatamente `D8_PrepareSchemaExplicitly`: 1 aprovado, 0 falhas |
| `d8-capture.trx` | Exatamente `D8_CaptureThroughActualGpsCasAndShadow`: 1 aprovado, 0 falhas |
| `d8-verification.json` | CAS aceitou 2; exportou 2; DTO verificado; chamadas HTTP externas = 0; replay científico inelegível |
| JSONL | 3 linhas válidas: 2 observações `data_kind=fixture` e 1 resumo completo; attempted=enqueued=written=2 |
| Integridade SHA-256 do JSONL | `081990DF64CE965B0ED1BCE82C6A3296170AB74593EA858FB0348BC04A3F2C77` |
| Identidade e D7 | Asserções conectadas conferem SHA dos metadados originais, dois eventos, Shadow e snapshots; média 20 km/h, janelas de 1 e 2 amostras; ETA 80/confiança alta |
| Tempos não comprovados | `CalculatedAt=null`, `FeatureCutoffUtc=null` |
| Rede/endpoints | Bridge exclusiva não interna confirmada; PostgreSQL e Redis somente em 127.0.0.1 |
| Receipt/cleanup | `d8-resources.json` é o recibo dos recursos; `d8-cleanup.json` registra os dois containers encerrados e rede removida, todos success=true, sem erro do ensaio |

No encerramento da sexta tentativa, inventários por ownership comprovaram zero
containers/redes remanescentes. IDs, StartedAt, RestartCount=0 e estado running
de transporte_redis, transporte_postgres, auth-postgres e auth-redis permaneceram
idênticos antes/depois. Essa comparação foi realizada naquela tarefa; o fechamento
documental atual releu apenas os resumos/contagens/hash dos artefatos, sem Docker,
sem emitir payloads e sem repetir o ensaio. Não há `receipt.json` separado.

## 2. Matriz de evidências D1–D8

| Componente | Evidência disponível | Falta para uso científico real |
|---|---|---|
| Previsão operacional | Witness do DTO final após `PhysicalEta.Apply`; ETA/confiança originais; contrato distingue ausência, DTO indisponível, ETA nulo, zero e inválido | Captura prospectiva real, versão do cálculo/configuração e semântica do instante ao qual os segundos se referem. O polling não comprova saída posterior do endpoint público |
| Identidade/CAS | SHA-256 de modal/provedor/veículo/tGPS originais; hook pós-CAS; viagem, versão, ocorrência-alvo e volta associados | Vínculo real auditável e fronteiras da execução. Hash e CAS provam associação/ordem de atualização, não autenticidade do relógio ou chegada |
| Tempo GPS/servidor | TimestampGps, RecebidoEmUtc, CapturedAt e RecordedAt com papéis distintos | Sincronização/erro dos relógios, emissão remota legada e semântica de recebimento por fonte; não intercambiar esses tempos |
| Velocidade D6/D7 | Valores e sequência imutáveis, tempos disponíveis, SHA e referência à mesma execução; disponibilidade local avaliada quando há referência de cálculo | Dados reais suficientes e prova temporal de TODAS as entradas; maior timestamp da janela não é corte global |
| ETA físico | CalculatedAt representa a referência `physicalNow` passada ao cálculo; diagnóstico do caminho efetivamente aplicado | Disponibilidade/versão da geometria e matching, predecessor realmente consultado, seleção operacional e parâmetros na mesma execução; referência do cálculo não é emissão externa |
| ETA legado | Valor final preservável sem mudar fórmula | Cliente não fornece emissão remota nem proveniência das features. CalculatedAt permanece nulo; relógio de resposta/captura não pode preenchê-lo |
| Replay D2/D4 | Núcleo e leitor offline existentes; verificações de identidade/rota/relógios | ToReplay recusa cálculo/corte ausentes; nenhuma captura real foi certificada. Preservar as recusas |
| Labels D1 | Bundle histórico íntegro em relação ao manifesto; passagens/journal e candidatos existentes | Zero chegadas independentes e viagens integralmente auditadas demonstradas em D1; interpolação/journal não são sensor externo |
| Comparador/ExtraTrees | Exportador, pipeline, avaliadores e bundle histórico existentes | `evaluate_shadow.py` não avalia automaticamente `operational_dto`: a comparação física usa public_eta. Bundle íntegro não prova features as-of, ausência de treino sobre TEST nem generalização ao regime atual |

Fontes locais: relatório D1, relatório D4, relatório D5 (as limitações de velocidade
ali descritas foram parcialmente superadas por D6/D7), entrada D7 do histórico,
`OperationalEtaCapture`, `OperationalEtaCaptureContract`, `OperationalEtaReplay`,
`HistoricalEtaShadow`, `HistoricalEtaOfflineExport` e `evaluate_shadow.py`.

## 3. Coleta real proposta — somente após autorização específica

Começar por uma janela curta, previamente delimitada por operador, linha/versão,
viagens e paradas, com testemunho independente disponível. Usar o hook, fila e
exportador existentes. Os opt-ins necessários são `HistoricalEtaShadow.Enabled`,
`OperationalDtoCaptureEnabled` e `OfflineExport.Enabled`; ML/Histórico continuam
desligados no primeiro piloto. `Fixture=false` apenas para fonte comprovadamente
real: a flag sozinha não autentica o dado. Nenhuma configuração foi aplicada.

O código atual não demonstra filtro de captura por viagem/linha nesses opt-ins.
Não inventar uma opção: a autorização deve cobrir a janela e seu volume inteiro,
ou exigir um filtro mínimo revisado antes de ativar. A seleção analítica pode ser
restrita depois, com critérios fixados antes de observar erros, sem ocultar perdas.

Manter quotas/fila/escrita/limite de linha existentes; medir no piloto tamanhos,
latência GPS/CAS, descartes, fila e disco. Defaults do exportador incluem 4096
bytes por linha, 16 MiB totais e retenção de 24h. Preparar custódia autorizada dos
arquivos fechados ANTES da rotação/expiração, com hashes, release, configuração,
regime, início/fim, política de sampling, falhas e denominadores de cada estágio.
Não aumentar retenção nem transportar dados sem revisão de privacidade.

O JSONL omite veículo bruto/coordenadas e não permite inverter o SHA para achar
um veículo. Parear localmente com telemetria original existente e registros de
viagem por ObservationId; conservar modal/provedor/Ordem/tGPS originais em custódia
restrita para recomputar o hash. Não usar o DTO Redis reidratado para recuperar os
metadados JsonIgnore. Se sampling/retenção tiver perdido o vínculo, marcar perda;
não reconstruí-lo por proximidade. Publicar apenas agregados e identificadores
pseudonimizados, mantendo a correspondência protegida fora do Git.

Riscos: perda não bloqueante na fila/exportação, truncamento por orçamento,
rotatividade de arquivos, sampling, reidentificação, mudança de versão/configuração,
restarts e relógios inconsistentes. Interromper a captura experimental se houver
regressão operacional, destino incorreto, identidade inconclusiva ou quota excedida;
desativar somente os opt-ins aprovados e preservar evidências. Nunca limpar banco
ou mudar ETA para recuperar o ensaio. Falta de label não deve bloquear o GPS.

## 4. Ground truth independente e pareamento

Não foi demonstrada uma fonte independente já disponível. Primeira opção proposta,
sujeita a viabilidade/autorização: observador humano numa parada definida, ou
registro original de um sistema independente se o custodiante comprovar sua
existência, relógio e origem. Não presumir câmera, sensor de porta ou integração.
O observador não consulta ETA/Shadow para decidir ou cronometrar a chegada.

Antes de coletar, fixar o evento físico (por exemplo, primeiro ingresso do veículo
na área de embarque delimitada), parada/ocorrência/sentido, regras para passagem sem
parada e repetição circular, resolução/erro máximo do relógio e tolerância de
anotação. Registrar fonte, autor, instante/intervalo de incerteza, veículo observado,
parada, evidência original e hashes; conferir relógio antes/depois e revisar conflitos
sem consultar qual modelo errou menos. Limites numéricos serão pré-registrados após
ensaio de anotação, antes de liberar o TEST. Intervalos não viram instantes exatos
por arredondamento ou ponto médio silencioso.

Classificação nova, sem alterar labels antigos:

- **Observada independente:** evento físico identificado com evidência original,
  vínculo veículo/parada inequívoco e erro temporal auditado. Independência precisa
  ser comprovada em relação ao GPS/matching que alimentou o ETA.
- **Inferida/interpolada/estimada:** derivada de GPS, passagem interpolada, geofence,
  matching ou estimador; manter método e classe originais, apenas diagnóstico separado.
- **Não verificável/não auditada:** sem prova suficiente, relógio/vínculo ambíguo,
  conflito, falha de observação ou execução incompleta. Excluir do erro primário,
  contabilizar cobertura. Ausência de chegada registrada não significa ETA zero.

Chave de previsão: ObservationId canônico + viagem + linha/sentido/padrão/versão +
ocorrência-alvo + parada física + volta + tGPS, e instante/política de previsão
quando comprovados. A chegada independente é ligada por veículo sob custódia,
execução auditada e ocorrência/volta; tGPS não é instante de chegada. Repetições ou
múltiplas candidatas exigem resolução documentada ou exclusão, nunca join pelo mais
próximo. Várias previsões podem compartilhar uma chegada; contar viagens/chegadas
distintas e não tratar essas previsões como chegadas independentes.

## 5. Elegibilidade e protocolo pareado

Arquivar previsão original não equivale a certificá-la para replay. Para a análise
primária, exigir cumulativamente:

1. Origem real comprovada, identidade integral e CAS aceito; execução inteira com
   fronteiras confiáveis, regime e histórico de proteções/conflitos auditados.
2. DTO original válido, política/versionamento conhecidos, sem fallback fabricado.
   ETA zero é valor; ETA nulo/indisponível entra na cobertura, não recebe erro zero.
3. Instante de referência da previsão conhecido. Emissão remota, resposta local,
   referência de cálculo e captura não são equivalentes. Nenhum atraso desconhecido
   pode ser corrigido silenciosamente usando tGPS ou CapturedAt.
4. Disponibilidade de cada entrada comprovada na mesma execução até o cálculo,
   incluindo geometria/matching/predecessor/seleção e features ExtraTrees. Conhecer
   a versão hoje não prova disponibilidade histórica. FeatureCutoffUtc só pode ser
   preenchido com semântica documentada e evidência completa; permanece nulo agora.
5. Chegada independente única, auditada, posterior à previsão com margem suficiente
   frente à incerteza dos relógios; nenhuma entrada depende dessa chegada futura.
6. Ambos estimadores no MESMO conjunto de chaves/instantes/alvos. Separar cobertura
   individual da interseção elegível; relatar todos os motivos de exclusão e perdas.

As capturas legadas com CalculatedAt desconhecido e todas as capturas sem corte
completo continuam inelegíveis ao replay estrito D4. Preservá-las como arquivo de
observação, sem métricas primárias. Uma comparação futura de caixa-preta exigiria
protocolo próprio para disponibilidade local/semântica do horizonte e incerteza;
não autoriza relaxar o reader ou declarar causalidade interna do legado.

Separar previsão original, previsão ExtraTrees e label independente em artefatos
vinculados por chave/hashes. Chegadas posteriores servem exclusivamente como label.
Proibir revisão retroativa de matching/rota/predecessor como feature, normalização
ou imputação usando TEST, amostras de fixture e labels derivados do próprio ETA.

Congelar bundle ExtraTrees já verificado e sua proveniência; não carregá-lo nem
retreiná-lo nesta etapa. Confirmar features, regime e término do treino antes de
qualquer inferência real. Para estudo futuro com treino: TRAIN mais antigo,
VALIDATION posterior e TEST final, cortes fixados previamente; purgar viagens que
atravessem cortes/regimes e manter todas as previsões da mesma viagem/chegada no
mesmo split. Ajustes somente em TRAIN/VALIDATION; TEST uma vez após congelamento.
Dependência por veículo/linha/dia deve ser declarada; não prometer generalização a
veículos inéditos sem separação específica. Incerteza deve respeitar agrupamento
por viagem/chegada; tamanho amostral não é a contagem bruta de previsões.

## 6. D9 priorizada: critérios de aceitação e bloqueio

| Ordem | Tarefa / dependência | Aceitação objetiva | NO-GO |
|---|---|---|---|
| 1 | Custodiante escolhe referência independente e protocolo; independe de código novo | Fonte disponível confirmada, definição física, relógio/erro, vínculo protegido e plano de cobertura integral aceitos | Só GPS/interpolação disponível ou vínculo/relógio não comprovado; limitar trabalho a captura diagnóstica |
| 2 | Preparar janela prospectiva e custódia; depende de 1 e autorização operacional separada | Release/flags/limites/rollback revisados; destino privado; retenção/cópia e denominadores definidos; observador preparado | Não haver seleção/volume autorizado, custódia antes da rotação, recursos ou possibilidade de desligamento seguro |
| 3 | Primeiro piloto real limitado, sem ML ativo; depende de 2 | Pelo menos uma execução integral com previsão original pós-CAS, vínculo com chegada independente e pacote de evidências fechado; todas as perdas explícitas | Regressão, lacunas de execução, ambiguidade de identidade/chegada; piloto não certifica splits nem acurácia |
| 4 | Fechar somente lacunas temporais comprovadas; pode acompanhar o piloto sem certificar suas linhas | Para cada entrada usada, identidade/versão, disponibilidade real e referência da mesma execução; testes de dado posterior/incompatível; contrato privado revisado | Emissão legada desconhecida ou dependência não atestada: manter nulos e inelegibilidade; não iniciar sequência de refatorações |
| 5 | Reconciliar/qualificar offline usando contratos e avaliadores existentes; depende de 3/4 | Pacote de auditoria integral compatível com D1; labels independentes rastreáveis; chaves únicas; análise de perdas; três intervalos cronológicos não vazios após purge | Não promover bundle antigo, relatório/flag real ou uma viagem piloto a coorte científica |
| 6 | Congelar coorte pareada e plano de avaliação; depende de 5 | Split/hash/modelo/configuração/semântica temporal congelados, ExtraTrees com features as-of e mesma chave/horizonte; revisão do consumo de operational_dto pelo avaliador | Se só public_eta físico for consumido, não chamá-lo de ETA original; se gates falham, sem comparação conclusiva |

**C0 passa a GO** somente com prova de execução integral, regime/fronteiras,
ausência documentada de proteção contaminante, chegada auditada não ambígua e
coorte real canônica com pacote externo rastreável que sobreviva a três intervalos
cronológicos disjuntos. O mínimo de uma viagem utilizável por intervalo da D1 é
apenas viabilidade, não suficiência estatística. Comparação científica exige também
causalidade/pareamento C1–C2 e suficiência definida antes de observar os resultados.
Os gates oficiais existentes (10.000 observações, 14 dias UTC, 200/50/50 viagens;
por linha 30/10/10) não são reduzidos nem substituídos pela D8/piloto. Um estudo menor
precisa declarar limite exploratório; não qualifica modelo oficial.

Próximo passo imediato: obter aceite do custodiante para a etapa 1, sem ativar
coleta. Próxima implementação mínima recomendada, após confirmar fonte e janela:
preservar no witness privado existente a evidência de disponibilidade das entradas
faltantes do ETA físico (referências imutáveis da geometria/diagnóstico, predecessor
e seleção da MESMA execução), com testes de incompatibilidade e entrada posterior.
Só acrescentar o que a evidência real não permitir obter dos registros existentes;
não criar pipeline/fila/exportador. Isso não resolve emissão remota legada e não
deve atrasar a obtenção da referência independente. Nenhuma implementação,
inferência, treinamento, métrica ou ativação está autorizada por este plano.

## 7. Validação deste fechamento

Somente leitura direcionada de fontes/relatórios e dos resumos da sexta execução,
parsing XML dos dois TRXs, contagens JSONL e SHA-256. Acesso elevado foi usado só
para ler a pasta com ACL privada, sem mudar permissões. Nenhum teste foi reexecutado,
nenhum payload emitido e nenhum serviço/Docker/SQL iniciado. Alterações desta tarefa
são documentais; `git diff --check` é a verificação final, sem novo commit ou push.
