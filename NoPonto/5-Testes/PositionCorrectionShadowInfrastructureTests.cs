using System.Text;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class PositionCorrectionShadowInfrastructureTests
{
    public static ShadowPosicaoOrigem Origin(char id = 'a', char fingerprint = 'b') => new(
        ShadowPosicaoContrato.ContractVersion, new string(id, 64), new string('c', 64),
        CorrecaoTemporalPosicaoOptions.PoliticaReferencia, new string(fingerprint, 64), 1,
        new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        new ContextoCausalPosicao("BRT-1", "BRT", "GPS", "10", Guid.NewGuid(), null, null),
        0.5, 1000, 20, null, [], [], EstadoMovimentoPosicao.Movimento, 0, 0, 32, false, []);

    [Fact]
    public void Defaults_ranges_resources_and_fingerprint()
    {
        var options = new PositionCorrectionShadowPipelineOptions();
        Assert.True(options.Valid());
        Assert.Equal(10_000, options.ChannelCapacity);
        Assert.Equal(65_536, options.MaxPayloadBytes);
        Assert.Equal(5_000, options.MaxStreamEntries);
        Assert.Equal(1_000, options.MaxDeadLetterEntries);
        Assert.Equal("noponto:position-correction:shadow", PositionCorrectionShadowResources.Stream);
        Assert.Equal("position-correction-shadow-postgres", PositionCorrectionShadowResources.ConsumerGroup);
        Assert.Equal("noponto:position-correction:shadow:dead-letter", PositionCorrectionShadowResources.DeadLetter);
        Assert.False(new PositionCorrectionShadowPipelineOptions { ChannelCapacity = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { PublisherBatchSize = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { WorkerBatchSize = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { ClaimIdleSeconds = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { MaxAttempts = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { MaxPayloadBytes = 1_048_577 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { TrimLimit = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { MaxStreamEntries = 0 }.Valid());
        Assert.False(new PositionCorrectionShadowPipelineOptions { MaxDeadLetterEntries = 0 }.Valid());
        var expected = ShadowPosicaoContrato.PolicyFingerprint(new());
        options.ChannelCapacity++;
        Assert.Equal(expected, ShadowPosicaoContrato.PolicyFingerprint(new()));
    }

    [Fact]
    public void Codec_roundtrip_utc_enums_and_size_limits()
    {
        var origin = Origin() with { TimestampGpsOrigemUtc = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.FromHours(-3)) };
        var bytes = PositionCorrectionShadowCodec.Serialize(origin);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"estadoMovimento\":\"Movimento\"", json);
        Assert.Contains("+00:00", json);
        Assert.Equal(TimeSpan.Zero, PositionCorrectionShadowCodec.Deserialize(bytes).TimestampGpsOrigemUtc.Offset);
        Assert.Equal(bytes, PositionCorrectionShadowCodec.Serialize(PositionCorrectionShadowCodec.Deserialize(bytes), bytes.Length));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Serialize(origin, bytes.Length - 1));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Deserialize(bytes, bytes.Length - 1));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Deserialize("{"u8.ToArray()));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Serialize(origin with { ContractVersion = "bad" }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Serialize(origin with { PosicaoB = double.NaN }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Serialize(origin with { PosicaoB = double.PositiveInfinity }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"Movimento\"", "0"))));
    }

    [Fact]
    public void Codec_preserva_sinal_atual_sem_amostras_historicas()
    {
        var origin = Origin() with { SinaisParada = [true] };

        PositionCorrectionShadowValidator.Validate(origin);
        var bytes = PositionCorrectionShadowCodec.SerializeEnvelope(origin, origin.TimestampGpsOrigemUtc);
        var restored = PositionCorrectionShadowCodec.DeserializeEnvelope(bytes).Origin;

        Assert.Equal(0, restored.SamplesUsed);
        Assert.Empty(restored.AmostrasCausais);
        Assert.Equal(new[] { true }, restored.SinaisParada);
    }

    [Fact]
    public void Validator_limita_sinais_pelo_maximo_configurado_sem_exigir_relacao_um_para_um()
    {
        var origin = Origin() with { MaxSamplesConfigured = 2, SinaisParada = [true, false] };
        PositionCorrectionShadowValidator.Validate(origin);
        Assert.Equal(origin.SinaisParada,
            PositionCorrectionShadowCodec.Deserialize(PositionCorrectionShadowCodec.Serialize(origin)).SinaisParada);

        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(
            origin with { SinaisParada = [true, false, true] }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(
            origin with { SamplesUsed = 1 }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(
            origin with { MaxSamplesConfigured = 0 }));
    }

    [Fact]
    public void Validator_noop_order_and_mapping()
    {
        var origin = Origin();
        PositionCorrectionShadowValidator.Validate(origin);
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(origin with { PosicaoB = 1.1 }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(origin with { ComprimentoRotaMetros = -1 }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(origin with { VelocidadeInstantaneaKmh = -1 }));
        Assert.Throws<FormatException>(() => PositionCorrectionShadowValidator.Validate(origin with { SamplesUsed = 33 }));
        Assert.False(new NoOpPositionCorrectionShadowIngress().TryOffer(origin));
        var receipts = new[] { new PositionCorrectionShadowReceipt(Origin('b'), DateTimeOffset.UtcNow),
            new PositionCorrectionShadowReceipt(Origin('a'), DateTimeOffset.UtcNow) };
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) },
            PositionCorrectionShadowRepository.OrderCanonically(receipts).Select(x => x.Origin.ShadowOriginId));
        using var db = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=55436;Database=unused;Username=test;Password=test",
                x => x.UseNetTopologySuite()).Options);
        var entity = db.Model.FindEntityType(typeof(NoPonto.Domain.Entities.PositionCorrectionShadowOrigin))!;
        Assert.Equal("PositionCorrectionShadowOrigins", entity.GetTableName());
        Assert.Equal("jsonb", entity.FindProperty("CandidateResults")!.GetColumnType());
        Assert.Equal(3, entity.GetIndexes().Count());
    }
}
