# Candidata MVP versionada localmente

Somente commits locais em branches secundárias. Nenhuma publicação ou implantação.

| Projeto | Branch | Worktree |
|---|---|---|
| Backend | release/mvp-local-20261014 | D:/repositorio_github/NoPonto/noponto-backend-mvp-candidate-20261014 |
| Frontend | release/mvp-presence-local-20261014 | D:/repositorio_github/NoPonto/noponto-frontend-mvp-candidate-20261014 |

Bases: backend6f64538b47cb3987c5bf023d9878931b69228dac; frontend139859b
(HEAD da branch ajustes, não uma troca para main). Os74 hashes do plano foram
verificados antes da seleção; nenhum divergente. Program/polling receberam
patches por grupo. Históricos receberam somente blocos pertinentes e acréscimos
da materialização, sem cópia integral do arquivo sujo.

## Commits e ajuste de fronteira

| Grupo | SHA | Conteúdo |
|---|---|---|
| B1 | d20785e | Cliente/opções ML fail-closed e testes independentes |
| B2 | 723ba96 | GPS/diagnósticos existentes, fixtures e documentação aprovada |
| B3 | 6a58253 | ETA físico, resolver, endpoint e matriz de segurança |
| B4 | a65e909 | Contrato HTTP de presença terminal e documentação |
| B5 | 3d71964 | Laboratório isolado e referência externa Shadow congelada |
| B6 | próprio commit deste documento | Validação local e fechamento |
| F1 | 30072b4 | Nove arquivos de presença frontend e histórico terminal |

`EtaFlagSafetyTests` saiu de B1 para B3 porque depende de PhysicalEta e das
fixtures de PhysicalEtaMvpTests. B1 foi validado sem esse teste; B2 compilou
isoladamente; B3 trouxe o teste junto de suas dependências. Não foram criados
commits intermediários quebrados. Campos/parâmetros de diagnóstico e físico no
polling e registros adjacentes no Program foram separados por patches.

Relação completa por SHA/arquivo: `commits.json` em
`D:/repositorio_github/NoPonto/mvp-materialized-20261014-01`.

## Validação executada nesta candidata

- B1:28 testes C# aprovados; restore pelo cache local NuGet, sem serviço externo.
- B2:build normal aprovado. B3:44 testes C# aprovados. B4:2 testes aprovados.
- Final: **771 C# aprovados**,36 classes presentes, zero falhas/ignorados;
  seleção local aprovada acrescida da matriz de segurança, sem alterar CI/CD.
  Inclui GPS/matching, causalidade/CAS em memória, flags, paradas intermediárias,
  HTTP real efêmero no controller, ausência de evidência e compatibilidade Radar.
- F1:**24 testes Node aprovados e TypeScript --noEmit aprovado**, executados na
  worktree candidata. Dependências instaladas reutilizadas por junction ignorada
  em node_modules; somente leitura, sem copiar/versionar dependências ou WIP.
- B5:**9 testes Python offline aprovados** na candidata. Nenhum treino, modelo
  real ou novo servidor FastAPI foi executado nesta materialização. A demonstração
  HTTP completa anterior permanece identificada no relatório12, não é reportada
  como nova execução. Nenhum modelo/output do experimento foi commitado.

Logs/TRX em `mvp-materialized-20261014-01`, fora dos repositórios. Builds normais
mantiveram cinco warnings preexistentes. Não foram usados PostgreSQL/Redis
existentes, Docker, bancos operacionais ou migrations.

## Laboratório: referência exata, ainda externa

`tools/eta_ml/tcc/external-shadow-reference.json` registra HEAD/branch da
worktree Shadow, os hashes SHA-256 dos três serviços históricos e do teste
HistoricalEtaShadowTests, e a localização da cópia congelada local. Os quatro
fontes estão preservados em
`D:/repositorio_github/NoPonto/mvp-materialized-20261014-01/shadow-source-reference`.

Essa cópia não é uma worktree Git nem um --shadow-root executável por si só.
O laboratório continua exigindo o repositório ML e uma worktree Shadow compatível
com esses fontes exatos; a CLI consulta também metadados Git. Clone limpo do
backend sozinho **não é autossuficiente**. Para outra máquina é preciso fornecer
essas referências separadas e conferir hashes, sem substituir GPS atual por
arquivo antigo. A pendência não bloqueia o candidato físico.

Fontes Shadow não foram incluídos na compilação normal/DI da API. Worker normal
continua recusando modelos sintéticos; nenhuma captura produtiva foi ativada.

## Preservação e exclusões

Cinco workspaces originais foram inventariados (backend, frontend, ML, Shadow e
MVP histórico): **2.284 arquivos idênticos**, índices byte a byte idênticos,
branches/status/HEAD e refs main preservados. Prova em `originals-before.json`
e `originals-preserved.json` no diretório de evidências.

Os72 arquivos aprovados fora dos históricos coincidem com as fontes originais
após normalização de CRLF; os13 arquivos adiados estão ausentes ou iguais ao
HEAD-base da candidata. Workflows iguais aos originais, nenhuma mudança CI/CD.
WIP startup/splash/theme/EAS, FormaPagamento, schedules, bytecode, modelos,
outputs/TAR/APK e credenciais não foram transportados como mudanças.

Únicos acréscimos além dos caminhos do plano: o JSON de referência externa B5
(necessário para congelar a dependência solicitada), este fechamento B6 e as
entradas pertinentes de histórico. Scripts de transporte/materialização e provas
ficaram fora do Git. Nenhum git add . foi utilizado.

## Homologação seguinte

1. Revisar os SHAs e a seleção; candidata física pronta para homologação isolada,
   sem push/PR ou main alterada. NOPONTO_DEPLOY_ENABLED não foi consultado nem
   modificado; permanece false conforme confirmação do operador.
2. Em tarefa autorizada, preparar API/PostGIS/Redis novos e descartáveis, com
   portas/volumes/configuração próprios e fixtures auditáveis. Não iniciar a API
   com as fontes operacionais atuais ou copiar .env produtivo. Flags temporárias:
   PhysicalEta ON, ML legado/EtaV2/Shadow/diagnóstico correlacionado OFF.
3. Apontar no cliente Expo compatível as variáveis EXPO_PUBLIC_API_BASE_URL e
   EXPO_PUBLIC_GPS_HUB_URL para o ambiente isolado; habilitar presença somente
   nessa sessão Metro. Validar Radar/parada consultada/terminal no celular.
   Nenhum APK é necessário se já houver cliente de desenvolvimento compatível;
   eventual build exige autorização própria.
4. Só depois decidir publicação/promoção. O workflow backend pode publicar em
   push/main mesmo com deploy false; o frontend gera APK em push/main. Nenhum
   desses eventos foi disparado.

Defaults seguros mantidos: físico/presença/novos recursos OFF, ML legado
fail-closed. A perda real de PassagemParada **não foi declarada corrigida**;
as lacunas4C.9 e de qualificação real de ML permanecem, sem relaxar cursor,
deduplicação, matching ou regras de treino.
