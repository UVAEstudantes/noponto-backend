using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;
namespace NoPonto.Tests;

// Synthetic fixtures; baseline below is the unchanged Apply from release124.
public sealed class PhysicalEtaCausalDiagnosticsTests
{
    private static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    [Theory]
    [InlineData("available")]
    [InlineData("physical_disabled")]
    [InlineData("options_invalid")]
    [InlineData("gps_time_invalid")]
    [InlineData("gps_old")]
    [InlineData("gps_future")]
    [InlineData("no_signal")]
    [InlineData("predecessor_absent")]
    [InlineData("matching_not_plausible")]
    [InlineData("identity_invalid")]
    [InlineData("target_absent")]
    [InlineData("topology_invalid")]
    [InlineData("position_invalid")]
    [InlineData("diagnostics_absent")]
    [InlineData("temporal_invalid")]
    [InlineData("bearing_invalid")]
    [InlineData("diagnostic_identity_mismatch")]
    [InlineData("projected_predecessor_absent")]
    [InlineData("previous_position_invalid")]
    [InlineData("projection_mismatch")]
    [InlineData("advance_insufficient")]
    [InlineData("temporal_timestamp_invalid")]
    [InlineData("temporal_interval_invalid")]
    [InlineData("distance_invalid")]
    [InlineData("route_length_invalid")]
    [InlineData("speed_invalid")]
    public void SingleDecisionPreservesRelease124OutputAndFreezesFirstFailedGate(string reason)
    {
        var f=PhysicalEtaMvpTests.Fixture(); var p=f.Posicao; var d=f.Diagnostico!;
        var options=new PhysicalEtaOptions{Enabled=reason!="physical_disabled",MaxAgeSeconds=reason=="options_invalid"?0:90};
        f=reason switch {
            "gps_time_invalid"=>f with{Posicao=p with{TimestampGps=DateTimeOffset.UnixEpoch}},
            "gps_old"=>f with{Posicao=p with{TimestampGps=T.AddSeconds(-91)}},
            "gps_future"=>f with{Posicao=p with{TimestampGps=T.AddSeconds(1)}},
            "no_signal"=>f with{Posicao=p with{Status=StatusVeiculo.SemSinal}},
            "predecessor_absent"=>f with{Posicao=p with{MatchingOperacionalPlausivel=false}},
            "matching_not_plausible"=>f with{Posicao=p with{MatchingOperacionalPlausivel=false,LatitudeAnterior=0,LongitudeAnterior=0,TimestampAnterior=T.AddSeconds(-15)}},
            "identity_invalid"=>f with{Posicao=p with{LinhaId=Guid.Empty}},
            "target_absent"=>f with{Posicao=p with{ProximaOcorrenciaParadaPadraoId=null}},
            "topology_invalid"=>f with{Posicao=p with{TopologiaPadrao="UNKNOWN"}},
            "position_invalid"=>f with{Posicao=p with{PosicaoNaRota=double.NaN}},
            "diagnostics_absent"=>f with{Diagnostico=null},
            "temporal_invalid"=>f with{Diagnostico=d with{ValidacaoTemporalPassou=false}},
            "bearing_invalid"=>f with{Diagnostico=d with{BearingConfiavel=false}},
            "diagnostic_identity_mismatch"=>f with{Diagnostico=d with{SentidoId=Guid.NewGuid()}},
            "projected_predecessor_absent"=>f with{Diagnostico=d with{PosicaoAnterior=null}},
            "previous_position_invalid"=>f with{Diagnostico=d with{PosicaoAnterior=double.NaN}},
            "projection_mismatch"=>f with{Diagnostico=d with{PosicaoNova=.25}},
            "advance_insufficient"=>f with{Diagnostico=d with{PosicaoAnterior=.2}},
            "temporal_timestamp_invalid"=>f with{Diagnostico=d with{TimestampAtual=T.AddSeconds(1)}},
            "temporal_interval_invalid"=>f with{Diagnostico=d with{DeltaTempoSegundos=181}},
            "distance_invalid"=>f with{Posicao=p with{DistanciaRestanteRotaMetros=double.NaN}},
            "route_length_invalid"=>f with{Posicao=p with{ComprimentoRotaMetros=500}},
            "speed_invalid"=>f with{Posicao=p with{Velocidade=0}},
            _=>f};
        var baseline=LegacyApply(f,T,options);
        var off=PhysicalEta.Apply(f,T,options);
        var on=PhysicalEta.Apply(f,T,options,true);
        Assert.Equal(baseline.Posicao,off.Posicao); Assert.Equal(baseline.Posicao,on.Posicao);
        Assert.Null(off.PhysicalCalculation);
        Assert.Equal(reason,on.PhysicalCalculation!.Reason.ToString());
        Assert.Same(on.Posicao,on.PhysicalCalculation.Position);
        if(reason=="physical_disabled")Assert.Same(f,off);
    }

