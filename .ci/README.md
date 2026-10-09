# CI NoPonto — Etapa 1

Base: main `aff181d`, conferida com `git fetch origin main` em 2026-10-09.

O workflow `.github/workflows/deploy.yml` valida Pull Requests destinados à main
e pushes na main. O check chama-se `CI offline (.NET 9)`. Somente push na main,
após o job `validate` aprovado, executa o build/push GHCR existente. Foram
preservados contexto, Dockerfile, plataforma linux/amd64, cache GHA, autenticação
GHCR e tags `latest`, timestamp-SHA curto e `build-N`. PRs agora também consomem
números de execução do workflow; a sequência das tags build-N pode ter lacunas.

`workflow_dispatch` e o job de deploy legado foram retirados porque copiavam o
Compose base e chamavam um script incompatível com a configuração GTFS-RT
informada. Não existe substituto de deploy nesta etapa. Secrets Tailscale/SSH não
são referenciados; não é necessário apagá-los. O histórico Git preserva o fluxo
antigo para consulta. Esta alteração só passa a valer no GitHub após integração;
execuções antigas e refs antigas precisam ser consideradas pelo operador.

## Seleção investigada

`offline-tests.json` é uma allowlist de classes, com exclusão explícita do replay
`DeterministicAggregationReplay_MeasuresIncrementalCostOnly`. Não se usa seleção
por ausência de "Integration" no nome. `offline_tests.py filter` gera o filtro
VSTest com ponto após o nome da classe, evitando classes com sufixos semelhantes.

| Classes | Dependências verificadas e motivo |
|---|---|
| GpsLeituraValidatorTests | Validação pura de coordenadas/timestamps |
| GpsTimestampDeduplicationTests | Watermarks fake, MemoryDistributedCache, fontes fixas e matching fake; não usa Redis real |
| GpsEtaClientTests, GpsBrtTelemetriaTests, GpsSourceAbstractionTests | HTTP interceptado por HttpMessageHandler de teste, sem sockets externos |
| GpsPollingFontesTests, GpsPollingCadenciaTests | Snapshot/gates em memória, tarefas e relógio; nenhuma coleta real |
| GpsPerformanceMetricsTests, GpsMatchingDiagnosticsTests | Contadores e FakeGpsPadraoRepository de GpsEnriquecimentoServiceTests; replay de benchmark excluído |
| GtfsRealtimeGpsTests | Protobuf sintético, handlers HTTP e relógio fake; medição limitada do parser não condiciona aprovação por latência |
| TelemetriaMlIdentidadeTests, TelemetriaMlSamplingTests | Contratos/serialização e sampling em memória |
| ViagemOperacionalRegraTests | Regras locais; Modelo_SnapshotSincronizado configura provider Npgsql com valores sintéticos, compara metadados e não abre conexão nem aplica migration |
| ViagemOperacionalCodecCompatibilidadeTests, CancelamentoCandidatoCursorTests | Codec, transições e factories em memória; não executam SQL |
| EstadoCausalPosicaoTests | Spies de repositório/cache e fontes fake; teste ETA OFF usa HttpClient sem BaseAddress e sem chamada de inferência |

As classes não foram alteradas. Nomes sem namespace de GpsLeituraValidatorTests
e GpsTimestampDeduplicationTests foram preservados na seleção.

Ficam fora: fixtures PostGIS/ViagemOperacional, integrações PostgreSQL/Redis,
retenção conectada, importações/persistência conectadas, auditorias de feeds reais,
benchmarks extensos e GTFS-RT integrado dependente de ZIP/serviços. A fixture
ViagemOperacional cria schema e executa migrations. Há suítes Redis com fallback
localhost:6380 e chaves canônicas; não se deve executar a suíte completa supondo
que todos os testes sejam offline. Testes antigos compilados fora do csproj
tampouco constituem cobertura deste check.

## Isolamento e resultados

O job de validação usa runner hospedado Ubuntu, token somente de leitura e
checkout sem credenciais persistentes. Não referencia secrets, não conecta
Tailscale e não inicia a API/Program.cs nem serviços PostgreSQL/Redis.

