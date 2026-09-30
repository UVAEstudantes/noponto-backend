using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class TelemetriaMlSamplingTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 30, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Disabled_SempreColeta() =>
        Assert.True(Policy(new() { Enabled = false, LinePercentage = 0 }).ShouldCollect(Position(), Instant));

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Percentual_RespeitaExtremosEClamp(int percentage, bool expected) =>
        Assert.Equal(expected, Policy(new() { Enabled = true, LinePercentage = percentage })
            .ShouldCollect(Position(), Instant));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(2000, 1440)]
    public void BlockMinutes_EhLimitado(int configured, int effective)
    {
        var metrics = new TelemetriaMlMetrics();
        Policy(new() { Enabled = true, LinePercentage = 50, BlockMinutes = configured }, metrics)
            .ShouldCollect(Position(), Instant);
        Assert.Equal(effective, metrics.CaptureSamplingState().BlockMinutes);
    }

    [Fact]
    public void SeedVazia_UsaDefaultEstavel()
    {
        var empty = Policy(new() { Enabled = true, LinePercentage = 37, Seed = "  " });
        var standard = Policy(new() { Enabled = true, LinePercentage = 37,
            Seed = TelemetriaMlSamplingOptions.SeedPadrao });
        Assert.Equal(standard.ShouldCollect(Position(), Instant), empty.ShouldCollect(Position(), Instant));
    }

    [Fact]
    public void Identidade_IgnoraVeiculoSentidoPadraoECase()
    {
        var a = Position() with { Ordem = "A", ModalFonte = " onibus ", CodigoLinha = " 006 ",
            SentidoId = Guid.NewGuid(), PadraoVersaoId = Guid.NewGuid() };
        var b = Position() with { Ordem = "B", ModalFonte = "ONIBUS", CodigoLinha = "006",
            SentidoId = Guid.NewGuid(), PadraoVersaoId = Guid.NewGuid() };
        Assert.True(TelemetriaMlSamplingPolicy.TryNormalizeIdentity(a, out var am, out var ac));
        Assert.True(TelemetriaMlSamplingPolicy.TryNormalizeIdentity(b, out var bm, out var bc));
        Assert.Equal((am, ac), (bm, bc));
        Assert.Equal(
            TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", 10, am, ac),
            TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", 10, bm, bc));
    }

    [Fact]
    public void ModalDiferente_ProduzIdentidadeEHashDiferentes()
    {
        var bus = TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", 10, "ONIBUS", "006");
        var brt = TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", 10, "BRT", "006");
        Assert.NotEqual(bus, brt);
    }

    [Fact]
    public void BlocoSeguinte_ProduzCanonicalEHashDiferentes()
    {
        var block = TelemetriaMlSamplingPolicy.CalculateBlockId(Instant, 60);
        Assert.NotEqual(
            TelemetriaMlSamplingPolicy.BuildCanonical("SEED", block, "ONIBUS", "006"),
            TelemetriaMlSamplingPolicy.BuildCanonical("SEED", block + 1, "ONIBUS", "006"));
        Assert.NotEqual(
            TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", block, "ONIBUS", "006"),
            TelemetriaMlSamplingPolicy.CalculateHashValue("SEED", block + 1, "ONIBUS", "006"));
    }

    [Fact]
    public void InstanciasDistintas_ReproduzemDecisao()
    {
        var options = new TelemetriaMlSamplingOptions { Enabled = true, LinePercentage = 37,
            BlockMinutes = 60, Seed = "SEED" };
        Assert.Equal(Policy(options).ShouldCollect(Position(), Instant),
            Policy(options).ShouldCollect(Position(), Instant));
    }

    [Theory]
    [InlineData("", "006")]
    [InlineData("ONIBUS", "")]
    [InlineData(" ", "006")]
    [InlineData("ONIBUS", " ")]
    public void IdentidadeInvalida_FailOpen(string modal, string codigo)
    {
        var metrics = new TelemetriaMlMetrics();
        Assert.True(Policy(new() { Enabled = true, LinePercentage = 0 }, metrics)
            .ShouldCollect(Position() with { ModalFonte = modal, CodigoLinha = codigo }, Instant));
        Assert.Equal(1, metrics.SamplingFailOpen);
    }

    [Fact]
    public void VetorConhecido_PreservaCanonicalEEndianess()
    {
        const long block = 497417;
        Assert.Equal("v1|NOPONTO_ML_V1|497417|ONIBUS|006",
            TelemetriaMlSamplingPolicy.BuildCanonical("NOPONTO_ML_V1", block, "ONIBUS", "006"));
        Assert.Equal(0xCD835F9F1DA54D44UL,
            TelemetriaMlSamplingPolicy.CalculateHashValue("NOPONTO_ML_V1", block, "ONIBUS", "006"));
    }

    [Fact]
    public void Metricas_SaoThreadSafeECandidateEquivaleSelecionadoMaisIgnorado()
    {
        var metrics = new TelemetriaMlMetrics();
        Parallel.For(0, 10_000, i =>
        {
            metrics.RegistrarSamplingCandidate();
            if ((i & 1) == 0) metrics.RegistrarSamplingSelected();
            else metrics.RegistrarSamplingSkipped();
        });
        Assert.Equal(10_000, metrics.SamplingCandidates);
        Assert.Equal(metrics.SamplingCandidates, metrics.SamplingSelected + metrics.SamplingSkipped);
        Assert.Equal(50, metrics.SamplingEffectivePercentage);
    }

    private static TelemetriaMlSamplingPolicy Policy(
        TelemetriaMlSamplingOptions options, TelemetriaMlMetrics? metrics = null) =>
        new(new FixedOptionsMonitor<TelemetriaMlSamplingOptions>(options), metrics ?? new(),
            NullLogger<TelemetriaMlSamplingPolicy>.Instance);

    private static PosicaoVeiculoDto Position() => new()
    {
        Ordem = "A1", ModalFonte = "ONIBUS", CodigoLinha = "006",
        TimestampGps = Instant, RecebidoEmUtc = Instant,
    };

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
