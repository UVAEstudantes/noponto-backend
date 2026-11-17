using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed class RadarProgramacoesRepository(TransporteDbContext db)
{
    public Task<List<RadarProgramacao>> ListarAsync(Guid instalacaoId, CancellationToken ct)
        => db.RadarProgramacoes.AsNoTracking().Where(x => x.InstalacaoId == instalacaoId)
            .OrderBy(x => x.Nome).ThenBy(x => x.Id).ToListAsync(ct);

    // Serializa inclusive duas primeiras inclusões, quando ainda não existe linha para bloquear.
    public async Task<IDbContextTransaction> BloquearInstalacaoAsync(Guid instalacaoId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({instalacaoId.ToString("N")}, 0))", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task<RadarProgramacao?> BuscarAsync(Guid instalacaoId, Guid id, CancellationToken ct)
        => db.RadarProgramacoes.SingleOrDefaultAsync(x => x.Id == id && x.InstalacaoId == instalacaoId, ct);

    public void Adicionar(RadarProgramacao programacao) => db.RadarProgramacoes.Add(programacao);
    public void Remover(RadarProgramacao programacao) => db.RadarProgramacoes.Remove(programacao);
    public Task SalvarAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