    [Fact] public void DiagnosticNeverEntersPublicOrRedisJson()
    {
        var f=PhysicalEtaMvpTests.Fixture();var options=new PhysicalEtaOptions{Enabled=true};
        var before=LegacyApply(f,T,options);var after=PhysicalEta.Apply(f,T,options,true);
        Assert.Equal(JsonSerializer.Serialize(before.Posicao),JsonSerializer.Serialize(after.Posicao));
        Assert.DoesNotContain("PhysicalCalculation",JsonSerializer.Serialize(after));
        Assert.DoesNotContain("physical_apply_reason",JsonSerializer.Serialize(after.Posicao));
    }

    [Theory][InlineData(true,"temporal_invalid")][InlineData(false,"bearing_invalid")]
    public void ExistingMatchingDiagnosticIsNotHiddenBehindGenericPlausibility(bool temporalFailure,string expected)
    {
        var f=PhysicalEtaMvpTests.Fixture();
        f=f with{Posicao=f.Posicao with{MatchingOperacionalPlausivel=false,LatitudeAnterior=0,LongitudeAnterior=0,TimestampAnterior=T.AddSeconds(-15)},
            Diagnostico=f.Diagnostico! with{ValidacaoTemporalPassou=!temporalFailure,BearingConfiavel=temporalFailure}};
        var result=PhysicalEta.Apply(f,T,new(){Enabled=true},true);
        Assert.Equal(expected,result.PhysicalCalculation!.Reason.ToString());
        Assert.Equal(LegacyApply(f,T,new(){Enabled=true}).Posicao,result.Posicao);
    }

    private static ViagemObservadaResultado Trip(PosicaoVeiculoDto p)
    {
        var state=new ViagemObservadaState(Guid.NewGuid(),p.Ordem,p.PadraoVersaoId!.Value,T.AddMinutes(-1),p.TimestampGps,
            p.PosicaoNaRota!.Value,PadraoOperacionalId:p.PadraoOperacionalId!.Value);
        return new(ViagemObservadaStatus.Updated,state){EstadoOperacional=new(state,p.CodigoLinha,p.LinhaId!.Value,p.SentidoId!.Value),
            ProximaOcorrenciaOperacional=new(p.ProximaOcorrenciaParadaPadraoId!.Value,p.PadraoVersaoId.Value,Guid.NewGuid(),2,.3)};
    }
    private sealed class Clock(DateTimeOffset now):TimeProvider {public override DateTimeOffset GetUtcNow()=>now;}

    [Theory][InlineData("bound","available")][InlineData("stale","not_recorded")]
    [InlineData("missing","not_recorded")][InlineData("invalid","not_recorded")][InlineData("later_clock","available")]
    public async Task ExportAndCountersUseOnlyBoundCalculationNotLaterState(string mode,string expected)
    {
        var directory=Path.Combine(Path.GetTempPath(),"noponto-physical-diagnostic-fixture-"+Guid.NewGuid().ToString("N"));
        var f=PhysicalEtaMvpTests.Fixture();f=f with{Posicao=f.Posicao with{ModalFonte="BRT",ProvedorFonte="GTFSRT_BRT"}};
        f=PhysicalEta.Apply(f,T,new(){Enabled=true},mode!="missing");
        if(mode=="stale")f=f with{Posicao=f.Posicao with{}}; // Same values, different calculation object.
        if(mode=="invalid")f=f with{PhysicalCalculation=new(f.Posicao,(PhysicalEtaReason)int.MaxValue)};
        var clock=new Clock(mode=="later_clock"?T.AddSeconds(301):T.AddSeconds(1));
        var settings=new HistoricalEtaShadowOptions{Enabled=true,QueueCapacity=1,MlEnabled=false,HistoricalEnabled=false};
        using var export=new HistoricalEtaOfflineExport(Options.Create(new HistoricalEtaOfflineExportOptions{
            Enabled=true,Fixture=true,DirectoryPath=directory}),Options.Create(settings),clock);
        var http=new HistoricalEtaRuntimeTests.HttpFactory();
        using var shadow=new HistoricalEtaShadow(Options.Create(settings),new HistoricalEtaRuntimeTests.Geometry(),http,clock,offlineExport:export);
        try {
            Assert.True(shadow.TryCapture(f,Trip(f.Posicao)));
            Assert.False(shadow.TryCapture(f,Trip(f.Posicao))); // Queue full -> bounded export, no worker/geometry/HTTP.
            Assert.Equal(2,shadow.Metrics["physical.apply."+expected]);
            if(mode=="later_clock")Assert.Equal(2,shadow.Metrics["physical.capture.proof_rejected"]);
            Assert.Equal(0,http.Calls);
            await export.StartAsync(default);await export.StopAsync(default);
            var row=Assert.Single(Directory.GetFiles(directory,"*.jsonl").SelectMany(File.ReadLines)
                .Select(line=>JsonSerializer.Deserialize<JsonElement>(line)),r=>r.TryGetProperty("physical_status",out _));
            Assert.Equal(expected,row.GetProperty("physical_apply_reason").GetString());
            Assert.Equal(mode=="later_clock"?"physical_unavailable":"success",row.GetProperty("physical_status").GetString());
            var text=row.GetRawText();Assert.DoesNotContain("SYNTHETIC",text);Assert.DoesNotContain("latitude",text,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("longitude",text,StringComparison.OrdinalIgnoreCase);
        } finally {await export.StopAsync(default);if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact] public void ShadowOffDoesNotCaptureOrCountReasons()
    {
        using var shadow=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions()),new HistoricalEtaRuntimeTests.Geometry(),new HistoricalEtaRuntimeTests.HttpFactory());
        Assert.False(shadow.CapturePhysicalDiagnostics);
        var f=PhysicalEtaMvpTests.Fixture();f=f with{Posicao=f.Posicao with{ModalFonte="BRT",ProvedorFonte="GTFSRT_BRT"}};
        Assert.False(shadow.TryCapture(f,Trip(f.Posicao)));
        Assert.DoesNotContain(shadow.Metrics.Keys,k=>k.StartsWith("physical.apply."));
    }

