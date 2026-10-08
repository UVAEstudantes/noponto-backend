using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;

namespace NoPonto.Application.GPS;

public sealed class HistoricalEtaShadowOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://eta-history:5200";
    public int QueueCapacity { get; set; } = 1000;
    public int BatchSize { get; set; } = 200;
    public int TimeoutMs { get; set; } = 1000;
    public int CircuitSeconds { get; set; } = 30;
    public int MaxAgeSeconds { get; set; } = 30;
    public int SinkCapacity { get; set; } = 500;
    public int SinkTtlSeconds { get; set; } = 300;
    public int MetricSampleCapacity { get; set; } = 1024;
    public bool MetricsLogEnabled { get; set; } = true;
    public int MetricsLogSeconds { get; set; } = 60;
    public bool Valid() => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        && QueueCapacity is >= 1 and <= 10000 && BatchSize is >= 1 and <= 200
        && TimeoutMs is >= 10 and <= 2000 && CircuitSeconds is >= 1 and <= 300
        && MaxAgeSeconds is >= 1 and <= 300 && SinkCapacity is >= 1 and <= 5000 && SinkTtlSeconds is >= 1 and <= 3600
        && MetricSampleCapacity is >=32 and <=4096 && MetricsLogSeconds is >=30 and <=3600;
}

public sealed record HistoricalEtaObservation(string RequestId, string ObservationId, PosicaoVeiculoDto Gps,
    Guid TripId, int Lap, OcorrenciaParada Target, DateTimeOffset CapturedAt, double? PublicEta);
public sealed record HistoricalEtaGeometry(Guid TargetId, Guid StopId, Guid VersionId, Guid PatternId,
    Guid LineId, Guid DirectionId, string LineCode, string Topology, double Destination, double Distance);
public sealed record HistoricalEtaResult(string RequestId, string ObservationId, string Vehicle, DateTimeOffset TimestampGps,
    Guid TargetId, Guid VersionId, int Lap, string Status, string? ModelVersion, double? HistoricalEta,
    double? PublicEta, double LatencyMs, DateTimeOffset RecordedAt);

