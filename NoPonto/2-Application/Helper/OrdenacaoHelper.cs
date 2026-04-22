using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Helpers;

public static class OrdenacaoHelper
{
    /// <summary>
    /// Aplica ordenação hierárquica em <see cref="PoiPorItinerarioDTO"/>.
    /// Formato: campos separados por vírgula, prefixo "-" para decrescente.
    /// Campos: prioridade | ordemParada | nome | categoria | distanciaMetros
    /// Exemplo: "ordemParada,-prioridade"
    /// </summary>
    public static IEnumerable<PoiPorItinerarioDTO> Ordenar(
        IEnumerable<PoiPorItinerarioDTO> fonte,
        string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
            return fonte.OrderBy(p => p.OrdemParada).ThenBy(p => p.Prioridade);

        var campos = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IOrderedEnumerable<PoiPorItinerarioDTO>? ordenado = null;

        foreach (var campo in campos)
        {
            var desc      = campo.StartsWith('-');
            var nomeCampo = desc ? campo[1..] : campo;

            Func<PoiPorItinerarioDTO, object> seletor = nomeCampo.ToLowerInvariant() switch
            {
                "prioridade"      => p => (object)p.Prioridade,
                "ordemparada"     => p => (object)p.OrdemParada,
                "nome"            => p => (object)p.Nome,
                "categoria"       => p => (object)p.Categoria,
                "distanciametros" => p => (object)p.DistanciaMetros,
                _                 => p => (object)p.OrdemParada
            };

            ordenado = ordenado is null
                ? (desc ? fonte.OrderByDescending(seletor) : fonte.OrderBy(seletor))
                : (desc ? ordenado.ThenByDescending(seletor) : ordenado.ThenBy(seletor));
        }

        return ordenado ?? fonte.OrderBy(p => p.OrdemParada);
    }

    /// <summary>
    /// Aplica ordenação em <see cref="PoiContagemPorItinerarioDTO"/>.
    /// Campos: totalPois | nomeLinha
    /// Prefixo "-" = decrescente. Exemplo: "-totalPois,nomeLinha"
    /// </summary>
    public static IEnumerable<PoiContagemPorItinerarioDTO> OrdenarContagem(
        IEnumerable<PoiContagemPorItinerarioDTO> fonte,
        string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
            return fonte.OrderByDescending(p => p.TotalPois);

        var campos = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IOrderedEnumerable<PoiContagemPorItinerarioDTO>? ordenado = null;

        foreach (var campo in campos)
        {
            var desc      = campo.StartsWith('-');
            var nomeCampo = desc ? campo[1..] : campo;

            Func<PoiContagemPorItinerarioDTO, object> seletor = nomeCampo.ToLowerInvariant() switch
            {
                "totalpois" => p => (object)p.TotalPois,
                "nomelinha" => p => (object)p.NomeLinha,
                _           => p => (object)p.TotalPois
            };

            ordenado = ordenado is null
                ? (desc ? fonte.OrderByDescending(seletor) : fonte.OrderBy(seletor))
                : (desc ? ordenado.ThenByDescending(seletor) : ordenado.ThenBy(seletor));
        }

        return ordenado ?? fonte.OrderByDescending(p => p.TotalPois);
    }
}