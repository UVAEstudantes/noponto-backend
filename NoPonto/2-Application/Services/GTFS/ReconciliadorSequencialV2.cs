using System.Collections.ObjectModel;
using System.Collections.Immutable;

namespace NoPonto.Application.GTFS;

public sealed record CandidataProjecaoV2(
    Guid ParadaId,
    string CodigoParada,
    int IndiceProjecao,
    double Latitude,
    double Longitude,
    double DistanciaAcumuladaMetros,
    double PosicaoNormalizada,
    double DistanciaLateralMetros,
    double? BearingLocalGraus,
    int Segmento,
    double QualidadeProjecao = 1,
    double? MargemAmbiguidadeMetros = null,
    double? DiferencaBearingGraus = null);

public sealed record ParametrosReconciliadorSequencialV2(
    double DistanciaLateralReferenciaMetros = 40,
    double MargemAmbiguidadeReferenciaMetros = 10,
    double DiferencaBearingReferenciaGraus = 70,
    double PesoDistanciaLateral = .65,
    double PesoAmbiguidade = .20,
    double PesoQualidadeProjecao = .15,
    double PesoBearing = .15,
    double EspacamentoMinimoMetros = 8,
    double PesoEspacamentoMinimo = 1.20,
    double SaltoSemPenalidadeMetros = 750,
    double SaltoMaximoPlausivelMetros = 5_000,
    double PesoSaltoExcessivo = 1.25,
    double RazaoEuclidianaMinima = .15,
    double PesoRazaoEuclidiana = .20,
    double PesoTrocaSegmentoIncoerente = .10,
    int SaltoSegmentosSemPenalidade = 25,
    int MinimoOcorrencias = 2,
    double CustoMaximoAceitavel = -.01,
    double CustoSkip = .35)
{
    internal void Validar()
    {
        if (DistanciaLateralReferenciaMetros <= 0 || MargemAmbiguidadeReferenciaMetros <= 0 ||
            DiferencaBearingReferenciaGraus <= 0 ||
            PesoDistanciaLateral < 0 || PesoAmbiguidade < 0 || PesoQualidadeProjecao < 0 ||
            PesoBearing < 0 || EspacamentoMinimoMetros < 0 || PesoEspacamentoMinimo < 0 ||
            SaltoSemPenalidadeMetros < EspacamentoMinimoMetros ||
            SaltoMaximoPlausivelMetros <= SaltoSemPenalidadeMetros || PesoSaltoExcessivo < 0 ||
            RazaoEuclidianaMinima is < 0 or > 1 || PesoRazaoEuclidiana < 0 ||
            PesoTrocaSegmentoIncoerente < 0 || SaltoSegmentosSemPenalidade < 0 ||
            MinimoOcorrencias < 1 || CustoSkip < 0)
            throw new ArgumentOutOfRangeException(nameof(ParametrosReconciliadorSequencialV2));
    }
}

public sealed record ComponentesCustoLocalV2(
    double DistanciaLateral,
    double Ambiguidade,
    double QualidadeProjecao,
    double Bearing,
    double Total);

public sealed record NoSequencialV2(
    int Id,
    CandidataProjecaoV2 Candidata,
    ComponentesCustoLocalV2 Custo);

public sealed record ComponentesCustoTransicaoV2(
    double DeltaProgressoMetros,
    double DistanciaEuclidianaMetros,
    double RazaoEuclidianaRota,
    double EspacamentoMinimo,
    double SaltoExcessivo,
    double RazaoEuclidiana,
    double TrocaSegmento,
    double Total);

public sealed record ArestaSequencialV2(
    int OrigemId,
    int DestinoId,
    ComponentesCustoTransicaoV2 Custo);

public sealed record DescarteSequencialV2(
    CandidataProjecaoV2 Candidata,
    string Motivo,
    double CustoSkip,
    ComponentesCustoLocalV2 CustoSelecionar);

