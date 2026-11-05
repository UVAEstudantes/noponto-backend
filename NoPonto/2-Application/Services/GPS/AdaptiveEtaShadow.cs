using Npgsql;
namespace NoPonto.Application.GPS;

// Bounded, in-memory shadow only. No public/persisted payloads, no query-driven EMA updates.
public sealed class AdaptiveEtaShadow(NpgsqlDataSource? source = null)
{
    // Internal revision replay seam; live instances always default to the current policy.
    internal bool RequireObservedSupport { get; init; } = true;
    internal IRemainingTravelTimeReference HybridReference { get; init; } = new FixedTravelTimeReference();
    internal sealed record Geometry(Guid Origin, double Coordinate, double? Length = null);
    private sealed class Stream
    {
        internal string Context = "";
        internal RecentSpeedEstimator Estimator = new();
        internal RecentSpeedEstimator.Sample? Sample;
        internal RecentSpeedEstimator.Estimate Estimate = new(null, null, "seed");
        internal Guid? Anchor;
        internal double Remaining;
        internal Geometry? Geometry;
        internal double? Progress;
        internal DateTimeOffset Seen;
        internal Queue<(DateTimeOffset Time, double Metres)> Points = new();
        internal SustainedReferenceQualification Qualification = new();
        internal string StructuralKey = "";
    }
    private readonly Dictionary<string, Stream> streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<Comparison> comparisons = new();
    internal sealed record Comparison(double? CurrentSeconds, double? CandidateSeconds,
        double? RecentKmh, double? SustainedKmh, string Reason,
        DistanceAdaptiveEta.Candidate? Stage2E = null, DistanceAdaptiveEta.Candidate? Stage2F = null,
        HybridEta.Candidate? Hybrid = null);
    private static string StructuralKey(PosicaoVeiculoDto p) => $"{p.LinhaId}/{p.SentidoId}/{p.PadraoOperacionalId}/{p.PadraoVersaoId}/{p.CodigoLinha}";
    internal Comparison[] Snapshot() { lock (streams) return comparisons.ToArray(); }
    // Read-only evidence for bounded local validation; never part of the public GPS contract.
    internal (DateTimeOffset Gps, double? Ema, double? Sustained, int Samples, double Span)? Evidence(string vehicle)
    {
        lock(streams)
        {
            if(!streams.TryGetValue(vehicle,out var s) || s.Sample is null || s.Points.Count==0) return null;
            var span=(s.Sample.Gps-s.Points.Peek().Time).TotalSeconds;
            double? speed=span>=120 && s.Points.Count>=4
                ? (s.Points.Last().Metres-s.Points.Peek().Metres)*3.6/span : null;
            return (s.Sample.Gps,s.Estimate.Ema,speed,s.Points.Count,span);
        }
    }
    internal double[] SustainedSpeeds()
    {
        lock(streams) return streams.Values.Where(s => s.Points.Count >= 4
                && (s.Points.Last().Time-s.Points.Peek().Time).TotalSeconds >= 120)
            .Select(s => (s.Points.Last().Metres-s.Points.Peek().Metres)*3.6/
                (s.Points.Last().Time-s.Points.Peek().Time).TotalSeconds).ToArray();
    }

