using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.Services;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("relacionamento")]
public class RelacionamentoController : ControllerBase
{
    private readonly RelacionarParadasJob _job;

    public RelacionamentoController(RelacionarParadasJob job)
    {
        _job = job;
    }

    [HttpPost("paradas-itinerarios")]
    public async Task<IActionResult> ExecutarRelacionamento(CancellationToken cancellationToken)
    {
        await _job.ExecutarAsync(cancellationToken);

        return Ok(new
        {
            mensagem = "Relacionamento executado com sucesso."
        });
    }
}
