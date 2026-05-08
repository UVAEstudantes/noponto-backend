namespace NoPonto.Application.DTOs.Compartilhado;

/// <summary>
/// Representa uma resposta paginada para endpoints de consulta.
/// </summary>
/// <typeparam name="T">Tipo do item retornado na coleção.</typeparam>
public sealed class PaginacaoRespostaDTO<T>
{
    /// <summary>
    /// Página atual (inicia em 1).
    /// </summary>
    public int Pagina { get; set; }

    /// <summary>
    /// Quantidade de registros por página.
    /// </summary>
    public int TamanhoPagina { get; set; }

    /// <summary>
    /// Total de registros encontrados para a consulta.
    /// </summary>
    public int TotalRegistros { get; set; }

    /// <summary>
    /// Total de páginas disponíveis para a consulta.
    /// </summary>
    public int TotalPaginas { get; set; }

    /// <summary>
    /// Registros retornados na página atual.
    /// </summary>
    public IReadOnlyList<T> Itens { get; set; } = [];
}
