# Retenção de logs ITS — preparação local, 24/10/2026

Decisão: **GO para solicitar autorização da terceira janela**, condicionado ao preflight imediato abaixo. Não é aprovação da janela nem prova de execução no Linux produtivo. Nenhum SSH, Docker, provedor, cron, deploy, CI ou configuração produtiva foi executado nesta tarefa. Base local `4067c02`; binário permanece release151, merge `15ad5cc`.

## Lacuna corrigida

`parse_logs` conserva seu papel de resumo; `sample` consulta apenas os últimos100s/6000linhas do container atual. Essa amostra pode sobrepor, truncar e perder o container removido. Não é arquivo de evidências por página. Continua usada para saúde/ciclos/seleção/flow do gate, sem transformar ausência de log em sucesso.

O novo `tools/gps/its_log_capture.py` segue `docker logs --follow --timestamps --since ... ID_IMUTÁVEL`, mantendo o stream antigo enquanto descobre a nova API. Somente `docker inspect` de metadados mínimos e `docker logs`: nenhum provedor, banco, API escritora, controle de container ou lock de deploy. A escrita local é incremental, com `fsync` por registro. Reiniciar/recriar a API não depende de consultar seus logs posteriormente. Falha de stream reconecta com sobreposição de2s e deduplicação por container/timestamp/conteúdo agregado sanitizado; desconexão inesperada permanece uma lacuna, mesmo se o backfill recuperar registros.

Schemas fechados preservam `ITS HTTP page`, `ITS normalized page`, `ITS timing`, `ITS provider wait`, `ITS acquisition`, `BUS hybrid` e `GPS flow`. Não há arquivo de linhas brutas, URL/cursor/token, payload, identidade veicular ou coordenadas. Identificadores permitidos são container, aquisição e sessão/geração diagnóstica; não identificam veículos. Campos desconhecidos são descartados; valores inválidos em campos permitidos marcam `schema_rejected`, sem reproduzir entrada/exceção.

HTTP/corpo/parse/normalização são tempos separados; gate, intervalo e espera pending também. Página normalizada mantém página global, janela UTC, provedores e motivos de rejeição. Resumo saudável é conciliado com HTTP concluído, páginas normalizadas, tentativas pending, janelas, registros e rejeições; divergência marca `acquisition_log_mismatch`. Isso detecta páginas ausentes sem supor que uma tentativa HTTP falha deva ter uma página normalizada.

`acquisition` é explícita no HTTP/normalização/timing. Resumo/híbrido não a emitem no binário151: associação por ordem serial do mesmo container fica marcada `same_container_serial_order`. `GPS flow` mantém sessão/geração/estágio explícitos; sua associação à aquisição é igualmente inferida, nunca identidade individual persistida. Após lacuna, a associação inferida é `unproven`; não se inventa prova. A comprovação da mesma observação898 continua sendo a interseção calculada em memória pelo servidor (`linked_complete_new`) mais renovação física recebida pela sonda SignalR, dentro90s. Inferência de aquisição, REST ou log de seleção sozinho não aprovam o gate.

## Limites, independência e encerramento

- Novo diretório exclusivo0700; arquivos0600; `umask077`. Nunca reutiliza/sobrescreve arquivo de outra janela.
- Arquivo `capture/aggregates.jsonl`: máximo8MiB e12000linhas, incluindo rodapé reservado. Entrada lida até64MiB, linha até16KiB; fila256linhas por stream, até2streams simultâneos,4containers distintos e32aberturas. Os leitores param ao detectar excesso; bytes em trânsito podem ultrapassar o teto de entrada por uma leitura por stream. O teto do arquivo é estrito.
- Duração independente1000s, limitada pelo CLI a1200s. `health.json` atômico até4KiB, heartbeat a cada ciclo de captura (~1s mais inspect com timeout3s); supervisor rejeita idade acima10s, PID encerrado, sessão/hash/imagem/container divergentes, lacuna ou truncamento.
- Processo em sessão própria, stdin/stdout/stderr DEVNULL, descritores fechados: não herda SSH ou deploy.lock. É iniciado pelo supervisor cron antes do baseline; hashes dos cinco arquivos e digest151 são obrigatórios antes de abrir o coletor.
- Captura não altera deadline600s, gate90s, TTL, aquisição, cadências, timeouts GPS ou critérios anteriores. Falha antes da ativação impede ITS; durante Hybrid determina `capture_evidence_failed` e rollback. Após confirmação inicial do Hybrid, a primeira decisão exige contadores positivos das cinco famílias ITS/página/timing/resumo/composição; stream vivo sem agregados reconhecidos não basta. Flow e a observação898 continuam sujeitos ao gate90s, sem antecipar ou afrouxar esse prazo. Não se aprova evidência incompleta. Classificação técnica `evidence_capture` é separada do motivo operacional.
- Supervisor tenta rollback **antes** de sinalizar a captura; não aguarda flush/join desse processo para trocar fonte ou liberar o lock. Watchdog independente também não depende dela. Se controlador morrer, captura continua por prazo próprio; isso não prolonga ITS nem garante retorno se Docker/host estiver indisponível.
- Após GTFS confirmado e `phase=complete`, aguardar apenas a finalização do arquivo, sem manter ITS por isso. Usar o verificador abaixo. Se encerramento abrupto, disco cheio, falta de footer, hash diferente, truncamento, gap ou aquisição interrompida: preservar prefixo, classificar corretamente e não declarar evidência completa.

