using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.Application.Radar;

public sealed class RadarOptions
{
    public bool Enabled { get; set; } // opt-in: nenhuma atividade em background
    public int MaxLinhas { get; set; } = 20;
    public int MaxContextos { get; set; } = 40;
    public int MaxRegistrosPorDia { get; set; } = 500;
    public int MaxSessoesAtivas { get; set; } = 2000;
}

public sealed class RadarException(int status, string codigo) : Exception(codigo)
{
    public int Status { get; } = status;
    public string Codigo { get; } = codigo;
}

// Somente dados internos; nunca serializar este comando numa resposta ou log.
public sealed record RadarCommand(string Operacao, Guid InstalacaoId, string Verificador,
    string Payload = "", long Revisao = 0, Guid? SessaoId = null, int MaxRegistros = 500, int MaxSessoes = 2000);
public sealed record RadarStoreResult(int Status, string Payload);
internal sealed record RadarStoredSession(Guid SessaoId, Guid InstalacaoId, Guid ParadaId,
    RadarContexto[] Contextos, Guid[] LinhasSelecionadas, Guid[] LinhasPrioritarias,
    int AntecedenciaMinutos, string Estado, long CriadaEmMs, long ExpiraEmMs, long Revisao)
{
    public RadarSessaoResponse Response() => new(SessaoId, InstalacaoId, ParadaId, Contextos,
        LinhasSelecionadas, LinhasPrioritarias, AntecedenciaMinutos, Estado,
        DateTimeOffset.FromUnixTimeMilliseconds(CriadaEmMs), DateTimeOffset.FromUnixTimeMilliseconds(ExpiraEmMs), Revisao);
}
public interface IRadarStore
{
    Task<RadarStoreResult> ExecutarAsync(RadarCommand command, CancellationToken ct);
}

public interface IRadarService
{
    Task<RadarRegistroResponse> RegistrarAsync(CancellationToken ct);
    Task<RadarInstalacaoResponse> InstalacaoAsync(string authorization, bool renovar, CancellationToken ct);
    Task<RadarSessaoResponse?> ConsultarAsync(string authorization, CancellationToken ct);
    Task<RadarSessaoResponse> ConfigurarAsync(string authorization, RadarConfigurarRequest request, bool ativar, CancellationToken ct);
    Task EncerrarAsync(string authorization, Guid sessaoId, CancellationToken ct);
    Task RevogarAsync(string authorization, CancellationToken ct);
}

