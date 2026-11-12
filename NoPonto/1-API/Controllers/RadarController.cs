using Microsoft.AspNetCore.Mvc;
using NoPonto.Application.Radar;
using NoPonto.API.Middlewares;

namespace NoPonto.API.Controllers;

[ApiController]
[Route("radar")]
[RequestSizeLimit(16_384)]
[ServiceFilter(typeof(RadarHttpFilter))]
public sealed class RadarController(IRadarService service) : ControllerBase
{
    private string Credential => Request.Headers.Authorization.ToString();

    /// <summary>Registra uma instalação Android anônima; guardar a credencial em armazenamento seguro.</summary>
    [HttpPost("instalacoes")]
    [ProducesResponseType(typeof(RadarRegistroResponse), 201)]
    public async Task<IActionResult> Registrar(CancellationToken ct)
        => CreatedAtAction(nameof(Instalacao), await service.RegistrarAsync(ct));

    /// <summary>Consulta somente os metadados da instalação autenticada.</summary>
    [HttpGet("instalacao")]
    [ProducesResponseType(typeof(RadarInstalacaoResponse), 200)]
    public async Task<IActionResult> Instalacao(CancellationToken ct)
        => Ok(await service.InstalacaoAsync(Credential, false, ct));

    /// <summary>Renova por 180 dias a credencial ainda válida, sem renovar a sessão.</summary>
    [HttpPost("instalacao/renovar")]
    public async Task<IActionResult> Renovar(CancellationToken ct)
        => Ok(await service.InstalacaoAsync(Credential, true, ct));

    /// <summary>Revoga imediatamente a credencial e remove a sessão dos índices.</summary>
    [HttpDelete("instalacao")]
    public async Task<IActionResult> Revogar(CancellationToken ct)
    {
        await service.RevogarAsync(Credential, ct);
        return NoContent();
    }

    /// <summary>Ativa uma sessão de uma hora; outra sessão ativa causa conflito.</summary>
    [HttpPost("sessao")]
    [ProducesResponseType(typeof(RadarSessaoResponse), 201)]
    public async Task<IActionResult> Ativar(RadarConfigurarRequest request, CancellationToken ct)
        => CreatedAtAction(nameof(Sessao), await service.ConfigurarAsync(Credential, request, true, ct));

    /// <summary>Consulta a sessão corrente sem estender seu prazo.</summary>
    [HttpGet("sessao")]
    [ProducesResponseType(typeof(RadarSessaoResponse), 200)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Sessao(CancellationToken ct)
    {
        var session = await service.ConsultarAsync(Credential, ct);
        return session is null ? NoContent() : Ok(session);
    }

    /// <summary>Substitui a configuração com comparação de identidade e revisão.</summary>
    [HttpPut("sessao")]
    [ProducesResponseType(typeof(RadarSessaoResponse), 200)]
    public async Task<IActionResult> Atualizar(RadarConfigurarRequest request, CancellationToken ct)
        => Ok(await service.ConfigurarAsync(Credential, request, false, ct));

    /// <summary>Encerra idempotentemente a sessão indicada da própria instalação.</summary>
    [HttpDelete("sessao/{sessaoId:guid}")]
    public async Task<IActionResult> Encerrar(Guid sessaoId, CancellationToken ct)
    {
        await service.EncerrarAsync(Credential, sessaoId, ct);
        return NoContent();
    }
}
