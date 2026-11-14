namespace NoPonto.Application.Health;

/// <summary>Process liveness, independent of dependencies and collectors.</summary>
public sealed record LiveHealthResponse(string Status, DateTimeOffset CheckedAt);

/// <summary>Readiness of essential local dependencies; no connection details.</summary>
public sealed record ReadyHealthResponse(string Status, DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, DependencyHealthResponse> Dependencies);

/// <summary>Safe status of a local dependency.</summary>
public sealed record DependencyHealthResponse(string Status, string? Reason);

/// <summary>Instance-local transport collection status, without vehicle data.</summary>
public sealed record TransportHealthResponse(string Status, DateTimeOffset CheckedAt,
    TransportSourcesResponse Sources, TransportProvidersResponse Providers);

/// <summary>Operational SPPO and BRT collection states.</summary>
public sealed record TransportSourcesResponse(SourceHealthResponse Sppo, SourceHealthResponse Brt);

/// <summary>Acquisition states of ITS and GTFS-RT BUS, when observed.</summary>
public sealed record TransportProvidersResponse(SourceHealthResponse Its, SourceHealthResponse GtfsRt);

/// <summary>Collection timestamps and structured diagnostics; empty valid collections have no data age.</summary>
public sealed record SourceHealthResponse(string Status, DateTimeOffset? LastSuccessAt,
    double? AgeSeconds, DateTimeOffset? LastDataAt, double? DataAgeSeconds,
    DateTimeOffset? LastAttemptAt, int ConsecutiveFailures, DateTimeOffset? LastErrorAt,
    string? LastError, string? Reason, string? ActiveSource);
