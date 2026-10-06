# 15 — Reconciliação de produção e contexto

**Etapa:** 1.5 · **Observação:** 2026-10-06, America/Fortaleza  
**Método:** inspeção SSH e consultas estritamente read-only; nenhum segredo registrado  
**Baseline local:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado:** produção confirmada nos itens explícitos; correspondência exata imagem↔commit não provada por label

## 1. Correções de interpretação da Etapa 1

Os documentos 01–13 são preservados como fotografia histórica. As correções abaixo prevalecem para a Etapa 2:

| Interpretação anterior | Reconciliação |
|---|---|
| Tabelas estruturais rodoviárias aparentemente vazias | **Incorreto.** `pg_stat_user_tables.n_live_tup` estava desatualizado. `COUNT(*)` leve confirmou estrutura V2 populada. |
| ML parado como possível incidente | O serviço antigo está desligado **intencionalmente** durante a migração ETA/ML V2; não é incidente por si só. |
| ETA V2 “workers presentes; efetividade não verificada” | Flags confirmadas OFF e tabela vazia: implementado/registrado, mas não ativo. |
| Serviço Python descrito como Flask | Correção: o runtime local usa FastAPI/Uvicorn. |
| Painel admin incluído no ecossistema auditado | Decisão do desenvolvedor: **FORA_DO_ESCOPO** do projeto/TCC atual. |
| Trem classificado experimental/parcial pelas flags desconhecidas | Runtime ferroviário principal está habilitado e possui schedule/dados; qualidade continua sujeita a validação. |
| Frontend genericamente descrito como integrado | Integração V2 foi confirmada; também há fachada compatível ativa e chamadas POI sem endpoint produtivo. |

## 2. Estado real do banco de produção

A API resolve seu `POSTGRES_HOST` para o mesmo endereço interno Docker do container `transporte_postgres`; isso confirma a instância usada sem revelar credenciais ou nome de banco. A imagem PostGIS é `16-3.4`, está healthy e contém todas as 21 migrations do repositório até `RailScheduleFoundation`.

### Contagens exatas e leves

| Tabela V2 | `COUNT(*)` |
|---|---:|
| `Modais` | 2 |
| `Linhas` | 502 |
| `Sentidos` | 951 |
| `Paradas` | 7.798 |
| `PadroesOperacionais` | 980 |
| `PadroesVersoes` | 980 |
| `OcorrenciasParadasPadroes` | 54.873 |
| `LinhasIdentidadesExternas` | 502 |
| `SentidosIdentidadesExternas` | 951 |
| `ParadasIdentidadesExternas` | 7.798 |
| `PadroesIdentidadesExternas` | 1.941 |
| `FontesEstruturais` | 2 |
| `ImportacoesEstruturais` | 2 |
| `PrevisoesEtaV2` | 0 |

Logo, endpoints estruturais usam banco V2 efetivamente populado. Não foram repetidas contagens em tabelas multimilionárias. As estimativas operacionais da Etapa 1 continuam apenas indicativas.

### Ferrovia

A observação anterior permanece válida: 1 schedule version, 23 patterns, 246 runs e 7.433 scheduled stops (estimativas de catálogo). O endpoint `/rail/vehicles/snapshot` está no Swagger e o runtime correspondente está habilitado.

## 3. Diferenças entre ambiente local e produção

- Docker Desktop local estava desligado por decisão do desenvolvedor e não foi iniciado.
- Compose local é um artefato de configuração/migração, não prova operacional.
- Produção usa Compose em diretório próprio e imagens GHCR `latest`.
- A imagem API tinha digest `sha256:d7ce8c...fce5`, criada em 2026-10-05 16:58:35 UTC; `NoPonto.dll` foi produzido no mesmo intervalo do commit local examinado. Swagger, migrations e contratos coincidem com o código. Sem label Git/revision, a igualdade binária com `53567bd` é **INDETERMINADA**.
- O frontend publicado, versão de loja e suas variáveis Expo não foram verificados; conclusões de frontend referem-se ao commit local.

## 4. Estado intencional do ML

`noponto_ml` está parado, sem restart automático, e marcado unhealthy no estado persistido. Conforme informação do desenvolvedor, isso é intencional durante a transição. O bind de modelos e a configuração HTTP permanecem como legado ativo de compatibilidade.

O backend ainda chama `GpsEtaClient` no ciclo GPS. Quando o destino está indisponível, o cliente captura a falha, inicia cooldown de 30 segundos e retorna zero previsões; matching, aceite Redis, viagem, SignalR e telemetria continuam. Portanto:

- serviço/modelo antigo: **LEGADO_ATIVO como dependência contratual, intencionalmente indisponível**;
- cliente/fallback antigo: **LEGADO_ATIVO e executado**;
- ETA V2 baseline/shadow: **EM_MIGRACAO, compilado e registrado, desligado**;
- ML supervisionado V2: **PLANEJADO**.

Não houve tentativa de reativação.

## 5. Feature flags relevantes

Somente valores não sensíveis foram consultados.

| Grupo | Estado em produção | Interpretação |
|---|---|---|
| `GPS_MATCHING_BATCH_ENABLED` | true | matching em lote vigente |
| combinado set-based | true | estratégia V2 combinada vigente |
| correção temporal | enabled=true | participa do fluxo operacional |
| shadow de correção | true | pipeline comparativo também ativo |
| ônibus primário | Zirix direto | fonte autoritativa configurada |
| BRT primário | BRT current | fonte autoritativa configurada |
| shadows ônibus/BRT | Data.Rio | observação comparativa; não prova fallback automático |
| sampling telemetria ML | true | coleta V2 ativa |
| ETA V2 enabled/shadow/canary | false / false / 0% | nenhuma previsão V2 produzida |
| Rail schedule runtime | true | runtime de schedule ativo |
| probes schedule-aware | true | scanner guiado por grade |
| estimação/publicação espacial | true / true | posições estimadas publicadas |
| schedule-first | true | runs programadas podem ser publicadas |
| TremRealtime/canary/scanner/gate | todos true | pipeline ferroviário vigente |

