using NoPonto.Application.Constantes;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Application.Exceptions;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Util;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class ParadaService : IParadaService
{
    private const int TamanhoMaximoPagina = 50;
    private const double RaioPadraoMetros = 500;
    private const double RaioMaximoMetros = 2_000;
    private const int LimitePadraoResultados = 50;
    private const int LimiteMaximoResultados = 100;

    private readonly IParadaRepository _paradaRepository;
    private readonly IItinerarioRepository _itinerarioRepository;
    private readonly ILogger<ParadaService> _logger;
    private readonly double _raioPadraoMetros;
    private readonly double _raioMaximoMetros;
    private readonly int _limiteResultados;

    public ParadaService(
        IParadaRepository paradaRepository,
        IItinerarioRepository itinerarioRepository,
        IConfiguration configuration,
        ILogger<ParadaService> logger)
    {
        _paradaRepository = paradaRepository;
        _itinerarioRepository = itinerarioRepository;
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
}
