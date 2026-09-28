namespace NoPonto.Application.GTFS;

public static class DecisoesCompeticaoDirecionalV2
{
    public const string PadraoA = "A";
    public const string PadraoB = "B";
    public const string AmbosPossiveis = "AMBOS_POSSIVEIS";
    public const string Indeterminado = "INDETERMINADO";
}

public sealed record EvidenciaPadraoDirecionalV2(
    string Padrao,
    Guid ParadaId,
    string CodigoParada,
    double DistanciaLateralMetros,
    double CustoMembership,
    double? BearingLocalGraus,
    double? LadoAssinadoMetros,
    double QualidadeProjecao = 1,
    double? MargemAmbiguidadeMetros = null,
    double? PosicaoNormalizada = null);

public sealed record ParametrosCompeticaoDirecionalV2(
    double ZonaNeutraLadoMetros = 1,
    double DiferencaBearingOpostaMinimaGraus = 150,
    double DistanciaMaximaMetros = 40,
    double DesvantagemLateralMaximaMetros = 10,
    double DesvantagemMembershipMaxima = .10,
    double PesoMembership = .25,
    double PesoDistanciaLateral = .10,
    double MargemMinima = 1)
{
    internal void Validar()
    {
        if (ZonaNeutraLadoMetros < 0 || DiferencaBearingOpostaMinimaGraus is < 0 or > 180 ||
            DistanciaMaximaMetros <= 0 || DesvantagemLateralMaximaMetros < 0 ||
            DesvantagemMembershipMaxima < 0 || PesoMembership < 0 || PesoDistanciaLateral < 0 ||
            MargemMinima < 0)
            throw new ArgumentOutOfRangeException(nameof(ParametrosCompeticaoDirecionalV2));
    }
}

public sealed record ResultadoCompeticaoDirecionalV2(
    EvidenciaPadraoDirecionalV2 EvidenciaA,
    EvidenciaPadraoDirecionalV2 EvidenciaB,
    double ScoreA,
    double ScoreB,
    double Margem,
    double? DiferencaBearingGraus,
    string Decisao,
    IReadOnlyList<string> Motivos);

/// <summary>
/// Compara duas projeções irmãs sem consultar benchmark, persistência ou runtime.
/// Só escolhe quando bearing oposto e mudança relativa de lado concordam, preferindo
/// abstenção se distância ou membership contradisserem essa evidência.
/// </summary>
public sealed class CompetidorDirecionalV2
{
    public ResultadoCompeticaoDirecionalV2 Competir(
        EvidenciaPadraoDirecionalV2 evidenciaA,
        EvidenciaPadraoDirecionalV2 evidenciaB,
        ParametrosCompeticaoDirecionalV2? parametros = null)
    {
        ArgumentNullException.ThrowIfNull(evidenciaA);
        ArgumentNullException.ThrowIfNull(evidenciaB);
        parametros ??= new();
        parametros.Validar();
        Validar(evidenciaA);
        Validar(evidenciaB);
        if (evidenciaA.ParadaId != evidenciaB.ParadaId)
            throw new ArgumentException("A competição exige a mesma ParadaId nos dois padrões.");

        var scoreA = Score(evidenciaA, parametros);
        var scoreB = Score(evidenciaB, parametros);
        var margin = Math.Abs(scoreA - scoreB);
        var reasons = new List<string>();
        if (evidenciaA.BearingLocalGraus is not { } bearingA || evidenciaB.BearingLocalGraus is not { } bearingB ||
            evidenciaA.LadoAssinadoMetros is not { } sideA || evidenciaB.LadoAssinadoMetros is not { } sideB)
            return Result(DecisoesCompeticaoDirecionalV2.Indeterminado, "EVIDENCIA_DIRECIONAL_INDISPONIVEL");

        var bearingDifference = ArcGisParadasReconciliador.DiferencaAngular(bearingA, bearingB);
        if (Math.Abs(sideA) < parametros.ZonaNeutraLadoMetros || Math.Abs(sideB) < parametros.ZonaNeutraLadoMetros)
            return Result(DecisoesCompeticaoDirecionalV2.Indeterminado, "LADO_NA_ZONA_NEUTRA", bearingDifference);
        if (bearingDifference < parametros.DiferencaBearingOpostaMinimaGraus)
            return Result(DecisoesCompeticaoDirecionalV2.AmbosPossiveis, "BEARINGS_NAO_SAO_OPOSTOS", bearingDifference);
        if (Math.Sign(sideA) == Math.Sign(sideB))
            return Result(DecisoesCompeticaoDirecionalV2.AmbosPossiveis, "PADROES_NO_MESMO_LADO", bearingDifference);

        var rightIsA = sideA < 0;
        var right = rightIsA ? evidenciaA : evidenciaB;
        var left = rightIsA ? evidenciaB : evidenciaA;
        if (right.DistanciaLateralMetros > parametros.DistanciaMaximaMetros ||
            right.DistanciaLateralMetros - left.DistanciaLateralMetros > parametros.DesvantagemLateralMaximaMetros)
            return Result(DecisoesCompeticaoDirecionalV2.AmbosPossiveis,
                "LADO_DIREITO_COM_DESVANTAGEM_LATERAL", bearingDifference);
        if (right.CustoMembership - left.CustoMembership > parametros.DesvantagemMembershipMaxima)
            return Result(DecisoesCompeticaoDirecionalV2.AmbosPossiveis,
                "LADO_DIREITO_COM_DESVANTAGEM_MEMBERSHIP", bearingDifference);
        if (margin < parametros.MargemMinima)
            return Result(DecisoesCompeticaoDirecionalV2.AmbosPossiveis, "MARGEM_INSUFICIENTE", bearingDifference);

        return Result(rightIsA ? DecisoesCompeticaoDirecionalV2.PadraoA : DecisoesCompeticaoDirecionalV2.PadraoB,
            "OPOSICAO_BEARING_E_TROCA_RELATIVA_DE_LADO", bearingDifference);

        ResultadoCompeticaoDirecionalV2 Result(string decision, string reason, double? difference = null)
        {
            reasons.Add(reason);
            return new(evidenciaA, evidenciaB, scoreA, scoreB, margin, difference, decision, reasons);
        }
    }

    private static double Score(EvidenciaPadraoDirecionalV2 evidence, ParametrosCompeticaoDirecionalV2 p)
    {
        var side = evidence.LadoAssinadoMetros is not { } value || Math.Abs(value) < p.ZonaNeutraLadoMetros
            ? 0 : value < 0 ? 1 : -1;
        return side - p.PesoMembership * evidence.CustoMembership -
            p.PesoDistanciaLateral * Math.Clamp(evidence.DistanciaLateralMetros / p.DistanciaMaximaMetros, 0, 1);
    }

    private static void Validar(EvidenciaPadraoDirecionalV2 x)
    {
        if (string.IsNullOrWhiteSpace(x.Padrao) || string.IsNullOrWhiteSpace(x.CodigoParada) ||
            !double.IsFinite(x.DistanciaLateralMetros) || x.DistanciaLateralMetros < 0 ||
            !double.IsFinite(x.CustoMembership) || x.CustoMembership < 0 ||
            x.BearingLocalGraus is { } bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
            x.LadoAssinadoMetros is { } side && !double.IsFinite(side) ||
            !double.IsFinite(x.QualidadeProjecao) || x.QualidadeProjecao is < 0 or > 1 ||
            x.MargemAmbiguidadeMetros is < 0 || x.PosicaoNormalizada is < 0 or > 1)
            throw new ArgumentException($"Evidência direcional inválida: {x.CodigoParada}.");
    }
}
