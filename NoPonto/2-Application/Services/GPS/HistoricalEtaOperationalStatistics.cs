namespace NoPonto.Application.GPS;

// Fixed cardinality, bounded rolling samples; no observation/vehicle identifiers.
public sealed class HistoricalEtaOperationalStatistics
{
    readonly object gate=new();
    readonly Dictionary<string,Queue<double>> samples=new()
    {
        ["resolver_ms"]=new(),["http_ms"]=new(),["processing_ms"]=new(),
        ["queue_wait_ms"]=new(),["end_to_end_ms"]=new(),
        ["prediction_difference_seconds"]=new(),["absolute_prediction_difference_seconds"]=new()
    };
    readonly int capacity;
    string modelStatus="unknown";
    string? modelVersion;
    DateTimeOffset? modelObservedAt;
    DateTimeOffset? validatedModelAt;
    public HistoricalEtaOperationalStatistics(int capacity){if(capacity is <32 or >4096)throw new ArgumentOutOfRangeException(nameof(capacity));this.capacity=capacity;}
    internal void Sample(string key,double value){if(!double.IsFinite(value))return;lock(gate){var q=samples[key];if(q.Count==capacity)q.Dequeue();q.Enqueue(value);}}
    internal void Model(string status,DateTimeOffset at,string? version=null){lock(gate){modelStatus=status;modelObservedAt=at;if(version is not null){
        if(modelVersion!=version){samples["prediction_difference_seconds"].Clear();samples["absolute_prediction_difference_seconds"].Clear();}
        modelVersion=version;validatedModelAt=at;}}}
    internal object Snapshot(){lock(gate){return new{
        sample_capacity=capacity,percentiles=samples.ToDictionary(x=>x.Key,x=>Summary(x.Value)),
        model_status=modelStatus,last_validated_model_version=modelVersion,last_validated_model_at=validatedModelAt,model_observed_at=modelObservedAt,
        comparison_kind="prediction_difference_not_arrival_error",arrival_evaluation_state="awaiting_trusted_labels"
    };}}
    internal static object Summary(IEnumerable<double> values){var a=values.Order().ToArray();
        double? P(double p)=>a.Length==0?null:a[(int)Math.Ceiling(p*a.Length)-1];
        return new{sample_count=a.Length,p50=P(.5),p90=P(.9),p95=P(.95)};
    }
}
