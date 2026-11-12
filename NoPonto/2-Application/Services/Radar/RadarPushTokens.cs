using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.Radar;

/// <summary>Chave persistente externa nos modos de rede; chave efêmera somente no sandbox.</summary>
public sealed class RadarTokenProtector : IDisposable
{
    private readonly byte[] key;
    public RadarTokenProtector(IOptions<RadarPushOptions>? options = null)
    {
        if (options?.Value is not { } settings || !settings.ExperimentalEnabled && !settings.PublicEnabled)
        { key = RandomNumberGenerator.GetBytes(32); return; }
        try
        {
            key = Convert.FromBase64String(File.ReadAllText(settings.EncryptionKeyFile).Trim());
            if (key.Length != 32) throw new CryptographicException();
        }
        catch { throw new InvalidOperationException("Chave protegida do Radar indisponível ou inválida."); }
    }
    public string Protect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, encrypted, tag);
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(encrypted).ToArray());
    }
    public string Unprotect(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 29) throw new CryptographicException();
            var plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            var result = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        { throw new RadarException(409, "push_token_exige_novo_cadastro"); }
    }
    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

/// <summary>Rede pública opt-in em Production; experimento restrito por instalação. Enrollment continua obrigatório.</summary>
public sealed class RadarPushPolicy(IHostEnvironment environment, IOptions<RadarPushOptions> options)
{
    public bool Sandbox => environment.IsEnvironment("Testing") && options.Value.ControlledTestsEnabled;
    private bool TransportConfigured => options.Value.ExternalTransportEnabled
        && options.Value.ProjectId != Guid.Empty && !string.IsNullOrWhiteSpace(options.Value.EncryptionKeyFile);
    public bool Public => environment.IsProduction() && options.Value.PublicEnabled
        && !options.Value.ExperimentalEnabled && TransportConfigured;
    public bool Experimental => !options.Value.PublicEnabled && options.Value.ExperimentalEnabled && options.Value.ExternalTransportEnabled
        && options.Value.ProjectId != Guid.Empty && options.Value.AllowedInstallationIds.Length is > 0 and <= 100
        && !string.IsNullOrWhiteSpace(options.Value.EncryptionKeyFile);
    public bool NetworkEnabled => Public || Experimental;
    public bool Enabled => Sandbox || NetworkEnabled;
    public bool Allows(Guid owner) => owner != Guid.Empty && (Sandbox || Public
        || Experimental && options.Value.AllowedInstallationIds.Contains(owner));
    public void RequireInstallation(Guid owner) { if (!Allows(owner)) throw new RadarException(403, "instalacao_nao_autorizada_para_push"); }
    public void RequireSandbox() { if (!Sandbox) throw new RadarException(404, "push_publico_bloqueado"); }
}

/// <summary>Capacidade efêmera acessível somente ao harness em processo, nunca publicada por endpoint/log.</summary>
public sealed class RadarPushTestAccess
{
    internal string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Accepts(string value) => value.Length == Value.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value), Encoding.ASCII.GetBytes(Value));
}

public sealed partial class RadarPushTokens(IRadarStore store, RadarTokenProtector protector,
    RadarPushPolicy policy) : IRadarPushTokens
{
    [GeneratedRegex(@"\A(ExpoPushToken|ExponentPushToken)\[[A-Za-z0-9_-]{1,160}\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
    public static bool TokenValido(string? token) => token is { Length: > 0 and <= 200 } && Format().IsMatch(token);

    public async Task<RadarPushTokenResponse> AssociarAsync(string authorization, RadarPushTokenRequest request, CancellationToken ct)
    {
        policy.RequireSandbox();
        var auth = RadarService.Parse(authorization);
        if (request.Platform != "android" || string.IsNullOrWhiteSpace(request.ExpoPushToken) || request.ExpoPushToken.Length > 200 || !Format().IsMatch(request.ExpoPushToken))
            throw new RadarException(400, "push_token_invalido");
        var result = await store.ExecutarAsync(auth with { Operacao = "push-put", Payload = JsonSerializer.Serialize(new
        { encrypted = protector.Protect(request.ExpoPushToken), fingerprint = RadarTokenProtector.Fingerprint(request.ExpoPushToken) }) }, ct);
        if (result.Status >= 400) throw new RadarException(result.Status, result.Payload);
        return new(true, long.Parse(result.Payload));
    }
    public async Task RemoverAsync(string authorization, CancellationToken ct)
    {
        policy.RequireSandbox();
        var result = await store.ExecutarAsync(RadarService.Parse(authorization) with { Operacao = "push-delete" }, ct);
        if (result.Status >= 400) throw new RadarException(result.Status, result.Payload);
    }
}
