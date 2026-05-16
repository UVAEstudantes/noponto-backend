namespace NoPonto.Application.DTOs.Admin.Configuracoes;

public sealed class AdminConfiguracoesDto
{
    public AdminGpsPollingDto GpsPolling { get; set; } = new();
    public AdminGpsHistoricoDto GpsHistorico { get; set; } = new();
}

public sealed class AdminGpsPollingDto
{
    public int IntervaloSegundos { get; set; }
    public int TtlAtivoSegundos { get; set; }
    public int TtlRecenteSegundos { get; set; }
    public double DistanciaMaximaRotaMetros { get; set; }
    public bool EnriquecerTodasLinhas { get; set; }
    public int MaxIdadeGpsSegundos { get; set; }
}

public sealed class AdminGpsHistoricoDto
{
    public bool Habilitado { get; set; }
    public double DistanciaRegistroMetros { get; set; }
}
