using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace NoPonto.API.Hubs;

/// <summary>
/// Hub SignalR para streaming de posições GPS em tempo real.
///
/// Protocolo cliente → servidor:
///   InscreverseLinha(codigoLinha)  — entra no grupo da linha
///   CancelarLinha(codigoLinha)     — sai do grupo da linha
///
/// Protocolo servidor → cliente:
///   PosicaoAtualizada(posicoes[])  — lista de PosicaoVeiculoDto da linha
/// </summary>
public sealed class GpsHub : Hub
{
    // Rastreia quais linhas cada conexão assinou.
    // Chave: connectionId → conjunto de códigos de linha.
    private static readonly ConcurrentDictionary<string, HashSet<string>> _linhasPorConexao =
        new();

    // Conjunto de linhas que têm pelo menos 1 conexão ativa.
    // Recalculado a partir de _linhasPorConexao para evitar race conditions
    // de contadores que ficam negativos ou pulam para zero erroneamente.
    private static readonly ConcurrentDictionary<string, byte> _linhasAtivas =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Linhas com pelo menos 1 cliente conectado agora.
    /// Lido pelo GpsPollingService para decidir quais linhas enriquecer/broadcastar.
    /// </summary>
    public static IReadOnlyCollection<string> LinhasComAssinantes =>
        (IReadOnlyCollection<string>)_linhasAtivas.Keys;

    // ── Chamadas do cliente ───────────────────────────────────────────────────

    public async Task InscreverseLinha(string codigoLinha)
    {
        if (string.IsNullOrWhiteSpace(codigoLinha))
            throw new HubException("codigoLinha é obrigatório.");

        var linha = codigoLinha.Trim().ToUpperInvariant();

        await Groups.AddToGroupAsync(Context.ConnectionId, GrupoLinha(linha));

        _linhasPorConexao.AddOrUpdate(
            Context.ConnectionId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { linha },
            (_, set) => { lock (set) { set.Add(linha); } return set; });

        _linhasAtivas[linha] = 1;
    }

    public async Task CancelarLinha(string codigoLinha)
    {
        if (string.IsNullOrWhiteSpace(codigoLinha))
            throw new HubException("codigoLinha é obrigatório.");

        var linha = codigoLinha.Trim().ToUpperInvariant();

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GrupoLinha(linha));

        if (_linhasPorConexao.TryGetValue(Context.ConnectionId, out var set))
            lock (set) { set.Remove(linha); }

        RecalcularLinhasAtivas();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _linhasPorConexao.TryRemove(Context.ConnectionId, out _);
        RecalcularLinhasAtivas();
        await base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static string GrupoLinha(string codigoLinha) => $"linha:{codigoLinha}";

    /// <summary>
    /// Reconstrói _linhasAtivas a partir do estado real de _linhasPorConexao.
    /// Evita race conditions de contadores que vão a zero momentaneamente.
    /// </summary>
    private static void RecalcularLinhasAtivas()
    {
        // Coleta todas as linhas que ainda têm pelo menos uma conexão
        var linhasComConexao = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in _linhasPorConexao.Values)
        {
            lock (set)
            {
                foreach (var linha in set)
                    linhasComConexao.Add(linha);
            }
        }

        // Remove do dicionário linhas que não têm mais conexões
        foreach (var linha in _linhasAtivas.Keys)
        {
            if (!linhasComConexao.Contains(linha))
                _linhasAtivas.TryRemove(linha, out _);
        }

        // Garante que linhas com conexão estão no dicionário
        foreach (var linha in linhasComConexao)
            _linhasAtivas[linha] = 1;
    }
}