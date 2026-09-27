using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public static class ClassificacoesAssociacaoParada
{
    public const string AutoOk = "AUTO_OK";
    public const string Ambiguo = "AMBIGUO";
    public const string Distante = "DISTANTE";
    public const string Regressao = "REGRESSAO";
    public const string Cruzamento = "CRUZAMENTO";
    public const string Circular = "CIRCULAR";
    public const string Manual = "MANUAL";
}

public sealed record ArcGisParadaCandidata(
    Guid ParadaId,
    string Codigo,
    string Nome,
    Point Localizacao,
    int? OrdemFonte = null,
    double? BearingGraus = null);

public sealed record ArcGisParadasParametros(
    double DistanciaMaximaMetros = 40,
    double DistanciaAutoMetros = 20,
    double DeltaAmbiguidadeMetros = 10,
    double SeparacaoMinimaProjecoesMetros = 30,
    double ToleranciaMesmaProjecaoMetros = 4,
    double DistanciaMinimaEntreParadasMetros = 8,
    double DistanciaDuplicidadeFisicaMetros = 6,
    double ToleranciaRegressaoMetros = 10,
    double DiferencaBearingMaximaGraus = 70,
    double ScoreMinimoAuto = .70,
    double JanelaBearingMetros = 30,
    double ComprimentoMinimoBearingMetros = 2)
{
    internal void Validar()
    {
        if (DistanciaMaximaMetros <= 0 || DistanciaAutoMetros <= 0 ||
            DistanciaAutoMetros > DistanciaMaximaMetros || DeltaAmbiguidadeMetros < 0 ||
            SeparacaoMinimaProjecoesMetros <= 0 || ToleranciaMesmaProjecaoMetros < 0 ||
            DistanciaMinimaEntreParadasMetros < 0 || DistanciaDuplicidadeFisicaMetros < 0 ||
            ToleranciaRegressaoMetros < 0 || DiferencaBearingMaximaGraus is < 0 or > 180 ||
            ScoreMinimoAuto is < 0 or > 1 || JanelaBearingMetros <= 0 ||
            ComprimentoMinimoBearingMetros <= 0 || ComprimentoMinimoBearingMetros > JanelaBearingMetros)
            throw new ArgumentOutOfRangeException(nameof(ArcGisParadasParametros),
                "Os thresholds espaciais devem ser positivos e internamente coerentes.");
    }
}

public sealed record ArcGisParadaScore(
    double DistanciaLateral,
    double CoerenciaOrdem,
    double AusenciaAmbiguidade,
    double SeparacaoFisica,
    double? CoerenciaDirecao,
    double Total);

public sealed record ArcGisAssociacaoParada(
    Guid ParadaId,
    string Codigo,
    string Nome,
    double Latitude,
    double Longitude,
    int IndiceOcorrencia,
    int OrdemSugerida,
    int? OrdemFonte,
    double PosicaoLinha,
    double DistanciaAcumuladaMetros,
    double DistanciaLateralMetros,
    double? BearingLocalGraus,
    double? LadoAssinadoMetros,
    double? DiferencaBearingGraus,
    ArcGisParadaScore Score,
    string Classificacao,
    IReadOnlyList<string> Motivos);

public sealed record ArcGisParadasResultado(
    Guid PadraoVersaoId,
    string Topologia,
    double ComprimentoMetros,
    IReadOnlyList<ArcGisAssociacaoParada> Associacoes)
{
    public IReadOnlyList<ArcGisAssociacaoParada> Automaticas =>
        Associacoes.Where(x => x.Classificacao == ClassificacoesAssociacaoParada.AutoOk).ToArray();
}

/// <summary>
/// Reconciliador espacial puro. Não consulta banco, não altera o padrão e não escolhe
/// silenciosamente uma projeção quando a mesma parada tem posições plausíveis distintas.
/// </summary>
public sealed class ArcGisParadasReconciliador
{
    private const double EarthRadius = 6_371_008.8;

