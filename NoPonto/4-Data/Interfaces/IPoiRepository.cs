using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Interfaces;

public interface IPoiRepository
{
    Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(
        string? nome, int page, int pageSize, CancellationToken cancellationToken);

    Task<List<PoiPorParadaDTO>> ListarPorParadaAsync(
        Guid paradaId, CancellationToken cancellationToken);

    Task<List<PoiPorItinerarioDTO>> ListarPorItinerarioAsync(
        Guid itinerarioId, CancellationToken cancellationToken);

    Task<PaginacaoRespostaDTO<PoiContagemPorItinerarioDTO>> ListarContagemPorItinerarioAsync(
        string? nomeLinha,
        int page,
        int pageSize,
        string? sort,
        CancellationToken cancellationToken);

    Task<List<PoiConsultaDTO>> ListarPorPontoAsync(
        double latitude, double longitude, double raioMetros, CancellationToken cancellationToken);

    Task<List<Poi>> UpsertPoisAsync(
        IEnumerable<PoiImportadoDTO> importados, int tamanhoLote, CancellationToken cancellationToken);

    Task<HashSet<Guid>> BuscarPoisJaRelacionadosNaParadaAsync(
        Guid paradaId, CancellationToken cancellationToken);

    Task InserirRelacaoEmLoteAsync(
        IEnumerable<PoiParada> relacoes, int tamanhoLote, CancellationToken cancellationToken);
}