“Ativo por flag” não é sinônimo de qualidade validada; mede configuração, não acurácia.

## 6. Containers reconciliados

| Container | Classificação | Justificativa |
|---|---|---|
| `noponto_api` | ATUAL | API ativa, contratos V2 e workers |
| `transporte_postgres` | ATUAL | banco efetivamente resolvido pela API |
| `transporte_redis` | ATUAL | cache/estado operacional ativo |
| `noponto_ml` | EM_MIGRACAO / legado desligado intencionalmente | destino ETA antigo preservado, serviço off |
| `noponto_v24_postgres` | LEGADO_INATIVO provável | container parado, banco V2 efetivo é outro; não remover sem inventário de volume |
| `auth_api`, `auth_postgres`, `auth_redis` | FORA_DO_ESCOPO desta auditoria | outra stack Compose; não integrado ao NoPonto observado |
| Portainer/Dozzle | infraestrutura compartilhada | operação do host, não arquitetura funcional NoPonto |

Container parado não foi automaticamente classificado como falha. Nenhum container foi alterado.

## 7. Divergências entre documentação e código

### Backend README

Descreve controllers, entidades, jobs ArcGIS/POI e endpoints excluídos da compilação. Continua útil como histórico da arquitetura inicial, mas não deve fundamentar diagramas atuais. Também descreve expansão BRT/trem como futura, embora esses fluxos já tenham implementação/implantação posterior.

### Roadmap

É mais aderente ao V2, porém os status são uma fotografia anterior: ferrovia aparece planejada em trechos enquanto schedule-first está ativo em produção. ETA V2 “próxima etapa” permanece coerente com flags OFF, apesar de sua fundação já estar codificada.

### Contexto de continuidade

Registra decisões e testes importantes, mas cita branch/worktree, bancos descartáveis e estágios anteriores. Não é prova do HEAD atual nem de produção.

### Frontend README

Versões e estrutura são de scaffold anterior. A fonte autoritativa é `package.json` e o código: Expo 55, React Native 0.83.6 e MapLibre/WebView. A dependência `react-native-maps` está instalada, mas sem import encontrado.

### Frontend versus API

A estrutura V2 está alinhada. A tela de linhas ainda chama endpoints POI removidos do backend compilado; a camada HTTP converte erro em lista vazia. O endpoint `/paradas/{id}/proximos-veiculos` tem service, mas nenhum call site atual. O detalhe/mapa antigo continua atendido por uma fachada construída sobre tabelas V2.

## 8. Origem das informações

### Fornecidas pelo desenvolvedor

- painel administrativo fora do escopo;
- ML antigo desligado intencionalmente;
- Docker local não deve ser iniciado nem tomado como referência;
- configurações antigas serão limpas futuramente;
- produção é referência operacional.

### Confirmadas tecnicamente

- exclusões de compilação e composição DI;
- call sites backend/frontend;
- arquitetura estrutural V2 e compatibilidade;
- contagens exatas de estrutura V2;
- banco efetivo da API na rede Docker;
- imagem/digest/data e Swagger de produção;
- flags listadas;
- estado dos containers;
- ETA V2 vazio/desligado e ML antigo indisponível;
- runtime ferroviário habilitado.

### Inferências explicitamente limitadas

- imagem API provavelmente corresponde ao commit local pela data e superfície, mas não há prova criptográfica do commit;
- `noponto_v24_postgres` parece legado, mas seu volume/conteúdo não foi examinado;
- chaves de Compose sem consumidores compilados são candidatas, não autorização de remoção;
- Data.Rio em shadow compara fontes; fallback em falha primária não foi inferido sem call path específico.

## 9. Questões ainda não resolvidas

1. Qual versão do frontend está distribuída e quais flags Expo (`RAIL_DEMO`, URLs e polling) foram usadas?
2. A UI de POIs deve ser reativada com endpoints V2 ou removida temporariamente?
3. Quando e com quais gates o ETA V2 shadow será habilitado?
4. O contrato SignalR atual será versionado ou a compatibilidade será mantida até depois do TCC?
5. Quais variáveis ArcGIS/BRT ainda são usadas em comandos manuais fora do runtime?
6. Existe consumidor externo dos endpoints de compatibilidade de mapa/detalhe?
7. Quais políticas de retenção/outbox e backup são efetivamente operadas?
8. Como o digest da imagem será associado a commit/SBOM em futuros deploys?

## 10. Veredito para a próxima etapa

**READY_FOR_STAGE_2**

Já é possível documentar a arquitetura definitiva sem misturar núcleo vigente e legado, desde que:

- o modelo V2 seja a base dos diagramas;
- ETA V2 seja apresentado como migração desligada em produção;
- ETA HTTP/fachadas sejam identificados como legado ativo de compatibilidade;
- POIs/rotinas/ML supervisionado não sejam apresentados como entregues;
- evidências de produção sejam datadas e não generalizadas como garantia de qualidade.

A Etapa 2 não foi iniciada.

## 11. Evidências operacionais

Comandos read-only utilizados: `docker ps/inspect/image inspect`; resolução DNS interna a partir da API; `stat` do assembly; Swagger local; `SELECT COUNT(*)` somente nas tabelas estruturais pequenas e `PrevisoesEtaV2`. Não foram lidos valores secretos, logs extensos ou tabelas multimilionárias; não houve escrita, restart, deploy ou migration.
