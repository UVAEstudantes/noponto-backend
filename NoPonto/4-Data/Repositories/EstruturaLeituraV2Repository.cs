using NoPonto.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;
using System.Globalization;
using System.Text;

namespace NoPonto.Data.Repositories;

public sealed class EstruturaLeituraV2Repository(TransporteDbContext db)
    : IEstruturaLeituraV2Repository
{
    public async Task<IReadOnlyList<ParadaLinhaCompactaDto>?> ListarParadasDaLinhaAsync(
        Guid linhaId, Guid? sentidoId, CancellationToken ct)
    {
        if (!await db.Linhas.AsNoTracking().AnyAsync(x => x.Id == linhaId, ct)) return null;
        return await ProjetarParadasDaLinha(db.OcorrenciasParadasPadroes.AsNoTracking(), linhaId, sentidoId).ToListAsync(ct);
    }

    internal static IQueryable<ParadaLinhaCompactaDto> ProjetarParadasDaLinha(
        IQueryable<OcorrenciaParadaPadrao> query, Guid linhaId, Guid? sentidoId) =>
        OcorrenciasAtuais(query).Where(x => x.PadraoVersao.PadraoOperacional.Sentido.LinhaId == linhaId
            && (!sentidoId.HasValue || x.PadraoVersao.PadraoOperacional.SentidoId == sentidoId))
        .OrderBy(x => x.PadraoVersao.PadraoOperacional.SentidoId).ThenBy(x => x.PadraoVersaoId)
        .ThenBy(x => x.Ordem).ThenBy(x => x.Id)
        .Select(x => new ParadaLinhaCompactaDto(x.Id, x.PadraoVersaoId,
            x.PadraoVersao.PadraoOperacional.SentidoId, x.Ordem, null, x.ParadaId,
            x.Parada.Codigo, x.Parada.Nome, x.Parada.Localizacao.Y, x.Parada.Localizacao.X));

    public async Task<IReadOnlyList<LinhaParadaCompactaDto>?> ListarLinhasDaParadaAsync(
        Guid paradaId, Guid? sentidoId, CancellationToken ct)
    {
        if (!await db.Paradas.AsNoTracking().AnyAsync(x => x.Id == paradaId, ct)) return null;
        return await ProjetarLinhasDaParada(db.OcorrenciasParadasPadroes.AsNoTracking(), paradaId, sentidoId).ToListAsync(ct);
    }

    internal static IQueryable<LinhaParadaCompactaDto> ProjetarLinhasDaParada(
        IQueryable<OcorrenciaParadaPadrao> query, Guid paradaId, Guid? sentidoId) =>
        OcorrenciasAtuais(query).Where(x => x.ParadaId == paradaId
            && (!sentidoId.HasValue || x.PadraoVersao.PadraoOperacional.SentidoId == sentidoId))
        .Select(x => new { LinhaId = x.PadraoVersao.PadraoOperacional.Sentido.LinhaId,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.Codigo,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.Nome,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.ModalId,
            x.PadraoVersao.PadraoOperacional.SentidoId, Sentido = x.PadraoVersao.PadraoOperacional.Sentido.Nome })
        .Distinct().OrderBy(x => x.Codigo).ThenBy(x => x.LinhaId).ThenBy(x => x.SentidoId)
        .Select(x => new LinhaParadaCompactaDto(x.LinhaId, x.Codigo, x.Nome, x.ModalId, x.SentidoId, x.Sentido));

    public async Task<PaginacaoRespostaDTO<ParadaConsultaV2Dto>> ListarParadasAsync(
        string? nome, Guid? modalId, string? codigoLinha, Guid? sentidoId,
        int pagina, int tamanhoPagina, CancellationToken ct)
    {
        if (pagina < 1 || tamanhoPagina is < 1 or > 100 || (long)(pagina - 1) * tamanhoPagina > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pagina));
        var atuais = OcorrenciasAtuais(db.OcorrenciasParadasPadroes.AsNoTracking());
        var query = FiltrarParadas(db.Paradas.AsNoTracking(), atuais, nome, modalId, codigoLinha, sentidoId);
        var total = await query.CountAsync(ct);
        var itens = await ProjetarParadas(PaginarParadas(query, pagina, tamanhoPagina)).ToListAsync(ct);
        if (itens.Count > 0)
        {
            var ids = itens.Select(x => x.Id).ToArray();
            var vinculos = await ProjetarVinculos(atuais.Where(x => ids.Contains(x.ParadaId))).Distinct().ToListAsync(ct);
            var porParada = vinculos.ToLookup(x => x.ParadaId);
            itens = itens.Select(x => CompletarParada(x, porParada[x.Id])).ToList();
        }
        return new() { Pagina = pagina, TamanhoPagina = tamanhoPagina, TotalRegistros = total,
            TotalPaginas = total == 0 ? 0 : (int)Math.Ceiling((double)total / tamanhoPagina), Itens = itens };
    }

    internal static IQueryable<OcorrenciaParadaPadrao> OcorrenciasAtuais(IQueryable<OcorrenciaParadaPadrao> query) =>
        query.Where(x => x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId);

    internal static IQueryable<Parada> FiltrarParadas(IQueryable<Parada> query,
        IQueryable<OcorrenciaParadaPadrao> atuais, string? nome, Guid? modalId, string? codigoLinha, Guid? sentidoId)
    {
        if (!string.IsNullOrWhiteSpace(nome))
        {
            var filtro = nome.Trim().ToLowerInvariant();
            query = query.Where(x => x.Nome.ToLower().Contains(filtro));
        }
        var vinculos = atuais;
        if (modalId.HasValue) vinculos = vinculos.Where(x => x.PadraoVersao.PadraoOperacional.Sentido.Linha.ModalId == modalId.Value);
        if (!string.IsNullOrWhiteSpace(codigoLinha))
        {
            var codigo = codigoLinha.Trim();
            vinculos = vinculos.Where(x => x.PadraoVersao.PadraoOperacional.Sentido.Linha.Codigo == codigo);
        }
        if (sentidoId.HasValue) vinculos = vinculos.Where(x => x.PadraoVersao.PadraoOperacional.SentidoId == sentidoId.Value);
        if (modalId.HasValue || !string.IsNullOrWhiteSpace(codigoLinha) || sentidoId.HasValue)
        {
            var somenteModal = string.IsNullOrWhiteSpace(codigoLinha) && !sentidoId.HasValue;
            query = query.Where(x => vinculos.Any(v => v.ParadaId == x.Id)
                || somenteModal && modalId.HasValue && x.ModalId == modalId);
        }
        return query;
    }

    internal static IQueryable<Parada> PaginarParadas(IQueryable<Parada> query, int pagina, int tamanho) =>
        query.OrderBy(x => x.Nome).ThenBy(x => x.Id).Skip((pagina - 1) * tamanho).Take(tamanho);

    internal static IQueryable<ParadaConsultaV2Dto> ProjetarParadas(IQueryable<Parada> query) =>
        query.Select(x => new ParadaConsultaV2Dto(x.Id, x.Codigo, x.Nome, x.Localizacao.Y, x.Localizacao.X)
        {
            Modais = x.ModalId.HasValue
                ? new[] { new ModalParadaV2Dto(x.ModalId.Value, x.Modal!.Nome) }
                : Array.Empty<ModalParadaV2Dto>()
        });

    internal sealed record VinculoParada(Guid ParadaId, Guid LinhaId, string Codigo, string Nome,
        Guid SentidoId, string Sentido, Guid ModalId, string Modal);

    internal static IQueryable<VinculoParada> ProjetarVinculos(IQueryable<OcorrenciaParadaPadrao> query) =>
        query.Select(x => new VinculoParada(x.ParadaId, x.PadraoVersao.PadraoOperacional.Sentido.LinhaId,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.Codigo,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.Nome,
            x.PadraoVersao.PadraoOperacional.SentidoId, x.PadraoVersao.PadraoOperacional.Sentido.Nome,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.ModalId,
            x.PadraoVersao.PadraoOperacional.Sentido.Linha.Modal.Nome));

    internal static ParadaConsultaV2Dto CompletarParada(ParadaConsultaV2Dto parada, IEnumerable<VinculoParada> vinculos)
    {
        var rows = vinculos.ToArray();
        return parada with
        {
            Linhas = rows.GroupBy(x => new { x.LinhaId, x.Codigo, x.Nome, x.ModalId })
                .OrderBy(x => x.Key.Codigo).ThenBy(x => x.Key.LinhaId)
                .Select(g => new LinhaParadaV2Dto(g.Key.LinhaId, g.Key.Codigo, g.Key.Nome, g.Key.ModalId,
                    g.Select(x => new SentidoEstruturalResumoDto(x.SentidoId, x.LinhaId, x.Sentido)).Distinct()
                        .OrderBy(x => x.Nome).ThenBy(x => x.Id).ToArray())).ToArray(),
            Modais = parada.Modais.Concat(rows.Select(x => new ModalParadaV2Dto(x.ModalId, x.Modal)))
                .Distinct().OrderBy(x => x.Nome).ThenBy(x => x.Id).ToArray()
        };
    }

    public async Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(
        string? codigo, string? nome, Guid? modalId, string? tipoRota,
        string? excluirTipoRota, int pagina, int tamanhoPagina, CancellationToken ct)
    {
        var query = db.Linhas.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var filtro = codigo.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Codigo, $"%{filtro}%"));
        }
        if (!string.IsNullOrWhiteSpace(nome))
        {
            var filtro = RemoverAcentos(nome.Trim());
            var pattern = $"%{filtro}%";
            query = query.Where(x => EF.Functions.ILike(x.Codigo, pattern)
                || EF.Functions.ILike(x.Nome.ToLower()
                    .Replace("á", "a").Replace("à", "a").Replace("ã", "a").Replace("â", "a")
                    .Replace("é", "e").Replace("ê", "e").Replace("í", "i")
                    .Replace("ó", "o").Replace("ô", "o").Replace("õ", "o")
                    .Replace("ú", "u").Replace("ü", "u").Replace("ç", "c"), pattern));
        }
        if (modalId.HasValue) query = query.Where(x => x.ModalId == modalId.Value);
        if (!string.IsNullOrWhiteSpace(tipoRota))
        {
            var filtro = tipoRota.Trim();
            query = query.Where(x => EF.Functions.ILike(x.TipoRota, filtro));
        }
        if (!string.IsNullOrWhiteSpace(excluirTipoRota))
        {
            var filtro = excluirTipoRota.Trim();
            query = query.Where(x => !EF.Functions.ILike(x.TipoRota, filtro));
        }

        var total = await query.CountAsync(ct);
        var itens = await query.OrderBy(x => x.Codigo).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamanhoPagina).Take(tamanhoPagina)
            .Select(x => new LinhaEstruturalResumoDto(x.Id, x.Codigo, x.Nome,
                x.TipoRota, x.Consorcio, x.ModalId, x.Modal.Nome))
            .ToListAsync(ct);
        var ids = itens.Select(x => x.LinhaId).ToArray();
        var endpoints = await db.OcorrenciasParadasPadroes.AsNoTracking()
            .Where(x => ids.Contains(x.PadraoVersao.PadraoOperacional.Sentido.LinhaId)
                && x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId)
            .Select(x => new { LinhaId = x.PadraoVersao.PadraoOperacional.Sentido.LinhaId,
                x.PadraoVersaoId, x.Ordem, x.Parada.Nome }).ToListAsync(ct);
        itens = itens.Select(item =>
        {
            var terminais = endpoints.Where(x => x.LinhaId == item.LinhaId)
                .GroupBy(x => x.PadraoVersaoId)
                .SelectMany(g => new[] { g.MinBy(x => x.Ordem)!.Nome, g.MaxBy(x => x.Ordem)!.Nome })
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return item with { TerminalA = terminais.ElementAtOrDefault(0),
                TerminalB = terminais.ElementAtOrDefault(1) };
        }).ToList();
        return new PaginacaoRespostaDTO<LinhaEstruturalResumoDto>
        {
            Pagina = pagina, TamanhoPagina = tamanhoPagina, TotalRegistros = total,
            TotalPaginas = total == 0 ? 0 : (int)Math.Ceiling((double)total / tamanhoPagina),
            Itens = itens
        };
    }

    private static string RemoverAcentos(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

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
