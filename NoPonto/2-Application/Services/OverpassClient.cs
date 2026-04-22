using System.Net;
using System.Text.Json;
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Services;

public sealed class OverpassClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OverpassClient> _logger;

    private static readonly SemaphoreSlim _semaforo = new(1, 1);

    // Prioridade: 1 = alta relevância, 2 = média, 3 = baixa
    private static readonly Dictionary<string, (string Categoria, int Prioridade)> TagsParaCategoria = new()
    {
        ["amenity=hospital"]    = ("Hospital",            1),
        ["amenity=bus_station"] = ("Terminal de Ônibus",  1),
        ["railway=station"]     = ("Estação de Trem/Metrô", 1),
        ["shop=mall"]           = ("Shopping",            1),
        ["amenity=university"]  = ("Universidade",        2),
        ["amenity=college"]     = ("Faculdade",           2),
        ["amenity=clinic"]      = ("Clínica",             2),
        ["shop=supermarket"]    = ("Supermercado",        2),
        ["amenity=marketplace"] = ("Mercado",             2),
        ["amenity=pharmacy"]    = ("Farmácia",            2),
        ["amenity=school"]      = ("Escola",              3),
        ["leisure=park"]        = ("Parque",              3),
    };

    public OverpassClient(HttpClient http, ILogger<OverpassClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<List<PoiImportadoDTO>> BuscarNaAreaAsync(
        double sul, double oeste, double norte, double leste,
        CancellationToken cancellationToken)
    {
        await _semaforo.WaitAsync(cancellationToken);
        try
        {
            return await BuscarComRetryAsync(sul, oeste, norte, leste, cancellationToken);
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            _semaforo.Release();
        }
    }

    private async Task<List<PoiImportadoDTO>> BuscarComRetryAsync(
        double sul, double oeste, double norte, double leste,
        CancellationToken cancellationToken,
        int tentativasMax = 5)  // era 3, agora 5
    {
        var delay = TimeSpan.FromSeconds(30);  // era 10s, começa em 30s

        for (var tentativa = 1; tentativa <= tentativasMax; tentativa++)
        {
            try
            {
                return await BuscarInternoAsync(sul, oeste, norte, leste, cancellationToken);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.TooManyRequests ||
                ex.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                if (tentativa == tentativasMax) throw;

                _logger.LogWarning(
                    "Overpass {status} — tentativa {t}/{max}. Aguardando {s}s...",
                    (int?)ex.StatusCode, tentativa, tentativasMax, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
                delay *= 2; // 30s → 60s → 120s → 240s
            }
        }

        throw new InvalidOperationException("Não deveria chegar aqui.");
    }

    private async Task<List<PoiImportadoDTO>> BuscarInternoAsync(
        double sul, double oeste, double norte, double leste,
        CancellationToken cancellationToken)
    {
        var bbox = $"{sul},{oeste},{norte},{leste}";

        var filtros = string.Join("\n", TagsParaCategoria.Keys.Select(tag =>
        {
            var partes = tag.Split('=');
            var k = partes[0];
            var v = partes[1];
            return $"""
                    node["{k}"="{v}"]({bbox});
                    way["{k}"="{v}"]({bbox});
                    relation["{k}"="{v}"]({bbox});
                    """;
        }));

        var filtrosEntrada = $"""
            node["entrance"]["name"]({bbox});
            node["entrance"="main"]({bbox});
            """;

        var query = $"[out:json][timeout:60];\n(\n{filtros}\n{filtrosEntrada}\n);\nout center tags;";

        _logger.LogDebug("Overpass query: {query}", query);

        var resposta = await _http.PostAsync(
            "https://overpass-api.de/api/interpreter",
            new StringContent(query),
            cancellationToken);

        resposta.EnsureSuccessStatusCode();

        var json = await resposta.Content.ReadAsStringAsync(cancellationToken);
        using var doc      = JsonDocument.Parse(json);
        var       elementos = doc.RootElement.GetProperty("elements");

        var resultado    = new List<PoiImportadoDTO>();
        var osmIdsVistos = new HashSet<string>();

        foreach (var el in elementos.EnumerateArray())
        {
            var tipo  = el.GetProperty("type").GetString();
            var osmId = el.GetProperty("id").GetInt64();
            var chave = $"{tipo}:{osmId}";

            if (!osmIdsVistos.Add(chave))
                continue;

            var tags = el.TryGetProperty("tags", out var t) ? t : default;
            var nome = ObterNome(tags);
            if (string.IsNullOrWhiteSpace(nome)) continue;

            var (categoria, prioridade) = ResolverCategoriaEPrioridade(tags);
            if (categoria is null) continue;

            double lat, lon;
            if (tipo == "node")
            {
                lat = el.GetProperty("lat").GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }
            else if (el.TryGetProperty("center", out var center))
            {
                lat = center.GetProperty("lat").GetDouble();
                lon = center.GetProperty("lon").GetDouble();
            }
            else continue;

            resultado.Add(new PoiImportadoDTO
            {
                OsmId      = osmId,
                Nome       = nome,
                Categoria  = categoria,
                Prioridade = prioridade,
                Latitude   = lat,
                Longitude  = lon
            });
        }

        _logger.LogInformation(
            "Overpass: {qtd} POIs em ({s:F4},{w:F4},{n:F4},{e:F4})",
            resultado.Count, sul, oeste, norte, leste);

        return resultado;
    }

    private static (string? Categoria, int Prioridade) ResolverCategoriaEPrioridade(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return (null, 0);
        foreach (var (tag, info) in TagsParaCategoria)
        {
            var partes = tag.Split('=');
            if (tags.TryGetProperty(partes[0], out var val) && val.GetString() == partes[1])
                return (info.Categoria, info.Prioridade);
        }
        return (null, 0);
    }

    private static string? ObterNome(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return null;
        if (tags.TryGetProperty("name:pt", out var pt))  return pt.GetString();
        if (tags.TryGetProperty("name",    out var nome)) return nome.GetString();
        return null;
    }
}