using Microsoft.Extensions.Caching.Distributed;

namespace NoPonto.Data.Interfaces;

/// <summary>Grava somente os payloads de posição no formato do IDistributedCache.</summary>
public interface IPosicaoVeiculoPayloadWriter
{
    Task GravarAtivoAsync(string chave, string json, DistributedCacheEntryOptions opcoes, CancellationToken ct);
    Task GravarRecenteAsync(string chave, string json, DistributedCacheEntryOptions opcoes, CancellationToken ct);
}
