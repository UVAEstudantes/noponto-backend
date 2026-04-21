using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;
using NoPonto.Data.Interfaces;

namespace NoPonto.Data.Repositories;

public sealed class LinhaRepository : ILinhaRepository
{
    private readonly TransporteDbContext _contexto;

    public LinhaRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<PaginacaoRespostaDTO<LinhaConsultaDTO>> ListarAsync(string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        var consulta = AplicarFiltro(_contexto.Linhas.AsNoTracking(), nome);
        var totalRegistros = await consulta.CountAsync(cancellationToken);

        var itens = await consulta
            .OrderBy(linha => linha.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(linha => new LinhaConsultaDTO
            {
                Id = linha.Id,
                Nome = linha.Nome,
                Codigo = linha.Codigo,
                ModalId = linha.ModalId
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<LinhaConsultaDTO>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        };
    }

    public async Task<IReadOnlyList<LinhaPorParadaConsultaDTO>> ListarPorParadaAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        var itens = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(relacao => relacao.ParadaId == paradaId)
            .Select(relacao => new
            {
                LinhaId = relacao.Itinerario.Sentido.LinhaId,
                LinhaNome = relacao.Itinerario.Sentido.Linha.Nome,
                SentidoId = relacao.Itinerario.SentidoId
            })
            .Distinct()
            .OrderBy(item => item.LinhaNome)
            .ThenBy(item => item.SentidoId)
            .Select(item => new LinhaPorParadaConsultaDTO
            {
                LinhaId = item.LinhaId,
                LinhaNome = item.LinhaNome,
                SentidoId = item.SentidoId
            })
            .ToListAsync(cancellationToken);

        return itens;
    }

    public Task<bool> ExistePorIdAsync(Guid linhaId, CancellationToken cancellationToken)
    {
        return _contexto.Linhas
            .AsNoTracking()
            .AnyAsync(linha => linha.Id == linhaId, cancellationToken);
    }

    private static IQueryable<NoPonto.Domain.Entities.Linha> AplicarFiltro(IQueryable<NoPonto.Domain.Entities.Linha> consulta, string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
            return consulta;

        var filtro = nome.Trim();

        return consulta.Where(linha =>
            EF.Functions.ILike(linha.Nome, $"%{filtro}%") ||
            EF.Functions.ILike(linha.Codigo, $"%{filtro}%"));
    }
}