using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Radar;

public sealed record RadarProgramacaoRequest
{
    [Required, StringLength(80, MinimumLength = 1)] public string Nome { get; init; } = "";
    [Required, StringLength(100)] public string Fuso { get; init; } = "";
    public TimeOnly HoraLocal { get; init; }
    public DateOnly? DataLocal { get; init; }
    [Required, MaxLength(7)] public DayOfWeek[] Dias { get; init; } = [];
    public bool Ativa { get; init; } = true;
    public long RevisaoEsperada { get; init; }
    [Required] public RadarConfigurarRequest Configuracao { get; init; } = new();
}

public sealed record RadarProgramacaoResponse(Guid Id, string Nome, string Fuso, TimeOnly HoraLocal,
    DateOnly? DataLocal, DayOfWeek[] Dias, string Estado, RadarConfigurarRequest Configuracao,
    DateTimeOffset? ProximaExecucaoUtc, DateTimeOffset? UltimaOcorrenciaUtc, string? UltimoResultado, long Revisao)
{
    internal static RadarProgramacaoResponse From(RadarProgramacao p) => new(p.Id, p.Nome, p.Fuso,
        p.HoraLocal, p.DataLocal, p.Dias.Select(x => (DayOfWeek)x).ToArray(), p.Estado,
        JsonSerializer.Deserialize<RadarConfigurarRequest>(p.Configuracao, RadarService.Json)!,
        p.ProximaExecucaoUtc, p.UltimaOcorrenciaUtc, p.UltimoResultado, p.Revisao);
}

public sealed class RadarProgramacoesService(IRadarService auth, RadarService radar,
    RadarProgramacoesRepository repository, TimeProvider clock)
{
    public async Task<RadarProgramacaoResponse> BuscarAsync(string credential, Guid id, CancellationToken ct)
    {
        var owner = await auth.InstalacaoAsync(credential, false, ct);
        return RadarProgramacaoResponse.From(await repository.BuscarAsync(owner.InstalacaoId, id, ct)
            ?? throw new RadarException(404, "programacao_inexistente"));
    }

    public async Task<RadarProgramacaoResponse[]> ListarAsync(string credential, CancellationToken ct)
    {
        var owner = await auth.InstalacaoAsync(credential, false, ct);
        return (await repository.ListarAsync(owner.InstalacaoId, ct)).Select(RadarProgramacaoResponse.From).ToArray();
    }

    public async Task<RadarProgramacaoResponse> SalvarAsync(string credential, Guid? id,
        RadarProgramacaoRequest r, CancellationToken ct)
    {
        var owner = await auth.InstalacaoAsync(credential, false, ct);
        if (string.IsNullOrWhiteSpace(r.Nome) || r.Nome.Length > 80 || r.Dias is null
            || r.Dias.Length > 7 || r.Dias.Distinct().Count() != r.Dias.Length || r.Configuracao is null
            || r.Configuracao.SessaoId.HasValue || r.Configuracao.RevisaoEsperada != 0
            || (id.HasValue ? r.RevisaoEsperada < 1 : r.RevisaoEsperada != 0))
            throw new RadarException(400, "programacao_invalida");
        var agenda = new RadarAgendaHorario(r.Fuso, r.HoraLocal, r.DataLocal, r.Dias);
        var now = clock.GetUtcNow();
        var next = agenda.Proxima(now);
        if (r.Ativa && next is null) throw new RadarException(422, "horario_sem_ocorrencia_futura");
        await radar.ValidarAsync(r.Configuracao, ct);
        await using var transaction = await repository.BloquearInstalacaoAsync(owner.InstalacaoId, ct);
        var p = id.HasValue ? await repository.BuscarAsync(owner.InstalacaoId, id.Value, ct)
            ?? throw new RadarException(404, "programacao_inexistente")
            : new RadarProgramacao { Id = Guid.NewGuid(), InstalacaoId = owner.InstalacaoId };
        if (id.HasValue && p.Revisao != r.RevisaoEsperada)
            throw new RadarException(409, "revisao_desatualizada");
        var existentes = await repository.ListarAsync(owner.InstalacaoId, ct);
        if (!id.HasValue && existentes.Count >= 20) throw new RadarException(422, "limite_de_programacoes");
        if (r.Ativa && existentes.Any(x => x.Id != p.Id && x.Estado == "ativa"
            && RadarAgendaConflitos.Sobrepoe(agenda, Agenda(x), now)))
            throw new RadarException(409, "horarios_sobrepostos");
        p.Nome = r.Nome.Trim();
        p.Fuso = r.Fuso;
        p.HoraLocal = r.HoraLocal;
        p.DataLocal = r.DataLocal;
        p.Dias = r.Dias.Select(x => (int)x).ToArray();
        p.Configuracao = JsonSerializer.Serialize(r.Configuracao, RadarService.Json);
        p.Estado = r.Ativa ? "ativa" : "pausada";
        p.ProximaExecucaoUtc = r.Ativa ? next : null;
        if (id.HasValue) p.Revisao++;
        else repository.Adicionar(p);
        await repository.SalvarAsync(ct);
        await transaction.CommitAsync(ct);
        return RadarProgramacaoResponse.From(p);
    }

    public async Task RemoverAsync(string credential, Guid id, long revisao, CancellationToken ct)
    {
        var owner = await auth.InstalacaoAsync(credential, false, ct);
        await using var transaction = await repository.BloquearInstalacaoAsync(owner.InstalacaoId, ct);
        var p = await repository.BuscarAsync(owner.InstalacaoId, id, ct);
        if (p is null) return;
        if (p.Revisao != revisao) throw new RadarException(409, "revisao_desatualizada");
        repository.Remover(p);
        await repository.SalvarAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static RadarAgendaHorario Agenda(RadarProgramacao p)
        => new(p.Fuso, p.HoraLocal, p.DataLocal, p.Dias.Select(x => (DayOfWeek)x).ToArray());
}
