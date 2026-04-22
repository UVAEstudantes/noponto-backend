using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Application.Helpers;
using NoPonto.Application.Interfaces;
using NoPonto.Data.Interfaces;

namespace NoPonto.Application.Services;

public sealed class PoiService : IPoiService
{
    private readonly IPoiRepository _repository;

    public PoiService(IPoiRepository repository) => _repository = repository;

    public Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(
        string? nome, int page, int pageSize, CancellationToken cancellationToken)
        => _repository.ListarAsync(nome, page, pageSize, cancellationToken);

    public Task<List<PoiPorParadaDTO>> ListarPorParadaAsync(
        Guid paradaId, CancellationToken cancellationToken)
        => _repository.ListarPorParadaAsync(paradaId, cancellationToken);

    public async Task<List<PoiPorItinerarioDTO>> ListarPorItinerarioAsync(
        Guid itinerarioId, string? sort, CancellationToken cancellationToken)
    {
        var dados = await _repository.ListarPorItinerarioAsync(itinerarioId, cancellationToken);
        return OrdenacaoHelper.Ordenar(dados, sort).ToList();
    }

    public async Task<List<PoiContagemPorItinerarioDTO>> ListarContagemPorItinerarioAsync(
        string? sort, CancellationToken cancellationToken)
    {
        var dados = await _repository.ListarContagemPorItinerarioAsync(cancellationToken);
        return OrdenacaoHelper.OrdenarContagem(dados, sort).ToList();
    }

    public Task<List<PoiConsultaDTO>> ListarPorPontoAsync(
        double latitude, double longitude, double raioMetros, CancellationToken cancellationToken)
        => _repository.ListarPorPontoAsync(latitude, longitude, raioMetros, cancellationToken);
}