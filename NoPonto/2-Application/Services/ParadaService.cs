using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Application.Exceptions;
using NoPonto.Application.GPS;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class ParadaService : IParadaService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int TamanhoMaximoPagina = 50;
    private const double RaioPadraoMetros = 500;
    private const double RaioMaximoMetros = 2_000;
    private const int LimitePadraoResultados = 50;
    private const int LimiteMaximoResultados = 100;

    private readonly IParadaRepository _paradaRepository;
    private readonly IItinerarioRepository _itinerarioRepository;
    private readonly IDistributedCache _cache;
    private readonly ILogger<ParadaService> _logger;
    private readonly double _raioPadraoMetros;
    private readonly double _raioMaximoMetros;
    private readonly int _limiteResultados;

    public ParadaService(
        IParadaRepository paradaRepository,
        IItinerarioRepository itinerarioRepository,
        IDistributedCache cache,
        IConfiguration configuration,
        ILogger<ParadaService> logger)
    {
        _paradaRepository = paradaRepository;
        _itinerarioRepository = itinerarioRepository;
        _cache = cache;
        _logger = logger;

        var raioPadraoConfigurado = configuration.GetValue<double?>("CONSULTA:PARADAS_PROXIMAS:RAIO_PADRAO_METROS");
        _raioPadraoMetros = raioPadraoConfigurado is > 0 ? raioPadraoConfigurado.Value : RaioPadraoMetros;

        var raioMaximoConfigurado = configuration.GetValue<double?>("CONSULTA:PARADAS_PROXIMAS:RAIO_MAXIMO_METROS");
        _raioMaximoMetros = raioMaximoConfigurado is > 0 ? raioMaximoConfigurado.Value : RaioMaximoMetros;

        if (_raioPadraoMetros > _raioMaximoMetros)
            _raioPadraoMetros = _raioMaximoMetros;

        var limiteConfigurado = configuration.GetValue<int?>("CONSULTA:PARADAS_PROXIMAS:LIMITE_RESULTADOS");
        _limiteResultados = limiteConfigurado is > 0 and <= LimiteMaximoResultados
            ? limiteConfigurado.Value
            : LimitePadraoResultados;
    }

    public async Task<IReadOnlyList<ParadaPorItinerarioConsultaDTO>> ListarPorItinerarioAsync(Guid itinerarioId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando paradas por itinerário via service. itinerarioId={itinerarioId}", itinerarioId);

        var itinerarioExiste = await _itinerarioRepository.ExistePorIdAsync(itinerarioId, cancellationToken);

        if (!itinerarioExiste)
            throw new NotFoundException(MensagemErro.ITINERARIO_NAO_ENCONTRADO);

        return await _paradaRepository.ListarPorItinerarioAsync(itinerarioId, cancellationToken);
    }

    public async Task<PaginacaoRespostaDTO<ParadaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        PaginacaoUtil.ValidarOuLancar(page, pageSize, TamanhoMaximoPagina);

        _logger.LogInformation(
            "Consultando paradas via service. filtroNome={nome}, pagina={page}, tamanhoPagina={pageSize}",
            nome,
            page,
            pageSize);

        return await _paradaRepository.ListarAsync(nome, page, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<ParadaProximaConsultaDTO>> ListarProximasAsync(
        double latitude,
        double longitude,
        double? raioMetros,
        CancellationToken cancellationToken)
    {
        if (latitude < -90 || latitude > 90)
            throw new ValidationException(MensagemErro.LATITUDE_INVALIDA);

        if (longitude < -180 || longitude > 180)
            throw new ValidationException(MensagemErro.LONGITUDE_INVALIDA);

        var raioConsulta = raioMetros ?? _raioPadraoMetros;

        if (raioConsulta <= 0)
            throw new ValidationException(MensagemErro.RAIO_INVALIDO);

        if (raioConsulta > _raioMaximoMetros)
            throw new ValidationException(MensagemErro.RaioAcimaDoMaximo(_raioMaximoMetros));

        _logger.LogInformation(
            "Consultando paradas próximas via service. latitude={latitude}, longitude={longitude}, raioMetros={raioMetros}, limiteResultados={limiteResultados}",
            latitude,
            longitude,
            raioConsulta,
            _limiteResultados);

        return await _paradaRepository.ListarProximasAsync(
            latitude,
            longitude,
            raioConsulta,
            _limiteResultados,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ParadaLinhaConsultaDTO>> ListarLinhasAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consultando linhas por parada via service. paradaId={paradaId}", paradaId);

        var paradaExiste = await _paradaRepository.ExistePorIdAsync(paradaId, cancellationToken);

        if (!paradaExiste)
            throw new NotFoundException(MensagemErro.PARADA_NAO_ENCONTRADA);

        return await _paradaRepository.ListarLinhasAsync(paradaId, cancellationToken);
    }

    public async Task<IReadOnlyList<ParadaProximoVeiculoDTO>> ListarProximosVeiculosAsync(
        Guid paradaId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Consultando proximos veiculos por parada via service. paradaId={paradaId}",
            paradaId);

        var paradaNome = await _paradaRepository.BuscarNomeAsync(paradaId, cancellationToken);
        if (string.IsNullOrWhiteSpace(paradaNome))
            throw new NotFoundException(MensagemErro.PARADA_NAO_ENCONTRADA);

        var linhas = await _paradaRepository.ListarLinhasAsync(paradaId, cancellationToken);
        if (linhas.Count == 0)
            return [];

        var codigos = linhas
            .Select(l => l.Codigo)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codigos.Count == 0)
            return [];

        var linhasRaw = await Task.WhenAll(
            codigos.Select(c => _cache.GetStringAsync(GpsPollingService.ChaveLinha(c), cancellationToken)));

        var ordens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in linhasRaw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            foreach (var ordem in raw.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ordens.Add(ordem);
            }
        }

        if (ordens.Count == 0)
            return [];

        var posicoes = await Task.WhenAll(
            ordens.Select(ordem => BuscarPosicaoAsync(ordem, cancellationToken)));

        var veiculos = posicoes.Where(p => p is not null).Select(p => p!).ToList();
        if (veiculos.Count == 0)
            return [];

        var itinerarioIds = veiculos
            .Where(v => v.ItinerarioId.HasValue && v.PosicaoNaRota.HasValue)
            .Select(v => v.ItinerarioId!.Value)
            .Distinct()
            .ToList();

        if (itinerarioIds.Count == 0)
            return [];

        var posicoesParada = await _paradaRepository.ListarPosicoesPorItinerariosAsync(
            paradaId, itinerarioIds, cancellationToken);

        if (posicoesParada.Count == 0)
            return [];

        var itinerariosSelecionados = posicoesParada
            .GroupBy(p => p.CodigoLinha, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(p => p.DistanciaMetros)
                .ThenBy(p => p.PosicaoLinha)
                .First())
            .ToList();

        var posicaoPorItinerario = itinerariosSelecionados.ToDictionary(
            item => item.ItinerarioId,
            item => item.PosicaoLinha);

        var itinerariosPermitidos = new HashSet<Guid>(posicaoPorItinerario.Keys);

        var resultados = new List<ParadaProximoVeiculoDTO>();

        foreach (var veiculo in veiculos)
        {
            if (veiculo.ItinerarioId is null || veiculo.PosicaoNaRota is null)
                continue;

            if (!itinerariosPermitidos.Contains(veiculo.ItinerarioId.Value))
                continue;

            if (!posicaoPorItinerario.TryGetValue(veiculo.ItinerarioId.Value, out var posicaoParada))
                continue;

            if (posicaoParada < veiculo.PosicaoNaRota.Value)
                continue;

            double? distanciaParadaMetros = null;
            if (veiculo.ComprimentoRotaMetros is double comprimentoRota)
            {
                var delta = posicaoParada - veiculo.PosicaoNaRota.Value;
                if (delta >= 0)
                    distanciaParadaMetros = delta * comprimentoRota;
            }

            var etaParada = CalcularEtaParadaSegundos(veiculo, paradaNome, distanciaParadaMetros);
            var horarioChegada = CalcularHorarioChegadaPrevisto(veiculo, etaParada);

            resultados.Add(new ParadaProximoVeiculoDTO
            {
                Ordem = veiculo.Ordem,
                CodigoLinha = veiculo.CodigoLinha,
                Status = veiculo.Status,
                ItinerarioId = veiculo.ItinerarioId,
                Latitude = veiculo.Latitude,
                Longitude = veiculo.Longitude,
                TimestampGps = veiculo.TimestampGps,
                ProximaParadaNome = veiculo.ProximaParadaNome,
                DistanciaProximaParadaMetros = veiculo.DistanciaProximaParadaMetros,
                EtaProximaParadaSegundos = veiculo.EtaProximaParadaSegundos,
                EtaConfianca = veiculo.EtaConfianca,
                DistanciaParadaMetros = distanciaParadaMetros,
                EtaParadaSegundos = etaParada,
                HorarioChegadaPrevisto = horarioChegada,
                HorarioChegadaPrevistoLocal = horarioChegada?.ToLocalTime()
                    .ToString("HH:mm", CultureInfo.InvariantCulture)
            });
        }

        return resultados
            .OrderBy(r => r.EtaParadaSegundos ?? double.MaxValue)
            .ThenBy(r => r.DistanciaParadaMetros ?? double.MaxValue)
            .ToList();
    }

    private async Task<PosicaoVeiculoDto?> BuscarPosicaoAsync(string ordem, CancellationToken ct)
    {
        var jsonAtivo = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoAtivo(ordem), ct);
        if (jsonAtivo is not null)
        {
            try
            {
                var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonAtivo, JsonOptions);
                if (posicao is not null)
                    return posicao;
            }
            catch
            {
            }
        }

        var jsonRecente = await _cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(ordem), ct);
        if (jsonRecente is not null)
        {
            try
            {
                var posicao = JsonSerializer.Deserialize<PosicaoVeiculoDto>(jsonRecente, JsonOptions);
                return posicao is null ? null : posicao with { Status = StatusVeiculo.SemSinal };
            }
            catch
            {
            }
        }

        return null;
    }

    private static double? CalcularEtaParadaSegundos(
        PosicaoVeiculoDto veiculo,
        string paradaNome,
        double? distanciaParadaMetros)
    {
        if (!string.IsNullOrWhiteSpace(veiculo.ProximaParadaNome)
            && string.Equals(
                veiculo.ProximaParadaNome.Trim(),
                paradaNome.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            if (veiculo.EtaProximaParadaSegundos.HasValue)
                return veiculo.EtaProximaParadaSegundos.Value;

            if (veiculo.DistanciaProximaParadaMetros.HasValue)
                return EstimarEtaSegundos(veiculo.DistanciaProximaParadaMetros.Value, veiculo);
        }

        if (distanciaParadaMetros.HasValue)
            return EstimarEtaSegundos(distanciaParadaMetros.Value, veiculo);

        return null;
    }

    private static double? EstimarEtaSegundos(double distanciaMetros, PosicaoVeiculoDto veiculo)
    {
        var velocidadeKmh = veiculo.VelocidadeMedia ?? veiculo.Velocidade;
        if (velocidadeKmh <= 0)
            return null;

        var velocidadeMps = velocidadeKmh * (1000.0 / 3600.0);
        if (velocidadeMps <= 0)
            return null;

        return distanciaMetros / velocidadeMps;
    }

    private static DateTimeOffset? CalcularHorarioChegadaPrevisto(
        PosicaoVeiculoDto veiculo,
        double? etaParadaSegundos)
    {
        if (!etaParadaSegundos.HasValue)
            return null;

        var chegada = veiculo.TimestampGps.AddSeconds(etaParadaSegundos.Value);
        var agora = DateTimeOffset.UtcNow;

        return chegada < agora ? agora : chegada;
    }
}