    [Fact] public void CardinalityAndRetentionDoNotGrowWithVehicleIdentity()
    {
        Assert.True(Enum.GetValues<PhysicalEtaReason>().Length<=32);
        var options=new HistoricalEtaShadowOptions{Enabled=true,QueueCapacity=1,SinkCapacity=4};
        using var shadow=new HistoricalEtaShadow(Options.Create(options),new HistoricalEtaRuntimeTests.Geometry(),new HistoricalEtaRuntimeTests.HttpFactory(),new Clock(T.AddSeconds(1)));
        for(var i=0;i<200;i++)
        {
            var f=PhysicalEtaMvpTests.Fixture();
            f=f with{Posicao=f.Posicao with{Ordem="PRIVATE_FIXTURE_"+i,ModalFonte="BRT",ProvedorFonte="GTFSRT_BRT"}};
            f=PhysicalEta.Apply(f,T,new(){Enabled=true},true);
            shadow.TryCapture(f,Trip(f.Posicao));
        }
        Assert.Equal(200,shadow.Metrics["physical.apply.available"]);
        Assert.Single(shadow.Metrics.Keys,k=>k.StartsWith("physical.apply."));
        Assert.True(shadow.Results.Count<=4); Assert.Equal(1,shadow.Metrics["queue_depth"]);
        Assert.DoesNotContain("PRIVATE_FIXTURE",JsonSerializer.Serialize(shadow.OperationalSnapshot));
    }

    internal static ResultadoEnriquecimentoGps LegacyApply(ResultadoEnriquecimentoGps input,
        DateTimeOffset now, PhysicalEtaOptions options)
    {
        if (!options.Enabled) return input;
        var p = input.Posicao;
        var quality = "previsao_indisponivel";
        double? seconds = null;
        var age = (now - p.TimestampGps).TotalSeconds;
        var d = input.Diagnostico;
        if (options.Valid() && p.TimestampGps > DateTimeOffset.UnixEpoch && age > options.MaxAgeSeconds)
            quality = "gps_desatualizado";
        else if (options.Valid() && p.TimestampGps > DateTimeOffset.UnixEpoch && age >= 0 && age <= options.MaxAgeSeconds
            && p.Status != StatusVeiculo.SemSinal && p.MatchingOperacionalPlausivel
            && p.LinhaId is { } line && line != Guid.Empty && p.SentidoId is { } direction && direction != Guid.Empty
            && p.PadraoOperacionalId is { } pattern && pattern != Guid.Empty
            && p.PadraoVersaoId is { } version && version != Guid.Empty
            && p.ProximaOcorrenciaParadaPadraoId is { } target && target != Guid.Empty
            && !string.IsNullOrWhiteSpace(p.CodigoLinha)
            && p.TopologiaPadrao is "LINEAR" or "CIRCULAR"
            && p.PosicaoNaRota is { } progress && double.IsFinite(progress) && progress is >= 0 and <= 1
            && d is { ValidacaoTemporalPassou: true, BearingConfiavel: true }
            && d.PadraoVersaoId == version && d.PadraoOperacionalId == pattern && d.SentidoId == direction
            && d.PosicaoAnterior is { } previous && double.IsFinite(previous) && previous is >= 0 and <= 1
            && d.PosicaoNova == progress && progress > previous
            && d.TimestampAtual == p.TimestampGps && d.TimestampAnterior < p.TimestampGps
            && d.DeltaTempoSegundos is > 0 and <= 180
            && p.DistanciaRestanteRotaMetros is { } distance && double.IsFinite(distance) && distance > 0
            && p.ComprimentoRotaMetros is { } length && double.IsFinite(length) && length > 0 && distance <= length
            && double.IsFinite(p.Velocidade) && p.Velocidade is >= 1 and <= 160)
        {
            var speed = p.VelocidadeMedia is { } mean && double.IsFinite(mean) && mean is >= 1 and <= 160
                ? mean : p.Velocidade;
            // Reuse the existing pure calculator; this does not enable EtaV2 or persistence.
            seconds = EtaV2LongitudinalSpeedV0.Predict(distance, speed, 1).EtaSegundos;
            if (seconds.HasValue) quality = "estimativa_fisica";
        }
        return input with { Posicao = p with { EtaProximaParadaSegundos = seconds, EtaConfianca = PhysicalEta.Prefix + quality } };
    }
}
