using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

internal static class GtfsLinhaModal
{
    // Origem realtime, agência e código da linha não comprovam tipo comercial BRT.
    internal static string TipoRota(string routeType) => routeType switch
    {
        "702" => "brt",
        "200" => "frescao",
        _ => "regular"
    };

    internal static async Task<Guid> ParaNovaLinhaAsync(
        TransporteDbContext db, string routeType, Guid onibusId, CancellationToken ct)
    {
        if (routeType != "702") return onibusId;
        return (await db.Modais.SingleOrDefaultAsync(x => x.Nome == "BRT", ct))?.Id
            ?? throw new InvalidDataException("Modal BRT persistido não encontrado para route_type 702.");
    }
}
