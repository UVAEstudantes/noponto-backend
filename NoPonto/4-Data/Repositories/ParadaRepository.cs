using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Paradas;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class ParadaRepository : IParadaRepository
{
    private readonly TransporteDbContext _contexto;

    public ParadaRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<IReadOnlyList<ParadaPorItinerarioConsultaDTO>> ListarPorItinerarioAsync(Guid itinerarioId, CancellationToken cancellationToken)
    {
        var itens = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(relacao => relacao.ItinerarioId == itinerarioId)
            .OrderBy(relacao => relacao.Ordem)
            .Select(relacao => new ParadaPorItinerarioConsultaDTO
            {
                ParadaId = relacao.ParadaId,
                Nome = relacao.Parada.Nome,
                Latitude = relacao.Parada.Localizacao.Y,
                Longitude = relacao.Parada.Localizacao.X,
                Ordem = relacao.Ordem
            })
            .ToListAsync(cancellationToken);

        return itens;
    }

    public async Task<PaginacaoRespostaDTO<ParadaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        var consulta = _contexto.Paradas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(nome))
        {
            var filtro = nome.Trim();
            consulta = consulta.Where(parada => EF.Functions.ILike(parada.Nome, $"%{filtro}%"));
        }

        var totalRegistros = await consulta.CountAsync(cancellationToken);

        var itens = await consulta
            .OrderBy(parada => parada.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(parada => new ParadaConsultaDTO
            {
                Id = parada.Id,
                Nome = parada.Nome,
                Latitude = parada.Localizacao.Y,
                Longitude = parada.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<ParadaConsultaDTO>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        };
    }

    public Task<bool> ExistePorIdAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        return _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(parada => parada.Id == paradaId, cancellationToken);
    }
}