    public ArcGisParadasResultado Reconciliar(
        Guid padraoVersaoId,
        LineString geometria,
        string topologia,
        IReadOnlyCollection<ArcGisParadaCandidata> paradas,
        ArcGisParadasParametros? parametros = null)
    {
        ArgumentNullException.ThrowIfNull(geometria);
        ArgumentNullException.ThrowIfNull(paradas);
        parametros ??= new ArcGisParadasParametros();
        parametros.Validar();
        if (geometria.SRID != 4326 || geometria.NumPoints < 2 || geometria.IsEmpty)
            throw new ArgumentException("A geometria deve ser LineString SRID 4326 com ao menos dois pontos.", nameof(geometria));
        if (topologia is not TopologiasPadrao.Linear and not TopologiasPadrao.Circular)
            throw new ArgumentException("Topologia desconhecida.", nameof(topologia));

        var segments = CriarSegmentos(geometria.Coordinates);
        var total = segments.Sum(x => x.Comprimento);
        if (total <= 0) throw new ArgumentException("A geometria possui comprimento nulo.", nameof(geometria));
        var crossing = !geometria.IsSimple;
        var circular = topologia == TopologiasPadrao.Circular || geometria.IsClosed;
        var associations = new List<ArcGisAssociacaoParada>();

        foreach (var stop in paradas.OrderBy(x => x.Codigo, StringComparer.Ordinal).ThenBy(x => x.ParadaId))
        {
            if (stop.Localizacao is null || stop.Localizacao.IsEmpty || stop.Localizacao.SRID != 4326)
                throw new ArgumentException($"Parada {stop.Codigo} deve possuir Point SRID 4326.", nameof(paradas));
            var projections = Projetar(stop.Localizacao.Coordinate, segments, total, parametros)
                .OrderBy(x => x.Lateral).ThenBy(x => x.Acumulada).ToArray();
            var best = projections[0];
            var plausible = projections.Where(x => x.Lateral <= parametros.DistanciaMaximaMetros &&
                x.Lateral <= best.Lateral + parametros.DeltaAmbiguidadeMetros).ToArray();
            var multiple = plausible.Skip(1).Any(x =>
                DistanciaCircular(x.Acumulada, best.Acumulada, total, circular) >= parametros.SeparacaoMinimaProjecoesMetros);
            var selected = multiple
                ? plausible.Where((x, i) => i == 0 || plausible.Take(i).All(y =>
                    DistanciaCircular(x.Acumulada, y.Acumulada, total, circular) >= parametros.SeparacaoMinimaProjecoesMetros)).ToArray()
                : [best];

            for (var index = 0; index < selected.Length; index++)
            {
                var p = selected[index];
                var reasons = new List<string>();
                string classification;
                if (p.Lateral > parametros.DistanciaMaximaMetros)
                {
                    classification = ClassificacoesAssociacaoParada.Distante;
                    reasons.Add("FORA_DISTANCIA_MAXIMA");
                }
                else if (multiple)
                {
                    classification = crossing ? ClassificacoesAssociacaoParada.Cruzamento
                        : circular ? ClassificacoesAssociacaoParada.Circular
                        : ClassificacoesAssociacaoParada.Ambiguo;
                    reasons.Add("MULTIPLAS_PROJECOES_PLAUSIVEIS");
                }
                else if (p.Lateral > parametros.DistanciaAutoMetros)
                {
                    classification = ClassificacoesAssociacaoParada.Manual;
                    reasons.Add("ENTRE_LIMITE_AUTO_E_MAXIMO");
                }
                else
                {
                    classification = ClassificacoesAssociacaoParada.AutoOk;
                    reasons.Add("PROJECAO_UNICA_PROXIMA");
                }

                double? bearingDifference = null;
                double? directionScore = null;
                if (stop.BearingGraus is { } stopBearing && p.Bearing is { } localBearing)
                {
                    bearingDifference = DiferencaAngular(stopBearing, localBearing);
                    directionScore = Math.Max(0, 1 - bearingDifference.Value / parametros.DiferencaBearingMaximaGraus);
                    if (bearingDifference > parametros.DiferencaBearingMaximaGraus)
                    {
                        classification = ClassificacoesAssociacaoParada.Manual;
                        reasons.Add("BEARING_INCOMPATIVEL");
                    }
                }
                else if (stop.BearingGraus.HasValue) reasons.Add("BEARING_LOCAL_INDISPONIVEL");
                else reasons.Add("SEM_BEARING_FONTE");

                var lateralScore = Math.Max(0, 1 - p.Lateral / parametros.DistanciaMaximaMetros);
                var ambiguityScore = multiple ? 0 : 1;
                var totalScore = directionScore is null
                    ? .60 * lateralScore + .25 * ambiguityScore + .10 + .05
                    : .55 * lateralScore + .20 * ambiguityScore + .10 + .05 + .10 * directionScore.Value;
                if (classification == ClassificacoesAssociacaoParada.AutoOk && totalScore < parametros.ScoreMinimoAuto)
                {
                    classification = ClassificacoesAssociacaoParada.Manual;
                    reasons.Add("SCORE_ABAIXO_MINIMO_AUTO");
                }
                associations.Add(new(stop.ParadaId, stop.Codigo, stop.Nome,
                    stop.Localizacao.Y, stop.Localizacao.X, index + 1, 0, stop.OrdemFonte,
                    p.Acumulada / total, p.Acumulada, p.Lateral, p.Bearing, p.LadoAssinado, bearingDifference,
                    new(lateralScore, 1, ambiguityScore, 1, directionScore, totalScore),
                    classification, reasons));
            }
        }

        AplicarRegressoes(associations, parametros, total, circular);
        AplicarProximidade(associations, paradas, parametros, circular, total);
        var ordered = associations.OrderBy(x => x.DistanciaAcumuladaMetros)
            .ThenBy(x => x.Codigo, StringComparer.Ordinal).ThenBy(x => x.IndiceOcorrencia).ToArray();
        for (var i = 0; i < ordered.Length; i++) ordered[i] = ordered[i] with { OrdemSugerida = i + 1 };
        return new(padraoVersaoId, circular ? TopologiasPadrao.Circular : TopologiasPadrao.Linear, total, ordered);
    }

