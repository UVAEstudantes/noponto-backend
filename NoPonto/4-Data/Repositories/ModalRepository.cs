using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Modais;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class ModalRepository : IModalRepository
{
    private readonly TransporteDbContext _contexto;

    public ModalRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<IReadOnlyList<ModalConsultaDTO>> ListarAsync(CancellationToken cancellationToken)
    {
        var itens = await _contexto.Modais
            .AsNoTracking()
            .OrderBy(modal => modal.Nome)
            .Select(modal => new ModalConsultaDTO
            {
                Id = modal.Id,
                Nome = modal.Nome
            })
            .ToListAsync(cancellationToken);

        return itens;
    }
}
