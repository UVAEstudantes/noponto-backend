using System.Text.Json;
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Services;

public sealed class OverpassClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OverpassClient> _logger;

    private static readonly Dictionary<string, string> TagsParaCategoria = new()
    {
        ["amenity=hospital"]    = "Hospital",
        ["amenity=clinic"]      = "Clínica",
        ["amenity=university"]  = "Universidade",
        ["amenity=school"]      = "Escola",
        ["amenity=college"]     = "Faculdade",
        ["shop=mall"]           = "Shopping",
        ["shop=supermarket"]    = "Supermercado",
        ["amenity=marketplace"] = "Mercado",
        ["amenity=pharmacy"]    = "Farmácia",
        ["leisure=park"]        = "Parque",
        ["amenity=bus_station"] = "Terminal de Ônibus",
        ["railway=station"]     = "Estação de Trem/Metrô",
    };

    public OverpassClient(HttpClient http, ILogger<OverpassClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<List<PoiImportadoDTO>> BuscarNaAreaAsync(
        double sul, double oeste, double norte, double leste,
        CancellationToken cancellationToken)
    {
        var bbox = $"{sul},{oeste},{norte},{leste}";

        // Cada filtro gera node + way + relation para capturar todos os tipos OSM.
        // "out center" devolve o centroide para ways/relations, então sempre temos lat/lon.
        var filtros = string.Join("\n", TagsParaCategoria.Keys.Select(tag =>
        {
            var partes = tag.Split('=');
            var chave  = partes[0];
            var valor  = partes[1];
            return $"""
                    node["{chave}"="{valor}"]({bbox});
                    way["{chave}"="{valor}"]({bbox});
                    relation["{chave}"="{valor}"]({bbox});
                    """;
        }));

        var query = $"[out:json][timeout:60];\n(\n{filtros}\n);\nout center tags;";

        _logger.LogDebug("Overpass query: {query}", query);

        var resposta = await _http.PostAsync(
            "https://overpass-api.de/api/interpreter",
            new StringContent(query),
            cancellationToken);

        resposta.EnsureSuccessStatusCode();

        var json = await resposta.Content.ReadAsStringAsync(cancellationToken);
        using var doc      = JsonDocument.Parse(json);
        var       elementos = doc.RootElement.GetProperty("elements");

        var resultado   = new List<PoiImportadoDTO>();
        // Deduplica por OsmId dentro da mesma bbox (relation pode aparecer como way também)
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

            var categoria = ResolverCategoria(tags);
            if (categoria is null) continue;

            // nodes têm lat/lon direto; ways/relations têm "center"
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
            else
            {
                continue; // sem coordenada, descarta
            }

            resultado.Add(new PoiImportadoDTO
            {
                OsmId     = osmId,
                Nome      = nome,
                Categoria = categoria,
                Latitude  = lat,
                Longitude = lon
            });
        }

        _logger.LogInformation(
            "Overpass: {qtd} POIs encontrados na bbox ({s},{w},{n},{e})",
            resultado.Count, sul, oeste, norte, leste);

        return resultado;
    }

    private static string? ResolverCategoria(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return null;
        foreach (var (tag, categoria) in TagsParaCategoria)
        {
            var partes = tag.Split('=');
            if (tags.TryGetProperty(partes[0], out var val) && val.GetString() == partes[1])
                return categoria;
        }
        return null;
    }

    private static string? ObterNome(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return null;
        if (tags.TryGetProperty("name:pt", out var pt))  return pt.GetString();
        if (tags.TryGetProperty("name", out var nome))    return nome.GetString();
        return null;
    }
}