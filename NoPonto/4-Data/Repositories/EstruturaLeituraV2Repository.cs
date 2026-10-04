using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.Data.Repositories;

public sealed class EstruturaLeituraV2Repository(TransporteDbContext db)
    : IEstruturaLeituraV2Repository
{
    public async Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(
        string? codigo, string? nome, int pagina, int tamanhoPagina, CancellationToken ct)
    {
        var query = db.Linhas.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var filtro = codigo.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Codigo, $"%{filtro}%"));
        }
        if (!string.IsNullOrWhiteSpace(nome))
        {
            var filtro = nome.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Codigo, $"%{filtro}%")
                || EF.Functions.ILike(x.Nome, $"%{filtro}%"));
        }

        var total = await query.CountAsync(ct);
        var itens = await query.OrderBy(x => x.Codigo).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamanhoPagina).Take(tamanhoPagina)
            .Select(x => new LinhaEstruturalResumoDto(x.Id, x.Codigo, x.Nome,
                x.TipoRota, x.Consorcio, x.ModalId, x.Modal.Nome))
            .ToListAsync(ct);
        return new PaginacaoRespostaDTO<LinhaEstruturalResumoDto>
        {
            Pagina = pagina, TamanhoPagina = tamanhoPagina, TotalRegistros = total,
            TotalPaginas = total == 0 ? 0 : (int)Math.Ceiling((double)total / tamanhoPagina),
            Itens = itens
        };
    }

    public Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string codigo, CancellationToken ct)
        => db.Linhas.AsNoTracking().Where(x => x.Codigo == codigo)
            .Select(x => new LinhaEstruturalResumoDto(x.Id, x.Codigo, x.Nome,
                x.TipoRota, x.Consorcio, x.ModalId, x.Modal.Nome))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(
        string codigoLinha, CancellationToken ct)
    {
        var linhaId = await db.Linhas.AsNoTracking().Where(x => x.Codigo == codigoLinha)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (linhaId is null) return null;
        return await db.Sentidos.AsNoTracking().Where(x => x.LinhaId == linhaId)
            .OrderBy(x => x.Nome).ThenBy(x => x.Id)
            .Select(x => new SentidoEstruturalResumoDto(x.Id, x.LinhaId, x.Nome))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(
        Guid sentidoId, CancellationToken ct)
    {
        if (!await db.Sentidos.AsNoTracking().AnyAsync(x => x.Id == sentidoId, ct)) return null;
        return await db.PadroesOperacionais.AsNoTracking().Where(x => x.SentidoId == sentidoId)
            .OrderBy(x => x.Chave).ThenBy(x => x.Id)
            .Select(x => new PadraoOperacionalResumoDto(x.Id, x.SentidoId, x.Chave,
                x.TipoServico, x.NomePublico, x.VersaoAtualId))
            .ToListAsync(ct);
    }

    public async Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(
        Guid padraoVersaoId, CancellationToken ct)
    {
        var cabecalho = await db.PadroesVersoes.AsNoTracking()
            .Where(x => x.Id == padraoVersaoId)
            .Select(x => new
            {
                LinhaId = x.PadraoOperacional.Sentido.Linha.Id,
                CodigoLinha = x.PadraoOperacional.Sentido.Linha.Codigo,
                NomeLinha = x.PadraoOperacional.Sentido.Linha.Nome,
                SentidoId = x.PadraoOperacional.Sentido.Id,
                NomeSentido = x.PadraoOperacional.Sentido.Nome,
                PadraoId = x.PadraoOperacionalId,
                x.PadraoOperacional.Chave,
                x.PadraoOperacional.TipoServico,
                x.PadraoOperacional.NomePublico,
                VersaoId = x.Id,
                x.Numero,
                x.Topologia,
                x.ComprimentoMetros,
                x.PublicadoEmUtc,
                EhAtual = x.PadraoOperacional.VersaoAtualId == x.Id,
                x.Geometria
            }).SingleOrDefaultAsync(ct);
        if (cabecalho is null) return null;

        var ocorrencias = await db.OcorrenciasParadasPadroes.AsNoTracking()
            .Where(x => x.PadraoVersaoId == padraoVersaoId)
            .OrderBy(x => x.Ordem).ThenBy(x => x.Id)
            .Select(x => new OcorrenciaItinerarioDto(x.Id, x.Ordem, null, x.ParadaId,
                x.Parada.Codigo, x.Parada.Nome, x.Parada.Localizacao.Y,
                x.Parada.Localizacao.X, x.PosicaoTracado, x.DistanciaAcumuladaMetros))
            .ToListAsync(ct);
        var geometria = new GeometriaLinhaDto("LineString", cabecalho.Geometria.Coordinates
            .Select(x => new[] { x.X, x.Y }).ToArray());
        return new ItinerarioPadraoVersaoDto(cabecalho.LinhaId, cabecalho.CodigoLinha,
            cabecalho.NomeLinha, cabecalho.SentidoId, cabecalho.NomeSentido,
            cabecalho.PadraoId, cabecalho.Chave, cabecalho.TipoServico, cabecalho.NomePublico,
            cabecalho.VersaoId, cabecalho.Numero, cabecalho.Topologia,
            cabecalho.ComprimentoMetros, cabecalho.PublicadoEmUtc, cabecalho.EhAtual,
            geometria, ocorrencias);
    }

    public Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid paradaId, CancellationToken ct)
        => db.Paradas.AsNoTracking().Where(x => x.Id == paradaId)
            .Select(x => new ParadaEstruturalDto(x.Id, x.Codigo, x.Nome,
                x.Localizacao.Y, x.Localizacao.X, x.ModalId, x.TipoLocal,
                x.ParadaPaiId, x.Plataforma))
            .SingleOrDefaultAsync(ct);
}