public static class HistoricalEtaAdapter
{
    public const string Contract = "noponto-eta-shadow-history-v1";
    public static HistoricalEtaObservation? Capture(PosicaoVeiculoDto gps, ViagemObservadaResultado? trip, DateTimeOffset now)
    {
        if (trip?.EstadoOperacional is not { } op || trip.Value.Estado is not { } state
            || trip.Value.Status is not (ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated)
            || op.Observada != state || !ViagemOperacionalRegra.IdentidadeConfiavel(op)
            || op.Estado is not (EstadoViagem.Ativa or EstadoViagem.PossivelFim)
            || trip.Value.ProximaOcorrenciaOperacional is not { } target
            || gps.TimestampGps != state.TimestampUltimaAtualizacao || gps.Ordem != state.OrdemVeiculo
            || gps.LinhaId != op.LinhaId || gps.SentidoId != op.SentidoId || gps.CodigoLinha != op.CodigoLinha
            || gps.PadraoOperacionalId != state.PadraoOperacionalId || gps.PadraoVersaoId != state.PadraoVersaoId
            || gps.TopologiaPadrao != state.Topologia || target.PadraoVersaoId != state.PadraoVersaoId
            || target.Id == Guid.Empty || target.ParadaId == Guid.Empty || state.ViagemId == Guid.Empty || state.Volta < 0
            || gps.LinhaId == Guid.Empty || gps.SentidoId == Guid.Empty
            || gps.PadraoOperacionalId == Guid.Empty || gps.PadraoVersaoId == Guid.Empty
            || target.Volta != state.Volta || gps.ModalFonte is not ("ONIBUS" or "BRT")
            || string.IsNullOrWhiteSpace(gps.ProvedorFonte) || string.IsNullOrWhiteSpace(gps.Ordem)
            || gps.TimestampGps <= DateTimeOffset.UnixEpoch || gps.TimestampGps > now
            || gps.PosicaoNaRota is not { } p || !double.IsFinite(p) || p < 0 || p >= target.PosicaoLinha
            || !double.IsFinite(target.PosicaoLinha) || target.PosicaoLinha > 1)
            return null;
        var observation = TelemetriaMlContrato.ObservacaoId(gps.ModalFonte, gps.ProvedorFonte, gps.Ordem, gps.TimestampGps);
        var request = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Contract}|{observation}|{target.Id:D}|{state.PadraoVersaoId:D}|{state.Volta}"))).ToLowerInvariant();
        // The public prediction targets the observational matching occurrence.
        // Never compare it with an operational target that differs.
        var eta = gps.ProximaOcorrenciaParadaPadraoId == target.Id
            && gps.EtaProximaParadaSegundos is { } value && double.IsFinite(value) && value >= 0 ? value : (double?)null;
        return new(request, observation, gps with { }, state.ViagemId, state.Volta, target, now, eta);
    }

    public static Dictionary<string, object?>? Payload(HistoricalEtaObservation o, HistoricalEtaGeometry g)
    {
        var p = o.Gps;
        if (g.TargetId != o.Target.Id || g.StopId != o.Target.ParadaId || g.VersionId != p.PadraoVersaoId
            || g.PatternId != p.PadraoOperacionalId || g.LineId != p.LinhaId || g.DirectionId != p.SentidoId
            || g.LineCode != p.CodigoLinha || g.Topology != p.TopologiaPadrao
            || g.Topology is not ("LINEAR" or "CIRCULAR") || !double.IsFinite(g.Destination)
            || Math.Abs(g.Destination-o.Target.PosicaoLinha)>1e-8
            || !double.IsFinite(g.Distance) || g.Distance <= 0 || p.ComprimentoRotaMetros is not { } length
            || !double.IsFinite(length) || length <= 0 || g.Distance > length + 10)
            return null;
        var local = p.TimestampGps.ToOffset(TimeSpan.FromHours(-3));
        static double? Speed(double? speed) => speed is { } x && double.IsFinite(x) && x is >= 0 and <= 160 ? x : null;
        return new()
        {
            ["shadow_contract"] = Contract, ["shadow_request_id"] = o.RequestId,
            ["modal"] = p.ModalFonte, ["codigo_linha"] = p.CodigoLinha, ["linha_id"] = g.LineId,
            ["sentido_id"] = g.DirectionId, ["padrao_id"] = g.PatternId, ["versao_id"] = g.VersionId,
            ["ocorrencia_id"] = g.TargetId, ["parada_id"] = g.StopId, ["topologia"] = g.Topology,
            ["posicao_gps"] = p.PosicaoNaRota, ["posicao_destino"] = g.Destination,
            ["distancia_metros"] = g.Distance, ["velocidade_kmh"] = Speed(p.Velocidade),
            ["velocidade_media_causal_kmh"] = Speed(p.VelocidadeMedia), ["hora_dia"] = local.Hour,
            ["dia_semana"] = (int)local.DayOfWeek
        };
    }
}

public interface IHistoricalEtaGeometry
{
    Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> items, CancellationToken ct);
}

