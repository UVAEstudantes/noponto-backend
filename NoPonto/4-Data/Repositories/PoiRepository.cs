using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class PoiRepository : IPoiRepository
{
    private readonly TransporteDbContext _contexto;

    public PoiRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        var consulta = _contexto.Pois.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(nome))
        {
            var filtro = nome.Trim();
            consulta = consulta.Where(poi => EF.Functions.ILike(poi.Nome, $"%{filtro}%"));
        }

        var totalRegistros = await consulta.CountAsync(cancellationToken);

        var itens = await consulta
            .OrderBy(poi => poi.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(poi => new PoiConsultaDTO
            {
                Id = poi.Id,
                Nome = poi.Nome,
                Latitude = poi.Localizacao.Y,
                Longitude = poi.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<PoiConsultaDTO>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        };
    }
}
