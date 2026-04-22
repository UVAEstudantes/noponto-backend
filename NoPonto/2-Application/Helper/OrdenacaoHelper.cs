// NoPonto.Application.Helpers/OrdenacaoHelper.cs
using NoPonto.Application.DTOs.Pois;

namespace NoPonto.Application.Helpers;

public static class OrdenacaoHelper
{
    /// <summary>
    /// Aplica ordenação hierárquica a partir de uma string como "-prioridade,ordemParada,nome".
    /// Campos suportados: prioridade, ordemParada, nome, categoria, distanciaMetros.
    /// Prefixo "-" = decrescente.
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
            var desc       = campo.StartsWith('-');
            var nomeCampo  = desc ? campo[1..] : campo;

            Func<PoiPorItinerarioDTO, object> seletor = nomeCampo.ToLowerInvariant() switch
            {
                "prioridade"      => p => p.Prioridade,
                "ordemparada"     => p => p.OrdemParada,
                "nome"            => p => p.Nome,
                "categoria"       => p => p.Categoria,
                "distanciametros" => p => p.DistanciaMetros,
                _                 => p => p.OrdemParada   // fallback seguro
            };

            ordenado = ordenado is null
                ? (desc ? fonte.OrderByDescending(seletor) : fonte.OrderBy(seletor))
                : (desc ? ordenado.ThenByDescending(seletor) : ordenado.ThenBy(seletor));
        }

        return ordenado ?? fonte.OrderBy(p => p.OrdemParada);
    }
}