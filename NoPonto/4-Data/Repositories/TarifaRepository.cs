using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Data.Interfaces;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed class TarifaRepository : ITarifaRepository
{
    private readonly TransporteDbContext _contexto;

    public TarifaRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    public async Task<PaginacaoRespostaDTO<TarifaConsultaDTO>> ListarAsync(
        string? codigoLinha,
        Guid? linhaId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var consulta = _contexto.Tarifas.AsNoTracking();

        if (linhaId.HasValue)
            consulta = consulta.Where(tarifa => tarifa.LinhaId == linhaId.Value);

        if (!string.IsNullOrWhiteSpace(codigoLinha))
        {
            var filtro = codigoLinha.Trim();
            consulta = consulta.Where(tarifa => EF.Functions.ILike(tarifa.Linha.Codigo, $"%{filtro}%"));
        }

        var totalRegistros = await consulta.CountAsync(cancellationToken);

        var itens = await consulta
            .OrderByDescending(tarifa => tarifa.ValidoDe)
            .ThenBy(tarifa => tarifa.Linha.Codigo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tarifa => new TarifaConsultaDTO
            {
                Id = tarifa.Id,
                LinhaId = tarifa.LinhaId,
                LinhaCodigo = tarifa.Linha.Codigo,
                ModalId = tarifa.ModalId,
                Tarifa = tarifa.Valor,
                ValidoDe = tarifa.ValidoDe,
                ValidoAte = tarifa.ValidoAte,
                Fonte = tarifa.Fonte,
                CreatedAt = tarifa.CreatedAt,
                UpdatedAt = tarifa.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<TarifaConsultaDTO>
        {
            Pagina = page,
            TamanhoPagina = pageSize,
            TotalRegistros = totalRegistros,
            TotalPaginas = totalRegistros == 0 ? 0 : (int)Math.Ceiling((double)totalRegistros / pageSize),
            Itens = itens
        };
    }

    public async Task<TarifaConsultaDTO> CriarAsync(Tarifa tarifa, CancellationToken cancellationToken)
    {
        _contexto.Tarifas.Add(tarifa);
        await _contexto.SaveChangesAsync(cancellationToken);

        var linhaCodigo = await _contexto.Linhas
            .AsNoTracking()
            .Where(linha => linha.Id == tarifa.LinhaId)
            .Select(linha => linha.Codigo)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new TarifaConsultaDTO
        {
            Id = tarifa.Id,
            LinhaId = tarifa.LinhaId,
            LinhaCodigo = linhaCodigo,
            ModalId = tarifa.ModalId,
            Tarifa = tarifa.Valor,
            ValidoDe = tarifa.ValidoDe,
            ValidoAte = tarifa.ValidoAte,
            Fonte = tarifa.Fonte,
            CreatedAt = tarifa.CreatedAt,
            UpdatedAt = tarifa.UpdatedAt
        };
    }
}
