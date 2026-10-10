using Microsoft.EntityFrameworkCore;
using Npgsql;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Data.Tarifas;
using NoPonto.Domain.Entities;
using NoPonto.Domain.Tarifas;

namespace NoPonto.Application.Tarifas;

public sealed class TarifasException(int status, string mensagem) : Exception(mensagem)
{
    public int Status { get; } = status;
}

public sealed class TarifaService(TransporteDbContext db, TarifasStore store)
{
    public async Task<Guid> ValidarEscopoAsync(Guid? modalId, Guid? linhaId, CancellationToken ct)
    {
        if (modalId is null && linhaId is null) throw new TarifasException(400, "Informe modalId ou linhaId.");
        if (modalId is not null && !await db.Modais.AnyAsync(x => x.Id == modalId, ct))
            throw new TarifasException(404, "Modal não encontrado.");
        if (linhaId is null) return modalId!.Value;
        var linha = await db.Linhas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == linhaId, ct)
            ?? throw new TarifasException(404, "Linha não encontrada.");
        if (modalId is not null && linha.ModalId != modalId)
            throw new TarifasException(400, "Linha não pertence ao modal informado.");
        if (!await db.Modais.AnyAsync(x => x.Id == linha.ModalId, ct))
            throw new TarifasException(404, "Modal da linha não encontrado.");
        return linha.ModalId;
    }

    public async Task<TarifasResolvidasResposta> ResolverAsync(Guid? modalId, Guid? linhaId, CancellationToken ct = default)
    {
        var modal = await ValidarEscopoAsync(modalId, linhaId, ct);
        var tarifas = await db.Tarifas.AsNoTracking().Where(x => x.ModalId == modal
            || (linhaId != null && x.LinhaId == linhaId)).ToArrayAsync(ct);
        var tarifa = tarifas.FirstOrDefault(x => linhaId != null && x.LinhaId == linhaId)
            ?? tarifas.FirstOrDefault(x => x.ModalId == modal);
        var formas = await db.FormasPagamentoVinculos.AsNoTracking()
            .Where(x => x.ModalId == modal || (linhaId != null && x.LinhaId == linhaId))
            .Select(x => new { x.FormaPagamento.Id, x.FormaPagamento.Nome, x.FormaPagamento.NomeNormalizado })
            .Distinct().ToArrayAsync(ct);
        return new(modal, linhaId,
            new(tarifa?.Valor, tarifa is null ? null : tarifa.LinhaId is null ? "MODAL" : "LINHA", tarifa?.Fonte),
            formas.OrderBy(x => x.NomeNormalizado, StringComparer.Ordinal).ThenBy(x => x.Id)
                .Select(x => new FormaPagamentoResposta(x.Id, x.Nome)).ToArray());
    }

    public async Task DefinirAsync(Guid? modalId, Guid? linhaId, decimal? valor, CancellationToken ct = default)
    {
        if (valor is null || !RegrasTarifarias.ValorValido(valor.Value))
            throw new TarifasException(400, "Valor deve ser não negativo, até 99999999.99, com no máximo duas casas decimais.");
        await ValidarEscopoAsync(modalId, linhaId, ct);
        await EscritaAsync(() => store.SalvarAsync(modalId, linhaId, valor.Value, "MANUAL", ct));
    }

    public async Task RemoverAsync(Guid? modalId, Guid? linhaId, CancellationToken ct = default)
    {
        await ValidarEscopoAsync(modalId, linhaId, ct);
        await db.Tarifas.Where(x => linhaId != null ? x.LinhaId == linhaId : x.ModalId == modalId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<FormaPagamentoResposta>> ListarFormasAsync(CancellationToken ct = default)
    {
        var formas = await db.FormasPagamento.AsNoTracking().ToArrayAsync(ct);
        return formas.OrderBy(x => x.NomeNormalizado, StringComparer.Ordinal).ThenBy(x => x.Id)
            .Select(x => new FormaPagamentoResposta(x.Id, x.Nome)).ToArray();
    }

    public async Task<FormaPagamentoResposta> CriarFormaAsync(string? nome, CancellationToken ct = default)
    {
        (string Nome, string Chave) normalized;
        try { normalized = RegrasTarifarias.NormalizarNome(nome); }
        catch (ArgumentException) { throw new TarifasException(400, "Nome inválido; use de 1 a 100 caracteres."); }
        if (await db.FormasPagamento.AnyAsync(x => x.NomeNormalizado == normalized.Chave, ct))
            throw new TarifasException(409, "Forma de pagamento já cadastrada.");
        var forma = new FormaPagamento { Id = Guid.NewGuid(), Nome = normalized.Nome,
            NomeNormalizado = normalized.Chave, CriadoEmUtc = DateTime.UtcNow };
        db.FormasPagamento.Add(forma);
        try { await EscritaAsync(() => db.SaveChangesAsync(ct)); }
        catch { db.Entry(forma).State = EntityState.Detached; throw; }
        return new(forma.Id, forma.Nome);
    }

    public async Task VincularAsync(Guid? modalId, Guid? linhaId, Guid formaId, bool remover, CancellationToken ct = default)
    {
        await ValidarEscopoAsync(modalId, linhaId, ct);
        if (!await db.FormasPagamento.AnyAsync(x => x.Id == formaId, ct))
            throw new TarifasException(404, "Forma de pagamento não encontrada.");
        if (remover)
            await db.FormasPagamentoVinculos.Where(x => x.FormaPagamentoId == formaId
                && (linhaId != null ? x.LinhaId == linhaId : x.ModalId == modalId)).ExecuteDeleteAsync(ct);
        else await EscritaAsync(() => store.VincularAsync(modalId, linhaId, formaId, ct));
    }

    internal static async Task EscritaAsync(Func<Task<int>> action)
    {
        try { await action(); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        { throw new TarifasException(409, "Registro já cadastrado."); }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        { throw new TarifasException(409, "Registro já cadastrado."); }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        { throw new TarifasException(404, "Escopo ou método removido durante a operação."); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23503")
        { throw new TarifasException(404, "Escopo ou método removido durante a operação."); }
    }
}
