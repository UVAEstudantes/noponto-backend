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
///   LinhaEsvaziada({ codigoLinha }) — snapshot vazio da linha identificada
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
    private static readonly ConcurrentDictionary<string, long> _linhasAtivas =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _geracaoInscricao;

    /// <summary>
    /// Linhas com pelo menos 1 cliente conectado agora.
    /// Lido pelo GpsPollingService para decidir quais linhas enriquecer/broadcastar.
    /// </summary>
    public static IReadOnlyCollection<string> LinhasComAssinantes =>
        (IReadOnlyCollection<string>)_linhasAtivas.Keys;

    // A nova inscrição precisa de um estado vazio mesmo se a linha já estava vazia.
    internal static IReadOnlyDictionary<string, long> AssinaturasAtivas =>
        _linhasAtivas.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    // ── Chamadas do cliente ───────────────────────────────────────────────────

    public async Task InscreverseLinha(string codigoLinha)
    {
        if (string.IsNullOrWhiteSpace(codigoLinha))
            throw new HubException("codigoLinha é obrigatório.");

        var linha = codigoLinha.Trim().ToUpperInvariant();

        await Groups.AddToGroupAsync(Context.ConnectionId, GrupoLinha(linha));

        var set = _linhasPorConexao.GetOrAdd(Context.ConnectionId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (set)
        {
            if (set.Add(linha))
                _linhasAtivas[linha] = Interlocked.Increment(ref _geracaoInscricao);
        }
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
            if (!_linhasAtivas.ContainsKey(linha))
                _linhasAtivas.TryAdd(linha, Interlocked.Increment(ref _geracaoInscricao));
    }
}
