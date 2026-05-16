namespace NoPonto.Application.DTOs.Admin.Configuracoes;

public sealed class AdminConfiguracoesUpdateDto
{
    public AdminGpsPollingUpdateDto? GpsPolling { get; set; }
    public AdminGpsHistoricoUpdateDto? GpsHistorico { get; set; }
}

public sealed class AdminGpsPollingUpdateDto
{
    public int? IntervaloSegundos { get; set; }
    public int? TtlAtivoSegundos { get; set; }
    public int? TtlRecenteSegundos { get; set; }
    public double? DistanciaMaximaRotaMetros { get; set; }
    public bool? EnriquecerTodasLinhas { get; set; }
    public int? MaxIdadeGpsSegundos { get; set; }
}

public sealed class AdminGpsHistoricoUpdateDto
{
    public bool? Habilitado { get; set; }
    public double? DistanciaRegistroMetros { get; set; }
}
