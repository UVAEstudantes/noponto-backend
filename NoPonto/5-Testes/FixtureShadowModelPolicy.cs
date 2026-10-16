using NoPonto.Application.GPS;
namespace NoPonto.Tests;
// Test-only fixture metadata approval. Never registered in production DI.
internal sealed class FixtureShadowModelPolicy : IHistoricalEtaModelPolicy
{
    public string ExpectedDigest => new('a',64);
    public bool Allows(HistoricalEtaObservation o)=>true;
    public HistoricalEtaObservation Enrich(HistoricalEtaObservation o,HistoricalEtaGeometry g)=>o;
}