    private static void AplicarRegressoes(List<ArcGisAssociacaoParada> items,
        ArcGisParadasParametros options, double total, bool circular)
    {
        var sourced = items.Where(x => x.OrdemFonte.HasValue).OrderBy(x => x.OrdemFonte).ThenBy(x => x.Codigo).ToArray();
        for (var i = 1; i < sourced.Length; i++)
        {
            var previous = sourced[i - 1]; var current = sourced[i];
            var regression = previous.DistanciaAcumuladaMetros - current.DistanciaAcumuladaMetros;
            if (circular && regression > total / 2) continue;
            if (regression <= options.ToleranciaRegressaoMetros) continue;
            Atualizar(items, current, ClassificacoesAssociacaoParada.Regressao, "ORDEM_FONTE_REGREDIU_NO_TRACADO",
                current.Score with { CoerenciaOrdem = 0, Total = Math.Max(0, current.Score.Total - .10) });
        }
    }

    private static void AplicarProximidade(List<ArcGisAssociacaoParada> items,
        IReadOnlyCollection<ArcGisParadaCandidata> stops, ArcGisParadasParametros options,
        bool circular, double total)
    {
        var positions = stops.ToDictionary(x => x.ParadaId, x => x.Localizacao.Coordinate);
        var eligible = items.Where(x => x.Classificacao == ClassificacoesAssociacaoParada.AutoOk).ToArray();
        for (var i = 0; i < eligible.Length; i++)
        for (var j = i + 1; j < eligible.Length; j++)
        {
            if (eligible[i].ParadaId == eligible[j].ParadaId) continue;
            var along = DistanciaCircular(eligible[i].DistanciaAcumuladaMetros,
                eligible[j].DistanciaAcumuladaMetros, total, circular);
            if (along >= options.DistanciaMinimaEntreParadasMetros) continue;
            var physical = Distancia(positions[eligible[i].ParadaId], positions[eligible[j].ParadaId]);
            if (physical > options.DistanciaDuplicidadeFisicaMetros) continue;
            Atualizar(items, eligible[i], ClassificacoesAssociacaoParada.Ambiguo, "PARADAS_DISTINTAS_FISICAMENTE_DUPLICADAS",
                eligible[i].Score with { SeparacaoFisica = 0, Total = Math.Max(0, eligible[i].Score.Total - .05) });
            Atualizar(items, eligible[j], ClassificacoesAssociacaoParada.Ambiguo, "PARADAS_DISTINTAS_FISICAMENTE_DUPLICADAS",
                eligible[j].Score with { SeparacaoFisica = 0, Total = Math.Max(0, eligible[j].Score.Total - .05) });
        }
    }

