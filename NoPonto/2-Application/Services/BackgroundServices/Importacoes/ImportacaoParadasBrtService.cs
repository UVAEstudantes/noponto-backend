using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class ImportacaoParadasBrtService
{
    private const string PrefixoCodigo = "BRT-";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImportacaoParadasBrtService> _logger;

    public ImportacaoParadasBrtService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ImportacaoParadasBrtService> logger)
    {
        _scopeFactory      = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _configuration     = configuration;
        _logger            = logger;
    }

    public Task ExecutarImportacaoAsync() =>
        ExecutarImportacaoAsync(CancellationToken.None);

    public async Task ExecutarImportacaoAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando importação de paradas BRT...");

        try
        {
            var tamanhoLote   = LerTamanhoLote();
            var tamanhoPagina = LerTamanhoPagina();

            using var escopo = _scopeFactory.CreateScope();
            var contexto     = escopo.ServiceProvider
                .GetRequiredService<TransporteDbContext>();

            var geometryFactory = NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326);

            var codigosExistentes = await contexto.Paradas
                .AsNoTracking()
                .Where(p => p.Codigo.StartsWith(PrefixoCodigo))
                .Select(p => p.Codigo)
                .ToListAsync(cancellationToken);

            var codigosConhecidos = new HashSet<string>(
                codigosExistentes, StringComparer.OrdinalIgnoreCase);

            var pagina         = 1;
            var offset         = 0;
            var totalInseridas = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "BRT — buscando página {pagina} (offset {offset})", pagina, offset);

                var url     = MontarUrl(offset, tamanhoPagina);
                var colecao = await BuscarGeoJsonAsync(url, cancellationToken);

                _logger.LogInformation(
                    "BRT — registros recebidos na página {pagina}: {qtd}",
                    pagina, colecao.Features.Count);

                if (colecao.Features.Count == 0)
                    break;

                var novas  = ConverterParadas(colecao.Features, codigosConhecidos, geometryFactory);
                var salvas = await InserirEmLotesAsync(contexto, novas, tamanhoLote, cancellationToken);

                totalInseridas += salvas;
                offset         += tamanhoPagina;
                pagina++;

                if (colecao.Features.Count < tamanhoPagina)
                    break;
            }

            _logger.LogInformation(
                "Importação BRT concluída — total inserido: {total}", totalInseridas);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha durante importação de paradas BRT.");
            throw;
        }
    }

    // ── Busca HTTP ────────────────────────────────────────────────────────────

    private async Task<BrtGeoJsonCollection> BuscarGeoJsonAsync(
        string url, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient();

        using var resposta = await http.GetAsync(url, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        var json = await resposta.Content.ReadAsStringAsync(cancellationToken);

        // Loga os primeiros 500 chars na primeira vez para diagnóstico de estrutura
        _logger.LogDebug("BRT GeoJSON (preview): {preview}",
            json[..Math.Min(json.Length, 500)]);

        return JsonSerializer.Deserialize<BrtGeoJsonCollection>(json, JsonOpts)
               ?? new BrtGeoJsonCollection();
    }

    // ── Conversão de features ─────────────────────────────────────────────────

    private List<Parada> ConverterParadas(
        IReadOnlyList<BrtFeature> features,
        HashSet<string> codigosConhecidos,
        GeometryFactory geometryFactory)
    {
        var lista        = new List<Parada>();
        var semCoordenada = 0;
        var semId         = 0;

        foreach (var feature in features)
        {
            if (!TentarConverter(feature, geometryFactory, out var parada))
            {
                // Distingue motivo para facilitar diagnóstico
                if (feature.Properties is null
                    || (feature.Properties.Nome is null
                        && feature.Properties.Name is null
                        && feature.Properties.NomeEstacao is null
                        && feature.Properties.Estacao is null
                        && feature.Properties.Label is null))
                    semId++;
                else
                    semCoordenada++;
                continue;
            }

            if (!codigosConhecidos.Add(parada.Codigo))
                continue;

            lista.Add(parada);
        }

        if (semCoordenada > 0)
            _logger.LogWarning("BRT — {n} features descartadas por coordenada inválida.", semCoordenada);
        if (semId > 0)
            _logger.LogWarning("BRT — {n} features descartadas por nome/id ausente.", semId);

        return lista;
    }

    private static bool TentarConverter(
        BrtFeature feature,
        GeometryFactory geometryFactory,
        out Parada parada)
    {
        parada = null!;

        var props = feature.Properties;
        var geo   = feature.RawGeometry;

        if (props is null || geo is null || geo.Value.ValueKind == JsonValueKind.Null)
            return false;

        // ── Nome ──────────────────────────────────────────────────────────────
        var nome = props.Nome
                ?? props.Name
                ?? props.NomeEstacao
                ?? props.Estacao
                ?? props.Label;

        // ── Identificador ─────────────────────────────────────────────────────
        var idBruto = props.IdEstacao
                ?? props.StopId
                ?? props.Codigo
                ?? props.Fid?.ToString(CultureInfo.InvariantCulture)
                ?? props.ObjectId?.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(idBruto))
            return false;

        // ── Coordenadas — suporta Point, MultiPoint e Polygon ─────────────────
        if (!geo.Value.TryGetProperty("type", out var typeProp))
            return false;

        var geoType = typeProp.GetString() ?? string.Empty;

        if (!geo.Value.TryGetProperty("coordinates", out var coordsProp))
            return false;

        (double Lon, double Lat)? ponto = geoType.ToUpperInvariant() switch
        {
            // Point: coordinates = [lon, lat]
            "POINT" => ExtrairPonto(coordsProp),

            // MultiPoint: coordinates = [[lon, lat], [lon, lat], ...]  → primeiro ponto
            "MULTIPOINT" => ExtrairPrimeiroPontoDeArray(coordsProp),

            // Polygon: coordinates = [[[lon,lat], ...]]  → centróide do anel externo
            "POLYGON" => ExtrairCentroidePoligono(coordsProp),

            // MultiPolygon → centróide do primeiro anel do primeiro polígono
            "MULTIPOLYGON" => ExtrairCentroideMultiPoligono(coordsProp),

            _ => null
        };

        if (ponto is null)
            return false;

        var (lon, lat) = ponto.Value;

        if (lon == 0 && lat == 0)
            return false;

        var localizacao = geometryFactory.CreatePoint(new Coordinate(lon, lat));
        localizacao.SRID = 4326;

        parada = new Parada
        {
            Id          = Guid.NewGuid(),
            Codigo      = $"{PrefixoCodigo}{idBruto.Trim().ToUpperInvariant()}",
            Nome        = nome.Trim(),
            Localizacao = localizacao,
        };

        return true;
    }

    // ── Extratores de coordenada ──────────────────────────────────────────────

    /// <summary>Point: coordinates = [lon, lat]</summary>
    private static (double Lon, double Lat)? ExtrairPonto(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array)
            return null;

        var arr = coords.EnumerateArray().ToList();
        if (arr.Count < 2)
            return null;

        // Cada elemento pode ser número direto ou, em casos raros, outro array
        if (arr[0].ValueKind == JsonValueKind.Array)
        {
            // coordinates = [[lon, lat]] — Point mal-formado; trata como MultiPoint
            return ExtrairPrimeiroPontoDeArray(coords);
        }

        if (!arr[0].TryGetDouble(out var lon) || !arr[1].TryGetDouble(out var lat))
            return null;

        return (lon, lat);
    }

    /// <summary>MultiPoint: coordinates = [[lon, lat], [lon, lat], ...]</summary>
    private static (double Lon, double Lat)? ExtrairPrimeiroPontoDeArray(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in coords.EnumerateArray())
        {
            var pt = ExtrairPonto(item);
            if (pt.HasValue)
                return pt;
        }

        return null;
    }

    /// <summary>Polygon: coordinates = [[[lon,lat], ...]] — usa centróide do anel externo</summary>
    private static (double Lon, double Lat)? ExtrairCentroidePoligono(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array)
            return null;

        // Primeiro elemento = anel externo
        var aneis = coords.EnumerateArray().ToList();
        if (aneis.Count == 0)
            return null;

        return Centroide(aneis[0]);
    }

    /// <summary>MultiPolygon: coordinates = [[[[lon,lat], ...]]]</summary>
    private static (double Lon, double Lat)? ExtrairCentroideMultiPoligono(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array)
            return null;

        var poligonos = coords.EnumerateArray().ToList();
        if (poligonos.Count == 0)
            return null;

        return ExtrairCentroidePoligono(poligonos[0]);
    }

    private static (double Lon, double Lat)? Centroide(JsonElement anel)
    {
        if (anel.ValueKind != JsonValueKind.Array)
            return null;

        var pontos = new List<(double Lon, double Lat)>();

        foreach (var item in anel.EnumerateArray())
        {
            var pt = ExtrairPonto(item);
            if (pt.HasValue)
                pontos.Add(pt.Value);
        }

        if (pontos.Count == 0)
            return null;

        return (
            pontos.Average(p => p.Lon),
            pontos.Average(p => p.Lat)
        );
    }

    // ── Persistência ──────────────────────────────────────────────────────────

    private async Task<int> InserirEmLotesAsync(
        TransporteDbContext contexto,
        List<Parada> paradas,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (paradas.Count == 0)
            return 0;

        var total = 0;

        for (var i = 0; i < paradas.Count; i += tamanhoLote)
        {
            var lote = paradas.Skip(i).Take(tamanhoLote).ToList();
            contexto.Paradas.AddRange(lote);
            await contexto.SaveChangesAsync(cancellationToken);
            contexto.ChangeTracker.Clear();
            total += lote.Count;

            _logger.LogInformation("BRT — lote inserido: {qtd} paradas.", lote.Count);
        }

        return total;
    }

    // ── URL ───────────────────────────────────────────────────────────────────

    private string MontarUrl(int offset, int tamanhoPagina)
    {
        var baseUrl = _configuration["ARCGIS:BRT:ESTACOES:BASE_URL"];

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "Variável ARCGIS__BRT__ESTACOES__BASE_URL não configurada.");

        var urlLimpa = baseUrl.Trim().Trim('"');
        var idx      = urlLimpa.IndexOf('?', StringComparison.Ordinal);
        if (idx >= 0) urlLimpa = urlLimpa[..idx];

        var parametros = new Dictionary<string, string?>
        {
            ["outFields"]         = "*",
            ["where"]             = "1=1",
            ["returnGeometry"]    = "true",
            ["f"]                 = "geojson",
            ["resultOffset"]      = offset.ToString(CultureInfo.InvariantCulture),
            ["resultRecordCount"] = tamanhoPagina.ToString(CultureInfo.InvariantCulture),
        };

        return QueryHelpers.AddQueryString(urlLimpa, parametros);
    }

    // ── Configurações ─────────────────────────────────────────────────────────

    private int LerTamanhoLote()
    {
        var v = _configuration["IMPORT:BATCH_SIZE"];
        if (int.TryParse(v, out var r) && r > 0) return r;
        throw new InvalidOperationException(
            "Variável IMPORT__BATCH_SIZE não configurada ou inválida.");
    }

    private int LerTamanhoPagina()
    {
        var v = _configuration["ARCGIS:BRT:ESTACOES:PAGE_SIZE"]
             ?? _configuration["ARCGIS:PARADAS:PAGE_SIZE"];
        if (int.TryParse(v, out var r) && r > 0) return r;
        return 1000;
    }

    // ── DTOs GeoJSON ──────────────────────────────────────────────────────────

    private sealed class BrtGeoJsonCollection
    {
        public List<BrtFeature> Features { get; init; } = [];
    }

    private sealed class BrtFeature
    {
        // Geometry fica como JsonElement? para suportar Point, MultiPoint, Polygon, etc.
        [JsonPropertyName("geometry")]
        public JsonElement? RawGeometry { get; init; }

        [JsonPropertyName("properties")]
        public BrtProperties? Properties { get; init; }
    }

    private sealed class BrtProperties
    {
        // Identificador principal deste serviço ArcGIS BRT
        [JsonPropertyName("fid")]
        public int? Fid { get; init; }

        // Mantidos como fallback para outros serviços ArcGIS
        public string? IdEstacao { get; init; }
        public string? StopId   { get; init; }
        public string? Codigo   { get; init; }
        public int?    ObjectId { get; init; }

        // Nome da estação/terminal
        public string? Nome        { get; init; }
        public string? Name        { get; init; }
        public string? NomeEstacao { get; init; }
        public string? Estacao     { get; init; }
        public string? Label       { get; init; }

        // Campos extras do BRT (não usados na importação, mas úteis para debug)
        public string? Tipo      { get; init; }
        public string? Corredor  { get; init; }
    }
}