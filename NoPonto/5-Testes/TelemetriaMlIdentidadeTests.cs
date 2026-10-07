using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class TelemetriaMlIdentidadeTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static PosicaoVeiculoDto Posicao() => new()
    {
        Ordem = "BRT-TESTE", CodigoLinha = "42", ModalFonte = "BRT", ProvedorFonte = "BRT_RIO",
        Latitude = -22.9, Longitude = -43.2, Velocidade = 20, Bearing = 90,
        TimestampGps = T, RecebidoEmUtc = T.AddSeconds(1),
        PadraoVersaoId = Guid.NewGuid(), PadraoOperacionalId = Guid.NewGuid(),
        LinhaId = Guid.NewGuid(), SentidoId = Guid.NewGuid(), TopologiaPadrao = "LINEAR",
        ProximaOcorrenciaParadaPadraoId = Guid.NewGuid(), PosicaoNaRota = .25,
        ComprimentoRotaMetros = 10_000, DistanciaProximaParadaMetros = 300, VelocidadeMedia = 18,
    };

    private static ViagemObservadaResultado Resultado(PosicaoVeiculoDto p)
    {
        var observada = new ViagemObservadaState(Guid.NewGuid(), p.Ordem, p.PadraoVersaoId!.Value,
            T.AddMinutes(-1), T, .25, PadraoOperacionalId: p.PadraoOperacionalId!.Value, Volta: 2);
        return new(ViagemObservadaStatus.Updated, observada)
        {
            EstadoOperacional = new(observada, p.CodigoLinha, p.LinhaId!.Value, p.SentidoId!.Value),
            ProximaOcorrenciaOperacional = new(Guid.NewGuid(), p.PadraoVersaoId.Value,
                Guid.NewGuid(), 2, .3),
        };
    }

    [Theory]
    [InlineData(ViagemObservadaStatus.Created, EstadoViagem.Ativa)]
    [InlineData(ViagemObservadaStatus.Updated, EstadoViagem.Ativa)]
    [InlineData(ViagemObservadaStatus.Updated, EstadoViagem.PossivelFim)]
    public void Compativel_PreservaIdentidadeEContratoDoConsumidor(
        ViagemObservadaStatus status, EstadoViagem fase)
    {
        var p = Posicao();
        var r = Resultado(p);
        r = r with { Status = status, EstadoOperacional = r.EstadoOperacional! with { Estado = fase } };
        var e = EventoTelemetriaMlFactory.Criar(p, r, T.AddSeconds(2));
        Assert.Equal(r.Estado!.ViagemId, e.ViagemId);
        Assert.Equal(2, e.Volta);
        Assert.Equal(r.ProximaOcorrenciaOperacional!.Id, e.OcorrenciaParadaPadraoId);
        Assert.Equal(r.ProximaOcorrenciaOperacional.Id, e.ProximaOcorrenciaParadaPadraoId);
        AssertGpsPreservado(p, e);
        Assert.Equal(e, JsonSerializer.Deserialize<EventoTelemetriaMl>(JsonSerializer.Serialize(e)));
    }

    [Theory]
    [InlineData("codigo")]
    [InlineData("linha")]
    [InlineData("versao")]
    [InlineData("sentido")]
    [InlineData("padrao")]
    [InlineData("topologia")]
    [InlineData("veiculo")]
    [InlineData("timestamp")]
    [InlineData("finalizada")]
    [InlineData("metadados_ausentes")]
    [InlineData("estado_divergente")]
    [InlineData("ocorrencia_outra_versao")]
    [InlineData("viagem_vazia")]
    [InlineData("matching_incompleto")]
    public void Incompativel_PreservaMatchingSemAssociarViagemAntiga(string caso)
    {
        var p = Posicao();
        var r = Resultado(p);
        var op = r.EstadoOperacional!;
        switch (caso)
        {
            case "codigo": p = p with { CodigoLinha = "43" }; break;
            case "linha": p = p with { LinhaId = Guid.NewGuid() }; break;
            case "versao": p = p with { PadraoVersaoId = Guid.NewGuid() }; break;
            case "sentido": p = p with { SentidoId = Guid.NewGuid() }; break;
            case "padrao": p = p with { PadraoOperacionalId = Guid.NewGuid() }; break;
            case "topologia": p = p with { TopologiaPadrao = "CIRCULAR" }; break;
            case "veiculo": p = p with { Ordem = "BRT-OUTRO" }; break;
            case "timestamp": p = p with { TimestampGps = T.AddSeconds(1) }; break;
            case "finalizada": r = r with { EstadoOperacional = op with { Estado = EstadoViagem.Finalizada } }; break;
            case "metadados_ausentes": r = r with { EstadoOperacional = null }; break;
            case "estado_divergente": r = r with { Estado = r.Estado! with { ViagemId = Guid.NewGuid() } }; break;
            case "ocorrencia_outra_versao": r = r with { ProximaOcorrenciaOperacional = r.ProximaOcorrenciaOperacional! with { PadraoVersaoId = Guid.NewGuid() } }; break;
            case "viagem_vazia":
                var vazia = r.Estado! with { ViagemId = Guid.Empty };
                r = r with { Estado = vazia, EstadoOperacional = op with { Observada = vazia } }; break;
            case "matching_incompleto": p = p with { SentidoId = null }; break;
        }
        AssertSemAssociacao(p, r);
    }

    [Theory]
    [InlineData(ViagemObservadaStatus.ItineraryChanged)]
    [InlineData(ViagemObservadaStatus.InfrastructureFailure)]
    [InlineData(ViagemObservadaStatus.Conflict)]
    [InlineData(ViagemObservadaStatus.RejectedOlderOrEqual)]
    [InlineData(ViagemObservadaStatus.InvalidState)]
    [InlineData(ViagemObservadaStatus.InvalidSequence)]
    [InlineData(ViagemObservadaStatus.OccurrenceNotFromItinerary)]
    public void ResultadoNaoConfirmado_NaoAutorizaIdentidadeMesmoComCamposIguais(ViagemObservadaStatus status)
    {
        var p = Posicao();
        AssertSemAssociacao(p, Resultado(p) with { Status = status });
    }

    [Fact]
    public void ResultadoAusente_PreservaGpsEFallbackObservacional() => AssertSemAssociacao(Posicao(), null);

    [Fact]
    public void ProximaOperacionalNula_CompativelMantemViagemEFallbackObservacional()
    {
        var p = Posicao();
        var r = Resultado(p) with { ProximaOcorrenciaOperacional = null };
        var e = EventoTelemetriaMlFactory.Criar(p, r, T.AddSeconds(2));
        Assert.Equal(r.Estado!.ViagemId, e.ViagemId);
        Assert.Equal(2, e.Volta);
        Assert.Equal(p.ProximaOcorrenciaParadaPadraoId, e.OcorrenciaParadaPadraoId);
        Assert.Null(e.ProximaOcorrenciaParadaPadraoId);
        AssertGpsPreservado(p, e);
    }

    [Fact]
    public void SemOcorrenciaObservacionalOuOperacional_PreservaObservacao()
    {
        var p = Posicao() with { ProximaOcorrenciaParadaPadraoId = null };
        var e = EventoTelemetriaMlFactory.Criar(p, null, T.AddSeconds(2));
        Assert.Null(e.OcorrenciaParadaPadraoId);
        AssertGpsPreservado(p, e);
    }

    private static void AssertSemAssociacao(PosicaoVeiculoDto p, ViagemObservadaResultado? r)
    {
        var e = EventoTelemetriaMlFactory.Criar(p, r, T.AddSeconds(2));
        Assert.Null(e.ViagemId);
        Assert.Null(e.Volta);
        Assert.Null(e.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(p.ProximaOcorrenciaParadaPadraoId, e.OcorrenciaParadaPadraoId);
        AssertGpsPreservado(p, e);
        Assert.Equal(e, JsonSerializer.Deserialize<EventoTelemetriaMl>(JsonSerializer.Serialize(e)));
    }

    private static void AssertGpsPreservado(PosicaoVeiculoDto p, EventoTelemetriaMl e)
    {
        Assert.Equal(p.Ordem, e.OrdemVeiculo);
        Assert.Equal(p.CodigoLinha, e.CodigoLinha);
        Assert.Equal(p.LinhaId, e.LinhaId);
        Assert.Equal(p.SentidoId, e.SentidoId);
        Assert.Equal(p.PadraoVersaoId, e.PadraoVersaoId);
        Assert.Equal(p.TimestampGps, e.TimestampGps);
        Assert.Equal(p.Latitude, e.LatitudeRecebida);
        Assert.Equal(p.Longitude, e.LongitudeRecebida);
        Assert.Equal(p.Velocidade, e.VelocidadeInstantanea);
        Assert.Equal(p.Bearing, e.Bearing);
        Assert.Equal(p.PosicaoNaRota, e.PosicaoNaRota);
        Assert.Equal(p.ComprimentoRotaMetros, e.ComprimentoRotaMetros);
        Assert.Equal(p.DistanciaProximaParadaMetros, e.DistanciaProximaParadaMetros);
        Assert.Equal(p.VelocidadeMedia, e.VelocidadeMediaCausal);
        TelemetriaMlValidator.Validar(e);
    }
}
