using System.Globalization;
using NoPonto.Application.GPS;

namespace NoPonto.ItsShadowLab;

internal sealed record LabOptions
{
    public string Line { get; init; } = "838";
    public int Cycles { get; init; } = 10;
    public int IntervalSeconds { get; init; } = 60;
    public int ItsTimeoutSeconds { get; init; } = 15;
    public int GtfsTimeoutSeconds { get; init; } = 15;
    public int Windows { get; init; } = 3;
    public int MaxPages { get; init; } = 6;
    public int PageLimit { get; init; } = 5000;
    public int MaxRecords { get; init; } = 30000;
    public int SpatialSeconds { get; init; } = 10;
    public required string Output { get; init; }
    public required IReadOnlySet<string> RouteIds { get; init; }

    internal ItsGpsShadowOptions ItsOptions => new()
    {
        ServiceCode = Line, AcquisitionBudgetSeconds = ItsTimeoutSeconds,
        IntervalSeconds = IntervalSeconds, RecentUtcWindows = Windows,
        MaxPages = MaxPages, PageLimit = PageLimit, MaxRecords = MaxRecords,
        SpatialTimestampToleranceSeconds = SpatialSeconds
    };

    public static LabOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(["--line", "--cycles", "--interval", "--output", "--its-timeout",
            "--gtfs-timeout", "--windows", "--max-pages", "--page-limit", "--max-records", "--spatial-seconds"]);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing option value.");
            var value = args[i + 1];
            if (args[i] == "--gtfs-route-id")
            {
                if (value.Length is < 1 or > 128 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                    throw new ArgumentException("Invalid route mapping.");
                routes.Add(value);
            }
            else if (!allowed.Contains(args[i]) || !values.TryAdd(args[i], value))
                throw new ArgumentException("Unknown or repeated option.");
        }
        int Number(string key, int fallback) => values.TryGetValue(key, out var value)
            ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n : throw new ArgumentException("Invalid integer option.") : fallback;
        if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("An output file is required.");
        var result = new LabOptions
        {
            Line = values.GetValueOrDefault("--line", "838"), Output = Path.GetFullPath(output), RouteIds = routes,
            Cycles = Number("--cycles", 10), IntervalSeconds = Number("--interval", 60),
            ItsTimeoutSeconds = Number("--its-timeout", 15), GtfsTimeoutSeconds = Number("--gtfs-timeout", 15),
            Windows = Number("--windows", 3), MaxPages = Number("--max-pages", 6),
            PageLimit = Number("--page-limit", 5000), MaxRecords = Number("--max-records", 30000),
            SpatialSeconds = Number("--spatial-seconds", 10)
        };
        if (result.Cycles is < 1 or > 1440 || result.GtfsTimeoutSeconds is < 1 or > 30
            || routes.Count is < 1 or > 32 || !result.ItsOptions.IsValid())
            throw new ArgumentException("Invalid limits or missing explicit GTFS route mapping.");
        return result;
    }
}
