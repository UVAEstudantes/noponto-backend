using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaV2PostgresIntegrationTests
{
    [Fact]
    public async Task SamplingCorrelationIdempotencyExpirationAndInvalidation()
    {
        var connectionString = Environment.GetEnvironmentVariable("ETA_V2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Contains("localhost", connectionString, StringComparison.OrdinalIgnoreCase);

        await using var source = new NpgsqlDataSourceBuilder(connectionString)
            .UseNetTopologySuite().Build();
        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(source, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.MigrateAsync();

        var ids = await SeedAsync(db);
        var metrics = new EtaV2Metrics();
        var repository = new EtaV2Repository(source, metrics);
        var now = DateTimeOffset.UtcNow;
        var request = Request(ids, now);
        Assert.True(await repository.TryInsertAsync(request, default));
        Assert.False(await repository.TryInsertAsync(request with { Id = Guid.NewGuid(),
            TimestampPrevisao = now.AddSeconds(5) }, default));

        // Mudança de alvo ignora o sampling do alvo anterior.
        Assert.True(await repository.TryInsertAsync(request with { Id = Guid.NewGuid(),
            OcorrenciaParadaPadraoId = ids.OtherOccurrence, OrdemOcorrencia = 2,
            TimestampPrevisao = now.AddSeconds(5) }, default));

        var wrongVehicle = request with { Id = Guid.NewGuid(), OrdemVeiculo = "OTHER-VEHICLE",
            TimestampPrevisao = now.AddSeconds(20) };
        var wrongTrip = request with { Id = Guid.NewGuid(), OrdemVeiculo = "OTHER-TRIP-VEHICLE",
            ViagemId = Guid.NewGuid(),
            TimestampPrevisao = now.AddSeconds(20) };
        var wrongLap = request with { Id = Guid.NewGuid(), Volta = 2,
            TimestampPrevisao = now.AddSeconds(20) };
        Assert.True(await repository.TryInsertAsync(wrongVehicle, default));
        Assert.True(await repository.TryInsertAsync(wrongTrip, default));
        Assert.True(await repository.TryInsertAsync(wrongLap, default));

        var passageAt = now.AddSeconds(50);
        var passage = new EventoViagem($"passagem:{request.ViagemId:D}:{ids.Occurrence:D}:1",
            "PassagemParada", request.ViagemId, request.OrdemVeiculo, "ETA-TEST",
            ids.Direction, ids.Version, passageAt, ids.Occurrence, ids.Stop, 1, .5,
            passageAt, passageAt, 36, null, 2, ids.Pattern, 1, ids.Line);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(1, await repository.ClosePassageAsync(passage, connection, transaction, default));
            // Segunda execução na mesma passagem é idempotente.
            Assert.Equal(0, await repository.ClosePassageAsync(passage, connection, transaction, default));
            await transaction.CommitAsync();
        }

        var realized = await db.PrevisoesEtaV2.SingleAsync(x => x.Id == request.Id);
        Assert.Equal(StatusPrevisaoEtaV2.Realizada, realized.Status);
        Assert.Equal(50, realized.EtaRealSegundos!.Value, 3);
        Assert.Equal(request.EtaPrevistoSegundos!.Value - 50, realized.ErroSegundos!.Value, 3);
        Assert.Equal(Math.Abs(realized.ErroSegundos.Value), realized.ErroAbsolutoSegundos);
        Assert.Equal(StatusPrevisaoEtaV2.Pendente,
            (await db.PrevisoesEtaV2.SingleAsync(x => x.Id == wrongVehicle.Id)).Status);
        Assert.Equal(StatusPrevisaoEtaV2.Pendente,
            (await db.PrevisoesEtaV2.SingleAsync(x => x.Id == wrongTrip.Id)).Status);
        Assert.Equal(StatusPrevisaoEtaV2.Pendente,
            (await db.PrevisoesEtaV2.SingleAsync(x => x.Id == wrongLap.Id)).Status);

        Assert.True(await repository.ExpireAsync(now.AddMinutes(1), default) >= 1);
        Assert.DoesNotContain(await db.PrevisoesEtaV2.Where(x => x.Status == StatusPrevisaoEtaV2.Realizada)
            .ToListAsync(), x => x.Id != request.Id);
    }

    private static EtaV2PredictionRequest Request(Ids i, DateTimeOffset now) => new(
        Guid.NewGuid(), "ETA-V1", Guid.NewGuid(), now, now, i.Line, i.Direction, i.Pattern,
        i.Version, i.Occurrence, 1, 1, .4, 1000, 36, 90, "BUS", "TEST", 100,
        EtaV2LongitudinalSpeedV0.Predictor, EtaV2LongitudinalSpeedV0.Version, null, 15);

    private static async Task<Ids> SeedAsync(TransporteDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "ETA-" + suffix };
        var line = new Linha { Id = Guid.NewGuid(), Codigo = "ETA-" + suffix, Nome = "ETA", Modal = modal };
        var direction = new Sentido { Id = Guid.NewGuid(), Nome = "IDA", Linha = line };
        var pattern = new PadraoOperacional { Id = Guid.NewGuid(), Sentido = direction,
            Chave = "ETA-" + suffix, TipoServico = "regular" };
        var version = new PadraoVersao { Id = Guid.NewGuid(), PadraoOperacional = pattern, Numero = 1,
            Geometria = new LineString([new Coordinate(-43.2, -22.9), new Coordinate(-43.19, -22.9)]) { SRID = 4326 },
            ComprimentoMetros = 1000, HashEstrutural = suffix, MetodoConstrucao = "TEST",
            Confianca = 1, AlgoritmoVersao = "TEST", ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
            CriadoEmUtc = DateTimeOffset.UtcNow };
        var stop1 = new Parada { Id = Guid.NewGuid(), Codigo = "ETA-S1-" + suffix, Nome = "S1",
            Localizacao = new Point(-43.195, -22.9) { SRID = 4326 }, Modal = modal };
        var stop2 = new Parada { Id = Guid.NewGuid(), Codigo = "ETA-S2-" + suffix, Nome = "S2",
            Localizacao = new Point(-43.19, -22.9) { SRID = 4326 }, Modal = modal };
        var occurrence = new OcorrenciaParadaPadrao { Id = Guid.NewGuid(), PadraoVersao = version,
            Parada = stop1, Ordem = 1, PosicaoTracado = .5, DistanciaAcumuladaMetros = 500 };
        var other = new OcorrenciaParadaPadrao { Id = Guid.NewGuid(), PadraoVersao = version,
            Parada = stop2, Ordem = 2, PosicaoTracado = 1, DistanciaAcumuladaMetros = 1000 };
        db.AddRange(modal, line, direction, pattern, version, stop1, stop2, occurrence, other);
        await db.SaveChangesAsync();
        return new(line.Id, direction.Id, pattern.Id, version.Id, occurrence.Id, other.Id, stop1.Id);
    }

    private sealed record Ids(Guid Line, Guid Direction, Guid Pattern, Guid Version,
        Guid Occurrence, Guid OtherOccurrence, Guid Stop);
}
