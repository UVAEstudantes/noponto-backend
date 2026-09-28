using System.Net;

namespace NoPonto.Tests;

internal sealed record GpsDiagnosticSourceFailure(
    string Source,
    int Cycle,
    int Attempt,
    int? HttpStatus,
    string ErrorType,
    DateTimeOffset TimestampUtc);

internal sealed record GpsDiagnosticSourceResult<T>(
    string Source,
    int Cycle,
    bool Available,
    bool RecoveredByRetry,
    T? Value,
    IReadOnlyList<GpsDiagnosticSourceFailure> Failures);

internal static class GpsDiagnosticSourceCollector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    internal static async Task<GpsDiagnosticSourceResult<T>> CollectAsync<T>(
        string source,
        int cycle,
        Func<CancellationToken, Task<T>> collect,
        CancellationToken ct = default)
    {
        var failures = new List<GpsDiagnosticSourceFailure>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var value = await collect(ct);
                return new(source, cycle, true, attempt == 2, value, failures);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var retryable = IsRetryable(exception, out var status);
                failures.Add(new(source, cycle, attempt, status, ErrorType(exception), DateTimeOffset.UtcNow));
                if (attempt == 2 || !retryable)
                    return new(source, cycle, false, false, default, failures);
                await Task.Delay(RetryDelay, ct);
            }
        }
        throw new InvalidOperationException("Fluxo de coleta diagnóstico inalcançável.");
    }

    internal static bool Comparable<TLeft, TRight>(
        GpsDiagnosticSourceResult<TLeft> left, GpsDiagnosticSourceResult<TRight> right) =>
        left.Available && right.Available;

    private static bool IsRetryable(Exception exception, out int? status)
    {
        status = exception is HttpRequestException { StatusCode: { } code } ? (int)code : null;
        return exception is OperationCanceledException
            || exception is TimeoutException
            || exception is HttpRequestException { StatusCode: null }
            || exception is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };
    }

    private static string ErrorType(Exception exception) => exception switch
    {
        OperationCanceledException => "timeout_or_cancellation",
        TimeoutException => "timeout",
        HttpRequestException => "http",
        _ => exception.GetType().Name
    };
}