    private static void Atualizar(List<ArcGisAssociacaoParada> items, ArcGisAssociacaoParada target,
        string classification, string reason, ArcGisParadaScore score)
    {
        var index = items.IndexOf(target);
        if (index >= 0) items[index] = target with {
            Classificacao = classification, Motivos = target.Motivos.Append(reason).Distinct().ToArray(), Score = score
        };
    }

    private static IReadOnlyList<Projecao> Projetar(Coordinate point, IReadOnlyList<Segmento> segments,
        double total, ArcGisParadasParametros options)
    {
        var raw = segments.Select(x => Projetar(point, x)).OrderBy(x => x.Acumulada).ToArray();
        var collapsed = new List<Projecao>();
        foreach (var projection in raw)
        {
            var equivalent = collapsed.FindIndex(x =>
                Math.Abs(x.Acumulada - projection.Acumulada) <= options.ToleranciaMesmaProjecaoMetros);
            if (equivalent < 0) collapsed.Add(projection);
            else if (projection.Lateral < collapsed[equivalent].Lateral) collapsed[equivalent] = projection;
        }
        if (collapsed.Count == 0) throw new InvalidOperationException("Geometria sem segmentos.");
        return collapsed.Select(x =>
        {
            var direction = CalcularDirecaoLocal(segments, total, x.Acumulada, point,
                options.JanelaBearingMetros, options.ComprimentoMinimoBearingMetros);
            return x with { Bearing = direction.Bearing, LadoAssinado = direction.LadoAssinado };
        }).ToArray();
    }

    private static Projecao Projetar(Coordinate point, Segmento segment)
    {
        var lat = (segment.A.Y + segment.B.Y + point.Y) / 3 * Math.PI / 180;
        var sx = EarthRadius * Math.Cos(lat) * Math.PI / 180; var sy = EarthRadius * Math.PI / 180;
        var vx = (segment.B.X - segment.A.X) * sx; var vy = (segment.B.Y - segment.A.Y) * sy;
        var wx = (point.X - segment.A.X) * sx; var wy = (point.Y - segment.A.Y) * sy;
        var denominator = vx * vx + vy * vy;
        var fraction = denominator <= 0 ? 0 : Math.Clamp((wx * vx + wy * vy) / denominator, 0, 1);
        var dx = wx - fraction * vx; var dy = wy - fraction * vy;
        return new(segment.AcumuladaInicial + fraction * segment.Comprimento,
            Math.Sqrt(dx * dx + dy * dy), null, null);
    }

    private static IReadOnlyList<Segmento> CriarSegmentos(Coordinate[] coordinates)
    {
        var result = new List<Segmento>(); var accumulated = 0d;
        for (var i = 1; i < coordinates.Length; i++)
        {
            var length = Distancia(coordinates[i - 1], coordinates[i]);
            if (length <= 0) continue;
            result.Add(new(coordinates[i - 1], coordinates[i], accumulated, length));
            accumulated += length;
        }
        return result;
    }

    private static double DistanciaCircular(double first, double second, double total, bool circular)
    {
        var direct = Math.Abs(first - second);
        return circular ? Math.Min(direct, total - direct) : direct;
    }

