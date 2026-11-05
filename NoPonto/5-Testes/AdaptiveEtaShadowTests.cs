using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;
using Xunit.Abstractions;
namespace NoPonto.Tests;

public sealed class AdaptiveEtaShadowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task November05_RealPostgisWitnesses_FeedPollingAndSelectedStopShadow()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../lab-output/r28-real-execution-morning-20261105-01"));
        Assert.True(Directory.Exists(root));
        int frames=0, comparisons=0, pairs=0, beforePairs=0, witnesses=0; double current=0,candidate=0;
        var reasons=new Dictionary<string,int>(); var sustained=new List<double>(); var speeds=new List<double>();
        foreach(var file in Directory.GetFiles(root,"GTFSRT-*-recorded-projection.json"))
        {
            var document=JsonSerializer.Deserialize<EtaReplayDocument>(File.ReadAllText(file),EtaCausalReplay.Json)!;
            var shadow=new AdaptiveEtaShadow {RequireObservedSupport=false};
            var before=new AdaptiveEtaShadow {RequireObservedSupport=false};
            foreach(var frame in document.Frames)
            {
                frames++;
                var f=frame.Position; var d=frame.Diagnostic;
                var p=new PosicaoVeiculoDto { Ordem=f.Vehicle,CodigoLinha=f.Line,LinhaId=f.LineId,SentidoId=f.Direction,
                    PadraoOperacionalId=f.Pattern,PadraoVersaoId=f.Version,ProximaOcorrenciaParadaPadraoId=f.Anchor,
                    TimestampGps=f.Gps,Status=f.Status,Velocidade=f.Speed*3.6,VelocidadeMedia=f.MeanSpeed*3.6,
                    PosicaoNaRota=f.Progress,ComprimentoRotaMetros=f.Length,DistanciaRestanteRotaMetros=f.AnchorDistance,
                    TopologiaPadrao=f.Topology,MatchingOperacionalPlausivel=f.Matching,ProvedorFonte=f.Provider };
                var diagnostic=d is null?null:new DiagnosticoEnriquecimentoGps(MotivoAusenciaLinhaGps.Nenhum,
                    true,d.Bearing,false,null,false,false,false,false,d.Temporal,false,null,null,
                    PadraoOperacionalId:d.Pattern,PadraoVersaoId:d.Version,SentidoId:d.Direction,
                    PosicaoAnterior:d.Previous,PosicaoNova:d.Current,TimestampAnterior:d.PreviousTime,TimestampAtual:d.CurrentTime,DeltaTempoSegundos:d.DeltaSeconds);
                var input=new ResultadoEnriquecimentoGps(p,null,ResultadoProjecaoOperacional.NaoSolicitada(),diagnostic);
                var options=new PhysicalEtaOptions {Enabled=true,AdaptiveShadowEnabled=true,MaxAgeSeconds=frame.MaxAgeSeconds};
                var witness=frame.Geometry.FirstOrDefault(g=>g.Outcome==EtaReplayGeometryOutcome.value && g.Distance is {} m && double.IsFinite(m) && m>=0);
                // A fixed target is a legitimate geometric origin: coordinate=-distance-to-target.
                // No route-length/wrap witness exists in these projected captures.
                AdaptiveEtaShadow.Geometry? geometry=witness is null?null:new(witness.Target,-witness.Distance!.Value);
                if(geometry is not null) witnesses++;
                shadow.Observe(input,frame.PhysicalNow,options,geometry);
                before.Observe(input,frame.PhysicalNow,options);
                sustained.AddRange(shadow.SustainedSpeeds());
                var applied=PhysicalEta.Apply(input,frame.PhysicalNow,options).Posicao;
                var resolver=new ReplayDistances(frame.Geometry);
                var baseline=await EventosParadaService.ProjectRoadEventsAsync(frame.Stops,[applied],frame.QueryNow,options,resolver,default);
                await EventosParadaService.ProjectRoadEventsAsync(frame.Stops,[applied],frame.QueryNow,options,resolver,default,adaptiveShadow:before);
                if(before.Snapshot().Last().CandidateSeconds.HasValue && before.Snapshot().Last().CurrentSeconds.HasValue) beforePairs++;
                var result=await EventosParadaService.ProjectRoadEventsAsync(frame.Stops,[applied],frame.QueryNow,options,resolver,default,adaptiveShadow:shadow);
                Assert.Equal(baseline.ToArray(),result.ToArray());
                var first=shadow.Snapshot().Last();
                // Querying the same GPS again must reuse the frozen estimator state.
                await EventosParadaService.ProjectRoadEventsAsync(frame.Stops,[applied],frame.QueryNow,options,resolver,default,adaptiveShadow:shadow);
                Assert.Equal(first,shadow.Snapshot().Last());
                comparisons++; reasons[first.Reason]=reasons.GetValueOrDefault(first.Reason)+1;
                if(first.RecentKmh is {} v) speeds.Add(v);
                if(first.CurrentSeconds is {} a && first.CandidateSeconds is {} b) {pairs++;current+=a;candidate+=b;}
            }
        }
        Assert.Equal(117,frames); Assert.Equal(frames,comparisons);
        Assert.NotEmpty(sustained); Assert.True(pairs>0);
        Assert.Equal(4,beforePairs); Assert.Equal(8,pairs);
        output.WriteLine($"Frames={frames}; before pairs={beforePairs}; witnesses={witnesses}; paired={pairs}; current sum={current:F3}s; candidate sum={candidate:F3}s; reasons={JsonSerializer.Serialize(reasons)}; sustained observations={sustained.Count}; sustained mean={(sustained.Count>0?sustained.Average():0):F3}km/h; recent observations={speeds.Count}; recent mean={(speeds.Count>0?speeds.Average():0):F3}km/h. Recorded PostGIS witnesses, corrected units; no live DB/rematching or arrival labels.");
    }
    private sealed class ReplayDistances(EtaReplayGeometry[] geometry):IRoadEtaDistances
    {
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests,CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<double?>>(requests.Select(r=>geometry.FirstOrDefault(g=>g.Target==r.TargetId && g.Outcome==EtaReplayGeometryOutcome.value)?.Distance).ToArray());
    }
}