Restore/build usam container SDK .NET 9 com rede para baixar NuGet, sem segredos
produtivos. A execução dos testes usa a mesma imagem SDK por Image ID capturado
após pull e mesmos outputs Release somente leitura, com
`--network none`, capacidades removidas e `no-new-privileges`. Não monta Docker
socket, não publica portas e não herda variáveis/conexões do host. Somente fonte,
assembly compilada e diretório descartável de resultados são montados. O script recusa
interfaces diferentes de loopback antes de descobrir testes. VSTest utiliza
loopback interno para comunicar-se com testhost. `dotnet vstest` executa a DLL
previamente compilada, sem restore, build ou reavaliação de targets MSBuild.
Referência: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-vstest
Referência do isolamento:
https://docs.docker.com/engine/network/drivers/none/

Teste com dependência externa introduzida por engano falha sem acessar produção.
O isolamento não é uma garantia contra PR malicioso que modifique o próprio
workflow; alterações de CI exigem revisão como qualquer código executável.

O avaliador TRX exige resultados não vazios, todos Passed, todas as classes da
allowlist presentes e nenhum teste fora da seleção. Não imprime payloads TRX.
Os seis testes Python incluem aprovação, classe ausente, classe conectada extra,
falha/ignorado, relatório vazio e limites do filtro. Não é feito upload de logs,
TRX ou configurações produtivas. Publicação permanece em job separado, usando
apenas os secrets GHCR já existentes, depois do check aprovado.

## Reprodução e pendências

Para gerar o filtro e verificar os testes auxiliares:

```text
python -B .ci/offline_tests.py filter
python -B -m unittest discover -s .ci -p 'test_*.py' -v
python -B .ci/offline_tests.py verify CAMINHO/offline.trx
```

O script `.ci/test-offline.sh` exige container Linux sem rede, outputs Release
previamente compilados e `NOPONTO_TEST_FILTER` gerado pelo comando acima. Usar
os comandos Docker do workflow como referência; não executar toda a suíte contra
conexões locais existentes. A validação não necessita subir dependências.

Após revisão e integração, verificar no GitHub: PR interno/fork, push main, job
build impedido quando validate falha, autenticação GHCR/cache/tags e ausência de
deploy. Configurar o check como obrigatório na proteção da main é uma alteração
administrativa separada, não realizada nesta etapa. Actions e SDK mantêm tags
de versões existentes; pinagem/migração de plataforma ficam para revisão futura.
Migrations no startup, deploy por digest, rollback e ordem de promoção permanecem
fora desta implementação. A suite offline não certifica integração nem produção.

## Validação local em 2026-10-09

- Restore e build Release no Windows/.NET SDK 9.0.306 aprovados: zero erros,
  cinco warnings preexistentes (CS8981, CS7022 e xUnit2031).
- VSTest sobre a DLL Release: 358 aprovados, zero falhas/ignorados, no Windows
  (25 s) e no container SDK Linux sem rede externa (7 s). Ambos os TRX foram
  aceitos pelo avaliador, com as 16 classes presentes.
- O teste Linux utilizou a DLL portátil compilada no Windows. Não constitui
  comprovação do restore/build completo do runner Linux. Tentativas locais de
  preparação/build Linux foram interrompidas por lentidão do Docker Desktop;
  restore offline com cache existente apresentou NU1900 por falta de rede para
  auditoria NuGet. A validação definitiva desse estágio permanece no Actions.
- Guarda de rede recusou container bridge com exit 1 antes dos testes.
- Seis testes Python aprovados; actionlint 1.7.7 sem diagnósticos. YAML e regras
  de eventos/gate/tags/isolamento conferidos com parser local.
- Diff verificado, incluindo arquivos novos. Nenhuma mudança em Program.cs,
  migrations, Dockerfile, Compose ou código funcional. Sem produção, deploy,
  build/push de imagem da API, commit ou push. Containers SDK desta verificação
  são descartáveis e foram encerrados; nenhum PostgreSQL/Redis foi iniciado.
