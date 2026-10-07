using System.Text.Json;
using System.Globalization;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemOperacionalCodecCompatibilidadeTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid V = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid O = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid L = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid S = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid P = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid I = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static ViagemOperacionalState Estado(EstadoViagem fase, bool candidato = false) => new(
        new(I, "CODEC", V, T, T.AddSeconds(60), .5, O, 2, P, O, 2, 0, 500, "LINEAR"),
        "42", L, S, fase, fase == EstadoViagem.Finalizada ? 2 : 0,
        fase == EstadoViagem.Finalizada ? T.AddSeconds(30) : null,
        candidato ? new(Guid.Parse("77777777-7777-7777-7777-777777777777"), S, L,
            T.AddSeconds(40), .2, -22.9, -43.2) : null);

    private static Dictionary<string, string> Campos(ViagemOperacionalState s) =>
        ViagemOperacionalCodec.Names.Zip(ViagemOperacionalCodec.Encode(s)).ToDictionary(x => x.First, x => x.Second);

    private static ViagemOperacionalState RecuperarJson(ViagemOperacionalState s)
    {
        var array = JsonSerializer.Deserialize<string[]>(JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(s)))!;
        Assert.Equal(27, array.Length);
        return ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(array)
            .ToDictionary(x => x.First, x => x.Second), "CODEC");
    }

    [Theory]
    [InlineData(EstadoViagem.Ativa, false)]
    [InlineData(EstadoViagem.PossivelFim, false)]
    [InlineData(EstadoViagem.Finalizada, false)]
    [InlineData(EstadoViagem.Finalizada, true)]
    [InlineData(EstadoViagem.Ativa, true)]
    [InlineData(EstadoViagem.PossivelFim, true)]
    public void RoundTrip_Contrato27Posicoes_PreservaEstado(EstadoViagem fase, bool candidato)
    {
        var s = Estado(fase, candidato);
        Assert.Equal(s, RecuperarJson(s));
        var campos = Campos(s);
        campos[ViagemOperacionalRedisScript.DurableVersion] = "3";
        campos[ViagemOperacionalRedisScript.DurableCheckpoint] = ViagemOperacionalCodec.Tick(T);
        Assert.Equal(s, ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Fact]
    public void SnapshotAntigoLiteral_PreservaOrdemERepresentacao()
    {
        // Snapshot independente do encoder: protege posições, GUID N, ticks e vazios do contrato antigo.
        string Tick(DateTimeOffset t) => t.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
        string[] snapshot = [I.ToString("N"), "CODEC", V.ToString("N"), Tick(T),
            Tick(T.AddSeconds(60)), "0.5", O.ToString("N"), "2", "42",
            L.ToString("N"), S.ToString("N"), "Finalizada", "2", Tick(T.AddSeconds(30)),
            "77777777777777777777777777777777", S.ToString("N"), Tick(T.AddSeconds(40)),
            "0.2", L.ToString("N"), "", "", P.ToString("N"), O.ToString("N"), "2", "0", "500", "LINEAR"];
        var decoded = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(snapshot)
            .ToDictionary(x => x.First, x => x.Second), "CODEC");
        Assert.Equal(Estado(EstadoViagem.Finalizada, true) with {
            Candidato = Estado(EstadoViagem.Finalizada, true).Candidato! with {
                LatitudeInicial = null, LongitudeInicial = null } }, decoded);
        Assert.Equal(snapshot, ViagemOperacionalCodec.Encode(decoded));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-22.9, null)]
    [InlineData(null, -43.2)]
    public void Finalizada_CoordenadasOpcionaisAntigasContinuamLegiveis(double? lat, double? lon)
    {
        var s = Estado(EstadoViagem.Finalizada, true);
        s = s with { Candidato = s.Candidato! with { LatitudeInicial = lat, LongitudeInicial = lon,
            Timestamp = T.AddSeconds(-1) } };
        Assert.Equal(s, RecuperarJson(s));
    }

    [Theory]
    [InlineData("CandidatoPadraoVersaoId", "")]
    [InlineData("CandidatoPadraoVersaoId", "00000000000000000000000000000000")]
    [InlineData("CandidatoSentidoId", "")]
    [InlineData("CandidatoLinhaId", "")]
    [InlineData("CandidatoTimestamp", "")]
    [InlineData("CandidatoPosicao", "NaN")]
    [InlineData("CandidatoPosicao", "1.1")]
    [InlineData("CandidatoLatitudeInicial", "91")]
    [InlineData("CandidatoLongitudeInicial", "-181")]
    [InlineData("CandidatoLatitudeInicial", "")]
    [InlineData("CandidatoLongitudeInicial", "")]
    public void FasesAdicionais_RejeitamCandidatoIncompletoOuInvalido(string campo, string valor)
    {
        foreach (var fase in new[] { EstadoViagem.Ativa, EstadoViagem.PossivelFim })
        {
            var campos = Campos(Estado(fase, true));
            campos[campo] = valor;
            Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
        }
    }

    [Theory]
    [InlineData(EstadoViagem.Ativa)]
    [InlineData(EstadoViagem.PossivelFim)]
    [InlineData(EstadoViagem.Finalizada)]
    public void CandidatoFuturo_EhRejeitado(EstadoViagem fase)
    {
        var campos = Campos(Estado(fase, true));
        campos["CandidatoTimestamp"] = ViagemOperacionalCodec.Tick(T.AddSeconds(61));
        Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Theory]
    [InlineData(EstadoViagem.Ativa)]
    [InlineData(EstadoViagem.PossivelFim)]
    public void FasesAdicionais_RejeitamCandidatoAnteriorAExecucao(EstadoViagem fase)
    {
        var campos = Campos(Estado(fase, true));
        campos["CandidatoTimestamp"] = ViagemOperacionalCodec.Tick(T.AddSeconds(-1));
        Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Theory]
    [InlineData("OcorrenciaCursorId", "")]
    [InlineData("OrdemCursor", "")]
    [InlineData("UltimaOcorrenciaParadaPadraoId", "")]
    [InlineData("UltimaParadaOrdem", "0")]
    [InlineData("ViagemId", "00000000000000000000000000000000")]
    [InlineData("LinhaId", "")]
    [InlineData("SentidoId", "")]
    [InlineData("PadraoOperacionalId", "")]
    [InlineData("PadraoVersaoId", "")]
    [InlineData("Topologia", "INVALIDA")]
    [InlineData("TimestampUltimaAtualizacao", "0")]
    [InlineData("ConfirmacoesPosTerminal", "3")]
    [InlineData("TimestampFim", "0")]
    public void CandidatoNaoRelaxouValidacoesDoEstado(string campo, string valor)
    {
        foreach (var fase in new[] { EstadoViagem.Ativa, EstadoViagem.PossivelFim, EstadoViagem.Finalizada })
        {
            var campos = Campos(Estado(fase, true));
            campos[campo] = valor;
            Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
        }
    }

    [Fact]
    public void FaseDesconhecidaComCandidato_EhRejeitada()
    {
        var campos = Campos(Estado(EstadoViagem.Ativa, true));
        campos["EstadoViagem"] = "3";
        Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Theory]
    [InlineData(26)]
    [InlineData(28)]
    public void QuantidadeDeCamposDiferenteDe27_EhRejeitada(int quantidade)
    {
        var campos = Campos(Estado(EstadoViagem.Ativa));
        if (quantidade == 26) campos.Remove("Topologia");
        else campos["CampoExtra"] = "";
        Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Fact]
    public void CoordenadaOrfaSemCandidato_EhRejeitada()
    {
        var campos = Campos(Estado(EstadoViagem.Ativa));
        campos["CandidatoLatitudeInicial"] = "-22.9";
        Assert.Throws<FormatException>(() => ViagemOperacionalCodec.Decode(campos, "CODEC"));
    }

    [Theory]
    [InlineData(EstadoViagem.Ativa)]
    [InlineData(EstadoViagem.PossivelFim)]
    public void RegrasAtuais_DivergenciaRepetidaNaoProduzCandidatoNasNovasFases(EstadoViagem fase)
    {
        var s = Estado(fase);
        var estrutura = new EstruturaViagem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "43", true, Guid.NewGuid());
        for (var i = 1; i <= 3; i++)
        {
            var gps = new PosicaoVeiculoDto { Ordem = "CODEC", CodigoLinha = "43",
                PadraoVersaoId = estrutura.PadraoVersaoId, TimestampGps = T.AddSeconds(60 + i * 20),
                PosicaoNaRota = .1 + i * .01, ComprimentoRotaMetros = 10_000,
                Latitude = -22.9, Longitude = -43.2 + i * .001 };
            var d = ViagemOperacionalRegra.Decidir(s, estrutura, gps,
                new(ViagemObservadaStatus.Updated, Guid.Empty, 0, []), Guid.NewGuid());
            Assert.Null(d.Estado.Candidato);
            Assert.Equal(I, d.Estado.Observada.ViagemId);
            Assert.Equal(fase, d.Estado.Estado);
            Assert.Empty(d.Eventos);
            s = RecuperarJson(d.Estado);
            Assert.All(ViagemOperacionalCodec.Encode(s).Skip(14).Take(7), value => Assert.Equal("", value));
        }
    }
}
