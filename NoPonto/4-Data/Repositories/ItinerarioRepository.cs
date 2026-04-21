using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Itinerarios;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class ItinerarioRepository : IItinerarioRepository
{
    private readonly TransporteDbContext _contexto;

    public ItinerarioRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<IReadOnlyList<ItinerarioPorLinhaConsultaDTO>> ListarPorLinhaAsync(Guid linhaId, CancellationToken cancellationToken)
    {
        var itens = await _contexto.Itinerarios
            .AsNoTracking()
            .Where(itinerario => itinerario.Sentido.LinhaId == linhaId)
            .OrderBy(itinerario => itinerario.SentidoId)
            .Select(itinerario => new ItinerarioPorLinhaConsultaDTO
            {
                Id = itinerario.Id,
                LinhaId = itinerario.Sentido.LinhaId,
                SentidoId = itinerario.SentidoId
            })
            .ToListAsync(cancellationToken);

        return itens;
    }

    public Task<bool> ExistePorIdAsync(Guid itinerarioId, CancellationToken cancellationToken)
    {
        return _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(itinerario => itinerario.Id == itinerarioId, cancellationToken);
    }
}