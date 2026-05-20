using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoPonto.Application.Trem;

/// <summary>
/// Consulta o endpoint interno do app SuperVia para obter
/// o próximo trem entre dois pares de estações.
/// </summary>
public sealed class SuperviaApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SuperviaApiClient> _logger;
    private readonly string _endpoint;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SuperviaApiClient(HttpClient http, ILogger<SuperviaApiClient> logger, IConfiguration config)
    {
        _http     = http;
        _logger   = logger;
        _endpoint = config["SUPERVIA:API:POST_ENDPOINT"]
                    ?? throw new InvalidOperationException("SUPERVIA:API:POST_ENDPOINT não configurado.");
    }

    /// <summary>
    /// Retorna o próximo trem entre duas estações.
    /// Retorna null se não houver resposta válida.
    /// </summary>
    public async Task<ProximoTremDto?> BuscarProximoTremAsync(
        string idEstacaoPartida,
        string idEstacaoDestino,
        CancellationToken ct = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent([
                new("s_partida", idEstacaoPartida),
                new("s_destino", idEstacaoDestino),
            ]);

            var response = await _http.PostAsync(_endpoint, content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var lista = JsonSerializer.Deserialize<List<ProximoTremDto>>(body, JsonOpts);

            return lista?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Falha ao consultar próximo trem {p}→{d}: {msg}",
                idEstacaoPartida, idEstacaoDestino, ex.Message);
            return null;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class ProximoTremDto
{
    [JsonPropertyName("tripname")]
    public string? TripName { get; init; }

    [JsonPropertyName("stopcode")]
    public string? StopCode { get; init; }

    [JsonPropertyName("estimativa")]
    public string? Estimativa { get; init; }

    [JsonPropertyName("hora_referencia")]
    public string? HoraReferencia { get; init; }

    [JsonPropertyName("minutos_ref")]
    public string? MinutosRef { get; init; }

    [JsonPropertyName("ramal_nome")]
    public string? RamalNome { get; init; }

    [JsonPropertyName("linha_nome")]
    public string? LinhaNome { get; init; }

    [JsonPropertyName("sentido")]
    public string? Sentido { get; init; }

    [JsonPropertyName("tipotrem_ref")]
    public string? TipoTrem { get; init; }

    [JsonPropertyName("plataforma")]
    public string? Plataforma { get; init; }

    /// <summary>Minutos até o próximo trem (0 se já chegou)</summary>
    public int MinutosParaChegada =>
        int.TryParse(MinutosRef, out var m) ? Math.Max(0, m) : 0;

    /// <summary>Data/hora estimada de chegada no local da consulta</summary>
    public DateTimeOffset? EstimativaDateTimeOffset
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Estimativa)) return null;
            if (DateTimeOffset.TryParse(Estimativa, out var dt)) return dt;
            if (DateTime.TryParse(Estimativa, out var dtt))
                return new DateTimeOffset(dtt, TimeSpan.FromHours(-3));
            return null;
        }
    }
}