using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public enum EstadoCausalCodecStatus
{
    Success,
    VersionUnsupported,
    InvalidState,
}

public readonly record struct EstadoCausalCodecResultado(
    EstadoCausalCodecStatus Status,
    EstadoCausalPosicao? Estado,
    string? Json,
    int Bytes);

public sealed class EstadoCausalPosicaoCodec(
    IOptionsMonitor<CorrecaoTemporalPosicaoOptions>? options = null)
{
    public const int VersaoAtual = 1;
    public const int LimiteDefensivoColecoes = 256;
    public const int MaxPayloadBytesPadrao = 65_536;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public EstadoCausalCodecResultado Serializar(EstadoCausalPosicao estado)
    {
        if (!Valido(estado))
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);

        try
        {
            var json = JsonSerializer.Serialize(estado, JsonOptions);
            var bytes = Encoding.UTF8.GetByteCount(json);
            return bytes <= MaxPayloadBytes
                ? new(EstadoCausalCodecStatus.Success, estado, json, bytes)
                : new(EstadoCausalCodecStatus.InvalidState, null, null, bytes);
        }
        catch (JsonException)
        {
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);
        }
        catch (NotSupportedException)
        {
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);
        }
    }

    public EstadoCausalCodecResultado Desserializar(int versao, string? json)
    {
        if (versao != VersaoAtual)
            return new(EstadoCausalCodecStatus.VersionUnsupported, null, null, 0);
        if (string.IsNullOrWhiteSpace(json))
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);

        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxPayloadBytes)
            return new(EstadoCausalCodecStatus.InvalidState, null, null, bytes);

        try
        {
            var estado = JsonSerializer.Deserialize<EstadoCausalPosicao>(json, JsonOptions);
            return estado is not null && Valido(estado)
                ? new(EstadoCausalCodecStatus.Success, estado, json, bytes)
                : new(EstadoCausalCodecStatus.InvalidState, null, null, 0);
        }
        catch (JsonException)
        {
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);
        }
        catch (NotSupportedException)
        {
            return new(EstadoCausalCodecStatus.InvalidState, null, null, 0);
        }
    }

    private static bool Valido(EstadoCausalPosicao estado) =>
        estado.Versao == VersaoAtual
        && estado.Contexto is not null
        && !string.IsNullOrWhiteSpace(estado.Contexto.Ordem)
        && !string.IsNullOrWhiteSpace(estado.Contexto.Modal)
        && !string.IsNullOrWhiteSpace(estado.Contexto.Provedor)
        && !string.IsNullOrWhiteSpace(estado.Contexto.CodigoLinha)
        && estado.Contexto.PadraoVersaoId != Guid.Empty
        && estado.UltimoTimestampGps > DateTimeOffset.UnixEpoch
        && Finito(estado.UltimaPosicao)
        && estado.UltimaPosicao is >= 0 and <= 1
        && Finito(estado.ComprimentoRotaMetros)
        && estado.ComprimentoRotaMetros > 0
        && estado.Amostras is not null
        && estado.SinaisParada is not null
        && estado.Amostras.Count <= LimiteDefensivoColecoes
        && estado.SinaisParada.Count <= LimiteDefensivoColecoes
        && Enum.IsDefined(estado.EstadoMovimento)
        && estado.Amostras.All(x => x.TimestampGps > DateTimeOffset.UnixEpoch
            && x.TimestampGps <= estado.UltimoTimestampGps
            && Finito(x.VelocidadeKmh) && x.VelocidadeKmh >= 0)
        && TimestampsOrdenados(estado.Amostras);

    private int MaxPayloadBytes =>
        options?.CurrentValue.StateMaxPayloadBytes ?? MaxPayloadBytesPadrao;

    private static bool TimestampsOrdenados(IReadOnlyList<AmostraCausalPosicao> amostras)
    {
        for (var i = 1; i < amostras.Count; i++)
            if (amostras[i].TimestampGps <= amostras[i - 1].TimestampGps)
                return false;
        return true;
    }

    private static bool Finito(double valor) => !double.IsNaN(valor) && !double.IsInfinity(valor);
}
