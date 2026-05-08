using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Linhas;
using NoPonto.Application.DTOs.Tarifas;
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

    public async Task<LinhaDetalhesDTO?> BuscarDetalhesAsync(Guid linhaId, CancellationToken cancellationToken)
    {
        var linha = await _contexto.Linhas
            .AsNoTracking()
            .Where(item => item.Id == linhaId)
            .Select(item => new
            {
                LinhaId = item.Id,
                LinhaNome = item.Nome,
                Codigo = item.Codigo
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (linha is null)
            return null;

        var sentidos = await _contexto.Sentidos
            .AsNoTracking()
            .Where(sentido => sentido.LinhaId == linhaId)
            .OrderBy(sentido => sentido.Nome)
            .Select(sentido => new
            {
                SentidoId = sentido.Id,
                SentidoNome = sentido.Nome
            })
            .ToListAsync(cancellationToken);

        var itinerarios = await _contexto.Itinerarios
            .AsNoTracking()
            .Where(itinerario => itinerario.Sentido.LinhaId == linhaId)
            .GroupJoin(
                _contexto.ParadasItinerario.AsNoTracking(),
                itinerario => itinerario.Id,
                paradaItinerario => paradaItinerario.ItinerarioId,
                (itinerario, paradas) => new
                {
                    ItinerarioId = itinerario.Id,
                    SentidoId = itinerario.SentidoId,
                    DistanciaMetros = itinerario.DistanciaMetros,
                    QuantidadeParadas = paradas.Count()
                })
            .ToListAsync(cancellationToken);

        var itinerariosPorSentido = itinerarios
            .GroupBy(item => item.SentidoId)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => (IReadOnlyList<LinhaDetalheItinerarioDTO>)grupo
                    .OrderBy(item => item.ItinerarioId)
                    .Select(item => new LinhaDetalheItinerarioDTO
                    {
                        ItinerarioId = item.ItinerarioId,
                        DistanciaMetros = item.DistanciaMetros,
                        QuantidadeParadas = item.QuantidadeParadas
                    })
                    .ToList());

        var sentidosDetalhe = sentidos
            .Select(sentido => new LinhaDetalheSentidoDTO
            {
                SentidoId = sentido.SentidoId,
                Nome = sentido.SentidoNome,
                Itinerarios = itinerariosPorSentido.GetValueOrDefault(sentido.SentidoId, [])
            })
            .ToList();

        var agora = DateTime.UtcNow;

        var tarifaAtual = await _contexto.Tarifas
            .AsNoTracking()
            .Where(tarifa => tarifa.LinhaId == linhaId
                && tarifa.ValidoDe <= agora
                && (tarifa.ValidoAte == null || tarifa.ValidoAte >= agora))
            .OrderByDescending(tarifa => tarifa.ValidoDe)
            .Select(tarifa => new TarifaResumoDTO
            {
                Tarifa = tarifa.Valor,
                ValidoDe = tarifa.ValidoDe,
                ValidoAte = tarifa.ValidoAte,
                Fonte = tarifa.Fonte
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new LinhaDetalhesDTO
        {
            LinhaId = linha.LinhaId,
            LinhaNome = linha.LinhaNome,
            Codigo = linha.Codigo,
            TarifaAtual = tarifaAtual,
            Sentidos = sentidosDetalhe
        };
    }

    public Task<Guid?> BuscarModalIdAsync(Guid linhaId, CancellationToken cancellationToken)
    {
        return _contexto.Linhas
            .AsNoTracking()
            .Where(linha => linha.Id == linhaId)
            .Select(linha => (Guid?)linha.ModalId)
            .FirstOrDefaultAsync(cancellationToken);
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