    internal sealed record GeometryCost(int Queries, int Rows, int Failures, double Milliseconds, bool BudgetExpired);
    private static string Context(ResultadoEnriquecimentoGps input)
    {
        var p=input.Posicao;
        var trip=input.ContextoOperacional?.Estado?.Observada.ViagemId ?? input.ContextoOperacional?.Observada?.ViagemId;
        return $"{p.Ordem}/{trip}/{p.LinhaId}/{p.SentidoId}/{p.PadraoOperacionalId}/{p.PadraoVersaoId}/{p.ProvedorFonte}";
    }
    private static bool Reliable(ResultadoEnriquecimentoGps input, DateTimeOffset now, PhysicalEtaOptions options)
    {
        var p=input.Posicao; var d=input.Diagnostico;
        return options.Valid() && p.TimestampGps>DateTimeOffset.UnixEpoch
            && (now-p.TimestampGps).TotalSeconds is >=0 && (now-p.TimestampGps).TotalSeconds<=options.MaxAgeSeconds
            && p.Status!=StatusVeiculo.SemSinal && p.MatchingOperacionalPlausivel
            && double.IsFinite(p.Velocidade) && p.Velocidade is >=0 and <=160
            && d?.ValidacaoTemporalPassou==true && d.BearingConfiavel
            && d.PadraoVersaoId==p.PadraoVersaoId && d.SentidoId==p.SentidoId && d.PadraoOperacionalId==p.PadraoOperacionalId
            && d.TimestampAtual==p.TimestampGps && p.ProximaOcorrenciaParadaPadraoId.HasValue
            && p.DistanciaRestanteRotaMetros is {} m && double.IsFinite(m) && m>=0;
    }
    internal async Task<GeometryCost> ObserveAsync(IReadOnlyList<ResultadoEnriquecimentoGps> inputs, DateTimeOffset now,
        PhysicalEtaOptions options, CancellationToken ct)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew(); int queries=0,rows=0,failures=0;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        foreach(var chunk in inputs.Take(2000).Chunk(200))
        {
            var geometry=new Dictionary<int,Geometry>();
            (ResultadoEnriquecimentoGps Input,int Index)[] pending;
            lock(streams) pending=chunk.Select((x,i)=>(Input:x,Index:i)).Where(x =>
                Reliable(x.Input,now,options) && x.Input.Posicao.PadraoVersaoId is {} version && version!=Guid.Empty
                && x.Input.Posicao.PosicaoNaRota is {} pos && double.IsFinite(pos) && pos is >=0 and <=1
                && !(streams.TryGetValue(x.Input.Posicao.Ordem,out var s) && s.Context==Context(x.Input)
                    && s.Sample?.Gps==x.Input.Posicao.TimestampGps)).ToArray();
            if(source is not null && pending.Length>0 && !budget.IsCancellationRequested)
                try
                {
                    queries++;
                    await using var command=source.CreateCommand(GeometrySql);
                    command.CommandTimeout=3;
                    command.Parameters.AddWithValue("versions",pending.Select(x=>x.Input.Posicao.PadraoVersaoId!.Value).ToArray());
                    command.Parameters.AddWithValue("positions",pending.Select(x=>x.Input.Posicao.PosicaoNaRota!.Value).ToArray());
                    await using var reader=await command.ExecuteReaderAsync(budget.Token);
                    while(await reader.ReadAsync(budget.Token))
                    {
                        var index=pending[(int)reader.GetInt64(0)-1].Index;
                        if(!reader.IsDBNull(1)&&!reader.IsDBNull(2)) { geometry[index]=new(chunk[index].Posicao.PadraoVersaoId!.Value,reader.GetDouble(1),reader.GetDouble(2)); rows++; }
                    }
                }
                catch(Exception) when(!ct.IsCancellationRequested) { failures++; /* Shadow failure never changes operational ETA. */ }
            for(var i=0;i<chunk.Length;i++) Observe(chunk[i],now,options,geometry.GetValueOrDefault(i));
        }
        return new(queries,rows,failures,timer.Elapsed.TotalMilliseconds,budget.IsCancellationRequested);
    }

    // Compute each route length once per batch, each repeated projection once.
    internal const string GeometrySql = """
        WITH input AS MATERIALIZED (
            SELECT * FROM unnest(@versions::uuid[],@positions::double precision[]) WITH ORDINALITY x(version,pos,n)
            WHERE pos>=0 AND pos<=1
        ), routes AS MATERIALIZED (
            SELECT v."Id" version,v."Geometria" geometry,ST_Length(v."Geometria"::geography) length
            FROM "PadroesVersoes" v JOIN (SELECT DISTINCT version FROM input) x ON v."Id"=x.version
        ), projections AS MATERIALIZED (
            SELECT x.version,x.pos,ST_Length(ST_LineSubstring(r.geometry,0,x.pos)::geography) prefix
            FROM (SELECT DISTINCT version,pos FROM input) x JOIN routes r ON r.version=x.version
        )
        SELECT x.n,p.prefix,r.length FROM input x JOIN projections p ON p.version=x.version AND p.pos=x.pos
        JOIN routes r ON r.version=x.version
        """;

    internal void Observe(ResultadoEnriquecimentoGps input, DateTimeOffset now, PhysicalEtaOptions options, Geometry? geometry = null)
    {
        lock (streams)
        {
            foreach (var key in streams.Where(x => now - x.Value.Seen > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray()) streams.Remove(key);
            var p = input.Posicao; var d = input.Diagnostico;
            var context = Context(input);
            var reliable = Reliable(input,now,options);
            if (!reliable) { streams.Remove(p.Ordem); return; }
            if (!streams.TryGetValue(p.Ordem, out var s) || s.Context != context)
            {
                if (streams.Count >= 2000) streams.Remove(streams.MinBy(x => x.Value.Seen).Key);
                streams[p.Ordem] = s = new() { Context = context };
            }
            if (s.Sample?.Gps == p.TimestampGps) return;
            var dt = s.Sample is null ? 0 : (p.TimestampGps-s.Sample.Gps).TotalSeconds;
            // A common geometric origin survives anchor changes. Fractions are never converted to metres.
            var metres = s.Sample is not null && s.Anchor == p.ProximaOcorrenciaParadaPadraoId
                ? s.Remaining-p.DistanciaRestanteRotaMetros!.Value : (double?)null;
            if(geometry is { } g && double.IsFinite(g.Coordinate) && s.Geometry is { } old && old.Origin==g.Origin)
            {
                metres=g.Coordinate-old.Coordinate;
                if(metres<0 && p.TopologiaPadrao=="CIRCULAR" && d is {Circular:true, MotivoTemporal:MotivoValidacaoTemporalGps.Accepted}
                    && s.Progress>d.PosicaoNova && d.PosicaoNova==p.PosicaoNaRota
                    && d.PosicaoAnterior==s.Progress && d.TimestampAnterior==s.Sample?.Gps
                    && g.Length is {} length && double.IsFinite(length) && length>0 && old.Length==length
                    && old.Coordinate>=0 && old.Coordinate<=length && g.Coordinate>=0 && g.Coordinate<=length)
                    metres+=length;
            }
            if (dt <= 0 || dt > 180 || metres is null || metres < 0 || metres*3.6/dt > 160)
            { s.Estimator = new(); s.Points.Clear(); s.Qualification=new(); metres = null; }
            var sample = new RecentSpeedEstimator.Sample(context, p.TimestampGps, p.Velocidade, true, metres);
            var estimate = s.Estimator.Observe(sample, now, options.MaxAgeSeconds);
            if (estimate.Reason is "invalid" or "outlier") { streams.Remove(p.Ordem); return; }
            var total = (s.Points.Count > 0 ? s.Points.Last().Metres : 0) + (metres ?? 0);
            s.Points.Enqueue((p.TimestampGps, total));
            while (s.Points.Count > 32 || p.TimestampGps-s.Points.Peek().Time > TimeSpan.FromMinutes(5)) s.Points.Dequeue();
            var referenceSpan=(p.TimestampGps-s.Points.Peek().Time).TotalSeconds;
            s.Qualification.Observe(p.TimestampGps,metres,p.Velocidade,estimate.Ema,
                referenceSpan>=120 && s.Points.Count>=4 ? (total-s.Points.Peek().Metres)*3.6/referenceSpan : null,
                s.Points.Peek().Time);
            s.Sample=sample; s.Estimate=estimate; s.Anchor=p.ProximaOcorrenciaParadaPadraoId;
            s.Remaining=p.DistanciaRestanteRotaMetros!.Value; s.Seen=now;
            s.Geometry=geometry; s.Progress=p.PosicaoNaRota;
            s.StructuralKey=StructuralKey(p);
        }
    }

    internal Comparison Compare(PosicaoVeiculoDto p, double? targetMetres, double? currentSeconds,
        DateTimeOffset now, PhysicalEtaOptions options, Guid? targetOccurrenceId = null)
    {
        DistanceAdaptiveEta.Candidate stageE = new(null,"uncertain","history_absent"),stageF=stageE;
        RecentSpeedEstimator.Sample? sample=null;RecentSpeedEstimator.Estimate? estimate=null;
        var eligible=PhysicalEta.CapturedSpeed(p,now,options).HasValue;
        lock (streams)
        {
            if (streams.TryGetValue(p.Ordem,out var s) && s.Sample?.Gps == p.TimestampGps)
            {
                var span = (s.Sample.Gps-s.Points.Peek().Time).TotalSeconds;
                var reference = span >= 120 && s.Points.Count >= 4
                    ? new DistanceAdaptiveEta.Reference(s.Context,s.Sample.Gps,
                        (s.Points.Last().Metres-s.Points.Peek().Metres)*3.6/span,s.Points.Count,span,true,s.Qualification.Qualified,
                        SustainedReferenceSupport.From(s.Points.ToArray())) : null;
                stageE=DistanceAdaptiveEta.Predict(s.Sample,s.Estimate,targetMetres ?? double.NaN,now,eligible,reference,false);
                stageF=DistanceAdaptiveEta.Predict(s.Sample,s.Estimate,targetMetres ?? double.NaN,now,eligible,reference,true);
                if(s.StructuralKey==StructuralKey(p)) {sample=s.Sample;estimate=s.Estimate;}
            }
        }
        // Reference lookup is outside the history lock and has a nonblocking/no-I/O contract.
        var hybrid=(options.HybridShadowEnabled || options.HybridPublicEnabled) ? HybridEta.Predict(sample,estimate,
            new(p.PadraoVersaoId??Guid.Empty,p.SentidoId??Guid.Empty,targetOccurrenceId,p.PosicaoNaRota??double.NaN,0,0),
            targetMetres??double.NaN,now,eligible,options,HybridReference) : null;
        var candidate=RequireObservedSupport?stageF:stageE;
        lock(streams)
        {
            var comparison=new Comparison(currentSeconds,candidate.Seconds,candidate.RecentKmh,candidate.ReferenceKmh,candidate.Reason,stageE,stageF,hybrid);
            comparisons.Enqueue(comparison);
            while(comparisons.Count>512) comparisons.Dequeue();
            return comparison;
        }
    }
}
