using System.Net;
using System.Text.Json;
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Services;

public sealed class OverpassClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OverpassClient> _logger;

    private static readonly SemaphoreSlim _semaforo = new(1, 1);

    private static readonly Dictionary<string, (string Categoria, int Prioridade)> TagsParaCategoria = new()
    {
        // ── Prioridade 1 — geradores de alto fluxo ────────────────────────────
        ["amenity=hospital"]         = ("Hospital",              1),
        ["amenity=bus_station"]      = ("Terminal de Ônibus",    1),
        ["amenity=ferry_terminal"]   = ("Terminal de Barcas",    1),
        ["railway=station"]          = ("Estação de Trem/Metrô", 1),
        ["railway=subway_entrance"]  = ("Entrada do Metrô",      1),
        ["leisure=shopping_centre"]  = ("Shopping",              1),
        ["shop=mall"]                = ("Shopping",              1),
        ["shop=department_store"]    = ("Loja de Departamentos", 1),
        ["building=retail"]          = ("Shopping",              1),
        ["leisure=stadium"]          = ("Estádio",               1),
        ["amenity=university"]       = ("Universidade",          1),

        // ── Prioridade 2 — serviços e equipamentos cotidianos ─────────────────
        ["amenity=college"]          = ("Faculdade",             2),
        ["amenity=clinic"]           = ("Clínica",               2),
        ["shop=supermarket"]         = ("Supermercado",          2),
        ["amenity=marketplace"]      = ("Mercado/Feira",         2),
        ["amenity=pharmacy"]         = ("Farmácia",              2),
        ["amenity=bank"]             = ("Banco",                 2),
        ["amenity=post_office"]      = ("Correios",              2),
        ["amenity=police"]           = ("Delegacia/Polícia",     2),
        ["amenity=fire_station"]     = ("Bombeiros",             2),
        ["amenity=theatre"]          = ("Teatro",                2),
        ["amenity=cinema"]           = ("Cinema",                2),
        ["tourism=museum"]           = ("Museu",                 2),
        ["amenity=library"]          = ("Biblioteca",            2),
        ["amenity=fuel"]             = ("Posto de Gasolina",     2),
        ["shop=convenience"]         = ("Conveniência/Mercearia",2),

        // ── Prioridade 3 — referências geográficas e equipamentos menores ─────
        ["amenity=school"]           = ("Escola",                3),
        ["leisure=park"]             = ("Parque",                3),
        ["tourism=attraction"]       = ("Atração Turística",     3),
        ["historic=landmark"]        = ("Marco Histórico",       3),
    };

    // Nomes que indicam shopping mesmo sem tag correta no OSM
    private static readonly string[] PalavrasChaveShopping =
        ["shopping", "mall", "plaza", "centre", "center", "galeria", "comercial"];

    private static readonly HashSet<string> CategoriasBaixaConfianca = new()
    {
        "Parque",
        "Atração Turística",
        "Marco Histórico",
        "Conveniência/Mercearia",
        "Posto de Gasolina",
        "Shopping", // building=retail pode ser qualquer coisa — valida pelo nome
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
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            _semaforo.Release();
        }
    }

    private async Task<List<PoiImportadoDTO>> BuscarComRetryAsync(
        double sul, double oeste, double norte, double leste,
        CancellationToken cancellationToken,
        int tentativasMax = 5)
    {
        var delay = TimeSpan.FromSeconds(30);

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
                delay *= 2;
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
            return $"""
                    node["{partes[0]}"="{partes[1]}"]({bbox});
                    way["{partes[0]}"="{partes[1]}"]({bbox});
                    relation["{partes[0]}"="{partes[1]}"]({bbox});
                    """;
        }));

        // Captura por nome: qualquer node/way/relation cujo nome contenha
        // palavras-chave de shopping — cobre casos sem tag correta no OSM
        var filtrosNomeShopping = $"""
            node["name"~"[Ss]hopping|[Mm]all|[Pp]laza|[Gg]aleria|[Cc]entre|[Cc]enter"]({bbox});
            way["name"~"[Ss]hopping|[Mm]all|[Pp]laza|[Gg]aleria|[Cc]entre|[Cc]enter"]({bbox});
            relation["name"~"[Ss]hopping|[Mm]all|[Pp]laza|[Gg]aleria|[Cc]entre|[Cc]enter"]({bbox});
            """;

        var filtrosEntrada = $"""
            node["entrance"]["name"]({bbox});
            node["entrance"="main"]({bbox});
            node["entrance"="yes"]["name"]({bbox});
            """;

        var query = $"[out:json][timeout:60];\n(\n{filtros}\n{filtrosNomeShopping}\n{filtrosEntrada}\n);\nout center tags;";

        _logger.LogDebug(
            "Overpass query ({s:F4},{w:F4},{n:F4},{e:F4})",
            sul, oeste, norte, leste);

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

            if (string.IsNullOrWhiteSpace(nome))
                continue;

            var (categoria, prioridade) = ResolverCategoriaEPrioridade(tags, nome);
            if (categoria is null)
                continue;

            // Categorias de baixa confiança: valida pelo nome
            if (CategoriasBaixaConfianca.Contains(categoria))
            {
                var nomeLower = nome.ToLowerInvariant();

                if (categoria == "Shopping")
                {
                    // Só aceita se o nome contém palavra-chave de shopping
                    if (!PalavrasChaveShopping.Any(p => nomeLower.Contains(p)))
                        continue;
                }
                else if (nome.Trim().Length < 5)
                {
                    // Demais categorias de baixa confiança: exige nome mínimo
                    continue;
                }
            }

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
                Nome       = nome.Trim(),
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

    private static (string? Categoria, int Prioridade) ResolverCategoriaEPrioridade(
        JsonElement tags, string nome)
    {
        if (tags.ValueKind == JsonValueKind.Undefined)
            return (null, 0);

        // 1. Tenta resolver pela tag OSM normal
        foreach (var (tag, info) in TagsParaCategoria)
        {
            var partes = tag.Split('=');
            if (tags.TryGetProperty(partes[0], out var val) && val.GetString() == partes[1])
                return (info.Categoria, info.Prioridade);
        }

        // 2. Fallback por nome: veio pela query de nome (sem tag reconhecida)
        //    Se o nome contém palavra-chave de shopping, classifica como Shopping
        var nomeLower = nome.ToLowerInvariant();
        if (PalavrasChaveShopping.Any(p => nomeLower.Contains(p)))
            return ("Shopping", 1);

        return (null, 0);
    }

    private static string? ObterNome(JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Undefined) return null;
        if (tags.TryGetProperty("name:pt", out var pt))   return pt.GetString();
        if (tags.TryGetProperty("name",    out var nome))  return nome.GetString();
        if (tags.TryGetProperty("brand",   out var brand)) return brand.GetString();
        return null;
    }
}