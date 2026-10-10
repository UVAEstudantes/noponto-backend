using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NoPonto.Application.GTFS;

namespace NoPonto.Application.Tarifas;

public sealed record ArcGisTarifaFeature(long Fid, string? Servico, string? TipoRota, string? Tarifas);

/// <summary>Consulta independente sem geometria; reutiliza a URL da fonte estrutural.</summary>
public sealed class ArcGisTarifasClient(HttpClient http)
{
    public async Task<IReadOnlyList<ArcGisTarifaFeature>> BaixarAsync(CancellationToken ct = default)
    {
        const int pageSize = 1000;
        var result = new List<ArcGisTarifaFeature>();
        var ids = new HashSet<long>();
        for (var page = 0; page < 10000; page++)
        {
            var url = QueryHelpers.AddQueryString(ArcGisSppoSnapshotClient.DefaultLayerUrl + "/query",
                new Dictionary<string, string?> {
                    ["where"] = "1=1", ["outFields"] = "servico,tipo_rota,tarifas,fid",
                    ["returnGeometry"] = "false", ["orderByFields"] = "fid", ["f"] = "json",
                    ["resultOffset"] = result.Count.ToString(CultureInfo.InvariantCulture),
                    ["resultRecordCount"] = pageSize.ToString(CultureInfo.InvariantCulture)
                });
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Resposta ArcGIS inválida.");
            var count = 0;
            foreach (var item in features.EnumerateArray())
            {
                var a = item.GetProperty("attributes");
                var fid = a.GetProperty("fid").GetInt64();
                if (!ids.Add(fid)) throw new InvalidDataException("Paginação ArcGIS repetiu fid.");
                result.Add(new(fid, Text(a, "servico"), Text(a, "tipo_rota"), Text(a, "tarifas")));
                count++;
            }
            var exceeded = root.TryGetProperty("exceededTransferLimit", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            if (!exceeded) return result;
            if (count == 0) throw new InvalidDataException("Paginação ArcGIS interrompida antes do fim.");
        }
        throw new InvalidDataException("Limite de páginas ArcGIS excedido.");
    }

    private static string? Text(JsonElement a, string name) =>
        !a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null ? null :
        v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
}