    internal static double DiferencaAngular(double first, double second)
    {
        var difference = Math.Abs(((first - second) % 360 + 360) % 360);
        return Math.Min(difference, 360 - difference);
    }

    private static double Bearing(Coordinate a, Coordinate b)
    {
        var p1 = a.Y * Math.PI / 180; var p2 = b.Y * Math.PI / 180;
        var dl = (b.X - a.X) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static DirecaoLocal CalcularDirecaoLocal(IReadOnlyList<Segmento> segments, double total,
        double acumulada, Coordinate point, double janela, double comprimentoMinimo)
    {
        var half = janela / 2;
        var before = Math.Max(0, acumulada - half);
        var after = Math.Min(total, acumulada + half);
        if (after - before < comprimentoMinimo)
        {
            if (before <= 0) after = Math.Min(total, comprimentoMinimo);
            else if (after >= total) before = Math.Max(0, total - comprimentoMinimo);
        }
        if (after - before < comprimentoMinimo) return new(null, null);
        var a = CoordenadaEm(segments, before);
        var b = CoordenadaEm(segments, after);
        if (Distancia(a, b) < comprimentoMinimo) return new(null, null);

        var projected = CoordenadaEm(segments, acumulada);
        var latitude = (a.Y + b.Y + projected.Y + point.Y) / 4 * Math.PI / 180;
        var sx = EarthRadius * Math.Cos(latitude) * Math.PI / 180;
        var sy = EarthRadius * Math.PI / 180;
        var vx = (b.X - a.X) * sx;
        var vy = (b.Y - a.Y) * sy;
        var wx = (point.X - projected.X) * sx;
        var wy = (point.Y - projected.Y) * sy;
        var length = Math.Sqrt(vx * vx + vy * vy);
        if (length < comprimentoMinimo) return new(null, null);
        var signedSide = (vx * wy - vy * wx) / length;
        return new(Bearing(a, b), Math.Abs(signedSide) < 1e-6 ? 0 : signedSide);
    }

    private static Coordinate CoordenadaEm(IReadOnlyList<Segmento> segments, double acumulada)
    {
        var segment = segments.FirstOrDefault(x => acumulada <= x.AcumuladaInicial + x.Comprimento)
            ?? segments[^1];
        var fraction = Math.Clamp((acumulada - segment.AcumuladaInicial) / segment.Comprimento, 0, 1);
        return new(segment.A.X + fraction * (segment.B.X - segment.A.X),
            segment.A.Y + fraction * (segment.B.Y - segment.A.Y));
    }

    private static double Distancia(Coordinate a, Coordinate b)
    {
        var p1 = a.Y * Math.PI / 180; var p2 = b.Y * Math.PI / 180;
        var dp = (b.Y - a.Y) * Math.PI / 180; var dl = (b.X - a.X) * Math.PI / 180;
        var h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private sealed record Segmento(Coordinate A, Coordinate B, double AcumuladaInicial, double Comprimento);
    private sealed record Projecao(double Acumulada, double Lateral, double? Bearing, double? LadoAssinado);
    private sealed record DirecaoLocal(double? Bearing, double? LadoAssinado);
}

public sealed record ArcGisParadasPersistenciaResultado(int Criadas, int Reutilizadas, int IgnoradasIncertas);

/// <summary>Persiste apenas AUTO_OK em versão ainda pendente; nunca publica a versão.</summary>
public sealed class ArcGisParadasPersistenciaService(TransporteDbContext db)
{
    public async Task<ArcGisParadasPersistenciaResultado> PersistirAutomaticasAsync(
        ArcGisParadasResultado resultado, CancellationToken ct = default)
    {
        var version = await db.PadroesVersoes.SingleOrDefaultAsync(x => x.Id == resultado.PadraoVersaoId, ct)
            ?? throw new InvalidOperationException("PadraoVersao não encontrada.");
        if (version.ResultadoValidacao != ResultadosValidacaoPadrao.Pendente || version.PublicadoEmUtc is not null)
            throw new InvalidOperationException("Somente uma PadraoVersao PENDENTE e não publicada pode receber ocorrências ArcGIS.");
        var automatic = resultado.Automaticas.OrderBy(x => x.OrdemSugerida).ToArray();
        var existing = await db.OcorrenciasParadasPadroes.Where(x => x.PadraoVersaoId == version.Id)
            .OrderBy(x => x.Ordem).ToArrayAsync(ct);
        if (existing.Length > 0)
        {
            var equivalent = existing.Length == automatic.Length && existing.Zip(automatic).All(pair =>
                pair.First.Id == IdOcorrencia(version.Id, pair.Second) &&
                pair.First.ParadaId == pair.Second.ParadaId && pair.First.Ordem == pair.Second.OrdemSugerida &&
                Math.Abs(pair.First.PosicaoTracado - pair.Second.PosicaoLinha) < 1e-9 &&
                Math.Abs(pair.First.DistanciaAcumuladaMetros - pair.Second.DistanciaAcumuladaMetros) < .001 &&
                Math.Abs(pair.First.DistanciaDaLinhaMetros - pair.Second.DistanciaLateralMetros) < .001);
            if (!equivalent) throw new InvalidOperationException(
                "A versão já possui ocorrências diferentes; não é permitido substituir silenciosamente o rascunho.");
            await RegistrarProvenienciaParadasAsync(version.Id, ct);
            await db.SaveChangesAsync(ct);
            return new(0, existing.Length, resultado.Associacoes.Count - automatic.Length);
        }
        if (automatic.Length == 0)
            return new(0, 0, resultado.Associacoes.Count);

        var stopIds = automatic.Select(x => x.ParadaId).Distinct().ToArray();
        var found = await db.Paradas.Where(x => stopIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(ct);
        if (found.Length != stopIds.Length) throw new InvalidOperationException("Há Parada candidata inexistente no banco.");
        db.OcorrenciasParadasPadroes.AddRange(automatic.Select(x => new OcorrenciaParadaPadrao {
            Id = IdOcorrencia(version.Id, x), PadraoVersaoId = version.Id, ParadaId = x.ParadaId,
            Ordem = x.OrdemSugerida, SourceSequence = x.OrdemFonte,
            PosicaoTracado = x.PosicaoLinha,
            DistanciaAcumuladaMetros = x.DistanciaAcumuladaMetros,
            DistanciaDaLinhaMetros = x.DistanciaLateralMetros,
            SourceShapeDistTraveledMetros = null
        }));
        await RegistrarProvenienciaParadasAsync(version.Id, ct);
        await db.SaveChangesAsync(ct);
        return new(automatic.Length, 0, resultado.Associacoes.Count - automatic.Length);
    }

    private async Task RegistrarProvenienciaParadasAsync(Guid versionId, CancellationToken ct)
    {
        var importIds = await db.PadroesVersoesImportacoes.Where(x => x.PadraoVersaoId == versionId
                && x.ImportacaoEstrutural.FonteEstrutural.Codigo == ArcGisEstruturalRegularService.FonteCodigo)
            .Select(x => x.ImportacaoEstruturalId).Distinct().ToArrayAsync(ct);
        var registered = await db.PadroesVersoesImportacoes.Where(x => x.PadraoVersaoId == versionId
                && x.Papel == PapeisImportacaoPadrao.Paradas && importIds.Contains(x.ImportacaoEstruturalId))
            .Select(x => x.ImportacaoEstruturalId).ToArrayAsync(ct);
        db.PadroesVersoesImportacoes.AddRange(importIds.Except(registered).Select(importId => new PadraoVersaoImportacao {
            PadraoVersaoId = versionId, ImportacaoEstruturalId = importId, Papel = PapeisImportacaoPadrao.Paradas
        }));
    }

    internal static Guid IdOcorrencia(Guid versionId, ArcGisAssociacaoParada association) =>
        EstruturaFinalRebuildService.DeterministicGuid("arcgis-stop-occurrence", versionId.ToString("N"),
            association.ParadaId.ToString("N"), association.PosicaoLinha.ToString("0.000000000", CultureInfo.InvariantCulture));
}
