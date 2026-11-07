using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.EstruturaV2;

namespace NoPonto.Application.Radar;

public interface IRadarPushContexts { Task<bool> ValidarAsync(RadarSessaoResponse session, CancellationToken ct); }

/// <summary>Cache limitado por escopo/batch. Serializa o DbContext e revalida vínculos após no máximo 1s.</summary>
public sealed class RadarPushContexts(IEstruturaLeituraV2Repository catalog, TimeProvider clock) : IRadarPushContexts
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(Guid Stop, RadarContexto Context), (DateTimeOffset Until, bool Valid)> cache = [];
    public async Task<bool> ValidarAsync(RadarSessaoResponse session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var context in session.Contextos)
            {
                var key = (session.ParadaId, context);
                var now = clock.GetUtcNow();
                if (!cache.TryGetValue(key, out var entry) || entry.Until <= now)
                {
                    var route = await catalog.BuscarItinerarioAsync(context.PadraoVersaoId, ct);
                    var valid = route is { EhVersaoAtual: true } && route.LinhaId == context.LinhaId
                        && route.SentidoId == context.SentidoId && route.PadraoOperacionalId == context.PadraoOperacionalId
                        && route.PadraoVersaoId == context.PadraoVersaoId && route.Ocorrencias.Any(o => o.ParadaId == session.ParadaId);
                    if (cache.Count >= 200) cache.Clear();
                    entry = (now.AddSeconds(1), valid);
                    cache[key] = entry;
                }
                if (!entry.Valid) return false;
            }
            return session.Contextos.Length > 0;
        }
        finally { gate.Release(); }
    }
}

public static class RadarPushContent
{
    public static bool Relevante(RadarConteudoCandidato current, RadarConteudoCandidato? previous, DateTimeOffset now)
    {
        if (previous is null) return current.Itens.Length > 0;
        if (current.SessaoId != previous.SessaoId || current.Revisao != previous.Revisao || current.ParadaId != previous.ParadaId) return true;
        if (current.Itens.Length != previous.Itens.Length) return true;
        foreach (var item in current.Itens)
        {
            var old = previous.Itens.FirstOrDefault(i => i.EventoId == item.EventoId);
            if (old is null || old.VeiculoId != item.VeiculoId || old.TipoEvento != item.TipoEvento || old.Prioritaria != item.Prioritaria
                || old.Source != item.Source || old.Quality != item.Quality || Math.Abs((old.PrevistoEm - item.PrevistoEm).TotalSeconds) >= 60) return true;
            foreach (var threshold in new[] { 300, 120, 60 })
                if ((old.PrevistoEm - previous.GeradoEm).TotalSeconds > threshold && (item.PrevistoEm - now).TotalSeconds <= threshold) return true;
        }
        return false;
    }

    public static ExpoRadarMessage? Criar(RadarPushSnapshot snapshot, string token, DateTimeOffset now)
    {
        var c = snapshot.Candidate;
        if (c.GeradoEm > now || c.ValidoAte <= now || c.Itens.Length > 3 || c.Itens.Any(i => i.PrevistoEm <= now)) return null;
        var until = new[] { c.ValidoAte, snapshot.Session.ExpiraEm, now.AddSeconds(30) }
            .Concat(c.Itens.Select(i => i.PrevistoEm)).Min();
        var ttl = (int)Math.Floor((until - now).TotalSeconds);
        if (ttl < 1) return null;
        var context = string.Join("|", snapshot.Session.Contextos.OrderBy(x => x.PadraoVersaoId)
            .Select(x => $"{x.LinhaId:N}:{x.SentidoId:N}:{x.PadraoOperacionalId:N}:{x.PadraoVersaoId:N}"));
        var lines = c.Itens.Length == 0 ? "Sem chegadas na janela. Consulte o aplicativo." : string.Join("; ", c.Itens.Select(i =>
            $"{Safe(i.CodigoLinha, 12)} {(i.TipoEvento == "DEPARTURE" ? "saída prevista" : "chegada prevista")} em {(int)Math.Ceiling((i.PrevistoEm - now).TotalMinutes)} min{(i.Prioritaria ? " (prioritária)" : "")}"));
        var result = new ExpoRadarMessage(token, "Radar · " + Safe(c.ParadaNome, 64),
            $"{lines}. Atualizado {now:HH:mm:ss} UTC; previsão válida até {until:HH:mm:ss} UTC.", ttl, until.ToUnixTimeSeconds(),
            new { mode = "remote-radar", sessionId = c.SessaoId, revision = c.Revisao, paradaId = c.ParadaId,
                contextKey = RadarTokenProtector.Fingerprint(context), validUntil = until });
        return JsonSerializer.SerializeToUtf8Bytes(result).Length <= 3500 ? result : null;
    }
    private static string Safe(string value, int max) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());
}

/// <summary>Sem worker/autodispatch: somente chamado explicitamente em testes controlados nesta etapa.</summary>
public sealed class RadarPushCoordinator(IRadarPushStore store, IExpoPushGateway gateway, RadarTokenProtector protector,
    IRadarPushContexts contexts, IOptions<RadarOptions> options, RadarPushPolicy policy, TimeProvider clock)
{
    public async Task<string> ProcessarAsync(RadarConteudoCandidato expected, CancellationToken ct)
    {
        if (!options.Value.PushEnabled || !options.Value.Enabled || !policy.Enabled || !policy.Allows(expected.InstalacaoId)) return "disabled";
        var snapshot = await store.CarregarAsync(expected.InstalacaoId, ct);
        if (snapshot is null || snapshot.Candidate.SessaoId != expected.SessaoId || snapshot.Candidate.Revisao != expected.Revisao
            || snapshot.Candidate.ParadaId != expected.ParadaId || snapshot.Candidate.ConsultadoEm != expected.ConsultadoEm) return "stale";
        if (!RadarPushContent.Relevante(snapshot.Candidate, snapshot.PreviousTokenRevision == snapshot.TokenRevision ? snapshot.Previous : null, clock.GetUtcNow())) return "unchanged";
        if (!await contexts.ValidarAsync(snapshot.Session, ct)) return "context_invalid";
        var message = RadarPushContent.Criar(snapshot, protector.Unprotect(snapshot.EncryptedToken), clock.GetUtcNow());
        if (message is null) return "expired";
        var claim = Guid.NewGuid().ToString("N");
        if (!await store.ClaimAsync(snapshot, claim, ct)) return "claimed_or_limited";
        ExpoResult outcome = new("unknown");
        try
        {
            outcome = await gateway.EnviarAsync(message, async token =>
                options.Value.PushEnabled && await contexts.ValidarAsync(snapshot.Session, token)
                    && await store.AutorizarAsync(snapshot, claim, token), ct);
            return outcome.Status;
        }
        finally
        {
            // Uma falha/cancelamento após claim é ambígua: marcar antes de permitir futuras decisões.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await store.FinalizarAsync(snapshot, claim, outcome, cleanup.Token);
        }
    }

    public async Task<int> ConsultarReceiptsAsync(CancellationToken ct)
    {
        if (!options.Value.PushEnabled || !options.Value.Enabled || !policy.Enabled) return 0;
        var pending = await store.ReceiptsAsync(Guid.NewGuid().ToString("N"), ct);
        if (pending.Count == 0) return 0;
        var responses = await gateway.ReceiptsAsync(pending.Select(p => p.Id).ToArray(), ct);
        foreach (var receipt in responses)
        {
            var known = pending.FirstOrDefault(p => p.Id == receipt.Id);
            if (known is not null) await store.ResolverReceiptAsync(known, receipt.Status, ct);
        }
        return responses.Count;
    }
}
