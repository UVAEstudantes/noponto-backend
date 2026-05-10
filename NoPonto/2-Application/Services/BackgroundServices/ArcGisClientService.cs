using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace NoPonto.Application.Services;

public sealed class ArcGisClientService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ArcGisClientService> _logger;
    private readonly string _urlConsulta;
    private readonly string _where;
    private readonly string _outFields;

    public ArcGisClientService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ArcGisClientService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var baseUrl =
            configuration["ARCGIS:ITINERARIOS:BASE_URL"]
            ?? configuration["ARCGIS:BASE_URL"];

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Variável de ambiente ARCGIS__ITINERARIOS__BASE_URL não configurada.");

        _where =
            configuration["ARCGIS:ITINERARIOS:WHERE"]
            ?? configuration["ARCGIS:WHERE"]
            ?? "tipo_rota='regular'";

        _outFields =
            configuration["ARCGIS:ITINERARIOS:OUT_FIELDS"]
            ?? configuration["ARCGIS:OUT_FIELDS"]
            ?? "servico,destino,direcao,shape_id,consorcio,tipo_dia,extensao";

        _urlConsulta = MontarUrlConsulta(baseUrl);
    }

    public async Task<IReadOnlyList<MetadadoItinerarioArcGis>> BuscarMetadadosAsync(
        int resultOffset,
        int resultRecordCount,
        CancellationToken cancellationToken)
    {
        var parametros = new Dictionary<string, string?>
        {
            ["where"] = _where,
            ["outFields"] = _outFields,
            ["returnGeometry"] = "false",
            ["f"] = "json",
            ["resultOffset"] = resultOffset.ToString(CultureInfo.InvariantCulture),
            ["resultRecordCount"] = resultRecordCount.ToString(CultureInfo.InvariantCulture)
        };

        var url = QueryHelpers.AddQueryString(_urlConsulta, parametros);
        using var resposta = await _httpClient.GetAsync(url, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        var conteudo = await resposta.Content.ReadAsStringAsync(cancellationToken);
        using var documento = JsonDocument.Parse(conteudo);

        if (!documento.RootElement.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var metadados = new List<MetadadoItinerarioArcGis>();

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("attributes", out var atributos)
                || atributos.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var servico   = LerString(atributos, "servico");
            var destino   = LerString(atributos, "destino");
            var direcao   = LerString(atributos, "direcao");
            var tipoRota  = LerString(atributos, "tipo_rota")?.Trim().ToLowerInvariant() ?? "regular";
            var shapeId   = LerString(atributos, "shape_id");
            var distancia = LerDouble(atributos, "extensao");
            var consorcio = LerString(atributos, "consorcio");

            _logger.LogInformation(
            "servico={Servico} tipo_rota={TipoRota} consorcio={Consorcio}",
            LerString(atributos, "servico"), tipoRota, consorcio ?? "(null)");

            if (string.IsNullOrWhiteSpace(servico)
                || string.IsNullOrWhiteSpace(destino)
                || string.IsNullOrWhiteSpace(direcao)
                || string.IsNullOrWhiteSpace(tipoRota)
                || string.IsNullOrWhiteSpace(shapeId)
                || !distancia.HasValue)
            {
                continue;
            }

            metadados.Add(new MetadadoItinerarioArcGis
            {
                Servico         = servico,
                Destino         = destino,
                Direcao         = direcao,
                TipoRota        = tipoRota,  // ← estava faltando no Add
                ShapeId         = shapeId,
                DistanciaMetros = distancia.Value,
                Consorcio       = consorcio  // ← estava faltando no Add
            });
        }

        return metadados;
    }

    public async Task<LineString?> BuscarGeometriaAsync(
        string shapeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shapeId))
            return null;

        var whereShapeId = shapeId.Replace("'", "''", StringComparison.Ordinal);

        var parametros = new Dictionary<string, string?>
        {
            ["where"] = $"shape_id='{whereShapeId}'",
            ["returnGeometry"] = "true",
            ["f"] = "geojson"
        };

        var url = QueryHelpers.AddQueryString(_urlConsulta, parametros);
        using var resposta = await _httpClient.GetAsync(url, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        var conteudo = await resposta.Content.ReadAsStringAsync(cancellationToken);
        using var documento = JsonDocument.Parse(conteudo);

        if (!documento.RootElement.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var enumeradorFeatures = features.EnumerateArray();

        if (!enumeradorFeatures.MoveNext())
            return null;

        var feature = enumeradorFeatures.Current;

        if (!feature.TryGetProperty("geometry", out var geometria)
            || geometria.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var lineString = ConverterParaLineString(geometria);

        if (lineString is null)
        {
            _logger.LogWarning("Geometria inválida para shape_id: {shapeId}", shapeId);
        }

        return lineString;
    }

    private static string MontarUrlConsulta(string baseUrl)
    {
        var url = baseUrl.Trim().Trim('"');

        if (Uri.TryCreate(url, UriKind.Absolute, out var uriAbsoluta))
        {
            url = uriAbsoluta.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        else
        {
            var indiceInterrogacao = url.IndexOf('?', StringComparison.Ordinal);

            if (indiceInterrogacao >= 0)
            {
                url = url[..indiceInterrogacao];
            }

            url = url.TrimEnd('/');
        }

        if (url.EndsWith("/query", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.EndsWith("/FeatureServer/1", StringComparison.OrdinalIgnoreCase))
            return $"{url}/query";

        return $"{url.TrimEnd('/')}/FeatureServer/1/query";
    }

    private static string? LerString(JsonElement objeto, string propriedade)
    {
        if (!objeto.TryGetProperty(propriedade, out var valor))
            return null;

        return valor.ValueKind switch
        {
            JsonValueKind.String => valor.GetString(),
            JsonValueKind.Number => valor.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static double? LerDouble(JsonElement objeto, string propriedade)
    {
        if (!objeto.TryGetProperty(propriedade, out var valor))
            return null;

        return LerDouble(valor);
    }

    private static double? LerDouble(JsonElement valor)
    {
        if (valor.ValueKind == JsonValueKind.Number && valor.TryGetDouble(out var numero))
            return numero;

        if (valor.ValueKind == JsonValueKind.String)
        {
            var texto = valor.GetString();

            if (string.IsNullOrWhiteSpace(texto))
                return null;

            if (double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeroInvariante))
                return numeroInvariante;

            if (double.TryParse(texto, NumberStyles.Float, new CultureInfo("pt-BR"), out var numeroPtBr))
                return numeroPtBr;
        }

        return null;
    }

    private static LineString? ConverterParaLineString(JsonElement geometria)
    {
        if (!geometria.TryGetProperty("type", out var tipoElemento)
            || tipoElemento.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (!geometria.TryGetProperty("coordinates", out var coordenadas))
            return null;

        var tipo = tipoElemento.GetString();

        if (string.Equals(tipo, "LineString", StringComparison.OrdinalIgnoreCase))
            return CriarLineString(coordenadas);

        if (string.Equals(tipo, "MultiLineString", StringComparison.OrdinalIgnoreCase)
            && coordenadas.ValueKind == JsonValueKind.Array)
        {
            var enumeradorLinhas = coordenadas.EnumerateArray();

            if (!enumeradorLinhas.MoveNext())
                return null;

            return CriarLineString(enumeradorLinhas.Current);
        }

        return null;
    }

    private static LineString? CriarLineString(JsonElement coordenadasElemento)
    {
        if (coordenadasElemento.ValueKind != JsonValueKind.Array)
            return null;

        var coordenadas = new List<Coordinate>();

        foreach (var ponto in coordenadasElemento.EnumerateArray())
        {
            if (ponto.ValueKind != JsonValueKind.Array)
                continue;

            var enumeradorPonto = ponto.EnumerateArray();

            if (!enumeradorPonto.MoveNext())
                continue;

            var longitude = LerDouble(enumeradorPonto.Current);

            if (!enumeradorPonto.MoveNext())
                continue;

            var latitude = LerDouble(enumeradorPonto.Current);

            if (!longitude.HasValue || !latitude.HasValue)
                continue;

            coordenadas.Add(new Coordinate(longitude.Value, latitude.Value));
        }

        if (coordenadas.Count < 2)
            return null;

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        return geometryFactory.CreateLineString([.. coordenadas]);
    }
}

// ── MUDANÇA 1: MetadadoItinerarioArcGis — adicionar Consorcio ─────────────────
// Ao final do arquivo ArcGisClientService.cs, substituir a classe por:

public sealed class MetadadoItinerarioArcGis
{
    public required string Servico { get; init; }
    public required string Destino { get; init; }
    public required string Direcao { get; init; }
    public required string TipoRota { get; set; } = "regular";
    public required string ShapeId { get; init; }
    public required double DistanciaMetros { get; init; }
    public string? Consorcio { get; init; }  // NOVO
}

// ── MUDANÇA 2: BuscarMetadadosAsync — ler o campo consorcio ──────────────────
// Dentro do foreach de features, após a leitura de distancia, adicionar:
//
//   var consorcio = LerString(atributos, "consorcio");
//
// E no metadados.Add(...), incluir:
//
//   Consorcio = consorcio
//
// O bloco completo do Add fica assim:

/*
    var servico   = LerString(atributos, "servico");
    var destino   = LerString(atributos, "destino");
    var direcao   = LerString(atributos, "direcao");
    var tipoRota  = LerString(atributos, "tipo_rota")?.Trim().ToLowerInvariant() ?? "regular";
    var shapeId   = LerString(atributos, "shape_id");
    var distancia = LerDouble(atributos, "extensao");
    var consorcio = LerString(atributos, "consorcio");   // <-- NOVO

    if (string.IsNullOrWhiteSpace(servico)
        || string.IsNullOrWhiteSpace(destino)
        || string.IsNullOrWhiteSpace(direcao)
        || string.IsNullOrWhiteSpace(tipoRota)
        || string.IsNullOrWhiteSpace(shapeId)
        || !distancia.HasValue)
    {
        continue;
    }

    metadados.Add(new MetadadoItinerarioArcGis
    {
        Servico         = servico,
        Destino         = destino,
        Direcao         = direcao,
        TipoRota        = tipoRota,
        ShapeId         = shapeId,
        DistanciaMetros = distancia.Value,
        Consorcio       = consorcio        // <-- NOVO
    });
*/