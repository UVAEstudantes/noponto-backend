namespace NoPonto.Application.DTOs.Admin.Sistema;

public sealed class AdminContainerDto
{
    public string Id { get; set; } = null!;
    public string Nome { get; set; } = null!;
    public string Imagem { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Estado { get; set; } = null!;
    public TimeSpan? Uptime { get; set; }
    public IReadOnlyList<string> Portas { get; set; } = [];
}
