using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

/// <summary>Harness opt-in; a suíte normal retorna sem executar carga.</summary>
public sealed class GpsMatchingBenchmarkTests : IClassFixture<PostgisGpsFixture>
{
    private const string VariavelExecucao = "RUN_GPS_MATCHING_BENCHMARK";
    private const string PrefixoApplicationName = "npb-";
    private const string FiltroApplicationName = PrefixoApplicationName + "%";
    private const string ApplicationNameAdministrativo = "noponto-benchmark-admin";
    private const string ApplicationNameMonitor = "noponto-benchmark-monitor";
    private const int LimiteApplicationName = 55;
    private const int ParalelismoControlado = 20;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly PostgisGpsFixture _db;
    private string _output = "";

    public GpsMatchingBenchmarkTests(PostgisGpsFixture db) => _db = db;

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task IndividualRealEControlado_VersusBatch50E100()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(VariavelExecucao),
                "true", StringComparison.OrdinalIgnoreCase)) return;

        _output = Environment.GetEnvironmentVariable("GPS_MATCHING_BENCHMARK_OUTPUT")
            ?? throw new InvalidOperationException(
                "Defina GPS_MATCHING_BENCHMARK_OUTPUT fora do repositório.");
        var diretorio = Path.GetDirectoryName(Path.GetFullPath(_output));
        if (string.IsNullOrWhiteSpace(diretorio) || !Directory.Exists(diretorio))
            throw new InvalidOperationException("O diretório de saída deve existir.");

        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException(
                "Defina POSTGIS_TEST_CONNECTION para banco descartável.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var ambiente = await AmbienteAsync(builder);
        await File.WriteAllTextAsync(_output, JsonSerializer.Serialize(
            new PersistedEvent("run-started", DateTimeOffset.UtcNow, ambiente), JsonOptions)
            + Environment.NewLine);

        var perfilA = new Profile("A-REAL", [
            new("OFF-REAL", false, 100, null),
            new("BATCH-50", true, 50, null),
            new("BATCH-100", true, 100, null),
        ]);
        var perfilB = new Profile("B-CONTROLLED", [
            new("CONTROLLED-20", false, 100, ParalelismoControlado),
            new("BATCH-50", true, 50, null),
            new("BATCH-100", true, 100, null),
        ]);

        await ExecutarPerfilAsync(perfilA, builder);
        await ExecutarPerfilAsync(perfilB, builder);
        await PersistirAsync(new PersistedEvent("run-completed", DateTimeOffset.UtcNow,
            new { output = _output }));
    }

    [Fact]
    [Trait("Category", "BenchmarkExplain")]
    public async Task ExplainRepresentativo_IndividualEBatch50E100()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_GPS_MATCHING_EXPLAIN"),
                "true", StringComparison.OrdinalIgnoreCase)) return;

        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException(
                "Defina POSTGIS_TEST_CONNECTION para banco descartável.");
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = $"{_db.Schema},public",
            ApplicationName = PrefixoApplicationName + "explain",
            // Mantém os SETs do auto_explain quando o repository devolve a
            // conexão física ao pool entre os três comandos deste teste.
            NoResetOnClose = true,
        };
        const string configurarAutoExplain = """
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_analyze = on;
            SET auto_explain.log_buffers = on;
            SET auto_explain.log_timing = on;
            SET auto_explain.log_nested_statements = on;
            """;
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString);
        dataSourceBuilder.UsePhysicalConnectionInitializer(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = configurarAutoExplain;
                cmd.ExecuteNonQuery();
            },
            async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = configurarAutoExplain;
                await cmd.ExecuteNonQueryAsync();
            });
        await using var source = dataSourceBuilder.Build();
        var logger = new BenchmarkLogger<GpsItinerarioRepository>();
        var repo = new GpsItinerarioRepository(source, logger);

        _ = await repo.BuscarEnriquecimentoAsync("GPS23", -22.9, -43.2, 90, 100);
        foreach (var tamanho in new[] { 50, 100 })
        {
            var entradas = Enumerable.Range(0, tamanho).Select(i =>
                new EntradaMatchingGlobalLote($"explain-{tamanho}-{i}", "GPS23",
                    -22.9, -43.2, 90, 100)).ToArray();
            var resultado = await repo.BuscarGlobaisEmLoteAsync(entradas, tamanho);
            Assert.Equal(tamanho, resultado.Resultados.Count);
            Assert.Equal(1, resultado.Metricas.MatchingBatchCommandsPostgres);
            Assert.All(resultado.Resultados,
                x => Assert.Equal(StatusBuscaItinerario.Found, x.Global.Status));
        }
        Assert.Equal(0, logger.ConnectionErrors);
        Assert.Equal(0, logger.Timeouts);
    }

    [Fact]
    [Trait("Category", "BenchmarkRecovery")]
    public async Task Recuperacao_ReconheceSessaoNpb_EObservaNormalizacao()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException(
                "Defina POSTGIS_TEST_CONNECTION para banco descartável.");
        var baseBuilder = new NpgsqlConnectionStringBuilder(connection)
        {
            ApplicationName = PrefixoApplicationName + "base-adversarial",
        };
        var appName = CriarApplicationName($"test-session-{Guid.NewGuid():N}");
        Assert.StartsWith(PrefixoApplicationName, appName, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(appName) < 63);
        Assert.True(Encoding.UTF8.GetByteCount(ApplicationNameAdministrativo) < 63);

        var sampleBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            ApplicationName = appName,
            Pooling = false,
        };
        await using (var sampleConnection = new NpgsqlConnection(sampleBuilder.ConnectionString))
        {
            await sampleConnection.OpenAsync();
            var observacao = await ObservarSessoesBenchmarkAsync(baseBuilder);
            Assert.True(observacao.Sessoes > 0);
            Assert.Equal(ApplicationNameAdministrativo,
                observacao.ApplicationNameAdministrativo);
            Assert.False(observacao.ApplicationNameAdministrativo.StartsWith(
                PrefixoApplicationName, StringComparison.Ordinal));
        }

        var recuperacao = await AguardarNormalizacaoBenchmarkAsync(baseBuilder,
            maxTentativas: 40, intervalo: TimeSpan.FromMilliseconds(25));
        Assert.True(recuperacao.ConexaoAdministrativaFuncional);
        Assert.True(recuperacao.Normalizado);
        Assert.Equal(0, recuperacao.Sessoes);
        Assert.Equal(0, recuperacao.QueriesAtivas);
    }

    private async Task ExecutarPerfilAsync(Profile perfil, NpgsqlConnectionStringBuilder builder)
    {
        int[] tamanhos = [100, 500, 1000, 3300];
        var assinaturas = new Dictionary<int, string>();
        var canonicos = new Dictionary<int, string>();
        var modosBloqueados = new HashSet<string>(StringComparer.Ordinal);
        var offSaturado = false;
        int? primeiroCorpusSaturado = null;

        foreach (var tamanho in tamanhos)
        {
            var limites = await LimitesServidorAsync(builder);
            await PersistirAsync(new PersistedEvent("scale-started", DateTimeOffset.UtcNow,
                new { perfil = perfil.Nome, corpus = tamanho, limites }));
            var corpus = CriarCorpus(tamanho);

            foreach (var modo in perfil.Modos)
            {
                if (offSaturado && !modo.Batch)
                {
                    await PersistirAsync(AmostraNaoExecutada(perfil, tamanho, modo,
                        SampleStatus.NotRunBecauseBaselineSaturated,
                        $"OFF real saturou primeiro em N={primeiroCorpusSaturado}."));
                    continue;
                }
                if (modosBloqueados.Contains(modo.Nome)) continue;

                var warmup = await ExecutarAsync(perfil,
                    corpus.Take(Math.Min(100, tamanho)).ToArray(), modo, builder,
                    targetCorpus: tamanho, round: 0, isWarmup: true);
                await PersistirAsync(warmup);
                if (warmup.Status == SampleStatus.Valid) continue;

                modosBloqueados.Add(modo.Nome);
                if (!modo.Batch && perfil.Nome == "A-REAL")
                {
                    offSaturado = true;
                    primeiroCorpusSaturado = tamanho;
                    await RecuperarBancoAsync(builder, perfil.Nome, tamanho, modo.Nome);
                }
                if (!modo.Batch && perfil.Nome == "B-CONTROLLED") return;
            }

            var repeticoes = tamanho == 3300 ? 3 : 5;
            string[][] ordens =
            [
                [perfil.Modos[0].Nome, "BATCH-50", "BATCH-100"],
                ["BATCH-100", perfil.Modos[0].Nome, "BATCH-50"],
                ["BATCH-50", "BATCH-100", perfil.Modos[0].Nome],
                [perfil.Modos[0].Nome, "BATCH-50", "BATCH-100"],
                ["BATCH-100", perfil.Modos[0].Nome, "BATCH-50"],
            ];

            for (var rodada = 0; rodada < repeticoes; rodada++)
            {
                foreach (var nome in ordens[rodada])
                {
                    var modo = perfil.Modos.Single(x => x.Nome == nome);
                    if (modosBloqueados.Contains(nome) || (offSaturado && !modo.Batch)) continue;

                    var sample = await ExecutarAsync(perfil, corpus, modo, builder,
                        targetCorpus: tamanho, round: rodada + 1, isWarmup: false);
                    if (sample.Status == SampleStatus.Valid)
                    {
                        if (assinaturas.TryAdd(tamanho, sample.Signature!))
                            canonicos[tamanho] = sample.Canonical!;
                        else if (!string.Equals(canonicos[tamanho], sample.Canonical,
                                     StringComparison.Ordinal))
                            sample = sample with
                            {
                                Status = SampleStatus.FunctionalDivergence,
                                Detail = PrimeiraDiferenca(canonicos[tamanho], sample.Canonical!,
                                    tamanho, perfil.Nome, modo.Nome),
                            };
                    }

                    await PersistirAsync(sample);
                    if (sample.Status == SampleStatus.Valid) continue;

                    modosBloqueados.Add(nome);
                    if (!modo.Batch && perfil.Nome == "A-REAL"
                        && sample.Status is SampleStatus.InfrastructureFailure
                            or SampleStatus.Timeout)
                    {
                        offSaturado = true;
                        primeiroCorpusSaturado ??= tamanho;
                        await RecuperarBancoAsync(builder, perfil.Nome, tamanho, modo.Nome);
                    }
                    else if (!modo.Batch && perfil.Nome == "B-CONTROLLED") return;
                }
            }
        }
    }

    private async Task<BenchmarkSample> ExecutarAsync(Profile perfil,
        EntradaEnriquecimentoGps[] corpus, Mode modo, NpgsqlConnectionStringBuilder baseBuilder,
        int targetCorpus, int round, bool isWarmup)
    {
        // PostgreSQL limita application_name a NAMEDATALEN-1 (63 bytes).
        var perfilCurto = perfil.Nome == "A-REAL" ? "a" : "b";
        var modoCurto = modo.Nome.Replace("CONTROLLED", "ctl", StringComparison.Ordinal)
            .Replace("BATCH", "b", StringComparison.Ordinal)
            .Replace("OFF-REAL", "off", StringComparison.Ordinal);
        var appName = CriarApplicationName(
            $"{perfilCurto}-{corpus.Length}-{round}-{modoCurto}-{Guid.NewGuid():N}");
        var mainBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            SearchPath = $"{_db.Schema},public",
            ApplicationName = appName,
        };
        var performance = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        var loggerRepo = new BenchmarkLogger<GpsItinerarioRepository>();
        var loggerService = new BenchmarkLogger<GpsEnriquecimentoService>();
        await using var source = NpgsqlDataSource.Create(mainBuilder.ConnectionString);
        var repo = new GpsItinerarioRepository(source, loggerRepo);
        var service = new GpsEnriquecimentoService(repo,
            Options.Create(new GpsPollingOptions()),
            Options.Create(new GpsMatchingBatchOptions
            {
                Enabled = modo.Batch,
                TamanhoChunk = modo.Chunk,
            }), loggerService);

        CancellationTokenSource? monitorCts = null;
        Task<ConnectionStats>? monitorTask = null;
        var sw = new Stopwatch();
        ResultadoEnriquecimentoGps[] resultados = [];
        Exception? unexpected = null;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(false);

        try
        {
            // Priming fora da janela: cria histórico do dirigido sem saturar o Perfil A.
            var dirigidos = corpus.Where(x => x.Posicao.Ordem.EndsWith("-D", StringComparison.Ordinal))
                .Select(x => new EntradaEnriquecimentoGps(x.Posicao with
                {
                    Bearing = 90,
                    TimestampGps = x.Posicao.TimestampGps.AddSeconds(-5),
                    TimestampServidor = x.Posicao.TimestampServidor.AddSeconds(-5),
                }, null)).ToArray();
            if (dirigidos.Length > 0)
                _ = modo.Batch
                    ? await ProcessarAsync(service, dirigidos, batch: true, performance: null)
                    : await ProcessarControladoAsync(service, dirigidos,
                        ParalelismoControlado, performance: null);
            if (loggerRepo.ConnectionErrors > 0)
                throw new InvalidOperationException("Falha de conexão durante o priming.");

            monitorCts = new CancellationTokenSource();
            monitorTask = MonitorarConexoesAsync(baseBuilder, appName, monitorCts.Token);
            // Dá ao monitor tempo para abrir sua conexão e fazer a primeira amostra
            // antes de operações batch curtas, sem incluir essa espera no cronômetro.
            await Task.Delay(50);
            sw.Start();
            resultados = modo.ControlledDegree.HasValue
                ? await ProcessarControladoAsync(service, corpus, modo.ControlledDegree.Value,
                    performance)
                : await ProcessarAsync(service, corpus, modo.Batch, performance);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            unexpected = ex;
            loggerService.Capture(ex);
        }
        finally { monitorCts?.Cancel(); }

        var connections = monitorTask is null ? new ConnectionStats() : await monitorTask;
        monitorCts?.Dispose();
        var loggerTimeouts = loggerRepo.Timeouts + loggerService.Timeouts;
        var connectionErrors = loggerRepo.ConnectionErrors
            + loggerService.ConnectionErrors + connections.ConnectionErrors;
        var afetados = resultados.Where(ResultadoComFalhaInfraestrutura).ToArray();
        var infraCount = afetados.Length;
        var status = Classificar(unexpected, loggerTimeouts, connectionErrors,
            infraCount, modo.Batch, performance);
        var canonical = resultados.Length == corpus.Length ? Canonico(resultados) : null;
        var signature = canonical is null ? null : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var detail = unexpected is null ? null : unexpected.GetType().Name + ": " + unexpected.Message;
        if (infraCount > 0)
            detail = $"{infraCount} entrada(s) com falha; primeira={afetados[0].Posicao.Ordem}.";

        return new(perfil.Nome, corpus.Length, targetCorpus, modo.Nome, modo.Chunk,
            modo.ControlledDegree, round, isWarmup, status, detail,
            sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds / corpus.Length,
            sw.Elapsed.TotalSeconds > 0 ? corpus.Length / sw.Elapsed.TotalSeconds : 0,
            performance.MatchingComandosPostgres,
            performance.MatchingComandosPostgres - performance.MatchingBatchCommandsPostgres,
            performance.MatchingBatchInputs, performance.MatchingBatchOperations,
            performance.MatchingBatchCommandsPostgres,
            performance.MatchingFallbackCommandsPostgres,
            performance.MatchingBatchCircuitOpened, performance.MatchingBatchProbes,
            performance.MatchingBatchProbesSucesso, performance.MatchingBatchProbesFalha,
            performance.MatchingBatchOperacoesDegradadas,
            performance.MatchingBatchInfrastructureFailures,
            performance.MatchingGlobaisSimples, performance.MatchingCombinados,
            performance.MatchingDirecionados,
            performance.ProjecaoOperacionalSolicitada,
            performance.ProjecaoOperacionalEncontrada,
            performance.ProjecaoOperacionalInelegivel,
            performance.ProjecaoOperacionalFalha,
            infraCount, afetados.FirstOrDefault()?.Posicao.Ordem,
            connections.MaxConnections, connections.MaxActive,
            connections.MaxIdle, connections.MaxWaiting, connectionErrors,
            loggerTimeouts,
            GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1,
            GC.CollectionCount(2) - gc2, GC.GetTotalAllocatedBytes(false) - allocated,
            signature, canonical);
    }

    private static SampleStatus Classificar(Exception? unexpected, int timeouts,
        int connectionErrors, int infraCount, bool batch, GpsCicloPerformance performance)
    {
        if (timeouts > 0) return SampleStatus.Timeout;
        if (connectionErrors > 0 || infraCount > 0
            || performance.MatchingBatchInfrastructureFailures > 0
            || performance.MatchingCombinadoFalha > 0
            || performance.MatchingDirecionadoFalha > 0
            || performance.ProjecaoOperacionalFalha > 0)
            return SampleStatus.InfrastructureFailure;
        if (unexpected is not null) return SampleStatus.UnexpectedError;
        if (batch && performance.MatchingFallbackCommandsPostgres > 0)
            return SampleStatus.Fallback;
        if (batch && performance.MatchingBatchCircuitOpened > 0)
            return SampleStatus.CircuitOpen;
        if (batch && (performance.MatchingBatchProbes > 0
                      || performance.MatchingBatchOperacoesDegradadas > 0))
            return SampleStatus.Fallback;
        return SampleStatus.Valid;
    }

    private static bool ResultadoComFalhaInfraestrutura(ResultadoEnriquecimentoGps resultado) =>
        resultado.Posicao.ItinerarioId is null
        || resultado.ProjecaoOperacional.Status == StatusProjecaoOperacional.FalhaInfraestrutura;

    private static async Task<ResultadoEnriquecimentoGps[]> ProcessarAsync(
        GpsEnriquecimentoService service, EntradaEnriquecimentoGps[] entradas,
        bool batch, GpsCicloPerformance? performance) => batch
            ? await service.EnriquecerLoteComContextoAsync(entradas, default, performance)
            : await Task.WhenAll(entradas.Select(x => service.EnriquecerComContextoAsync(
                x.Posicao, x.Contexto, default, performance)));

    private static async Task<ResultadoEnriquecimentoGps[]> ProcessarControladoAsync(
        GpsEnriquecimentoService service, EntradaEnriquecimentoGps[] entradas,
        int grau, GpsCicloPerformance? performance)
    {
        var resultados = new ResultadoEnriquecimentoGps[entradas.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, entradas.Length),
            new ParallelOptions { MaxDegreeOfParallelism = grau }, async (indice, ct) =>
            {
                var entrada = entradas[indice];
                resultados[indice] = await service.EnriquecerComContextoAsync(
                    entrada.Posicao, entrada.Contexto, ct, performance);
            });
        return resultados;
    }

    private EntradaEnriquecimentoGps[] CriarCorpus(int quantidade)
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, quantidade).Select(i =>
        {
            var tipo = i % 4;
            var sufixo = tipo == 3 ? "D" : tipo == 2 ? "C" : "S";
            var ordem = $"BENCH-{i:D5}-{sufixo}";
            var posicao = new PosicaoVeiculoDto
            {
                Ordem = ordem,
                CodigoLinha = "GPS23",
                Latitude = tipo == 2 ? -22.8998 : -22.9,
                Longitude = -43.2,
                Bearing = tipo == 3 ? 270 : 90,
                Velocidade = 30,
                TimestampGps = t0.AddSeconds(5),
                TimestampServidor = t0.AddSeconds(5),
            };
            return new EntradaEnriquecimentoGps(posicao,
                tipo == 2 ? Contexto(ordem, t0) : null);
        }).ToArray();
    }

    private ContextoOperacional Contexto(string ordem, DateTimeOffset timestamp)
    {
        var observada = new ViagemObservadaState(Guid.Parse("10000000-0000-0000-0000-000000000001"),
            ordem, _db.R1, timestamp, timestamp, .40);
        return new([], observada, new(observada, "GPS23",
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("10000000-0000-0000-0000-000000000003")));
    }

    private static string Canonico(IEnumerable<ResultadoEnriquecimentoGps> resultados)
    {
        var sb = new StringBuilder();
        foreach (var x in resultados.OrderBy(x => x.Posicao.Ordem, StringComparer.Ordinal))
        {
            var p = x.Posicao;
            var a = x.ProjecaoOperacional;
            sb.Append(p.Ordem).Append('|').Append(p.CodigoLinha).Append('|')
                .Append(F(p.Latitude)).Append('|').Append(F(p.Longitude)).Append('|')
                .Append(p.ItinerarioId).Append('|').Append(F(p.PosicaoNaRota)).Append('|')
                .Append(F(p.ComprimentoRotaMetros)).Append('|').Append(F(p.Bearing)).Append('|')
                .Append(F(p.VelocidadeMedia)).Append('|').Append(p.ProximaParadaNome).Append('|')
                .Append(F(p.DistanciaProximaParadaMetros)).Append('|').Append(p.Status).Append('|')
                .Append(a.Status).Append('|').Append(a.Projecao?.ItinerarioId).Append('|')
                .Append(F(a.Projecao?.PosicaoNaRota)).Append('|')
                .Append(F(a.Projecao?.DistanciaRotaMetros)).Append('|')
                .Append(F(a.Projecao?.ComprimentoRotaMetros)).AppendLine();
        }
        return sb.ToString();
    }

    private static string PrimeiraDiferenca(string esperado, string atual,
        int corpus, string perfil, string modo)
    {
        var linhasEsperadas = esperado.Split('\n');
        var linhasAtuais = atual.Split('\n');
        for (var i = 0; i < Math.Min(linhasEsperadas.Length, linhasAtuais.Length); i++)
            if (!string.Equals(linhasEsperadas[i], linhasAtuais[i], StringComparison.Ordinal))
                return $"Divergência em perfil={perfil}, N={corpus}, modo={modo}, linha={i}: "
                    + $"esperado={linhasEsperadas[i]} | atual={linhasAtuais[i]}";
        return $"Divergência em perfil={perfil}, N={corpus}, modo={modo}: "
            + $"linhas esperadas={linhasEsperadas.Length}, atuais={linhasAtuais.Length}.";
    }

    private async Task<ConnectionStats> MonitorarConexoesAsync(
        NpgsqlConnectionStringBuilder baseBuilder, string appName, CancellationToken ct)
    {
        var monitorBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            ApplicationName = ApplicationNameMonitor,
            MaxPoolSize = 1,
        };
        var stats = new ConnectionStats();
        try
        {
            await using var source = NpgsqlDataSource.Create(monitorBuilder.ConnectionString);
            await using var conn = await source.OpenConnectionAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT count(*)::int,
                      count(*) FILTER (WHERE state='active')::int,
                      count(*) FILTER (WHERE state='idle')::int,
                      count(*) FILTER (WHERE state='active' AND wait_event IS NOT NULL)::int
                    FROM pg_stat_activity WHERE datname=current_database()
                      AND application_name=@app
                    """;
                cmd.Parameters.AddWithValue("app", appName);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    stats.MaxConnections = Math.Max(stats.MaxConnections, reader.GetInt32(0));
                    stats.MaxActive = Math.Max(stats.MaxActive, reader.GetInt32(1));
                    stats.MaxIdle = Math.Max(stats.MaxIdle, reader.GetInt32(2));
                    stats.MaxWaiting = Math.Max(stats.MaxWaiting, reader.GetInt32(3));
                }
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { stats.ConnectionErrors++; }
        return stats;
    }

    private async Task RecuperarBancoAsync(NpgsqlConnectionStringBuilder baseBuilder,
        string perfil, int corpus, string modo)
    {
        var estado = await AguardarNormalizacaoBenchmarkAsync(baseBuilder,
            maxTentativas: 120, intervalo: TimeSpan.FromMilliseconds(250));
        var recovery = new Recovery(perfil, corpus, modo, estado.Normalizado,
            estado.ConexaoAdministrativaFuncional, estado.Sessoes,
            estado.QueriesAtivas, estado.RecoveryMs);
        await PersistirAsync(new PersistedEvent("database-recovery",
            DateTimeOffset.UtcNow, recovery));
        if (!estado.Normalizado)
            throw new InvalidOperationException("PostgreSQL não normalizou após saturação do OFF.");
    }

    private static async Task<RecoveryState> AguardarNormalizacaoBenchmarkAsync(
        NpgsqlConnectionStringBuilder baseBuilder, int maxTentativas, TimeSpan intervalo)
    {
        var sw = Stopwatch.StartNew();
        var conexaoAdministrativaFuncional = false;
        var observacao = new BenchmarkSessions(-1, -1, "");
        for (var tentativa = 0; tentativa < maxTentativas; tentativa++)
        {
            try
            {
                observacao = await ObservarSessoesBenchmarkAsync(baseBuilder);
                conexaoAdministrativaFuncional = true;
                if (observacao.Sessoes == 0 && observacao.QueriesAtivas == 0)
                {
                    sw.Stop();
                    return new(true, true, 0, 0, sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (NpgsqlException) { }
            await Task.Delay(intervalo);
        }
        sw.Stop();
        return new(false, conexaoAdministrativaFuncional, observacao.Sessoes,
            observacao.QueriesAtivas, sw.Elapsed.TotalMilliseconds);
    }

    private static async Task<BenchmarkSessions> ObservarSessoesBenchmarkAsync(
        NpgsqlConnectionStringBuilder baseBuilder)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            ApplicationName = ApplicationNameAdministrativo,
        };
        await using var conn = new NpgsqlConnection(adminBuilder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)::int,
                   count(*) FILTER (WHERE state='active')::int,
                   current_setting('application_name')
            FROM pg_stat_activity
            WHERE datname=current_database()
              AND application_name LIKE @application_name_pattern
            """;
        cmd.Parameters.AddWithValue("application_name_pattern", FiltroApplicationName);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
    }

    private static string CriarApplicationName(string identificador)
    {
        var appName = PrefixoApplicationName + identificador;
        return appName.Length <= LimiteApplicationName
            ? appName
            : appName[..LimiteApplicationName];
    }

    private static async Task<ServerLimits> LimitesServidorAsync(
        NpgsqlConnectionStringBuilder builder)
    {
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT current_setting('max_connections')::int,
                   current_setting('superuser_reserved_connections')::int
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task<EnvironmentInfo> AmbienteAsync(NpgsqlConnectionStringBuilder builder)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version(), postgis_full_version(), count(*) FROM \"Itinerarios\"";
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.ProcessorCount, Environment.Version.ToString(),
            System.Runtime.GCSettings.IsServerGC, builder.MaxPoolSize,
            builder.Timeout, builder.CommandTimeout,
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            ParalelismoControlado,
            "GpsPollingOptions.GrauParalelismoEnriquecimento default=20");
    }

    private async Task PersistirAsync(object value) =>
        await File.AppendAllTextAsync(_output,
            JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);

    private static BenchmarkSample AmostraNaoExecutada(Profile perfil, int corpus,
        Mode modo, SampleStatus status, string detail) => new(
            Profile: perfil.Nome, Corpus: corpus, TargetCorpus: corpus, Mode: modo.Nome,
            Chunk: modo.Chunk, ControlledDegree: modo.ControlledDegree,
            Round: 0, IsWarmup: false, Status: status, Detail: detail,
            TotalMs: 0, PerPositionMs: 0, Throughput: 0,
            PostgresCommands: 0, IndividualCommands: 0, BatchInputs: 0,
            BatchOperations: 0, BatchCommands: 0, FallbackCommands: 0,
            CircuitOpened: 0, Probes: 0, ProbesSuccess: 0, ProbesFailed: 0,
            Degraded: 0, BatchInfrastructureFailures: 0,
            GlobalSimple: 0, Combined: 0, Directed: 0,
            ProjectionRequested: 0, ProjectionFound: 0, ProjectionIneligible: 0,
            ProjectionFailed: 0, InfrastructureFailureCount: 0,
            FirstInfrastructureFailure: null,
            PeakConnections: 0, PeakActive: 0, PeakIdle: 0, PeakWaiting: 0,
            ConnectionErrors: 0, TimeoutErrors: 0,
            Gen0: 0, Gen1: 0, Gen2: 0, AllocatedBytes: 0,
            Signature: null, Canonical: null);

    private static string F(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private sealed record Profile(string Nome, Mode[] Modos);
    private sealed record Mode(string Nome, bool Batch, int Chunk, int? ControlledDegree);
    private enum SampleStatus
    {
        Valid, InfrastructureFailure, Timeout, Fallback, CircuitOpen,
        FunctionalDivergence, UnexpectedError, NotRunBecauseBaselineSaturated,
    }

    private sealed class ConnectionStats
    {
        public int MaxConnections { get; set; }
        public int MaxActive { get; set; }
        public int MaxIdle { get; set; }
        public int MaxWaiting { get; set; }
        public int ConnectionErrors { get; set; }
    }

    private sealed class BenchmarkLogger<T> : ILogger<T>
    {
        public int Timeouts { get; private set; }
        public int ConnectionErrors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Capture(exception);
        }
        public void Capture(Exception exception)
        {
            if (exception is NpgsqlException) ConnectionErrors++;
            if (EhTimeout(exception)) Timeouts++;
        }
        private static bool EhTimeout(Exception ex) => ex is TimeoutException
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || ex.InnerException is not null && EhTimeout(ex.InnerException);
    }

    private sealed record EnvironmentInfo(string OS, int Processors, string DotNet,
        bool ServerGc, int MaxPoolSize, int ConnectionTimeoutSeconds,
        int CommandTimeoutSeconds, string PostgreSql, string PostGis, long Itineraries,
        int ControlledDegree, string ControlledDegreeOrigin);
    private sealed record ServerLimits(int MaxConnections, int SuperuserReservedConnections);
    private sealed record Recovery(string Profile, int Corpus, string Mode,
        bool Normalized, bool AdministrativeConnectionFunctional,
        int RemainingBenchmarkSessions, int RemainingActiveBenchmarkQueries,
        double RecoveryMs);
    private sealed record BenchmarkSessions(int Sessoes, int QueriesAtivas,
        string ApplicationNameAdministrativo);
    private sealed record RecoveryState(bool Normalizado, bool ConexaoAdministrativaFuncional,
        int Sessoes, int QueriesAtivas, double RecoveryMs);
    private sealed record PersistedEvent(string Event, DateTimeOffset Timestamp, object Data);

    // Contagens instrumentadas no boundary do repository; não são pg_stat_statements.
    private sealed record BenchmarkSample(string Profile, int Corpus, int TargetCorpus, string Mode,
        int Chunk, int? ControlledDegree, int Round, bool IsWarmup,
        SampleStatus Status, string? Detail,
        double TotalMs, double PerPositionMs, double Throughput,
        int PostgresCommands, int IndividualCommands, int BatchInputs,
        int BatchOperations, int BatchCommands, int FallbackCommands,
        int CircuitOpened, int Probes, int ProbesSuccess, int ProbesFailed,
        int Degraded, int BatchInfrastructureFailures,
        int GlobalSimple, int Combined, int Directed,
        int ProjectionRequested, int ProjectionFound, int ProjectionIneligible,
        int ProjectionFailed, int InfrastructureFailureCount,
        string? FirstInfrastructureFailure,
        int PeakConnections, int PeakActive, int PeakIdle, int PeakWaiting,
        int ConnectionErrors, int TimeoutErrors,
        int Gen0, int Gen1, int Gen2, long AllocatedBytes,
        string? Signature, [property: JsonIgnore] string? Canonical);
}