public sealed class RadarService(IRadarStore store, IEstruturaLeituraV2Repository catalogo,
    IOptions<RadarOptions> options) : IRadarService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RadarRegistroResponse> RegistrarAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var result = await Call(new("register", id, Hash(secret), MaxRegistros: options.Value.MaxRegistrosPorDia), ct);
        return new(id, $"{id:N}.{secret}", DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(result)));
    }

    public async Task<RadarInstalacaoResponse> InstalacaoAsync(string authorization, bool renovar, CancellationToken ct)
    {
        var auth = Parse(authorization);
        var expiry = await Call(auth with { Operacao = renovar ? "renew" : "installation" }, ct);
        return new(auth.InstalacaoId, DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(expiry)));
    }

    public async Task<RadarSessaoResponse?> ConsultarAsync(string authorization, CancellationToken ct)
    {
        var value = await Call(Parse(authorization) with { Operacao = "get" }, ct);
        return value.Length == 0 ? null : JsonSerializer.Deserialize<RadarStoredSession>(value, Json)!.Response();
    }

    public async Task<RadarSessaoResponse> ConfigurarAsync(string authorization, RadarConfigurarRequest request,
        bool ativar, CancellationToken ct)
    {
        var auth = Parse(authorization);
        // Autentica antes de consultar o catálogo; o script autentica novamente na escrita.
        await Call(auth with { Operacao = "installation" }, ct);
        await ValidarAsync(request, ct);
        if (ativar ? request.RevisaoEsperada != 0 || request.SessaoId.HasValue
            : request.RevisaoEsperada < 1 || request.SessaoId.GetValueOrDefault() == Guid.Empty)
            throw new RadarException(400, "revisao_invalida");
        var payload = JsonSerializer.Serialize(request, Json);
        var result = await Call(auth with { Operacao = ativar ? "activate" : "update", Payload = payload,
            Revisao = request.RevisaoEsperada, SessaoId = ativar ? Guid.NewGuid() : request.SessaoId,
            MaxSessoes = options.Value.MaxSessoesAtivas }, ct);
        return JsonSerializer.Deserialize<RadarStoredSession>(result, Json)!.Response();
    }

    public async Task EncerrarAsync(string authorization, Guid sessaoId, CancellationToken ct)
        => await Call(Parse(authorization) with { Operacao = "end", SessaoId = sessaoId }, ct);
    public async Task RevogarAsync(string authorization, CancellationToken ct)
        => await Call(Parse(authorization) with { Operacao = "revoke" }, ct);

    internal async Task ValidarAsync(RadarConfigurarRequest r, CancellationToken ct)
    {
        var limits = options.Value;
        if (r.ParadaId == Guid.Empty || r.AntecedenciaMinutos is < 2 or > 15
            || r.Contextos is null || r.LinhasSelecionadas is null || r.LinhasPrioritarias is null
            || r.LinhasSelecionadas.Length < 1 || r.LinhasSelecionadas.Length > limits.MaxLinhas
            || r.Contextos.Length < 1 || r.Contextos.Length > limits.MaxContextos
            || r.LinhasPrioritarias.Length > limits.MaxLinhas
            || r.LinhasSelecionadas.Contains(Guid.Empty) || r.LinhasPrioritarias.Contains(Guid.Empty)
            || r.LinhasSelecionadas.Distinct().Count() != r.LinhasSelecionadas.Length
            || r.LinhasPrioritarias.Distinct().Count() != r.LinhasPrioritarias.Length
            || r.Contextos.Any(c => c is null || c.LinhaId == Guid.Empty || c.SentidoId == Guid.Empty
                || c.PadraoOperacionalId == Guid.Empty || c.PadraoVersaoId == Guid.Empty)
            || r.Contextos.Select(c => c.PadraoVersaoId).Distinct().Count() != r.Contextos.Length)
            throw new RadarException(400, "limites_ou_identificadores_invalidos");
        if (r.LinhasPrioritarias.Except(r.LinhasSelecionadas).Any())
            throw new RadarException(422, "prioridade_nao_selecionada");
        if (!r.Contextos.Select(c => c.LinhaId).ToHashSet().SetEquals(r.LinhasSelecionadas))
            throw new RadarException(422, "contexto_incompativel");
        if (await catalogo.BuscarParadaAsync(r.ParadaId, ct) is null)
            throw new RadarException(422, "parada_inexistente");
        foreach (var c in r.Contextos)
        {
            var itinerary = await catalogo.BuscarItinerarioAsync(c.PadraoVersaoId, ct);
            if (itinerary is null || !itinerary.EhVersaoAtual || itinerary.LinhaId != c.LinhaId
                || itinerary.SentidoId != c.SentidoId || itinerary.PadraoOperacionalId != c.PadraoOperacionalId
                || !itinerary.Ocorrencias.Any(p => p.ParadaId == r.ParadaId))
                throw new RadarException(422, "contexto_incompativel_ou_desatualizado");
        }
    }

    private async Task<string> Call(RadarCommand command, CancellationToken ct)
    {
        var result = await store.ExecutarAsync(command, ct);
        if (result.Status >= 400) throw new RadarException(result.Status, result.Payload);
        return result.Payload;
    }

    internal static RadarCommand Parse(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new RadarException(401, "credencial_invalida");
        var token = authorization[7..];
        if (token.Length != 97 || token[32] != '.' || !Guid.TryParseExact(token[..32], "N", out var id)
            || token[33..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new RadarException(401, "credencial_invalida");
        return new("installation", id, Hash(token[33..]));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
}