public sealed record DecisaoMembershipV2(
    CandidataProjecaoV2 Candidata,
    ComponentesCustoLocalV2 CustoSelecionar,
    double CustoSkip,
    string Decisao);

public sealed record DiagnosticoSequencialV2(
    int QuantidadeNos,
    int QuantidadeArestas,
    bool GrafoAciclico,
    string CriterioDesempate,
    IReadOnlyDictionary<int, int?> Predecessores,
    IReadOnlyList<string> Avisos);

public sealed record ResultadoSequencialV2(
    bool Sucesso,
    IReadOnlyList<NoSequencialV2> Sequencia,
    double? CustoTotal,
    double? CustoLocalTotal,
    double? CustoTransicaoTotal,
    double CustoSkipTotal,
    double? SegundoMelhorCusto,
    double? MargemAlternativa,
    IReadOnlyList<DescarteSequencialV2> Descartadas,
    IReadOnlyList<DecisaoMembershipV2> DecisoesMembership,
    DiagnosticoSequencialV2 Diagnostico,
    string? MotivoSemSequencia);

/// <summary>
/// Solver puro de custo mínimo para padrões lineares. O skip é representado pela
/// ausência do nó no caminho; nenhuma candidata é obrigatória.
/// </summary>
public sealed class ReconciliadorSequencialV2
{
    private const double EarthRadius = 6_371_008.8;
    private const double Epsilon = 1e-9;

    public ResultadoSequencialV2 Resolver(
        IReadOnlyCollection<CandidataProjecaoV2> candidatas,
        ParametrosReconciliadorSequencialV2? parametros = null)
    {
        ArgumentNullException.ThrowIfNull(candidatas);
        parametros ??= new();
        parametros.Validar();
        ValidarCandidatas(candidatas);

        var ordered = candidatas
            .OrderBy(x => x.DistanciaAcumuladaMetros)
            .ThenBy(x => x.CodigoParada, StringComparer.Ordinal)
            .ThenBy(x => x.ParadaId)
            .ThenBy(x => x.IndiceProjecao)
            .Select((x, i) => new NoSequencialV2(i, x, CalcularCustoLocal(x, parametros)))
            .ToArray();
        var edges = ConstruirArestas(ordered, parametros);
        var incoming = edges.GroupBy(x => x.DestinoId).ToDictionary(x => x.Key, x => x.ToArray());
        var bestByNode = new Caminho?[ordered.Length];
        var secondByNode = new Caminho?[ordered.Length];
        long generation = 0;

        for (var i = 0; i < ordered.Length; i++)
        {
            Inserir(new Caminho(ordered[i], null, null, ordered[i].Custo.Total - parametros.CustoSkip,
                    ordered[i].Custo.Total, 0, 1,
                    ImmutableHashSet.Create(ordered[i].Candidata.ParadaId), generation++),
                ref bestByNode[i], ref secondByNode[i]);
            if (!incoming.TryGetValue(i, out var predecessors)) continue;
            foreach (var edge in predecessors.OrderBy(x => x.OrigemId))
            {
                foreach (var previous in new[] { bestByNode[edge.OrigemId], secondByNode[edge.OrigemId] })
                {
                    if (previous is null || previous.ParadasUsadas.Contains(ordered[i].Candidata.ParadaId)) continue;
                    var candidate = new Caminho(ordered[i], previous, edge,
                        previous.Total + edge.Custo.Total + ordered[i].Custo.Total - parametros.CustoSkip,
                        previous.Local + ordered[i].Custo.Total,
                        previous.Transicao + edge.Custo.Total,
                        previous.Quantidade + 1,
                        previous.ParadasUsadas.Add(ordered[i].Candidata.ParadaId), generation++);
                    Inserir(candidate, ref bestByNode[i], ref secondByNode[i]);
                }
            }
        }

        var finals = bestByNode.Concat(secondByNode).Where(x => x is not null && x.Quantidade >= parametros.MinimoOcorrencias)
            .Cast<Caminho>().OrderBy(x => x.Total).ThenBy(x => x.OrdemGeracao).ToArray();
        var best = finals.FirstOrDefault();
        var second = finals.Skip(1).FirstOrDefault();
        var success = best is not null && best.Total <= parametros.CustoMaximoAceitavel;
        var selected = success ? Reconstruir(best!).ToArray() : [];
        var selectedIds = selected.Select(x => x.Id).ToHashSet();
        var discards = ordered.Where(x => !selectedIds.Contains(x.Id)).Select(x => new DescarteSequencialV2(
            x.Candidata, success ? "SKIP_MENOR_QUE_SELECIONAR_NO_CAMINHO_GLOBAL" : "NENHUMA_SEQUENCIA_ACEITAVEL",
            parametros.CustoSkip, x.Custo)).ToArray();
        var decisions = ordered.Select(x => new DecisaoMembershipV2(x.Candidata, x.Custo,
            parametros.CustoSkip, selectedIds.Contains(x.Id) ? "SELECIONAR" : "SKIP")).ToArray();
        var predecessorsChosen = selected.ToDictionary(x => x.Id, x =>
        {
            var index = Array.IndexOf(selected, x);
            return index == 0 ? (int?)null : selected[index - 1].Id;
        });
        var warnings = new List<string>();
        if (candidatas.GroupBy(x => x.ParadaId).Any(x => x.Count() > 1))
            warnings.Add("MULTIPLAS_PROJECOES_RESOLVIDAS_COM_RESTRICAO_DE_UNICIDADE_LINEAR");
        if (candidatas.Any(x => x.DiferencaBearingGraus is null))
            warnings.Add("BEARING_AUSENTE_NAO_GEROU_RECOMPENSA");

        var skipBaseline = ordered.Length * parametros.CustoSkip;
        return new(success, selected,
            success ? skipBaseline + best!.Total : null,
            success ? best!.Local : null,
            success ? best!.Transicao : null,
            success ? discards.Sum(x => x.CustoSkip) : 0,
            success && second is not null ? skipBaseline + second.Total : null,
            success && second is not null ? second.Total - best!.Total : null,
            discards, decisions,
            new(ordered.Length, edges.Count, true,
                "CUSTO_TOTAL_ASC; CODIGO_PARADA/PARADA_ID/INDICE_PROJECAO_ASC",
                new ReadOnlyDictionary<int, int?>(predecessorsChosen), warnings),
            success ? null : best is null ? "MENOS_DE_OCORRENCIAS_MINIMAS" : "CUSTO_ACIMA_DO_LIMITE");
    }

