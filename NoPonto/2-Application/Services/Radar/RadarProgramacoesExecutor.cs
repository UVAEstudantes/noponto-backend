using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Radar;

public sealed class RadarProgramacoesExecutor(TransporteDbContext db, RadarProgramacoesRepository repository,
    RadarService radar, IRadarStore store, IOptions<RadarOptions> options, TimeProvider clock)
{
    public Task<List<RadarProgramacao>> VencidasAsync(CancellationToken ct)
        => db.RadarProgramacoes.AsNoTracking().Where(x => x.Estado == "ativa"
            && x.ProximaExecucaoUtc <= clock.GetUtcNow())
            .OrderBy(x => x.ProximaExecucaoUtc).ThenBy(x => x.Id).Take(100).ToListAsync(ct);

    public async Task ExecutarAsync(Guid owner, Guid id, CancellationToken ct)
    {
        await using var transaction = await repository.BloquearInstalacaoAsync(owner, ct);
        var p = await repository.BuscarAsync(owner, id, ct);
        var now = clock.GetUtcNow();
        if (p is null || p.Estado != "ativa" || !p.ProximaExecucaoUtc.HasValue || p.ProximaExecucaoUtc > now) return;
        var occurrence = p.ProximaExecucaoUtc.Value;
        var session = SessaoId(p.Id, occurrence);
        string result;
        if (occurrence.AddHours(1) <= now) result = "perdida";
        else
        {
            try
            {
                var config = JsonSerializer.Deserialize<RadarConfigurarRequest>(p.Configuracao, RadarService.Json)
                    ?? throw new RadarException(422, "configuracao_invalida");
                await radar.ValidarAsync(config, ct);
                var response = await store.ExecutarAsync(new("scheduled-activate", owner, "",
                    p.Configuracao, SessaoId: session, MaxSessoes: options.Value.MaxSessoesAtivas,
                    OcorrenciaMs: occurrence.ToUnixTimeMilliseconds()), ct);
                if (response.Status is 429 or >= 500) throw new RadarException(503, "execucao_temporariamente_indisponivel");
                result = response.Status < 400 ? "ativada" : response.Payload;
                if (response.Status == 401) p.Estado = "erro";
            }
            catch (RadarException e) when (e.Status is 400 or 422)
            { result = e.Codigo; p.Estado = "erro"; }
        }
        db.RadarProgramacaoExecucoes.Add(new() { ProgramacaoId = id, OcorrenciaUtc = occurrence,
            SessaoId = session, Resultado = result });
        p.UltimaOcorrenciaUtc = occurrence;
        p.UltimoResultado = result;
        p.ProximaExecucaoUtc = p.Estado == "ativa" ? RadarProgramacoesService.Agenda(p).Proxima(now) : null;
        if (p.Estado == "ativa" && p.ProximaExecucaoUtc is null) p.Estado = "concluida";
        p.Revisao++;
        await repository.SalvarAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static Guid SessaoId(Guid programacao, DateTimeOffset ocorrencia)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"radar-agenda-v1:{programacao:N}:{ocorrencia.ToUnixTimeMilliseconds()}"))
            .AsSpan(0, 16));
}
