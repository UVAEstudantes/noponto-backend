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
            var urlImportacao = MontarUrlImportacaoParadas();
            var tamanhoLote = LerTamanhoLoteParadas();

            var colecao = await BuscarParadasGeoJsonAsync(urlImportacao, cancellationToken);
            var features = colecao.Features;

            _logger.LogInformation("Total de paradas recebidas: {total}", features.Count);

            var codigosExistentes = await _contexto.Paradas
                .AsNoTracking()
                .Select(parada => parada.Codigo)
                .ToListAsync(cancellationToken);

            var codigosConhecidos = new HashSet<string>(codigosExistentes, StringComparer.OrdinalIgnoreCase);
            var paradasNovas = new List<Parada>();

            foreach (var feature in features)
            {
                if (!TentarConverterParaParada(feature, out var parada))
                    continue;

                if (!codigosConhecidos.Add(parada.Codigo))
                    continue;

                paradasNovas.Add(parada);
            }

            await InserirParadasEmLotesAsync(paradasNovas, tamanhoLote, cancellationToken);

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

    private async Task InserirParadasEmLotesAsync(
        List<Parada> paradas,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (paradas.Count == 0)
        {
            _logger.LogInformation("Nenhuma parada nova para inserir.");
            return;
        }

        var totalLotes = (int)Math.Ceiling((double)paradas.Count / tamanhoLote);

        for (var indice = 0; indice < paradas.Count; indice += tamanhoLote)
        {
            var numeroLote = (indice / tamanhoLote) + 1;
            var lote = paradas
                .Skip(indice)
                .Take(tamanhoLote)
                .ToList();

            _logger.LogInformation("Inserindo lote {numero}", numeroLote);

            _contexto.Paradas.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();

            _logger.LogInformation(
                "Lote {numero} inserido com sucesso. Quantidade: {quantidade}",
                numeroLote,
                lote.Count);
        }

        _logger.LogInformation("Total de paradas inseridas: {total}", paradas.Count);
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
        var valorConfigurado = _configuration["IMPORTACAO_PARADAS_LOTE"];

        if (int.TryParse(valorConfigurado, out var valor) && valor > 0)
            return valor;

        return 1000;
    }

    private string MontarUrlImportacaoParadas()
    {
        var urlImportacaoParadas = _configuration["IMPORTACAO_PARADAS_URL"];

        if (!string.IsNullOrWhiteSpace(urlImportacaoParadas))
            return urlImportacaoParadas.Trim().Trim('"');

        var baseUrlParadas = _configuration["ARCGIS:PARADAS:BASE_URL"];

        if (string.IsNullOrWhiteSpace(baseUrlParadas))
            throw new InvalidOperationException("Variável IMPORTACAO_PARADAS_URL não configurada.");

        var where = _configuration["ARCGIS:PARADAS:WHERE"] ?? "1=1";
        var outFields = _configuration["ARCGIS:PARADAS:OUT_FIELDS"] ?? "*";
        var urlBaseNormalizada = baseUrlParadas.Trim().Trim('"');

        var parametros = new Dictionary<string, string?>
        {
            ["where"] = where,
            ["outFields"] = outFields,
            ["f"] = "geojson",
            ["returnGeometry"] = "true"
        };

        return QueryHelpers.AddQueryString(urlBaseNormalizada, parametros);
    }
}
