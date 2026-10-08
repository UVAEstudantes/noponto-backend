using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaTripEvidenceTests
{
    internal static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir=dir.Parent)
        {
            var file=Path.Combine(dir.FullName,"tools/eta_ml/fixtures/trip-evidence-v1.json");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("Shared synthetic evidence fixture missing");
    }
    internal static EtaEvidenceEvent[] Events()
    {
        using var file=JsonDocument.Parse(File.ReadAllText(FixturePath()));
        return file.RootElement.GetProperty("bundle").GetProperty("events").EnumerateArray()
            .Select(e=>EtaTripEvidence.Parse<EtaEvidenceEvent>(e.GetRawText())).ToArray();
    }
    internal static EtaEvidenceEvent Sign(EtaEvidenceEvent e) => e with { Digest=EtaTripEvidence.Digest(e) };
    [Fact] public void SharedPythonDigestsAndHeadMatchProducer()
    {
        EtaEvidenceHead? head=null;
        foreach(var e in Events()) { Assert.Equal(e.Digest,EtaTripEvidence.Digest(e)); head=EtaTripEvidence.Apply(head,e); }
        using var fixture=JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var expected=EtaTripEvidence.Parse<EtaEvidenceHead>(fixture.RootElement.GetProperty("bundle").GetProperty("heads")[0].GetRawText());
        Assert.Equal(expected,head);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void ProtectionAndCandidateAreIrreversible(long flag)
    {
        var e=Events(); var head=EtaTripEvidence.Apply(null,e[0]);
        var transition=Sign(e[1] with { NegativeFlags=flag }); head=EtaTripEvidence.Apply(head,transition);
        var cleared=Sign(e[2] with { PreviousDigest=transition.Digest,NegativeFlags=0 });
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,cleared));
        var recovered=Sign(cleared with { NegativeFlags=flag });
        Assert.Equal(flag,EtaTripEvidence.Apply(head,recovered).Last.NegativeFlags);
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(256)] [InlineData(512)]
    public void UnknownConditionsCannotBeCleared(long flag)
    {
        var e=Events(); var head=EtaTripEvidence.Apply(null,e[0]);
        var dirty=Sign(e[1] with { UnknownFlags=flag });head=EtaTripEvidence.Apply(head,dirty);
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,Sign(e[2] with {PreviousDigest=dirty.Digest})));
    }
    [Theory] [InlineData("identity")] [InlineData("epoch")] [InlineData("owner")]
    [InlineData("gap")] [InlineData("counter")] [InlineData("time")]
    [InlineData("unknown_contract")] [InlineData("digest")] [InlineData("coverage")]
    public void RejectsBrokenProof(string reason)
    {
        var e=Events(); var head=EtaTripEvidence.Apply(null,e[0]);var bad=e[1];
        bad=reason switch {
            "identity"=>bad with{IdentityHash=new string('f',64)},
            "epoch"=>bad with{EpochId=Guid.NewGuid()},"owner"=>bad with{OwnerToken=2},
            "gap"=>bad with{CoveredFromUs=bad.CoveredFromUs+1},
            "counter"=>bad with{Pending=1},"time"=>bad with{RecordedUs=bad.GpsUs!.Value-1},
            "unknown_contract"=>bad with{Contract="v999"},
            "coverage"=>bad with{CoverageProven=false},_=>bad };
        bad=Sign(bad);if(reason=="digest")bad=bad with{Digest=EtaTripEvidence.Zero};
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,bad));
    }
    [Fact] public void CloseRequiresSettledDecisionsAndNoFurtherEvent()
    {
        var e=Events();EtaEvidenceHead? head=null;foreach(var item in e[..^1])head=EtaTripEvidence.Apply(head,item);
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,Sign(e[^1] with{Settled=e[^1].Settled-1,Pending=1})));
        head=EtaTripEvidence.Apply(head,e[^1]);Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,e[^1]));
    }
    [Fact] public void MissingFieldsNeverDeserializeIntoTrustedDefaults()
    {
        Assert.Throws<JsonException>(()=>EtaTripEvidence.Parse<EtaEvidenceEvent>("{}"));
        Assert.Throws<JsonException>(()=>EtaTripEvidence.Parse<EtaProducerEpoch>("{}"));
        Assert.Throws<JsonException>(()=>EtaTripEvidence.Parse<EtaProducerEpoch>("{\"epoch_id\":null,\"epoch_id\":null}"));
    }
    [Fact] public void UnknownGpsIsNullAndPermanentlyUncovered()
    {
        var e=Events();var head=EtaTripEvidence.Apply(null,e[0]);
        var unknown=Sign(e[1] with{Kind="Transition",GpsUs=null,CoverageProven=false,
            UnknownFlags=EtaTripEvidence.Gap|EtaTripEvidence.MissingDecision,CoveredThroughUs=head.Last.CoveredThroughUs});
        var next=EtaTripEvidence.Apply(head,unknown);
        Assert.Null(next.Last.GpsUs);Assert.False(next.Last.CoverageProven);
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,Sign(unknown with{UnknownFlags=0})));
    }
    [Fact] public void QualityDispatchIsSeparateFromOperationalEvents()
    {
        var e=Events()[0];
        Assert.True(ViagemOutboxWorker.IsQuality(new(e.EventId,EtaTripEvidence.Serialize(e),0)));
        Assert.False(ViagemOutboxWorker.IsQuality(new("start:fixture","{\"tipo\":\"ViagemIniciada\"}",0)));
        Assert.True(ViagemOutboxWorker.IsQuality(new("quality:bad","{\"contract\":\"unknown\"}",0)));
    }
    [Fact] public void ClockAndRehashedHeartbeatCannotProveCoverage()
    {
        var e=Events();var head=EtaTripEvidence.Apply(null,e[0]);
        var heartbeat=Sign(e[1] with{Admitted=0,Settled=0,WitnessHash=e[0].WitnessHash});
        Assert.Throws<FormatException>(()=>EtaTripEvidence.Apply(head,heartbeat));
    }
}
