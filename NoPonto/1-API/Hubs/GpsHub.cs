using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace NoPonto.API.Hubs;

/// <summary>
/// Hub SignalR para streaming de posições GPS em tempo real.
///
/// Protocolo cliente → servidor:
///   InscreverseLinha(codigoLinha)    — entra no grupo da linha
///   CancelarLinha(codigoLinha)       — sai do grupo da linha
///
/// Protocolo servidor → cliente:
///   PosicaoAtualizada(posicoes[])    — lista de PosicaoVeiculoDto da linha
/// </summary>
public sealed class GpsHub : Hub
{
    // Contador de assinantes por linha — lido pelo GpsPollingService
    private static readonly ConcurrentDictionary<string, int> _contadorPorLinha =
        new(StringComparer.OrdinalIgnoreCase);

    // Linhas assinadas por cada connectionId — para cleanup no disconnect
    private static readonly ConcurrentDictionary<string, HashSet<string>> _linhasPorConexao =
        new();

    /// <summary>Linhas que têm pelo menos 1 cliente conectado agora.</summary>
    public static IReadOnlyCollection<string> LinhasComAssinantes => [.. _contadorPorLinha.Keys];

    // ── Chamadas do cliente ───────────────────────────────────────────────────

    /// <summary>
    /// Assina uma linha. O cliente receberá PosicaoAtualizada a cada ciclo de ~20s.
    /// Limite recomendado: 5–8 linhas por cliente.
    /// </summary>
    public async Task InscreverseLinha(string codigoLinha)
    {
        if (string.IsNullOrWhiteSpace(codigoLinha))
            throw new HubException("codigoLinha é obrigatório.");

        var linha = codigoLinha.Trim().ToUpperInvariant();

        await Groups.AddToGroupAsync(Context.ConnectionId, GrupoLinha(linha));

        _contadorPorLinha.AddOrUpdate(linha, 1, (_, n) => n + 1);

        _linhasPorConexao.AddOrUpdate(
            Context.ConnectionId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { linha },
            (_, set) => { lock (set) { set.Add(linha); } return set; });
    }

    /// <summary>Cancela a assinatura de uma linha específica.</summary>
    public async Task CancelarLinha(string codigoLinha)
    {
        if (string.IsNullOrWhiteSpace(codigoLinha))
            throw new HubException("codigoLinha é obrigatório.");

        var linha = codigoLinha.Trim().ToUpperInvariant();

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GrupoLinha(linha));
        DecrementarContador(linha);

        if (_linhasPorConexao.TryGetValue(Context.ConnectionId, out var set))
            lock (set) { set.Remove(linha); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_linhasPorConexao.TryRemove(Context.ConnectionId, out var linhas))
            foreach (var linha in linhas)
                DecrementarContador(linha);

        await base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static string GrupoLinha(string codigoLinha) => $"linha:{codigoLinha}";

    private static void DecrementarContador(string linha)
    {
        _contadorPorLinha.AddOrUpdate(linha, 0, (_, n) => Math.Max(0, n - 1));
        if (_contadorPorLinha.TryGetValue(linha, out var atual) && atual == 0)
            _contadorPorLinha.TryRemove(new KeyValuePair<string, int>(linha, 0));
    }
}