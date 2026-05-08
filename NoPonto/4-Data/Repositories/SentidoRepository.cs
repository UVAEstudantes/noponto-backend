using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Sentidos;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class SentidoRepository : ISentidoRepository
{
    private readonly TransporteDbContext _contexto;

    public SentidoRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<PaginacaoRespostaDTO<SentidoConsultaDTO>> ListarAsync(Guid? linhaId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var consulta = _contexto.Sentidos.AsNoTracking();

        if (linhaId.HasValue)
        {
            consulta = consulta.Where(sentido => sentido.LinhaId == linhaId.Value);
        }

        var totalRegistros = await consulta.CountAsync(cancellationToken);

        var itens = await consulta
            .OrderBy(sentido => sentido.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(sentido => new SentidoConsultaDTO
            {
                Id = sentido.Id,
                Nome = sentido.Nome,
                LinhaId = sentido.LinhaId,
                LinhaNome = sentido.Linha.Nome
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<SentidoConsultaDTO>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        };
    }
}
