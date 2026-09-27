namespace NoPonto.Application.GTFS;

public static class ClassificacoesSuporteSequencialV2
{
    public const string ExclusivoA = "EXCLUSIVO_A";
    public const string ExclusivoB = "EXCLUSIVO_B";
    public const string Compartilhado = "COMPARTILHADO";
    public const string Ambiguo = "AMBIGUO";
}

public sealed record OcorrenciaCaminhoV2(
    Guid ParadaId,
    string CodigoParada,
    double DistanciaAcumuladaMetros,
    double PosicaoNormalizada);

public sealed record ParametrosSuporteSequencialV2(
    int TamanhoMinimoBloco = 2,
    int MaximoItensEntreCompartilhadas = 2,
    double DeltaProgressoMaximoBlocoMetros = 1_500)
{
    internal void Validar()
    {
        if (TamanhoMinimoBloco < 2 || MaximoItensEntreCompartilhadas < 0 ||
            DeltaProgressoMaximoBlocoMetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(ParametrosSuporteSequencialV2));
    }
}

public sealed record DiagnosticoSuporteCaminhoV2(
    string? Predecessor,
    string? Sucessor,
    double? DeltaPredecessorMetros,
    double? DeltaSucessorMetros,
    int Indice);

public sealed record ResultadoSuporteParadaV2(
    Guid ParadaId,
    string CodigoParada,
    string Classificacao,
    DiagnosticoSuporteCaminhoV2? SuporteA,
    DiagnosticoSuporteCaminhoV2? SuporteB,
    int TamanhoBlocoCompartilhado,
    string? OrdemBloco,
    string DecisaoDirecionalLocal,
    IReadOnlyList<string> Motivos);

public sealed record ResultadoSuporteSequencialV2(
    IReadOnlyList<ResultadoSuporteParadaV2> Paradas,
    int QuantidadeBlocosCompartilhados);

/// <summary>
/// Diagnostica membership entre dois caminhos lineares já selecionados. Blocos
/// coerentes nos dois caminhos prevalecem sobre uma competição direcional local.
/// </summary>
public sealed class SuporteSequencialPadroesIrmaosV2
{
    public ResultadoSuporteSequencialV2 Analisar(
        IReadOnlyList<OcorrenciaCaminhoV2> caminhoA,
        IReadOnlyList<OcorrenciaCaminhoV2> caminhoB,
        IReadOnlyDictionary<Guid, ResultadoCompeticaoDirecionalV2>? competicoes = null,
        ParametrosSuporteSequencialV2? parametros = null)
    {
        ArgumentNullException.ThrowIfNull(caminhoA);
        ArgumentNullException.ThrowIfNull(caminhoB);
        parametros ??= new();
        parametros.Validar();
        Validar(caminhoA, nameof(caminhoA));
        Validar(caminhoB, nameof(caminhoB));
        competicoes ??= new Dictionary<Guid, ResultadoCompeticaoDirecionalV2>();

        var byA = caminhoA.Select((x, i) => (x, i)).ToDictionary(x => x.x.ParadaId);
        var byB = caminhoB.Select((x, i) => (x, i)).ToDictionary(x => x.x.ParadaId);
        var shared = byA.Keys.Intersect(byB.Keys).ToHashSet();
        var adjacencyA = Adjacencias(caminhoA, shared, parametros);
        var adjacencyB = Adjacencias(caminhoB, shared, parametros);
        var graph = shared.ToDictionary(x => x, _ => new HashSet<Guid>());
        foreach (var pair in adjacencyA)
            if (adjacencyB.Contains(Normalizar(pair.Item1, pair.Item2)))
            {
                graph[pair.Item1].Add(pair.Item2);
                graph[pair.Item2].Add(pair.Item1);
            }

        var blocks = new Dictionary<Guid, (int Id, int Size, string Order)>();
        var visited = new HashSet<Guid>();
        var blockId = 0;
        foreach (var start in shared.OrderBy(x => byA[x].i))
        {
            if (!visited.Add(start)) continue;
            var component = new List<Guid>();
            var queue = new Queue<Guid>(); queue.Enqueue(start);
            while (queue.TryDequeue(out var current))
            {
                component.Add(current);
                foreach (var next in graph[current]) if (visited.Add(next)) queue.Enqueue(next);
            }
            if (component.Count < parametros.TamanhoMinimoBloco) continue;
            blockId++;
            var orderedA = component.OrderBy(x => byA[x].i).ToArray();
            var indexesB = orderedA.Select(x => byB[x].i).ToArray();
            var order = indexesB.SequenceEqual(indexesB.Order()) ? "DIRETA"
                : indexesB.SequenceEqual(indexesB.OrderDescending()) ? "REVERSA" : "MISTA";
            foreach (var id in component) blocks[id] = (blockId, component.Count, order);
        }

        var ids = byA.Keys.Union(byB.Keys).OrderBy(x => byA.TryGetValue(x, out var a) ? a.i : int.MaxValue)
            .ThenBy(x => byB.TryGetValue(x, out var b) ? b.i : int.MaxValue).ToArray();
        var results = new List<ResultadoSuporteParadaV2>();
        foreach (var id in ids)
        {
            var inA = byA.TryGetValue(id, out var a);
            var inB = byB.TryGetValue(id, out var b);
            var code = inA ? a.x.CodigoParada : b.x.CodigoParada;
            var decision = competicoes.TryGetValue(id, out var competition)
                ? competition.Decisao : DecisoesCompeticaoDirecionalV2.Indeterminado;
            var reasons = new List<string>();
            string classification;
            if (inA && !inB) { classification = ClassificacoesSuporteSequencialV2.ExclusivoA; reasons.Add("PRESENTE_SOMENTE_NO_CAMINHO_A"); }
            else if (!inA) { classification = ClassificacoesSuporteSequencialV2.ExclusivoB; reasons.Add("PRESENTE_SOMENTE_NO_CAMINHO_B"); }
            else if (blocks.TryGetValue(id, out var block))
            {
                classification = ClassificacoesSuporteSequencialV2.Compartilhado;
                reasons.Add("BLOCO_SEQUENCIAL_COERENTE_NOS_DOIS_PADROES");
            }
            else if (decision == DecisoesCompeticaoDirecionalV2.PadraoA)
            { classification = ClassificacoesSuporteSequencialV2.ExclusivoA; reasons.Add("COMPETICAO_DIRECIONAL_FAVORECE_A_SEM_BLOCO"); }
            else if (decision == DecisoesCompeticaoDirecionalV2.PadraoB)
            { classification = ClassificacoesSuporteSequencialV2.ExclusivoB; reasons.Add("COMPETICAO_DIRECIONAL_FAVORECE_B_SEM_BLOCO"); }
            else { classification = ClassificacoesSuporteSequencialV2.Ambiguo; reasons.Add("SUPORTE_INSUFICIENTE_PARA_EXCLUSIVIDADE"); }

            results.Add(new(id, code, classification,
                inA ? Diagnostico(caminhoA, a.i) : null,
                inB ? Diagnostico(caminhoB, b.i) : null,
                blocks.TryGetValue(id, out var info) ? info.Size : 0,
                blocks.TryGetValue(id, out info) ? info.Order : null,
                decision, reasons));
        }
        return new(results, blocks.Values.Select(x => x.Id).Distinct().Count());
    }

