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

    public async Task<IReadOnlyList<ParadaProximaConsultaDTO>> ListarProximasAsync(
        double latitude,
        double longitude,
        double raioMetros,
        int limite,
        CancellationToken cancellationToken)
    {
        FormattableString sql = $@"
SELECT
    p.""Id"" AS ""ParadaId"",
    p.""Nome"" AS ""Nome"",
    ST_Y(p.""Localizacao"") AS ""Latitude"",
    ST_X(p.""Localizacao"") AS ""Longitude"",
    ST_Distance(
        p.""Localizacao""::geography,
        ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography
    ) AS ""DistanciaMetros""
FROM ""Paradas"" p
WHERE ST_DWithin(
    p.""Localizacao""::geography,
    ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography,
    {raioMetros}
)
ORDER BY ""DistanciaMetros""
LIMIT {limite};";

        var itens = await _contexto.Database
            .SqlQuery<ParadaProximaConsultaDTO>(sql)
            .ToListAsync(cancellationToken);

        return itens;
    }

    public async Task<IReadOnlyList<ParadaLinhaConsultaDTO>> ListarLinhasAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        var registros = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(relacao => relacao.ParadaId == paradaId)
            .Select(relacao => new
            {
                LinhaId = relacao.Itinerario.Sentido.LinhaId,
                LinhaNome = relacao.Itinerario.Sentido.Linha.Nome,
                LinhaCodigo = relacao.Itinerario.Sentido.Linha.Codigo,
                SentidoId = relacao.Itinerario.SentidoId,
                SentidoNome = relacao.Itinerario.Sentido.Nome,
                ItinerarioId = relacao.ItinerarioId
            })
            .ToListAsync(cancellationToken);

        var itens = registros
            .GroupBy(item => new { item.LinhaId, item.LinhaNome, item.LinhaCodigo })
            .OrderBy(grupo => grupo.Key.LinhaNome)
            .Select(grupo => new ParadaLinhaConsultaDTO
            {
                LinhaId = grupo.Key.LinhaId,
                LinhaNome = grupo.Key.LinhaNome,
                Codigo = grupo.Key.LinhaCodigo,
                QuantidadeItinerarios = grupo.Select(item => item.ItinerarioId).Distinct().Count(),
                Sentidos = grupo
                    .GroupBy(item => new { item.SentidoId, item.SentidoNome })
                    .OrderBy(sentido => sentido.Key.SentidoNome)
                    .Select(sentido => new ParadaLinhaSentidoDTO
                    {
                        SentidoId = sentido.Key.SentidoId,
                        SentidoNome = sentido.Key.SentidoNome
                    })
                    .ToList()
            })
            .ToList();

        return itens;
    }

    public Task<bool> ExistePorIdAsync(Guid paradaId, CancellationToken cancellationToken)
    {
        return _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(parada => parada.Id == paradaId, cancellationToken);
    }
}
