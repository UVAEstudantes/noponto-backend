using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class MotivoFimEventoTests
{
    internal static EventoViagem End(string? reason = null)
    {
        var trip = Guid.Parse("b23dfb7a-5267-437e-866a-000000000001");
        return new($"fim:{trip:D}", "ViagemFinalizada", trip, "BUS-CONTRACT", "CONTRACT",
            Guid.Parse("b23dfb7a-5267-437e-866a-000000000002"),
            Guid.Parse("b23dfb7a-5267-437e-866a-000000000003"),
            new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            PadraoOperacionalId: Guid.Parse("b23dfb7a-5267-437e-866a-000000000004"), Volta: 0,
            LinhaId: Guid.Parse("b23dfb7a-5267-437e-866a-000000000005"), MotivoFim: reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PerdaContinuidadeCircular")]
    [InlineData("MudancaOperacional")]
    public void EndReason_JsonAndStreamRoundTrip_PreservesIdentityAndOptionalField(string? reason)
    {
        var end = End(reason);
        EventoViagemValidator.Validar(end);
        var json = JsonSerializer.Serialize(end);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(reason is not null, document.RootElement.TryGetProperty("motivo_fim", out var field));
        if (reason is not null) Assert.Equal(reason, field.GetString());
        var fromJson = JsonSerializer.Deserialize<EventoViagem>(json)!;
        Assert.Equal(end, fromJson);
        var fields = ViagemOperacionalRepository.Fields(fromJson);
        Assert.Equal(reason is not null, fields.ContainsKey("motivo_fim"));
        var fromStream = EventoViagemValidator.Parse(fields);
        Assert.Equal(end, fromStream);
        Assert.Equal(json, JsonSerializer.Serialize(fromStream));
        Assert.Equal("fim:b23dfb7a-5267-437e-866a-000000000001", fromStream.EventId);
        Assert.Equal(2, fromStream.SchemaVersion);
        Assert.Null(fromStream.ParadaId);
        Assert.Null(fromStream.TimestampPassagem);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("case")]
    [InlineData("whitespace")]
    public void EndReason_UnknownValues_RemainRejectedByValidatorAndParser(string caseId)
    {
        var reason = caseId switch { "empty" => "", "case" => "mudancaoperacional",
            "whitespace" => "MudancaOperacional ", _ => "OutroMotivo" };
        var end = End(reason);
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(end));
        Assert.Throws<FormatException>(() => EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(end)));
    }

    [Theory]
    [InlineData("ViagemIniciada", "MudancaOperacional")]
    [InlineData("PassagemParada", "MudancaOperacional")]
    [InlineData("ViagemIniciada", "PerdaContinuidadeCircular")]
    [InlineData("PassagemParada", "PerdaContinuidadeCircular")]
    public void EndReason_OnOtherEventTypes_RemainsRejected(string type, string reason)
    {
        var end = End(reason);
        var occurrence = Guid.Parse("b23dfb7a-5267-437e-866a-000000000006");
        var e = type == "ViagemIniciada"
            ? end with { Tipo = type, EventId = $"inicio:{end.ViagemId:D}" }
            : end with { Tipo = type, EventId = $"passagem:{end.ViagemId:D}:{occurrence:D}:0",
                OcorrenciaParadaPadraoId = occurrence, ParadaId = occurrence, Ordem = 1, PosicaoLinha = .2,
                TimestampPassagem = end.TimestampEvento, TimestampGps = end.TimestampEvento,
                VelocidadeInstantanea = 20 };
        EventoViagemValidator.Validar(e with { MotivoFim = null });
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(e));
        Assert.Throws<FormatException>(() => EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(e)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PerdaContinuidadeCircular")]
    [InlineData("MudancaOperacional")]
    public void EndReason_DoesNotRelaxIdentityOrSchemaValidation(string? reason)
    {
        var e = End(reason);
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(e with { EventId = "fim:outra" }));
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(e with { SchemaVersion = 3 }));
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(e with { LinhaId = null }));
    }

    [Fact]
    public void EndReason_LegacyJsonWithoutReason_DoesNotInferOrRewriteReason()
    {
        const string legacy = """
            {"event_id":"fim:b23dfb7a-5267-437e-866a-000000000001","tipo":"ViagemFinalizada","viagem_id":"b23dfb7a-5267-437e-866a-000000000001","ordem_veiculo":"BUS-CONTRACT","codigo_linha":"CONTRACT","sentido_id":"b23dfb7a-5267-437e-866a-000000000002","padrao_versao_id":"b23dfb7a-5267-437e-866a-000000000003","timestamp_evento":"2026-10-20T12:00:00+00:00","ocorrencia_parada_padrao_id":null,"parada_id":null,"ordem":null,"posicao_linha":null,"timestamp_passagem":null,"timestamp_gps":null,"velocidade_instantanea":null,"velocidade_media":null,"schema_version":2,"padrao_operacional_id":"b23dfb7a-5267-437e-866a-000000000004","volta":0,"linha_id":"b23dfb7a-5267-437e-866a-000000000005"}
            """;
        var end = JsonSerializer.Deserialize<EventoViagem>(legacy)!;
        Assert.Equal(End(), end);
        Assert.Null(end.MotivoFim);
        EventoViagemValidator.Validar(end);
        Assert.Equal(legacy, JsonSerializer.Serialize(end));
        Assert.Equal(legacy, JsonSerializer.Serialize(EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(end))));
        var explicitNull = JsonSerializer.Deserialize<EventoViagem>(legacy[..^1] + ",\"motivo_fim\":null}")!;
        Assert.Equal(end, explicitNull);
        Assert.Equal(legacy, JsonSerializer.Serialize(explicitNull));
    }
}
