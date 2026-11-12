namespace NoPonto.Application.Radar;

/// <summary>Previsão candidata, nunca confirmação de chegada ou presença física.</summary>
public sealed record RadarItemCandidato(string EventoId, Guid LinhaId, string CodigoLinha,
    string? VeiculoId, string TipoEvento, DateTimeOffset PrevistoEm, long EtaSegundos,
    bool Prioritaria, string Source, string Quality, DateTimeOffset? EvidenciaEm, Guid SentidoId = default,
    string? Modal = null, string? TipoRota = null, string? OperationalStatus = null, string? TrainCode = null,
    Guid? RailVehicleId = null, Guid? ExpectedRunId = null, Guid? ScheduledStopId = null,
    string? DestinationName = null, string? LineName = null)
{
    public string Confirmacao => "previsao";
}

/// <summary>Último conteúdo interno de uma sessão/revisão. Não representa envio de notificação.</summary>
public sealed record RadarConteudoCandidato(Guid SessaoId, Guid InstalacaoId, long Revisao,
    Guid ParadaId, string ParadaNome, DateTimeOffset ConsultadoEm, DateTimeOffset GeradoEm, DateTimeOffset ValidoAte,
    RadarItemCandidato[] Itens, int Adicionais, bool PresencaTerminalDisponivel = false, int LinhasAdicionais = 0);

public interface IRadarMonitorStore
{
    Task<IReadOnlyList<Guid>> ParadasAsync(int limite, CancellationToken ct);
    Task<IReadOnlyList<RadarSessaoResponse>> SessoesAsync(Guid parada, int limite, CancellationToken ct);
}

/// <summary>Fronteira interna para N5. A escrita e a leitura conferem a sessão corrente atomicamente.</summary>
public interface IRadarCandidateStore
{
    Task<bool> TentarGravarAsync(RadarConteudoCandidato candidato, CancellationToken ct);
    Task<RadarConteudoCandidato?> LerAtualAsync(Guid instalacao, CancellationToken ct);
}

public interface IRadarStopProcessor
{
    Task<RadarStopResult> ProcessarAsync(Guid parada, int limiteSessoes, CancellationToken ct);
}
public sealed record RadarStopResult(int Sessoes, int ConsultasEventos, int ConsultasEstrutura, int Produzidos, int Descartados, bool Saturado = false);
public sealed record RadarCycleResult(bool Ignorado, int Paradas, int Sessoes, int ConsultasEventos,
    int ConsultasEstrutura, int Produzidos, int Descartados, int Falhas, int Timeouts, double DuracaoMs, int Saturadas = 0);

public interface IRadarEngine
{
    Task<RadarCycleResult> ExecutarCicloAsync(CancellationToken ct);
}
