using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

internal enum MatchingBatchOperation { Global, Combined, Directed, Stage }
internal enum MatchingBatchPhase { Prepare, Connection, ExecuteReader, Read, Materialize, Fallback, Other }

// One bounded, sequential accumulator per repository operation (or stage).
// ExecuteReader and Read include network/buffering/server work, not pure SQL time.
internal sealed class MatchingBatchPhaseDiagnostics : IDisposable
{
    private readonly ILogger _logger;
    private readonly MatchingBatchOperation _operation;
    private readonly MotorCombinadoMatchingLote _engine;
    private readonly int _inputs;
    private readonly IReadOnlyList<MetricaComandoMatchingLote>? _commands;
    private readonly CancellationToken _ct;
    private readonly TimeProvider _clock;
    private readonly long[] _ticks = new long[7];
    private readonly bool _enabled;
    private MatchingBatchPhase _phase;
    private long _stamp;
    private bool _completed;
    private bool _disposed;
    internal int Rows { get; set; }
    internal int ChunkFailures { get; set; }

    internal MatchingBatchPhaseDiagnostics(ILogger logger, MatchingBatchOperation operation,
        int inputs, CancellationToken ct = default,
        IReadOnlyList<MetricaComandoMatchingLote>? commands = null,
        MotorCombinadoMatchingLote engine = MotorCombinadoMatchingLote.NaoAplicavel,
        TimeProvider? clock = null)
    {
        _logger = logger; _operation = operation; _inputs = inputs; _ct = ct;
        _commands = commands; _engine = engine; _clock = clock ?? TimeProvider.System;
        try { _enabled = logger.IsEnabled(LogLevel.Information); }
        catch (Exception) { _enabled = false; } // Optional diagnostics cannot change matching.
        if (_enabled) _stamp = _clock.GetTimestamp();
    }

    internal void Move(MatchingBatchPhase phase)
    {
        if (!_enabled || _disposed) return;
        var now = _clock.GetTimestamp();
        _ticks[(int)_phase] += _clock.GetElapsedTime(_stamp, now).Ticks;
        _stamp = now;
        _phase = phase;
    }

    internal void Complete() => _completed = true;

    public void Dispose()
    {
        if (_disposed) return;
        Move(_phase);
        _disposed = true;
        if (!_enabled) return;
        try
        {
            double Ms(MatchingBatchPhase phase) => TimeSpan.FromTicks(_ticks[(int)phase]).TotalMilliseconds;
            _logger.LogInformation(
                "Matching batch phases: operation={operation} engine={engine} inputs={inputs} outcome={outcome} " +
                "commands={commands} fallback_commands={fallback_commands} rows={rows} chunk_failures={chunk_failures} " +
                "prepare_ms={prepare_ms:F3} connection_ms={connection_ms:F3} execute_reader_ms={execute_reader_ms:F3} " +
                "read_ms={read_ms:F3} materialize_ms={materialize_ms:F3} fallback_ms={fallback_ms:F3} " +
                "other_ms={other_ms:F3} wall_ms={wall_ms:F3}",
                _operation, _engine, _inputs, _completed ? ChunkFailures > 0 ? "Degraded" : "Completed"
                    : _ct.IsCancellationRequested ? "Cancelled" : "Failed",
                _commands?.Count(x => x.Origem == OrigemComandoMatchingLote.Batch) ?? 0,
                _commands?.Count(x => x.Origem == OrigemComandoMatchingLote.FallbackIndividual) ?? 0,
                Rows, ChunkFailures, Ms(MatchingBatchPhase.Prepare), Ms(MatchingBatchPhase.Connection),
                Ms(MatchingBatchPhase.ExecuteReader), Ms(MatchingBatchPhase.Read), Ms(MatchingBatchPhase.Materialize),
                Ms(MatchingBatchPhase.Fallback), Ms(MatchingBatchPhase.Other), TimeSpan.FromTicks(_ticks.Sum()).TotalMilliseconds);
        }
        catch (Exception) { } // Do not turn an unavailable logging provider into operational fallback.
    }
}
