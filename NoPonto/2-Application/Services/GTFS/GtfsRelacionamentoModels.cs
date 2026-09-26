using NetTopologySuite.Geometries;

namespace NoPonto.Application.GTFS;

public enum ClassificacaoGtfs
{
    GtfsAutoritativo,
    GtfsInsuficiente,
    SemGtfs,
    Ambiguo
}

public sealed record GtfsRoute(string RouteId, string RouteShortName);
public sealed record GtfsTrip(string RouteId, string ServiceId, string TripId, string DirectionId, string ShapeId);
public sealed record GtfsStopTime(string TripId, string StopId, int StopSequence, double? ShapeDistTraveledMetros);
public sealed record GtfsShapePoint(string ShapeId, int Sequence, double Latitude, double Longitude, double? DistanceMetros);
public sealed record GtfsStop(string StopId, string StopName, double Latitude, double Longitude);

public sealed record GtfsOcorrencia(
    string StopId,
    int StopSequence,
    double? ShapeDistTraveledMetros);

public sealed record GtfsPadrao(
    string PadraoExternoId,
    string RouteId,
    string RouteShortName,
    string DirectionId,
    string ShapeId,
    IReadOnlyList<GtfsOcorrencia> Ocorrencias,
    IReadOnlyList<Coordinate> Shape,
    int QuantidadeTrips);

public sealed record GtfsFeed(
    IReadOnlyList<GtfsRoute> Routes,
    IReadOnlyList<GtfsTrip> Trips,
    IReadOnlyList<GtfsStopTime> StopTimes,
    IReadOnlyList<GtfsShapePoint> Shapes,
    IReadOnlyList<GtfsStop> Stops,
    IReadOnlyList<GtfsPadrao> Padroes);

public sealed record GtfsParadaPendente(string Codigo, string Nome, double Latitude, double Longitude);

public sealed record GtfsProjecaoOcorrencia(
    Guid ParadaId,
    string ParadaCodigo,
    int Ordem,
    int SourceStopSequence,
    double? SourceShapeDistTraveledMetros,
    double PosicaoLinha,
    double DistanciaMetros);

public sealed record GtfsDryRunItem(
    Guid LinhaId,
    string LinhaCodigo,
    Guid? SentidoId,
    string? SentidoNome,
    Guid? PadraoVersaoId,
    ClassificacaoGtfs Classificacao,
    string? PadraoExternoId,
    int RelacoesLegadas,
    int RelacoesPropostas,
    int Mantidas,
    int Removidas,
    int Adicionadas,
    int MudancasOrdem,
    int MudancasPosicao,
    double? DistanciaEspacialMaximaMetros,
    IReadOnlyList<string> Motivos,
    IReadOnlyList<GtfsProjecaoOcorrencia> Ocorrencias,
    IReadOnlyList<GtfsParadaPendente> ParadasACriar);

public sealed record GtfsDryRunRelatorio(IReadOnlyList<GtfsDryRunItem> Itens)
{
    public IReadOnlyDictionary<ClassificacaoGtfs, int> Totais =>
        Itens.GroupBy(x => x.Classificacao).ToDictionary(x => x.Key, x => x.Count());
}
