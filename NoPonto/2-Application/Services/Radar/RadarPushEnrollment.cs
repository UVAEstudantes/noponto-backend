using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

/// <summary>Enrollment iniciado no dispositivo. Id aleatório local deve existir antes da requisição.</summary>
public sealed record RadarPushEnrollmentRequest
{
    public Guid EnrollmentId { get; init; }
    public Guid ProjectId { get; init; }
    [Required, MaxLength(200)] public string ExpoPushToken { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public override string ToString() => "RadarPushEnrollmentRequest(redacted)";
}
/// <summary>Prova recebida exclusivamente no push; nunca disponível por consulta HTTP.</summary>
public sealed record RadarPushConfirmationRequest
{
    public Guid EnrollmentId { get; init; }
    [Required, RegularExpression("^[A-F0-9]{64}$")] public string Proof { get; init; } = "";
    public override string ToString() => "RadarPushConfirmationRequest(redacted)";
}
/// <summary>Estado mínimo de associação sem token ou challenge.</summary>
public sealed record RadarPushStatus(bool Registered, bool PossessionConfirmed, long Revision);

public sealed class RadarPushEnrollment(IRadarStore store, RadarTokenProtector protector, IExpoPushGateway gateway,
    RadarPushPolicy policy, IOptions<RadarPushOptions> options, TimeProvider clock, IOptions<RadarOptions> radarOptions)
{
    private async Task<RadarStoreResult> Run(string authorization, string operation, object? data, CancellationToken ct)
    {
        if (!radarOptions.Value.Enabled || !radarOptions.Value.PushEnabled) throw new RadarException(404, "push_experimental_desabilitado");
        var auth = RadarService.Parse(authorization);
        policy.RequireInstallation(auth.InstalacaoId);
        if (!policy.NetworkEnabled) throw new RadarException(404, "push_experimental_desabilitado");
        var result = await store.ExecutarAsync(auth with { Operacao = operation,
            Payload = data is null ? "" : JsonSerializer.Serialize(data) }, ct);
        if (result.Status >= 400) throw new RadarException(result.Status, result.Payload);
        return result;
    }
    public async Task IniciarAsync(string authorization, RadarPushEnrollmentRequest request, CancellationToken ct)
    {
        if (request.EnrollmentId == Guid.Empty || request.ProjectId != options.Value.ProjectId
            || !RadarPushTokens.TokenValido(request.ExpoPushToken) || request.Capabilities is null
            || request.Capabilities.Length > 1 || request.Capabilities.Any(c => c != "native-v1"))
            throw new RadarException(400, "push_enrollment_invalido");
        var proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await Run(authorization, "push-challenge", new { enrollmentId = request.EnrollmentId.ToString("D"),
            verifier = RadarTokenProtector.Fingerprint(proof), encrypted = protector.Protect(request.ExpoPushToken),
            fingerprint = RadarTokenProtector.Fingerprint(request.ExpoPushToken), maxPending = options.Value.MaxPendingReceipts,
            nativeV1 = request.Capabilities.Contains("native-v1") }, ct);
        var until = clock.GetUtcNow().AddSeconds(120);
        var message = new ExpoRadarMessage(request.ExpoPushToken, policy.Public ? "NoPonto" : "NoPonto Dev", "Confirmando este aparelho para o Radar.",
            120, until.ToUnixTimeSeconds(), new { mode = "radar-enrollment", enrollmentId = request.EnrollmentId,
                proof, projectId = request.ProjectId, validUntil = until });
        var result = await gateway.EnviarAsync(message, async token =>
        {
            try { await Run(authorization, "push-challenge-authorize", new { enrollmentId = request.EnrollmentId.ToString("D"), limit = options.Value.MaxRequestsPerMinute }, token); return true; }
            catch (RadarException e) when (e.Status is 401 or 409 or 429) { return false; }
        }, ct);
        await Run(authorization, "push-challenge-result", new { enrollmentId = request.EnrollmentId.ToString("D"),
            status = result.Status, ticket = result.TicketId ?? "" }, ct);
        if (result.Status != "ticket") throw new RadarException(503, "push_confirmacao_nao_enviada");
    }
    public async Task<RadarPushTokenResponse> ConfirmarAsync(string authorization, RadarPushConfirmationRequest request, CancellationToken ct)
    {
        if (request.EnrollmentId == Guid.Empty || request.Proof.Length != 64 || request.Proof.Any(c => !Uri.IsHexDigit(c)))
            throw new RadarException(400, "push_confirmacao_invalida");
        var result = await Run(authorization, "push-confirm", new { enrollmentId = request.EnrollmentId.ToString("D"),
            verifier = RadarTokenProtector.Fingerprint(request.Proof) }, ct);
        return new(true, long.Parse(result.Payload), policy.Public ? "public" : "experimental", true);
    }
    public async Task<RadarPushStatus> ConsultarAsync(string authorization, CancellationToken ct)
    {
        var result = await Run(authorization, "push-status", null, ct);
        return JsonSerializer.Deserialize<RadarPushStatus>(result.Payload, RadarService.Json)!;
    }
    public async Task RemoverAsync(string authorization, CancellationToken ct)
        => await Run(authorization, "push-delete", null, ct);
}
