# Candidata local — flags e congelamento proposto

## Correção mínima

`GpsEtaOptions.Enabled`, `FromConfiguration` e o fallback do construtor de
`GpsEtaClient` agora ficam **OFF quando ausentes**. Somente opt-in explícito
habilita o legado. Na configuração efetiva, PhysicalEta ON prevalece sobre
ML legado ON; o polling já possuía sua própria proteção contra essa chamada.
BASE_URL, EtaV2 e Shadow não ativam o cliente legado. Booleano inválido continua
recusado por erro de configuração, sem fallback permissivo.

| PhysicalEta | ML legado configurado | ML efetivo | Físico |
|---|---|---|---|
| ausente/OFF | ausente/OFF | OFF | OFF, caminho determinístico anterior preservado |
| ON | ausente/OFF | OFF | ON, somente com prova causal válida |
| ON | ON | OFF | ON; precedência explícita |
| OFF | ON explícito | ON | OFF; opt-in deliberado ainda suportado |

PhysicalEta, Shadow, presença visual e demais flags não foram habilitados.
Nenhum appsettings, Compose, .env, variável remota ou CI/CD alterado. O default
true descrito nos relatórios históricos09–13 foi corrigido **nesta revisão**;
esses relatórios permanecem como evidência do estado anterior. A futura
configuração do MVP deve continuar deixando ML explicitamente OFF.

Arquivos funcionais alterados: `GpsEtaOptions.cs` e `GpsEtaClient.cs`.
Testes: novo `EtaFlagSafetyTests.cs`, ajuste de expectativa em
`GpsEtaClientTests.cs` e opt-in explícito em três fixtures de
`GpsPerformanceMetricsTests.cs` que testam inferência/timeout intencionais.
Não houve alteração em matching, cursor, passagens, TTL ou ETA físico.

## Validação realmente executada

Evidências: `D:/repositorio_github/NoPonto/mvp-candidate-20261014-01`.

- **RED:** oito casos novos, cinco falhas/ três aprovados antes da correção.
  Reproduzidas chamadas ML com flags ausentes/conflitantes e default permissivo.
- **GREEN:**113 C# aprovados, zero falhas/ignorados, com rebuild normal.
  Oito casos de segurança + regressões de cliente/métricas, estado causal/CAS,
  físico/parada consultada, eventos e compatibilidade frontend. Cinco warnings
  de compilação preexistentes. `flags-red.trx` e `targeted.trx` preservados.
- **24 Node aprovados**, terminais/presença/favoritos/SignalR/Radar. Repetidos
  somente para verificar o contrato visual da candidata; frontend sem edição.
- Suítes763/76/50 anteriores, TypeScript e treino ML não repetidos: nenhuma
  mudança nesses componentes que justificasse reexecutá-los integralmente.

Homologação técnica possível e executada: hosts HTTP efêmeros reais em loopback,
controllers existentes, relógios controlados, resolver estrutural fixture e
cache em memória. Terceira ocorrência a4100m/36km/h:410s na captura e405s após5s,
dois veículos e identidade do alvo preservada. Sem predecessor/regressão/GPS
antigo/velocidade zero: sem ETA físico. Veículo parado sem versão permanece no
endpoint de linha; legado com flags OFF continua disponível sem inferência ML.

**Limite:** não é o Program operacional com PostGIS/Redis, nem aplicativo em
dispositivo. Não foi criado banco/container novo; nenhuma conexão a serviços
existentes. O resolver SQL real não foi reexecutado: sua geometria não mudou.
Estas fixtures não medem precisão ou trajetória real no Rio.

## Proposta exata de commits — NÃO EXECUTADA

`commit-plan.json` no diretório de evidências lista arquivos individualmente,
sub-hunks compartilhados, dependências, títulos propostos e exclusões.

| Ordem | Conteúdo / fronteira |
|---|---|
| B1 — segurança | GpsEtaOptions/GpsEtaClient, testes cliente/métricas e EtaFlagSafetyTests; nenhum arquivo produtivo de configuração |
| B2 — GPS existente | Diagnósticos4C.4/4C.7 e fixtures/proteções já locais; hunks GPS/contadores, sem afirmar correção de PassagemParada |
| B3 — físico/resolver | PhysicalEta, RoadEtaDistances, EventosParadaService, testes físico/consultada e script PostGIS isolado; DI físico/resolver e hunks Apply/guard no polling |
| B4 — contrato terminal | TerminalPresenceContractTests e documentação da presença; depende das fixtures físicas B3 |
| B5 — TCC | tools/eta_ml/tcc e relatórios06/12; registrar dependência externa ShadowRoot e revisão/hashes dos fontes, sem copiar GPS antigo |
| B6 — validação/documentação | tools/validation e fechamento13/14; somente entradas correspondentes do histórico |
| F1 — frontend separado | Nove arquivos de presença listados no JSON e somente entrada terminal do histórico frontend; sem main/push/APK |

Compartilhados:

- `Program.cs`: separar registro do diagnóstico (B2) de opções PhysicalEta e
  registro IRoadEtaDistances (B3), hoje adjacentes no mesmo hunk.
- `GpsPollingService.cs`: separar campo/parâmetro/atribuição do diagnóstico,
  BrtRead/Report/retry/contadores (B2) dos equivalentes físicos, guard ML e
  `PhysicalEta.Apply` (B3). Assinatura do construtor exige edição de sub-hunk.
- Históricos: distribuir entradas respectivas sem incluir automaticamente todo
  arquivo. As fronteiras intermediárias ainda precisam de build na revisão
  futura: nenhum commit parcial foi materializado ou declarado aprovado.

Fora desta release proposta: comentários em FormaPagamento, documentação de
schedules e WIP frontend de startup/theme/eas (incluindo novos StartupLogo e
startupDiagnostics), bytecode, outputs ML, modelos/TAR/APK, caches e snapshots.
ML sem mudança de fontes; nada a commitar ali por esta correção.
Inventário de legado do relatório13 preservado, sem exclusão.

## Git e próxima ação

Inventário anterior `before.json`: backend fix/gps61 arquivos sujos/untracked,
frontend ajustes21, ML feature/eta-history-mvp9; nenhum staged. Cópias/hash dos
quatro arquivos inicialmente limpos alterados para segurança estão em
`original-security-files` e `security-before.json`. A prova final compara todos
os91 arquivos preexistentes; histórico ativo permitido somente por acréscimo.

Próxima ação: revisar/aprovar a seleção de arquivos e sub-hunks do plano para
congelar uma revisão candidata. Sem commit/push/merge/stage automático nesta
tarefa. Manter NOPONTO_DEPLOY_ENABLED=false. Antes da promoção, homologar a
configuração explicitamente física no ambiente descartável/dispositivo.

Pendências reais: revisão imutável e homologação completa ainda ausentes;
laboratório depende de fontes Shadow não commitados em outra worktree e precisa
de referência congelada/disponível para reprodução em clone limpo. A perda real
de passagens segue sem correção comprovada4C.9; não anunciá-la resolvida. Isso não
impede tecnicamente o ETA observacional, mas limita o escopo que pode ser
homologado. Modelo real/labels não são exigidos para o MVP físico + laboratório
sintético; Shadow operacional permanece fora do runtime e OFF.

Nenhum acesso ao homeserver/produção, build Docker/EAS, publicação, migração ou
alteração em CI/CD foi realizado.
