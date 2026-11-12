using Microsoft.AspNetCore.Mvc.ApiExplorer;

namespace NoPonto.API;

internal static class SwaggerGroups
{
    internal static IList<string> Tags(ApiDescription api)
    {
        if (string.Equals(api.GroupName, "admin", StringComparison.OrdinalIgnoreCase)) return ["Administração"];
        api.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        controller ??= "";
        if (controller.StartsWith("Radar", StringComparison.Ordinal)) return ["Radar"];
        return controller switch
        {
            "Modais" or "EstruturaLinhas" or "EstruturaSentidos" or "PadroesVersoes" => ["Catálogo"],
            "EstruturaParadas" or "EventosParada" => ["Paradas e Estações"],
            "Veiculos" or "UnifiedLocation" => ["Veículos"],
            "RailVehicles" or "TrensRjRefresh" => ["Trens"],
            "Tarifas" or "FormasPagamento" => ["Tarifas e Pagamentos"],
            _ => ["Sistema e Compatibilidade"]
        };
    }
}
