using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class ImportacaoParadasService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TransporteDbContext _contexto;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImportacaoParadasService> _logger;
    private readonly GeometryFactory _geometryFactory;

    public ImportacaoParadasService(
        IHttpClientFactory httpClientFactory,
        TransporteDbContext contexto,
        IConfiguration configuration,
        ILogger<ImportacaoParadasService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _contexto = contexto;
        _configuration = configuration;
        _logger = logger;
        _geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    }

    public Task ExecutarImportacaoAsync()
    {
        return ExecutarImportacaoAsync(CancellationToken.None);
    }

    public async Task ExecutarImportacaoAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando importação de paradas...");

        try
        {
            var tamanhoLote = LerTamanhoLoteParadas();
            var tamanhoPagina = LerTamanhoPaginaParadas();

            var codigosExistentes = await _contexto.Paradas
                .AsNoTracking()
                .Select(parada => parada.Codigo)
                .ToListAsync(cancellationToken);

            var codigosConhecidos = new HashSet<string>(codigosExistentes, StringComparer.OrdinalIgnoreCase);

            var pagina = 1;
            var offset = 0;
            var totalParadasInseridas = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation("Buscando página {pagina} com offset {offset}", pagina, offset);

                var urlImportacao = MontarUrlParadas(offset);
                var colecao = await BuscarParadasGeoJsonAsync(urlImportacao, cancellationToken);
                var features = colecao.Features;

                _logger.LogInformation("Registros recebidos nesta página: {quantidade}", features.Count);

                if (features.Count == 0)
                    break;

                var paradasNovas = ConverterParadasDaPagina(features, codigosConhecidos);
                var inseridasNaPagina = await InserirParadasEmLotesAsync(paradasNovas, tamanhoLote, cancellationToken);

                totalParadasInseridas += inseridasNaPagina;
                offset += tamanhoPagina;
                pagina++;
            }

            _logger.LogInformation("Total de paradas inseridas: {total}", totalParadasInseridas);

            _logger.LogInformation("Importação de paradas finalizada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha durante importação de paradas.");
            throw;
        }
    }

    private async Task<ParadaGeoJsonDTO> BuscarParadasGeoJsonAsync(string urlImportacao, CancellationToken cancellationToken)
    {
        var clienteHttp = _httpClientFactory.CreateClient();

        using var resposta = await clienteHttp.GetAsync(urlImportacao, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        await using var streamJson = await resposta.Content.ReadAsStreamAsync(cancellationToken);

        var dados = await JsonSerializer.DeserializeAsync<ParadaGeoJsonDTO>(
            streamJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        return dados ?? new ParadaGeoJsonDTO();
    }

    private async Task<int> InserirParadasEmLotesAsync(
        List<Parada> paradas,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (paradas.Count == 0)
        {
            return 0;
        }

        var totalInseridas = 0;

        for (var indice = 0; indice < paradas.Count; indice += tamanhoLote)
        {
            var lote = paradas
                .Skip(indice)
                .Take(tamanhoLote)
                .ToList();

            _contexto.Paradas.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();
            totalInseridas += lote.Count;

            _logger.LogInformation("Lote inserido: {quantidade}", lote.Count);
        }

        return totalInseridas;
    }

    private List<Parada> ConverterParadasDaPagina(
        IReadOnlyList<ParadaFeatureGeoJsonDTO> features,
        HashSet<string> codigosConhecidos)
    {
        var paradasNovas = new List<Parada>();

        foreach (var feature in features)
        {
            if (!TentarConverterParaParada(feature, out var parada))
                continue;

            if (!codigosConhecidos.Add(parada.Codigo))
                continue;

            paradasNovas.Add(parada);
        }

        return paradasNovas;
    }

    private bool TentarConverterParaParada(ParadaFeatureGeoJsonDTO feature, out Parada parada)
    {
        parada = null!;

        var codigo = feature.Properties?.StopId?.Trim();
        var nome = feature.Properties?.StopName?.Trim();
        var tipoGeometria = feature.Geometry?.Type?.Trim();
        var coordenadas = feature.Geometry?.Coordinates;

        if (string.IsNullOrWhiteSpace(codigo)
            || string.IsNullOrWhiteSpace(nome)
            || !string.Equals(tipoGeometria, "Point", StringComparison.OrdinalIgnoreCase)
            || coordenadas is null
            || coordenadas.Count < 2)
        {
            return false;
        }

        var longitude = coordenadas[0];
        var latitude = coordenadas[1];

        var localizacao = _geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
        localizacao.SRID = 4326;

        parada = new Parada
        {
            Id = Guid.NewGuid(),
            Codigo = codigo,
            Nome = nome,
            Localizacao = localizacao
        };

        return true;
    }

    private int LerTamanhoLoteParadas()
    {
        var valorConfigurado = _configuration["IMPORT:BATCH_SIZE"];

        if (int.TryParse(valorConfigurado, out var valor) && valor > 0)
            return valor;

        throw new InvalidOperationException("Variável IMPORT__BATCH_SIZE não configurada ou inválida.");
    }

    private int LerTamanhoPaginaParadas()
    {
        var valorConfigurado = _configuration["ARCGIS:PARADAS:PAGE_SIZE"];

        if (int.TryParse(valorConfigurado, out var valor) && valor > 0)
            return valor;

        throw new InvalidOperationException("Variável ARCGIS__PARADAS__PAGE_SIZE não configurada ou inválida.");
    }

    private string MontarUrlParadas(int offset)
    {
        var baseUrlParadas = _configuration["ARCGIS:PARADAS:BASE_URL"];

        if (string.IsNullOrWhiteSpace(baseUrlParadas))
            throw new InvalidOperationException("Variável ARCGIS__PARADAS__BASE_URL não configurada.");

        var where = _configuration["ARCGIS:PARADAS:WHERE"];

        if (string.IsNullOrWhiteSpace(where))
            throw new InvalidOperationException("Variável ARCGIS__PARADAS__WHERE não configurada.");

        var outFields = _configuration["ARCGIS:PARADAS:OUT_FIELDS"];

        if (string.IsNullOrWhiteSpace(outFields))
            throw new InvalidOperationException("Variável ARCGIS__PARADAS__OUT_FIELDS não configurada.");

        var tamanhoPagina = LerTamanhoPaginaParadas();
        var urlBaseNormalizada = baseUrlParadas.Trim().Trim('"');

        if (Uri.TryCreate(urlBaseNormalizada, UriKind.Absolute, out var uriAbsoluta))
        {
            urlBaseNormalizada = uriAbsoluta.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var indiceInterrogacao = urlBaseNormalizada.IndexOf('?', StringComparison.Ordinal);

        if (indiceInterrogacao >= 0)
        {
            urlBaseNormalizada = urlBaseNormalizada[..indiceInterrogacao];
        }

        var parametros = new Dictionary<string, string?>
        {
            ["where"] = where,
            ["outFields"] = outFields,
            ["returnGeometry"] = "true",
            ["f"] = "geojson",
            ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["resultRecordCount"] = tamanhoPagina.ToString(CultureInfo.InvariantCulture)
        };

        return QueryHelpers.AddQueryString(urlBaseNormalizada, parametros);
    }
}
