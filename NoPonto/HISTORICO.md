# Histórico

## 2026-11-15

- URLs de ITS, GTFS-RT ônibus/BRT e Expo Push configuráveis pelo .NET, com validação HTTP(S) e fallback para os endereços existentes. Trens RJ reutiliza `TremRealtime:BaseUrl`.
- Compose versionado encaminha as cinco URLs sem alterar fontes, limites, timeouts ou mecanismo de deploy. Nenhum `.env` foi editado.
- Validação: 43 testes xUnit direcionados em projeto temporário com os fontes reais; renderização do Compose e comparação dos valores anteriores aprovadas. Build completo e integrações externas não executados.

Históricos anteriores foram centralizados no repositório `noponto-docs` durante a limpeza documental.

- Promoção controlada das cinco URLs do `.env` para uma nova composição congelada: simulação sem escrita, aplicação explícita na mesma imagem, backup privado e rollback automático/manual. CI existente preservado; Radar aceita a URL já promovida sem permitir editá-la pelo overlay de Radar. Nenhum arquivo de produção ou `.env` foi alterado durante a implementação.
- Validação da promoção: 22 testes novos e 10 regressões de Radar/CI aprovados com fakes; preflight real de leitura aprovado, conferindo integridade dos arquivos protegidos e do container. Aplicação e rollback reais não executados.


## 2026-11-15 � Etapa 1: validade f�sica expl�cita de ETA

- Eventos f�sicos/h�bridos publicam PredictionValidUntilUtc ancorado no TimestampGps + PhysicalEta.MaxAgeSeconds; refresh e substitui��o h�brida preservam o instante. Campo opcional aditivo, sem mudar algoritmos, programa��o ou ferrovia.
- RadarSelector remoto limita conte�do tamb�m pela expira��o publicada. Testes focados de ETA f�sico, endpoint h�brido e Radar; sem produ��o/deploy. Depend�ncia: frontend novo deve consumir o campo antes de alterar a configura��o temporal.


## 2026-11-16 — ETA rodoviário operacional mínimo

- EventosParadaService usa OperationalRoadEta singleton limitado a 2.000 referências. Mediana das velocidades de progresso entre fixes distintos (até seis pontos/120 s); referência inicial PhysicalEta:OperationalReferenceKmh=20 (validada entre 5 e 30). Preserva geometria/identidade/ocorrência, gates causais e MaxAgeSeconds existentes. Velocidades observadas abaixo de 5 limitam a referência; zero ou ausência de avanço não recebem ETA otimista.
- Suavização do horário absoluto por fix distinto: peso 25% longe e 75% até 500 m, variação limitada a 60 s. Replay não renova previsão nem validade. Falha excepcional do resolvedor pode reutilizar somente distância já validada do mesmo fix/progresso/próxima ocorrência; rejeição SQL, fix novo, referência vencida ou incompatível continuam indisponíveis. Sem alteração do pipeline GPS, frontend ou ferrovia. Publicação híbrida não sobrescreve o novo estimador neste endpoint; shadow permanece.
- Compilação e 49 testes direcionados aprovados (quatro novos cenários); seis avisos preexistentes. Primeira rodada: 48/49, corrigida tolerância de arredondamento da asserção e acrescentada regressão de replay sem avanço; rodada final 49/49. git diff --check limpo.
- Deploy bloqueado pelo workflow existente: build/publicação/deploy somente em push na main, sem workflow_dispatch de branch. Rollback documentado em .deploy/deploy-api.sh (imagem anterior local, snapshots e health check), não exercitado. Sem push, merge, Docker, banco ou produção. Detalhes em noponto-docs/00-auditoria/2026-11-16/eta-operacional-minimo.md.


## 2026-11-16 - Radar 8: estabilidade do ETA operacional

- Mediana de progresso com dois intervalos validos nao e limitada por leitura instantanea lenta; suavizacao permanece ativa com avanco, inclusive abaixo de 5 km/h. Sem avanco continua indisponivel.
- Distancia ausente conserva somente historico compativel e fisicamente valido, sem publicar ETA nessa resposta nem renovar TTL. Expiracao, regressao e proxima ocorrencia incompativel descartam referencia.
- Validade mantida em 90 s: override/preflight fixam esse valor e PhysicalEta participa do polling GPS; ajuste isolado para 120 s nao foi realizado.
- Compilacao e 53 testes direcionados aprovados (quatro casos novos); seis avisos preexistentes, diff check limpo. Regressao sintetica reproduziu salto de 1033 s por velocidade lenta e 2364 s apos perda de historico, antes da correcao. Sem frontend ou mudanca de geometria/GPS.

## 2026-11-16 - Semi-Rotinas: schema inicial (incompleto)
- Migration aditiva RadarProgramacoes e RadarProgramacaoExecucoes, com indices por instalacao/vencimento e identidade unica da ocorrencia. Nao aplicada. Backend ainda sem CRUD/worker/mapeamento EF; nao publicar esta etapa isoladamente.
- Compilacao bloqueada pelo ambiente: approval required by policy, AskForApproval Never. Sem testes ou alteracoes em producao.

## 2026-11-16 - Semi-Rotinas v1: gerenciamento e execucao

- CRUD REST em /radar/programacoes autenticado pela instalacao, configuracao validada no catalogo existente, revisao otimista, pausa e exclusao. Limite de 20 programacoes; janelas de uma hora sem sobreposicao, serializadas por advisory lock transacional inclusive na primeira inclusao.
- Worker reutiliza sessoes, indices e push do Radar. Identidade estavel por ocorrencia e resultado atomico no Redis evitam duplicacao apos falha antes do commit PostgreSQL. Sessao manual tem precedencia; ocorrencias vencidas nao geram backlog; falhas transitorias permanecem pendentes; revogacao e contexto invalido suspendem a programacao.
- Migration inicial completada com designer, snapshot, constraints e nomes de indices/chaves alinhados ao EF. Rollback preserva as tabelas aditivas. Fusos IANA e horario de verao mantidos; conflitos recorrentes reservam conservadoramente combinacoes futuras de offsets.
- Validacao: build aprovado, 66 testes focados aprovados com PostgreSQL/Redis locais. Rodada ampliada final: 258/259; unico erro em teste preexistente de TTL (45,037 s para teto de 45 s), em revalidacao. Harness DI de push atualizado para o novo DbContext; snapshot sem mudancas pendentes. Deploy ainda pendente.

- Validacao final desta etapa: 259/259 testes Radar aprovados no Linux, sem ignorados, incluindo PostgreSQL e Redis descartaveis; diferencas de relogio Windows/Linux e contador acumulado explicam falhas das repeticoes anteriores. Publicacao depende de validacao na main e migration antes da imagem.