    private static HashSet<(Guid, Guid)> Adjacencias(IReadOnlyList<OcorrenciaCaminhoV2> path,
        HashSet<Guid> shared, ParametrosSuporteSequencialV2 p)
    {
        var selected = path.Select((x, i) => (x, i)).Where(x => shared.Contains(x.x.ParadaId)).ToArray();
        var result = new HashSet<(Guid, Guid)>();
        for (var i = 1; i < selected.Length; i++)
        {
            var first = selected[i - 1]; var second = selected[i];
            if (second.i - first.i - 1 > p.MaximoItensEntreCompartilhadas ||
                second.x.DistanciaAcumuladaMetros - first.x.DistanciaAcumuladaMetros > p.DeltaProgressoMaximoBlocoMetros)
                continue;
            result.Add(Normalizar(first.x.ParadaId, second.x.ParadaId));
        }
        return result;
    }

    private static (Guid, Guid) Normalizar(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? (first, second) : (second, first);

    private static DiagnosticoSuporteCaminhoV2 Diagnostico(IReadOnlyList<OcorrenciaCaminhoV2> path, int index) =>
        new(index > 0 ? path[index - 1].CodigoParada : null,
            index + 1 < path.Count ? path[index + 1].CodigoParada : null,
            index > 0 ? path[index].DistanciaAcumuladaMetros - path[index - 1].DistanciaAcumuladaMetros : null,
            index + 1 < path.Count ? path[index + 1].DistanciaAcumuladaMetros - path[index].DistanciaAcumuladaMetros : null,
            index);

    private static void Validar(IReadOnlyList<OcorrenciaCaminhoV2> path, string name)
    {
        if (path.GroupBy(x => x.ParadaId).Any(x => x.Count() > 1) ||
            path.Any(x => string.IsNullOrWhiteSpace(x.CodigoParada) || !double.IsFinite(x.DistanciaAcumuladaMetros) ||
                x.DistanciaAcumuladaMetros < 0 || !double.IsFinite(x.PosicaoNormalizada) || x.PosicaoNormalizada is < 0 or > 1) ||
            path.Zip(path.Skip(1)).Any(x => x.First.DistanciaAcumuladaMetros >= x.Second.DistanciaAcumuladaMetros))
            throw new ArgumentException("O caminho deve ser linear, único e estritamente crescente.", name);
    }
}