    private static ComponentesCustoLocalV2 CalcularCustoLocal(
        CandidataProjecaoV2 candidate, ParametrosReconciliadorSequencialV2 p)
    {
        var lateral = p.PesoDistanciaLateral * Math.Clamp(
            candidate.DistanciaLateralMetros / p.DistanciaLateralReferenciaMetros, 0, 1);
        var ambiguity = p.PesoAmbiguidade * (candidate.MargemAmbiguidadeMetros is { } margin
            ? 1 - Math.Clamp(margin / p.MargemAmbiguidadeReferenciaMetros, 0, 1)
            : 1);
        var quality = p.PesoQualidadeProjecao * (1 - candidate.QualidadeProjecao);
        var bearing = candidate.DiferencaBearingGraus is { } difference
            ? p.PesoBearing * Math.Clamp(difference / p.DiferencaBearingReferenciaGraus, 0, 1)
            : 0;
        return new(lateral, ambiguity, quality, bearing,
            lateral + ambiguity + quality + bearing);
    }

    private static List<ArestaSequencialV2> ConstruirArestas(
        NoSequencialV2[] nodes, ParametrosReconciliadorSequencialV2 p)
    {
        var result = new List<ArestaSequencialV2>();
        for (var i = 0; i < nodes.Length; i++)
        for (var j = i + 1; j < nodes.Length; j++)
        {
            var from = nodes[i].Candidata;
            var to = nodes[j].Candidata;
            var delta = to.DistanciaAcumuladaMetros - from.DistanciaAcumuladaMetros;
            if (delta <= Epsilon || delta > p.SaltoMaximoPlausivelMetros || from.ParadaId == to.ParadaId) continue;
            var straight = Distancia(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            var ratio = Math.Clamp(straight / delta, 0, 1);
            var spacing = delta < p.EspacamentoMinimoMetros && p.EspacamentoMinimoMetros > 0
                ? p.PesoEspacamentoMinimo * (1 - delta / p.EspacamentoMinimoMetros) : 0;
            var gap = delta > p.SaltoSemPenalidadeMetros
                ? p.PesoSaltoExcessivo * (delta / p.SaltoSemPenalidadeMetros - 1) : 0;
            var ratioCost = ratio < p.RazaoEuclidianaMinima && p.RazaoEuclidianaMinima > 0
                ? p.PesoRazaoEuclidiana * (1 - ratio / p.RazaoEuclidianaMinima) : 0;
            var segmentJump = Math.Max(0, to.Segmento - from.Segmento - p.SaltoSegmentosSemPenalidade);
            var segmentCost = p.PesoTrocaSegmentoIncoerente * Math.Min(1, segmentJump /
                (double)Math.Max(1, p.SaltoSegmentosSemPenalidade));
            result.Add(new(i, j, new(delta, straight, ratio, spacing, gap, ratioCost,
                segmentCost, spacing + gap + ratioCost + segmentCost)));
        }
        return result;
    }

    private static void Inserir(Caminho candidate, ref Caminho? best, ref Caminho? second)
    {
        if (best is null || Comparar(candidate, best) < 0)
        {
            if (best is not null) second = best;
            best = candidate;
        }
        else if (second is null || Comparar(candidate, second) < 0)
            second = candidate;
    }

    private static int Comparar(Caminho first, Caminho second)
    {
        var cost = first.Total.CompareTo(second.Total);
        return cost != 0 ? cost : first.OrdemGeracao.CompareTo(second.OrdemGeracao);
    }

    private static IEnumerable<NoSequencialV2> Reconstruir(Caminho path)
    {
        var stack = new Stack<NoSequencialV2>();
        for (var current = path; current is not null; current = current.Anterior) stack.Push(current.No);
        return stack;
    }

    private static void ValidarCandidatas(IEnumerable<CandidataProjecaoV2> candidates)
    {
        foreach (var x in candidates)
            if (string.IsNullOrWhiteSpace(x.CodigoParada) || x.IndiceProjecao < 1 ||
                !double.IsFinite(x.Latitude) || x.Latitude is < -90 or > 90 ||
                !double.IsFinite(x.Longitude) || x.Longitude is < -180 or > 180 ||
                !double.IsFinite(x.DistanciaAcumuladaMetros) || x.DistanciaAcumuladaMetros < 0 ||
                !double.IsFinite(x.PosicaoNormalizada) || x.PosicaoNormalizada is < 0 or > 1 ||
                !double.IsFinite(x.DistanciaLateralMetros) || x.DistanciaLateralMetros < 0 ||
                x.BearingLocalGraus is { } bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
                x.Segmento < 0 || !double.IsFinite(x.QualidadeProjecao) || x.QualidadeProjecao is < 0 or > 1 ||
                x.MargemAmbiguidadeMetros is < 0 || x.DiferencaBearingGraus is < 0 or > 180)
                throw new ArgumentException($"Candidata inválida: {x.CodigoParada}.", nameof(candidates));
    }

    private static double Distancia(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180;
        var dp = (lat2 - lat1) * Math.PI / 180; var dl = (lon2 - lon1) * Math.PI / 180;
        var h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private sealed record Caminho(NoSequencialV2 No, Caminho? Anterior, ArestaSequencialV2? Aresta,
        double Total, double Local, double Transicao, int Quantidade,
        ImmutableHashSet<Guid> ParadasUsadas, long OrdemGeracao);
}