public sealed class HistoricalEtaGeometryQuery(NpgsqlDataSource source) : IHistoricalEtaGeometry
{
    internal const string Sql = """
        SELECT x.n,o."Id",o."ParadaId",v."Id",p."Id",l."Id",s."Id",l."Codigo",v."Topologia",o."PosicaoTracado",
        CASE WHEN x.pos>=0 AND x.pos<o."PosicaoTracado" AND o."PosicaoTracado"<=1
        THEN ST_Length(ST_LineSubstring(v."Geometria",x.pos,o."PosicaoTracado")::geography) END
        FROM unnest(@targets::uuid[],@versions::uuid[],@positions::double precision[]) WITH ORDINALITY x(target,version,pos,n)
        JOIN "OcorrenciasParadasPadroes" o ON o."Id"=x.target AND o."PadraoVersaoId"=x.version
        JOIN "PadroesVersoes" v ON v."Id"=o."PadraoVersaoId"
        JOIN "PadroesOperacionais" p ON p."Id"=v."PadraoOperacionalId"
        JOIN "Sentidos" s ON s."Id"=p."SentidoId" JOIN "Linhas" l ON l."Id"=s."LinhaId"
        ORDER BY x.n
        """;
    public async Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> items, CancellationToken ct)
    {
        if (items.Count is <1 or >200) throw new ArgumentException("Batch bound");
        var results = new HistoricalEtaGeometry?[items.Count];
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='500ms'; SET LOCAL lock_timeout='100ms'; SET LOCAL search_path=public,pg_catalog;",conn,tx))
            await settings.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(Sql,conn,tx) { CommandTimeout = 1 };
        command.Parameters.AddWithValue("targets",items.Select(x=>x.Target.Id).ToArray());
        command.Parameters.AddWithValue("versions",items.Select(x=>x.Target.PadraoVersaoId).ToArray());
        command.Parameters.AddWithValue("positions",items.Select(x=>x.Gps.PosicaoNaRota!.Value).ToArray());
        await using (var reader=await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(10)) results[checked((int)reader.GetInt64(0))-1]=new(reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),
                    reader.GetGuid(4),reader.GetGuid(5),reader.GetGuid(6),reader.GetString(7),reader.GetString(8),reader.GetDouble(9),reader.GetDouble(10));
        await tx.RollbackAsync(ct);
        return results;
    }
}

public sealed class HistoricalEtaShadow : BackgroundService
{
    readonly HistoricalEtaShadowOptions options;
    readonly IHistoricalEtaGeometry geometry;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly Channel<HistoricalEtaObservation> queue;
    readonly Queue<HistoricalEtaResult> sink = new();
    readonly object gate = new();
    readonly ConcurrentDictionary<string,long> counters = new();
    readonly HistoricalEtaOperationalStatistics statistics;
    readonly ILogger<HistoricalEtaShadow>? logger;
    readonly DateTimeOffset metricsStartedAt;
    int awaitingRecovery;
    int queueHighWater;
    long circuitUntil;
    int depth;
    int accepting=1;
    public HistoricalEtaShadow(IOptions<HistoricalEtaShadowOptions> settings, IHistoricalEtaGeometry geometry,
        IHttpClientFactory clients, TimeProvider? clock=null, ILogger<HistoricalEtaShadow>? logger=null)
    {
        options=settings.Value; if(!options.Valid()) throw new ArgumentException("Invalid historical Shadow options");
        this.geometry=geometry; this.clock=clock??TimeProvider.System;
        this.logger=logger;metricsStartedAt=this.clock.GetUtcNow();statistics=new(options.MetricSampleCapacity);
        http=clients.CreateClient("eta-history-shadow"); http.BaseAddress=new Uri(options.BaseUrl);
        queue=Channel.CreateBounded<HistoricalEtaObservation>(new BoundedChannelOptions(options.QueueCapacity)
            {FullMode=BoundedChannelFullMode.Wait,SingleReader=true,SingleWriter=false});
    }
    void Count(string key) => counters.AddOrUpdate(key,1,(_,n)=>n+1);
    public IReadOnlyDictionary<string,long> Metrics => new Dictionary<string,long>(counters.ToArray()){["queue_depth"]=Volatile.Read(ref depth)};
    public object OperationalSnapshot {
        get {
            var c=Metrics;var elapsed=Math.Max(0,(clock.GetUtcNow()-metricsStartedAt).TotalSeconds);
            double? Rate(string numerator,string denominator)=>c.GetValueOrDefault(denominator)==0?null:(double)c.GetValueOrDefault(numerator)/c.GetValueOrDefault(denominator);
            return new{schema="noponto-eta-shadow-operations-v1",at_utc=clock.GetUtcNow(),enabled=options.Enabled,
                scope="instance_lifetime_counters_bounded_recent_samples",uptime_seconds=elapsed,counters=c,
                candidate_per_second=elapsed==0?(double?)null:c.GetValueOrDefault("candidate")/elapsed,
                eligible_rate=Rate("eligible","candidate"),enqueue_rate=Rate("enqueued","eligible"),
                operational_coverage=Rate("success","candidate"),comparable_prediction_rate=Rate("comparable_prediction","success"),
                queue_capacity=options.QueueCapacity,queue_depth=Math.Clamp(Volatile.Read(ref depth),0,options.QueueCapacity),
                queue_high_water=Math.Min(Volatile.Read(ref queueHighWater),options.QueueCapacity),
                queue_occupancy=(double)Math.Clamp(Volatile.Read(ref depth),0,options.QueueCapacity)/options.QueueCapacity,
                circuit_state=!options.Enabled?"disabled":Volatile.Read(ref accepting)==0?"stopped":
                    clock.GetUtcNow().ToUnixTimeMilliseconds()<Interlocked.Read(ref circuitUntil)?"open":Volatile.Read(ref awaitingRecovery)==1?"probe_allowed":"closed",
                aggregation=statistics.Snapshot()};
        }
    }
    void OpenCircuit(){Count("circuit_openings");Interlocked.Exchange(ref awaitingRecovery,1);Interlocked.Exchange(ref circuitUntil,clock.GetUtcNow().AddSeconds(options.CircuitSeconds).ToUnixTimeMilliseconds());}
    void EndToEnd(IReadOnlyList<HistoricalEtaObservation> batch){foreach(var o in batch)statistics.Sample("end_to_end_ms",Math.Max(0,(clock.GetUtcNow()-o.CapturedAt).TotalMilliseconds));}
    public IReadOnlyList<HistoricalEtaResult> Results { get {lock(gate){Purge();return sink.ToArray();}} }
    void Purge() {while(sink.TryPeek(out var r) && r.RecordedAt<clock.GetUtcNow().AddSeconds(-options.SinkTtlSeconds)) sink.Dequeue();}
    void Record(HistoricalEtaObservation o,string status,double latency=0,string? model=null,double? prediction=null)
    {
        Count(status);
        if(status=="success"&&prediction is { } historical&&o.PublicEta is { } current){
            Count("comparable_prediction");statistics.Sample("prediction_difference_seconds",historical-current);
            statistics.Sample("absolute_prediction_difference_seconds",Math.Abs(historical-current));
        }
        if(!Monitor.TryEnter(gate)){Count("sink_contention_drop");return;}
        try{Purge();while(sink.Count>=options.SinkCapacity){sink.Dequeue();Count("sink_evicted");}sink.Enqueue(new(o.RequestId,o.ObservationId,o.Gps.Ordem,
            o.Gps.TimestampGps,o.Target.Id,o.Target.PadraoVersaoId,o.Lap,status,model,prediction,o.PublicEta,latency,clock.GetUtcNow()));}
        finally{Monitor.Exit(gate);}
    }
    public bool TryCapture(PosicaoVeiculoDto gps,ViagemObservadaResultado? trip)
    {
        if(!options.Enabled) return false;
        try {
            Count("candidate"); var o=HistoricalEtaAdapter.Capture(gps,trip,clock.GetUtcNow());
            if(o is null){Count("ineligible");Count(trip is null?"rejected.identity":trip?.ProximaOcorrenciaOperacional is null?"rejected.missing_target":
                gps.TimestampGps>clock.GetUtcNow()||gps.TimestampGps<=DateTimeOffset.UnixEpoch?"rejected.gps_time":
                gps.PosicaoNaRota is not { } p||!double.IsFinite(p)||p<0||p>=trip.Value.ProximaOcorrenciaOperacional!.PosicaoLinha?"rejected.position_or_wrap":"rejected.identity");return false;}
            Count("eligible");
            if(Volatile.Read(ref accepting)==0){Record(o,"stopped");return false;}
            if(clock.GetUtcNow().ToUnixTimeMilliseconds()<Interlocked.Read(ref circuitUntil)){Record(o,"circuit_open");return false;}
            Interlocked.Increment(ref depth);
            if(queue.Writer.TryWrite(o)){Count("enqueued");int current=Volatile.Read(ref depth),previous;
                do{previous=Volatile.Read(ref queueHighWater);if(previous>=current)break;}while(Interlocked.CompareExchange(ref queueHighWater,current,previous)!=previous);
                return true;}
            Interlocked.Decrement(ref depth); Record(o,Volatile.Read(ref accepting)==0?"stopped":"queue_full"); return false;
        } catch {Count("capture_failure");return false;}
    }
    internal async Task ProcessAsync(IReadOnlyList<HistoricalEtaObservation> batch,CancellationToken stop)
    {
        if(batch.Count is <1 or >200) throw new ArgumentException("Batch bound");
        var start=Stopwatch.GetTimestamp(); var pending=new List<HistoricalEtaObservation>();
        Count("batches");counters.AddOrUpdate("batch_observations",batch.Count,(_,n)=>n+batch.Count);
        foreach(var o in batch)
        {
            statistics.Sample("queue_wait_ms",Math.Max(0,(clock.GetUtcNow()-o.CapturedAt).TotalMilliseconds));
            if(clock.GetUtcNow()-o.CapturedAt>TimeSpan.FromSeconds(options.MaxAgeSeconds)) Record(o,"expired");
            else if(clock.GetUtcNow().ToUnixTimeMilliseconds()<Interlocked.Read(ref circuitUntil))Record(o,"circuit_open");
            else pending.Add(o);
        }
        if(pending.Count==0){statistics.Sample("processing_ms",Stopwatch.GetElapsedTime(start).TotalMilliseconds);EndToEnd(batch);return;}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(options.TimeoutMs);
        bool mlAttempted=false;
        try {
            IReadOnlyList<HistoricalEtaGeometry?> resolved;
            var resolverStart=Stopwatch.GetTimestamp();
            try{resolved=await geometry.ResolveAsync(pending,timeout.Token);}
            finally{statistics.Sample("resolver_ms",Stopwatch.GetElapsedTime(resolverStart).TotalMilliseconds);}
            if(resolved.Count!=pending.Count)throw new InvalidOperationException("Geometry batch size mismatch");
            var sent=new List<HistoricalEtaObservation>();var payload=new List<Dictionary<string,object?>>();
            for(var i=0;i<pending.Count;i++){
                var row=resolved[i] is { } g?HistoricalEtaAdapter.Payload(pending[i],g):null;
                if(row is null)Record(pending[i],"geometry_refused");else{sent.Add(pending[i]);payload.Add(row);}
            }
            pending=sent;
            if(sent.Count==0)return;
            Count("http_batches");counters.AddOrUpdate("http_observations",sent.Count,(_,n)=>n+sent.Count);
            mlAttempted=true;
            var httpStart=Stopwatch.GetTimestamp();
            try {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/eta/batch"){Content=JsonContent.Create(payload)};
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode){
                Count(response.StatusCode==System.Net.HttpStatusCode.ServiceUnavailable?"http_503_batches":"http_failure_batches");
                foreach(var o in sent)Record(o,response.StatusCode==System.Net.HttpStatusCode.ServiceUnavailable?"model_unavailable":"http_failure",Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                statistics.Model(response.StatusCode==System.Net.HttpStatusCode.ServiceUnavailable?"unavailable":"http_failure",clock.GetUtcNow());OpenCircuit();return;
            }
            var model=response.Headers.TryGetValues("X-Model-Version",out var versions)?versions.SingleOrDefault():null;
            if(model is null || model.Length!=64 || !model.All(Uri.IsHexDigit)
                || !response.Headers.TryGetValues("X-Model-Data-Kind",out var kinds) || kinds.SingleOrDefault()!="real")
                throw new InvalidOperationException("Untrusted model metadata");
            if(response.Content.Headers.ContentLength>256*1024)throw new InvalidOperationException("Response bound");
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
            using var content=new MemoryStream();var buffer=new byte[8192];int read;
            while((read=await stream.ReadAsync(buffer,timeout.Token))>0){if(content.Length+read>256*1024)throw new InvalidOperationException("Response bound");content.Write(buffer,0,read);}
            using var json=JsonDocument.Parse(content.ToArray());var rows=json.RootElement.EnumerateArray().ToArray();
            if(rows.Length!=sent.Count)throw new InvalidOperationException("Response size mismatch");
            // Validate the WHOLE batch before recording a prediction.
            for(var i=0;i<rows.Length;i++)
                if(rows[i].GetProperty("shadow_request_id").GetString()!=sent[i].RequestId
                    || !rows[i].GetProperty("eta_segundos").TryGetDouble(out var eta)||!double.IsFinite(eta)||eta<0)
                    throw new InvalidOperationException("Response correlation/ETA invalid");
            statistics.Model("available",clock.GetUtcNow(),model);
            for(var i=0;i<rows.Length;i++)Record(sent[i],"success",Stopwatch.GetElapsedTime(start).TotalMilliseconds,model,rows[i].GetProperty("eta_segundos").GetDouble());
            Count("valid_response_batches");
            if(Interlocked.Exchange(ref awaitingRecovery,0)==1)Count("circuit_recoveries");Interlocked.Exchange(ref circuitUntil,0);
            } finally{statistics.Sample("http_ms",Stopwatch.GetElapsedTime(httpStart).TotalMilliseconds);}
        } catch(OperationCanceledException){foreach(var o in pending)Record(o,stop.IsCancellationRequested?"stopped":"timeout",Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if(!stop.IsCancellationRequested){Count(mlAttempted?"http_timeouts":"resolver_timeouts");if(mlAttempted)statistics.Model("timeout",clock.GetUtcNow());OpenCircuit();}}
          catch(Exception e){var status=stop.IsCancellationRequested?"stopped":e is PostgresException {SqlState:"57014" or "55P03"}||e is NpgsqlException {InnerException:TimeoutException}?"timeout":"failure";
            foreach(var o in pending)Record(o,status,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if(!stop.IsCancellationRequested){Count(mlAttempted?"http_exception_batches":"resolver_failure_batches");if(mlAttempted)statistics.Model(status,clock.GetUtcNow());OpenCircuit();}}
          finally{statistics.Sample("processing_ms",Stopwatch.GetElapsedTime(start).TotalMilliseconds);EndToEnd(batch);}
    }
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if(!options.Enabled)return;
        using var logStop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var logs=EmitMetricsAsync(logStop.Token);
        try{while(await queue.Reader.WaitToReadAsync(stop)){
            await Task.Delay(50,stop);var batch=new List<HistoricalEtaObservation>();
            while(batch.Count<options.BatchSize&&queue.Reader.TryRead(out var o)){Interlocked.Decrement(ref depth);batch.Add(o);}
            await ProcessAsync(batch,stop);
        }}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        finally{Interlocked.Exchange(ref accepting,0);queue.Writer.TryComplete();while(queue.Reader.TryRead(out var o)){Interlocked.Decrement(ref depth);Record(o,"stopped");}
            logStop.Cancel();await logs;}
    }
    internal bool EmitMetrics(){if(!options.Enabled||!options.MetricsLogEnabled||logger is null||!logger.IsEnabled(LogLevel.Information))return false;
        try{logger.LogInformation("HistoricalEtaShadowMetrics {SnapshotJson}",JsonSerializer.Serialize(OperationalSnapshot));return true;}
        catch{Count("metrics_log_failure");return false;}}
    async Task EmitMetricsAsync(CancellationToken stop){if(!options.MetricsLogEnabled||logger is null)return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(options.MetricsLogSeconds),clock);
        try{while(await timer.WaitForNextTickAsync(stop))EmitMetrics();}catch(OperationCanceledException)when(stop.IsCancellationRequested){}}
    public override void Dispose(){Interlocked.Exchange(ref accepting,0);base.Dispose();http.Dispose();}
}
