namespace NoPonto.Application.DTOs.Admin.Sistema;

public sealed class AdminSistemaMetricasDto
{
    public AdminProcessoMetricasDto ProcessoAtual { get; set; } = null!;
    public AdminRedisMetricasDto? RedisInfo { get; set; }
    public IReadOnlyList<AdminTabelaMetricasDto> BancoDados { get; set; } = [];
}

public sealed class AdminProcessoMetricasDto
{
    public double CpuTotalSegundos { get; set; }
    public double CpuPercentual { get; set; }
    public long MemoriaWorkingSetBytes { get; set; }
    public long MemoriaPrivadaBytes { get; set; }
}

public sealed class AdminRedisMetricasDto
{
    public Dictionary<string, string> Memory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdminTabelaMetricasDto
{
    public string Tabela { get; set; } = null!;
    public string Tamanho { get; set; } = null!;
    public long Registros { get; set; }
}
