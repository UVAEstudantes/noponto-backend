using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoPonto.Application.Radar;

/// <summary>Somente sandbox local. Um token não comprova posse do dispositivo.</summary>
public sealed record RadarPushTokenRequest
{
    [Required, MaxLength(200)] public string ExpoPushToken { get; init; } = "";
    [Required, RegularExpression("^android$")] public string Platform { get; init; } = "android";
    public override string ToString() => "RadarPushTokenRequest(redacted)";
}
/// <summary>Metadados sem token ou hash; não confirma posse nem habilita produção.</summary>
public sealed record RadarPushTokenResponse(bool Registered, long Revision,
    string Mode = "controlled_test_only", bool PossessionConfirmed = false);

public sealed class RadarPushOptions
{
    public bool ControlledTestsEnabled { get; set; }
    public bool ExperimentalEnabled { get; set; }
    public bool PublicEnabled { get; set; }
    public bool ExternalTransportEnabled { get; set; }
    public string EncryptionKeyFile { get; set; } = "";
    public string ExpoAccessTokenFile { get; set; } = "";
    public Guid ProjectId { get; set; }
    public Guid[] AllowedInstallationIds { get; set; } = [];
    public int MaxRequestsPerMinute { get; set; } = 60;
    public int MaxPendingReceipts { get; set; } = 256;
    public int MaxAttempts { get; set; } = 3;
    public int MaxConcurrency { get; set; } = 2;
    public int MinIntervalSeconds { get; set; } = 30;
}

// Internos ao backend; nunca devolver snapshots ou resultados do provedor em endpoints.
public sealed record RadarPushSnapshot(RadarConteudoCandidato Candidate, RadarSessaoResponse Session,
    string RawCandidate, string EncryptedToken, string Fingerprint, long TokenRevision,
    RadarConteudoCandidato? Previous, long PreviousTokenRevision, string RawPrevious = "")
{
    public override string ToString() => "RadarPushSnapshot(redacted)";
}
public sealed record RadarPushReceipt(string Id, Guid Owner, string Fingerprint, long TokenRevision);
public sealed record ExpoResult(string Status, string? TicketId = null);
public sealed record ExpoReceiptResult(string Id, string Status);

/// <summary>Mensagem Expo compacta. O destinatário é sensível e não deve aparecer em logs.</summary>
public sealed record ExpoRadarMessage(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("ttl")] int Ttl,
    [property: JsonPropertyName("expiration")] long Expiration,
    [property: JsonPropertyName("data")] object Data)
{
    [JsonPropertyName("priority")] public string Priority => "normal";
    [JsonPropertyName("channelId")] public string ChannelId => "radar-espera-updates-v1";
    [JsonPropertyName("tag")] public string Tag => "noponto-radar-espera";
    [JsonPropertyName("collapseId")] public string CollapseId => "noponto-radar-espera";
    public override string ToString() => "ExpoRadarMessage(redacted)";
}

public interface IRadarPushTokens
{
    Task<RadarPushTokenResponse> AssociarAsync(string authorization, RadarPushTokenRequest request, CancellationToken ct);
    Task RemoverAsync(string authorization, CancellationToken ct);
}
public interface IRadarPushStore
{
    Task<RadarPushSnapshot?> CarregarAsync(Guid owner, CancellationToken ct);
    Task<bool> ClaimAsync(RadarPushSnapshot snapshot, string claim, CancellationToken ct);
    Task<bool> AutorizarAsync(RadarPushSnapshot snapshot, string claim, CancellationToken ct);
    Task FinalizarAsync(RadarPushSnapshot snapshot, string claim, ExpoResult result, CancellationToken ct);
    Task<IReadOnlyList<RadarPushReceipt>> ReceiptsAsync(string claim, CancellationToken ct);
    Task ResolverReceiptAsync(RadarPushReceipt receipt, string status, CancellationToken ct);
}
public interface IExpoPushGateway
{
    Task<ExpoResult> EnviarAsync(ExpoRadarMessage message, Func<CancellationToken, Task<bool>> authorize, CancellationToken ct);
    Task<IReadOnlyList<ExpoReceiptResult>> ReceiptsAsync(IReadOnlyList<string> ids, CancellationToken ct);
}