Rodapé distingue `archive_complete` (fechamento sem lacunas detectadas) de `evidence_complete` (também sem aquisição aberta). Aquisição cancelada no rollback pode deixar arquivo íntegro e prefixo útil, mas `evidence_complete=false`; analisar somente aquisições completas, sem aprovar retrospectivamente um gate falho. SHA256 verifica integridade, não autenticidade nem ausência absoluta de perda silenciosa no logging driver. Perda no daemon/host antes de ser observável não pode ser recuperada por uma sonda passiva; os resumos conciliados e gaps tornam as lacunas detectáveis explícitas.

## Preflight futuro obrigatório — não executado agora

1. Nova autorização específica. Reinspecionar release151 saudável e digest exato:
   `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`.
   BUS GTFSRT_BUS/fallback vazio/FullFallback/trace=false; BRT GTFSRT_BRT. Preservar todos os Compose efetivos e overrides referenciados nos labels.
2. Copiar **cinco** arquivos para diretório novo privado: `its_window.py`, `its_log_capture.py`, `probe_http.py`, os dois overrides. Conferir SHA256 com [resumo](log-capture-preflight-20261024.summary.json); registrar os cinco em `state.json.operational_sha256` (nome do arquivo → hash). Os hashes são dos bytes UTF-8/LF canônicos versionados: preservar esses bytes ao copiar; checkout Windows com autocrlf exige exportação canônica antes da transferência. Não usar o supervisor antigo0fdf0d6 sem a captura nova.
3. Verificar lock, uma única API escritora, rollback de configuração com mesma imagem, limites e ausência de outra janela/deploy. Renderizar diferenças somente das três chaves BUS e trace, sem imprimir ambientes. Não iniciar outra API.
4. Preservar cron existente; instalar somente job temporário marcado, após autorização. Executar prova real `phase=proof` via cron, com ancestralidade independente, prova nova e horário recente; não simular `--tick` pelo SSH. Não armar antes de comprovar watchdog/rollback. A prova cron não comprova permissões/funcionamento do Docker: esses devem ser conferidos no preflight e pela captura saudável antes do baseline/ativação.
5. `scheduled` inicia captura em sessão própria e exige saúde/imagem/hash antes de qualquer ITS. Baseline mínimo120s: BUS838/898/006/457/483/355/805; BRT35/60/67 apenas controle. Vazio006 legítimo não é falha. Confirmar REST/SignalR, CPU/RAM/swap, CAS/ACK, frescor40/60/180s/mediana/p90 e arquivo crescendo nos eventos permitidos (baseline GTFS pode não emitir as famílias ITS).
6. Hybrid temporário ITS_CURRENT + GTFSRT_BUS, trace=true, até600s. Conferir contribuição de ambas, etapas de aquisição/seleção/geração/CAS/REST/SignalR/ACK e gate89890s inalterado. Qualquer condição do runbook, inclusive captura sem prova de saúde, retorna GTFS. Seguir supervisão humana efetiva; cron é contingência.
7. Retorno obrigatório GTFSRT_BUS/fallback vazio/FullFallback/trace=false, mesmo com resultado positivo. Uma API, saúde de dependências, imagem151, fonte real e `phase=complete`. Só então remover somente job temporário. Nunca atrasar rollback para coletar logs; preservar arquivos dos labels.
8. Verificar, copiar e reter evidência privada após rollback. Não instalar/armar nesta preparação:

```bash
# Futuro, depois do rollback confirmado; não consulta container removido.
python3 -B "$TRIAL/its_log_capture.py" --directory "$TRIAL/capture" --verify
sha256sum "$TRIAL/capture/aggregates.jsonl" "$TRIAL/capture/health.json"
```

Exigir `integrity_valid=true`; interpretar separadamente completude, gaps e aquisições sem timing/resumo. Caso a captura permaneça ativa após supervisor encerrado, pode receber TERM somente após conferir seu PID/cmdline/nascimento contra health.json; não matar por PID antigo sem validação. Não aguardar isso para executar rollback.

## Validação local

`python -B tools/gps/test_its_log_capture.py`:19/19. `python -B tools/gps/test_its_window.py`:41/41 (36 existentes +5 novos). Total60, sem rede/Docker/infraestrutura real. Subprocessos locais reais provam prefixo fsync após `os._exit(23)` e EOF/código7; transportes falsos simulam recriação/desconexão/rollback. Permissões0700/0600 são solicitadas e testadas; semântica POSIX efetiva, cron e Docker reais permanecem gates do preflight Linux, não foram simulados como aprovação produtiva.

Não houve testes .NET, suíte offline C#, CI ou nova coleta/E2E: nenhum binário/backend C# foi alterado. Artefatos e laboratórios preexistentes preservados. Custo adicional futuro: um processo Python limitado e até dois clientes Docker de leitura, fila/arquivo limitados e fsync por agregado; custo real de CPU/I/O ainda precisa ser observado no host. Não há novas requisições ITS ou escrita adicional no pipeline.

A aquisição histórica de48,6s continua sem causa interna comprovada: esta alteração prepara a evidência para investigá-la, não a resolve por hipótese. **GO documental/técnico para pedir autorização**, com execução ainda condicionada aos gates imediatos; NO-GO se captura/cron/lock/rollback não forem comprovados antes de ativar.

### Hashes operacionais (SHA256, UTF-8/LF)

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed` |
| `its_log_capture.py` | `d1ce61448534750fb005c1952b81149062bd9bd3217836d47b75fd61a5f1ca31` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |
