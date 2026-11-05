using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;

namespace NoPonto.Tests;

// Test harness only: no operational registration, acquisition, credentials, DB
// connection or implicit I/O. Caller provides already-authorized isolated inputs.
internal sealed class EtaCausalReplay : IDisposable
{
    internal const int MaxFrames = 32, MaxStops = 64, MaxBytes = 262144;
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly EtaCausalTestDiagnostics diagnostics;
    private readonly List<EtaReplayFrame> frames = [];
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly EtaReplayOrigin origin;
    private bool disposed;
    private EtaCausalReplay(EtaCausalTestDiagnostics diagnostics, TimeProvider clock, EtaReplayOrigin origin)
    { this.diagnostics = diagnostics; this.clock = clock; started = clock.GetTimestamp(); this.origin = origin; }

    internal static EtaCausalReplay? Create(IHostEnvironment environment, bool enabled,
        EtaReplayOrigin origin, TimeProvider? clock = null)
    {
        var diagnostics = EtaCausalTestDiagnostics.Create(environment, enabled, clock, capacity:256, sampleSeconds:0);
        return diagnostics is null ? null : new(diagnostics, clock ?? TimeProvider.System, origin);
    }

    private string Token(string domain, string value) => Convert.ToHexString(HMACSHA256.HashData(secret,
        Encoding.UTF8.GetBytes(domain + "|" + value)))[..24];
    private Guid Id(Guid value) => value == Guid.Empty ? value : new Guid(HMACSHA256.HashData(secret, value.ToByteArray()).AsSpan(0,16));
    private Guid? Id(Guid? value) => value is { } id ? Id(id) : null;
    private void Guard()
    {
        if (disposed || clock.GetElapsedTime(started).TotalSeconds >= 300 || frames.Count >= MaxFrames)
            throw new InvalidOperationException("Replay session inactive or limit reached.");
    }

    internal async Task<EtaReplayExpected> CaptureAsync(ResultadoEnriquecimentoGps input, EventosParadaService.StopContext[] stops,
        DateTimeOffset physicalNow, DateTimeOffset queryNow, PhysicalEtaOptions options,
        IRoadEtaDistances? authorizedResolver, CancellationToken ct = default)
    {
        Guard();
        if (!options.Enabled) throw new InvalidDataException("Only physical replay is supported.");
        if (stops.Length is < 1 or > MaxStops) throw new InvalidDataException("Replay stop limit.");
        var p = input.Posicao;
        var projection = new EtaReplayPosition(Token("vehicle", p.Ordem), Token("line", p.CodigoLinha),
            Id(p.LinhaId), Id(p.SentidoId), Id(p.PadraoOperacionalId), Id(p.PadraoVersaoId),
            Id(p.ProximaOcorrenciaParadaPadraoId), p.TimestampGps, p.Status, p.Velocidade, p.VelocidadeMedia,
            p.PosicaoNaRota, p.ComprimentoRotaMetros, p.DistanciaRestanteRotaMetros,
            p.TopologiaPadrao is "LINEAR" or "CIRCULAR" ? p.TopologiaPadrao : "INVALID",
            p.MatchingOperacionalPlausivel, p.TemHistorico, p.TimestampAnterior,
            KnownProvider(p.ProvedorFonte));
        var d = input.Diagnostico;
        var diagnostic = d is null ? null : new EtaReplayDiagnostic(d.BearingConfiavel, d.ValidacaoTemporalPassou,
            Id(d.PadraoOperacionalId), Id(d.PadraoVersaoId), Id(d.SentidoId), d.PosicaoAnterior, d.PosicaoNova,
            d.TimestampAnterior, d.TimestampAtual, d.DeltaTempoSegundos);
        var targets = stops.Select(s => s with { Id = Id(s.Id), ParadaId = Id(s.ParadaId),
            PadraoVersaoId = Id(s.PadraoVersaoId), PadraoOperacionalId = Id(s.PadraoOperacionalId),
            SentidoId = Id(s.SentidoId), LinhaId = Id(s.LinhaId), CodigoLinha = Token("line", s.CodigoLinha),
            Modal = "onibus", TipoRota = string.Equals(s.TipoRota,"brt",StringComparison.OrdinalIgnoreCase) ? "brt" : "onibus" }).ToArray();
        var recorder = authorizedResolver is null ? null : new Recorder(authorizedResolver, Id);
        var applied = PhysicalEta.Apply(input, physicalNow, options, captureDiagnostic:true, testDiagnostics:diagnostics);
        var events = await EventosParadaService.ProjectRoadEventsAsync(stops, [applied.Posicao], queryNow, options, recorder, ct, diagnostics);
        var evt = events.SingleOrDefault();
        var expected = new EtaReplayExpected(applied.PhysicalCalculation!.Reason,events.Count,
            evt?.EstimatedAt is { } at ? (at-queryNow).TotalSeconds : null);
        frames.Add(new(projection, diagnostic, targets, physicalNow, queryNow, options.Enabled,
            options.MaxAgeSeconds, authorizedResolver is not null, recorder?.Results.ToArray() ?? [],expected));
        return expected;
    }

    private static string KnownProvider(string? provider) => provider switch
    {
        "ITS_CURRENT" => "ITS_CURRENT", "GTFSRT_BUS" or "GTFS_RT" => "GTFSRT_BUS",
        "ZIRIX_DIRECT" or "SPPO_ZIRIX" => "ZIRIX_DIRECT", "DATARIO" => "DATARIO",
        "BRT_CURRENT" or "BRT_RIO" => "BRT_CURRENT", null or "" => "", _ => "OTHER"
    };
    internal string ExportProjection()
    {
        if (disposed) throw new ObjectDisposedException(nameof(EtaCausalReplay));
        var json = JsonSerializer.Serialize(new EtaReplayDocument("noponto-eta-r28-projected-v1", origin, frames.ToArray()), Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Replay byte limit.");
        return json;
    }
    internal IReadOnlyList<EtaCausalSample> Snapshot() => diagnostics.Snapshot();
    internal static EtaReplayDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new InvalidDataException("Replay byte limit.");
        try
        {
            var document = JsonSerializer.Deserialize<EtaReplayDocument>(bytes, Json);
            if (document is null || document.Schema != "noponto-eta-r28-projected-v1"
                || document.Frames is null || document.Frames.Length is < 1 or > MaxFrames
                || !Enum.IsDefined(document.Origin)
                || document.Frames.Any(f => f is null || f.Position is null || f.Stops is null
                    || f.Geometry is null || f.Expected is null || !f.PhysicalEnabled || !Enum.IsDefined(f.Expected.ApplyReason)
                    || f.Expected.EventCount is <0 or >1 || (f.Expected.EtaSeconds.HasValue && !double.IsFinite(f.Expected.EtaSeconds.Value))
                    || f.Stops.Length is < 1 or > MaxStops || f.Geometry.Length > MaxStops
                    || f.Stops.Any(s => s is null) || f.Geometry.Any(g => g is null || !Enum.IsDefined(g.Outcome))))
                throw new InvalidDataException();
            return document;
        }
        catch { throw new InvalidDataException("Invalid projected replay document; raw input omitted."); }
    }

    internal async Task ReplayAsync(EtaReplayDocument document, CancellationToken ct = default)
    {
        foreach (var frame in document.Frames)
        {
            Guard();
            var f = frame.Position;
            var p = new PosicaoVeiculoDto { Ordem=f.Vehicle, CodigoLinha=f.Line, LinhaId=f.LineId,
                SentidoId=f.Direction, PadraoOperacionalId=f.Pattern, PadraoVersaoId=f.Version,
                ProximaOcorrenciaParadaPadraoId=f.Anchor, TimestampGps=f.Gps, Status=f.Status,
                Velocidade=f.Speed, VelocidadeMedia=f.MeanSpeed, PosicaoNaRota=f.Progress,
                ComprimentoRotaMetros=f.Length, DistanciaRestanteRotaMetros=f.AnchorDistance,
                TopologiaPadrao=f.Topology, MatchingOperacionalPlausivel=f.Matching,
                LatitudeAnterior=f.HasHistory ? 0 : null, LongitudeAnterior=f.HasHistory ? 0 : null,
                TimestampAnterior=f.HistoryTimestamp, ProvedorFonte=f.Provider };
            var d=frame.Diagnostic;
            var diagnostic=d is null ? null : new DiagnosticoEnriquecimentoGps(MotivoAusenciaLinhaGps.Nenhum,
                !string.IsNullOrWhiteSpace(f.Line),d.Bearing,false,null,false,false,false,false,d.Temporal,
                false,null,null,PadraoOperacionalId:d.Pattern,PadraoVersaoId:d.Version,SentidoId:d.Direction,
                PosicaoAnterior:d.Previous,PosicaoNova:d.Current,TimestampAnterior:d.PreviousTime,
                TimestampAtual:d.CurrentTime,DeltaTempoSegundos:d.DeltaSeconds);
            var input = new ResultadoEnriquecimentoGps(p,null,ResultadoProjecaoOperacional.NaoSolicitada(),diagnostic);
            var options=new PhysicalEtaOptions {Enabled=frame.PhysicalEnabled,MaxAgeSeconds=frame.MaxAgeSeconds};
            var resolver=new RecordedResolver(frame.Geometry);
            var result=await CaptureAsync(input,frame.Stops,frame.PhysicalNow,frame.QueryNow,options,
                frame.ResolverPresent ? resolver : null,ct);
            if(resolver.Incomplete || result.ApplyReason!=frame.Expected.ApplyReason || result.EventCount!=frame.Expected.EventCount
                || result.EtaSeconds.HasValue!=frame.Expected.EtaSeconds.HasValue
                || (result.EtaSeconds.HasValue && Math.Abs(result.EtaSeconds.Value-frame.Expected.EtaSeconds!.Value)>0.000001))
                throw new InvalidDataException("Recorded result does not match replay or geometry witness is incomplete.");
        }
    }
    public void Dispose()
    { disposed=true; diagnostics.Dispose(); frames.Clear(); CryptographicOperations.ZeroMemory(secret); }

    private sealed class Recorder(IRoadEtaDistances inner, Func<Guid,Guid> token) : IRoadEtaDistances
    {
        internal readonly List<EtaReplayGeometry> Results=[];
        public async Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests,CancellationToken ct)
        {
            try
            {
                var result=await inner.ResolveAsync(requests,ct);
                foreach(var pair in requests.Select((r,i)=>(r,i))) Results.Add(new(token(pair.r.TargetId),
                    result.Count != requests.Count ? EtaReplayGeometryOutcome.count_mismatch : EtaReplayGeometryOutcome.value,
                    result.Count == requests.Count ? result[pair.i] : null));
                return result;
            }
            catch(Exception ex) when(!ct.IsCancellationRequested)
            {
                var outcome=ex is TimeoutException or OperationCanceledException
                    || ex is Npgsql.NpgsqlException {InnerException:TimeoutException}
                    || ex is Npgsql.PostgresException {SqlState:"57014" or "55P03"}
                        ? EtaReplayGeometryOutcome.timeout : EtaReplayGeometryOutcome.error;
                foreach(var request in requests) Results.Add(new(token(request.TargetId),outcome,null));
                throw;
            }
        }
    }
    private sealed class RecordedResolver(EtaReplayGeometry[] values) : IRoadEtaDistances
    {
        internal bool Incomplete;
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests,CancellationToken ct)
        {
            var matches=requests.Select(r=>
            {
                var candidates=values.Where(v=>v.Target==r.TargetId).ToArray();
                if(candidates.Length!=1) { Incomplete=true; throw new InvalidDataException("Missing or duplicate recorded geometry."); }
                return candidates[0];
            }).ToArray();
            if(matches.Any(v=>v.Outcome==EtaReplayGeometryOutcome.timeout)) throw new TimeoutException();
            if(matches.Any(v=>v.Outcome==EtaReplayGeometryOutcome.error)) throw new IOException();
            return Task.FromResult<IReadOnlyList<double?>>(matches.Any(v=>v.Outcome==EtaReplayGeometryOutcome.count_mismatch)
                ? [] : matches.Select(v=>v.Distance).ToArray());
        }
    }
}

internal enum EtaReplayOrigin { Synthetic, RecordedProjection }
internal enum EtaReplayGeometryOutcome { value, timeout, error, count_mismatch }
internal sealed record EtaReplayDocument(string Schema,EtaReplayOrigin Origin,EtaReplayFrame[] Frames);
internal sealed record EtaReplayFrame(EtaReplayPosition Position,EtaReplayDiagnostic? Diagnostic,
    EventosParadaService.StopContext[] Stops,DateTimeOffset PhysicalNow,DateTimeOffset QueryNow,
    bool PhysicalEnabled,int MaxAgeSeconds,bool ResolverPresent,EtaReplayGeometry[] Geometry,EtaReplayExpected Expected);
internal sealed record EtaReplayPosition(string Vehicle,string Line,Guid? LineId,Guid? Direction,Guid? Pattern,
    Guid? Version,Guid? Anchor,DateTimeOffset Gps,StatusVeiculo Status,double Speed,double? MeanSpeed,
    double? Progress,double? Length,double? AnchorDistance,string Topology,bool Matching,bool HasHistory,
    DateTimeOffset? HistoryTimestamp,string Provider);
internal sealed record EtaReplayDiagnostic(bool Bearing,bool? Temporal,Guid? Pattern,Guid? Version,
    Guid? Direction,double? Previous,double? Current,DateTimeOffset? PreviousTime,DateTimeOffset? CurrentTime,double? DeltaSeconds);
internal sealed record EtaReplayGeometry(Guid Target,EtaReplayGeometryOutcome Outcome,double? Distance);
internal sealed record EtaReplayExpected(PhysicalEtaReason ApplyReason,int EventCount,double? EtaSeconds);
