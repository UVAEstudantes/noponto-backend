using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("rail/vehicles")]
public sealed class RailVehiclesController(IRailPublishedSnapshotProvider published) : ControllerBase
{
    /// <summary>
    /// Snapshot in-memory dos trens com posição longitudinal visualizável.
    /// Dwell, AwaitingDeparture e TerminalHold permanecem imóveis. O cliente nunca deve
    /// extrapolar após FreshUntilUtc. A geometria é obtida separadamente por PadraoVersaoId.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(RailVehiclesSnapshotDto), StatusCodes.Status200OK)]
    public ActionResult<RailVehiclesSnapshotDto> Snapshot(
        [FromQuery] Guid? linhaId = null,
        [FromQuery] Guid? sentidoId = null)
    {
        var snapshot = published.CaptureSnapshot();
        var vehicles = snapshot.PublicVehicles
            .Where(x => linhaId is null || x.LinhaId == linhaId)
            .Where(x => sentidoId is null || x.SentidoId == sentidoId)
            .Where(x => x.State is RailRunState.InSegment or RailRunState.Dwell
                or RailRunState.AwaitingDeparture or RailRunState.TerminalHold)
            .Select(RailVehicleSnapshotDto.From)
            .ToArray();
        return Ok(new RailVehiclesSnapshotDto(snapshot.GeneratedAtUtc, vehicles));
    }
}
