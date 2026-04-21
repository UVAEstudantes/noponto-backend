namespace NoPonto.Application.DTOs.Compartilhado;

/// <summary>
/// Representa uma resposta padronizada de erro da API.
/// </summary>
public sealed class ErroRespostaDTO
{
    /// <summary>
    /// Código HTTP retornado na resposta.
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Mensagem amigável de erro.
    /// </summary>
    public string Mensagem { get; set; } = null!;

    /// <summary>
    /// Data e hora UTC da ocorrência do erro.
    /// </summary>
    public DateTime Timestamp { get; set; }
}
