using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NetTopologySuite.Geometries;
using NoPonto.Application.GPS;
using NoPonto.Domain.Entities;
using NoPonto.Migrations;
using Npgsql;
using StackExchange.Redis;

namespace NoPonto.Tests;

// Test-only guard shared by the explicit D8 preparation and capture steps. No connection defaults.
internal sealed record HistoricalEtaCaptureLab(string Postgres, string Redis, string Guard, string Output)
{
    internal HistoricalEtaShadowOptions ShadowOptions() => new() {
        Enabled = true, OperationalDtoCaptureEnabled = true, MlEnabled = false,
        HistoricalEnabled = false, MetricsLogEnabled = false, TimeoutMs = 2000 };
    internal HistoricalEtaOfflineExportOptions ExportOptions() => new() {
        Enabled = true, Fixture = true, DirectoryPath = Output };
    internal static HistoricalEtaCaptureLab Parse(string? postgres, string? redis, string? guard, string? output)
    {
        if (!Guid.TryParseExact(guard, "N", out _) || string.IsNullOrWhiteSpace(postgres)
            || string.IsNullOrWhiteSpace(redis) || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("D8 requires explicit disposable resources.");
        var pg = new NpgsqlConnectionStringBuilder(postgres);
        var endpoint = redis.Split(':');
        if (pg.Host != "127.0.0.1" || pg.Database != "noponto_d8_" + guard || pg.Username != "postgres"
            || pg.SearchPath is not (null or "") || !string.IsNullOrEmpty(pg.Password)
            || pg.Port is < 1024 or > 65535 || pg.Port is 5432 or 6379
            || endpoint.Length != 2 || endpoint[0] != "127.0.0.1"
            || !int.TryParse(endpoint[1], out var port) || port is < 1024 or > 65535
            || port is 5432 or 6379 || port == pg.Port)
            throw new InvalidOperationException("D8 endpoint identity rejected.");
        if (!Path.IsPathFullyQualified(output) || output.StartsWith(@"\\", StringComparison.Ordinal)
            || Path.GetFileName(output) != "capture-" + guard || Directory.Exists(output) || File.Exists(output))
            throw new InvalidOperationException("D8 needs a new private capture directory.");
        for (var parent = new DirectoryInfo(output).Parent; parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("D8 output cannot traverse links.");
        return new(postgres, redis, guard!, output);
    }

    internal static HistoricalEtaCaptureLab FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_POSTGRES"),
        Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_REDIS"),
        Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_GUARD"),
        Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_D8_OUTPUT"));

    internal async Task VerifyOwnership(NpgsqlDataSource source, IConnectionMultiplexer redis)
    {
        await using var c = await source.OpenConnectionAsync();
        if ((string?)await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync() != "noponto_d8_" + Guard
            || (string?)await new NpgsqlCommand("SELECT token FROM noponto_shadow_fixture_guard", c).ExecuteScalarAsync() != Guard
            || (string?)await redis.GetDatabase().StringGetAsync("fixture:guard") != Guard)
            throw new InvalidOperationException("D8 ownership proof rejected; no schema/data writes allowed.");
    }

    internal static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    internal static string GenerateSchema()
    {
        // Generates SQL locally. Does not open a connection, call EnsureCreated or run migrations.
        using var context = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused", o => o.UseNetTopologySuite()).Options);
        // These durable tables are SQL-only, absent from the EF model. Read just their DDL
        // definitions; do not migrate, replay other migrations or create migration history.
        Migration[] durable = [new ViagemDuravelPostgres(), new IntegridadeCircularDuravel(),
            new RemoverIndiceAtualizadoEmUtcViagensOperacionais()];
        return context.Database.GenerateCreateScript() + "\n" + string.Join("\n",
            durable.SelectMany(m => m.UpOperations).Cast<SqlOperation>().Select(op => op.Sql));
    }

    internal static async Task Seed(TransporteDbContext context)
    {
        context.Modais.Add(new() { Id = Id(1), Nome = "D8 SYNTHETIC" });
        context.Linhas.Add(new() { Id = Id(2), ModalId = Id(1), Codigo = "42", Nome = "D8 SYNTHETIC" });
        context.Sentidos.Add(new() { Id = Id(3), LinhaId = Id(2), Nome = "D8 SYNTHETIC" });
        var pattern = new PadraoOperacional { Id = Id(4), SentidoId = Id(3), Ativo = true, Chave = "D8", TipoServico = "TESTE" };
        context.PadroesOperacionais.Add(pattern);
        await context.SaveChangesAsync();
        context.PadroesVersoes.Add(new() { Id = Id(5), PadraoOperacionalId = Id(4), Numero = 1,
            Geometria = new LineString([new(-43.21, -22.9), new(-43.19, -22.9)]) { SRID = 4326 },
            ComprimentoMetros = 2050, Topologia = "LINEAR", HashEstrutural = "d8-synthetic",
            MetodoConstrucao = "TESTE", AlgoritmoVersao = "TESTE", Confianca = 1,
            ResultadoValidacao = ResultadosValidacaoPadrao.Valida, Relatorio = "{}", CriadoEmUtc = DateTimeOffset.UtcNow });
        context.Paradas.Add(new() { Id = Id(6), Codigo = "D8", Nome = "D8 SYNTHETIC",
            Localizacao = new Point(-43.194, -22.9) { SRID = 4326 } });
        for (var i = 0; i < 2; i++) context.OcorrenciasParadasPadroes.Add(new() {
            Id = Id(7 + i), PadraoVersaoId = Id(5), ParadaId = Id(6), Ordem = i + 1,
            PosicaoTracado = i == 0 ? .05 : .8, DistanciaAcumuladaMetros = i == 0 ? 100 : 1640, DistanciaDaLinhaMetros = 0 });
        await context.SaveChangesAsync();
        pattern.VersaoAtualId = Id(5);
        await context.SaveChangesAsync();
    }
}
