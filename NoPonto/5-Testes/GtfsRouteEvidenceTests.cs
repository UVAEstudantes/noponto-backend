using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsRouteEvidenceTests
{
    private static readonly Guid Source = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Line = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static GtfsRoute Route(string code = "843") => new("O0" + code + "AAA0A", code) { AgencyId = "22005", RouteType = "700" };
    private static Dictionary<string, Guid> Binding(GtfsRoute r) => new() { [r.RouteId] = Line };

    [Fact] public void MissingMetadataPlanUsesSourceEvidenceAndRepeatsWithoutDuplication()
    {
        var r = Route();
        var created = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, Binding(r), []);
        Assert.Equal(2, created.Length);
        Assert.Contains(created, x => x.Tipo == "ROUTE_AGENCY" && x.ExternalId == "O0843AAA0A:22005");
        Assert.Contains(created, x => x.Tipo == "ROUTE_ORIGIN_V1" && x.ExternalId == "O0843AAA0A:BUS");
        Assert.All(created, x => { Assert.Equal(Source, x.FonteEstruturalId); Assert.Equal(Line, x.LinhaId); Assert.NotEqual(Guid.Empty, x.Id); });
        Assert.Empty(GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, Binding(r), created));
        var partial = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, Binding(r), [created[0]]);
        Assert.Single(partial); Assert.Equal(created[1].ExternalId, partial[0].ExternalId);
        Assert.Equal(2, created.Select(x => x.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("837", "22005", "700", "BUS")]
    [InlineData("844", "22004", "700", "BUS")]
    [InlineData("anything", "22005", "702", "UNKNOWN")]
    [InlineData("843", "unknown", "700", "UNKNOWN")]
    public void ClassificationIsSharedAndNeverInferredFromServiceCode(string code, string agency, string type, string expected)
    {
        var r = new GtfsRoute("route", code) { AgencyId = agency, RouteType = type };
        var rows = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, new Dictionary<string, Guid> { [r.RouteId] = Line }, []);
        Assert.Equal("route:" + expected, Assert.Single(rows, x => x.Tipo == "ROUTE_ORIGIN_V1").ExternalId);
    }

    [Theory]
    [InlineData("agency")]
    [InlineData("owner")]
    [InlineData("origin")]
    [InlineData("duplicate")]
    public void ConflictingEvidenceIsRejectedWithoutRewritingExistingRows(string kind)
    {
        var r = Route(); var rows = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, Binding(r), []);
        if (kind == "agency") rows[0].ExternalId = r.RouteId + ":22004";
        if (kind == "owner") rows[0].LinhaId = Guid.NewGuid();
        if (kind == "origin") rows[1].ExternalId = r.RouteId + ":BRT";
        var existing = kind == "duplicate" ? rows.Concat([rows[0]]).ToArray() : rows;
        var original = existing.Select(x => (x.Id, x.ExternalId, x.LinhaId)).ToArray();
        Assert.Throws<InvalidDataException>(() => GtfsDatarioPlanPersister.PlanMissingRouteEvidence([r], Source, Binding(r), existing));
        Assert.Equal(original, existing.Select(x => (x.Id, x.ExternalId, x.LinhaId)).ToArray());
    }

    [Fact] public void OtherRouteEvidenceAndMissingBindingsCannotLeakIntoRequestedRoute()
    {
        var a = Route("838"); var b = Route(); var old = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([a], Source, Binding(a), []);
        var rows = GtfsDatarioPlanPersister.PlanMissingRouteEvidence([b], Source, Binding(b), old);
        Assert.Equal(2, rows.Length); Assert.All(rows, x => Assert.StartsWith(b.RouteId + ":", x.ExternalId));
        Assert.Throws<InvalidDataException>(() => GtfsDatarioPlanPersister.PlanMissingRouteEvidence([b], Source, new Dictionary<string, Guid>(), []));
        Assert.All(old, x => Assert.StartsWith(a.RouteId + ":", x.ExternalId));
    }

    private static MemoryStream Zip(string routes, string? agencies = "agency_id,agency_name\n22005,Santa Cruz\n")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            using (var w = new StreamWriter(zip.CreateEntry("routes.txt").Open(), Encoding.UTF8)) w.Write(routes);
            if (agencies is not null) using (var w = new StreamWriter(zip.CreateEntry("agency.txt").Open(), Encoding.UTF8)) w.Write(agencies);
        }
        stream.Position = 0; return stream;
    }
    private const string Header = "route_id,route_short_name,agency_id,route_type\n";

    [Fact] public void MetadataReaderSelectsExplicitRouteWithoutRequiringOrReadingTripsShapesStops()
    {
        using var zip = Zip(Header + "O0838AAA0A,838,22005,700\nO0843AAA0A,843,22005,700\n");
        var r = Assert.Single(new GtfsFeedParser().ReadRouteMetadata(zip, ["O0843AAA0A"]));
        Assert.Equal("843", r.RouteShortName); Assert.Equal("22005", r.AgencyId); Assert.Equal("700", r.RouteType);
    }

    [Theory]
    [InlineData("missing_agency")]
    [InlineData("wrong_agency")]
    [InlineData("duplicate_route")]
    [InlineData("missing_route")]
    public void MetadataReaderRejectsAbsentOrAmbiguousSourceEvidence(string mode)
    {
        using var zip = Zip(Header + "O0843AAA0A,843,22005,700\n" + (mode == "duplicate_route" ? "O0843AAA0A,843,22005,700\n" : ""),
            mode == "missing_agency" ? null : mode == "wrong_agency" ? "agency_id,agency_name\n22004,Other\n" : "agency_id,agency_name\n22005,Santa Cruz\n");
        Assert.Throws<InvalidDataException>(() => new GtfsFeedParser().ReadRouteMetadata(zip, [mode == "missing_route" ? "absent" : "O0843AAA0A"]));
    }

    [Fact] public void MetadataCliIsExplicitBoundedPinnedAndRequiresNewUndoReportForApply()
    {
        var args = new[] { "structural-import", "--gtfs", "feed.zip", "--route-metadata", "O0843AAA0A", "--metadata-sha256", new string('a', 64) };
        Assert.False(StructuralImportCommand.TryParse(args, out _, out _));
        Assert.True(StructuralImportCommand.TryParse([.. args, "--dry-run"], out var dry, out _));
        Assert.True(dry!.DryRun); Assert.Equal(new[] { "O0843AAA0A" }, dry.MetadataRoutes);
        Assert.True(StructuralImportCommand.TryParse([.. args, "--metadata-report", "new-journal.json"], out var apply, out _));
        Assert.False(apply!.DryRun); Assert.Equal("new-journal.json", apply.MetadataReport);
        Assert.False(StructuralImportCommand.TryParse([.. args, "--crosswalk", "other.json", "--dry-run"], out _, out _));
        Assert.False(StructuralImportCommand.TryParse([.. args, "--route-metadata", "different", "--dry-run"], out _, out _));
        Assert.False(StructuralImportCommand.TryParse(["structural-import", "--gtfs", "feed.zip", "--metadata-report", "x"], out _, out _));
    }
    [Fact] public async Task MetadataCliRejectsChangedFingerprintAndExistingJournalBeforeDatabaseAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        var journal = Path.GetTempFileName();
        using var zip = Zip(Header + "O0843AAA0A,843,22005,700\n");
        await File.WriteAllBytesAsync(path, zip.ToArray()); await File.WriteAllTextAsync(journal, "preserve-existing-journal");
        try
        {
            var reads = 0; string? Env(string _) { reads++; throw new InvalidOperationException(); }
            var args = new[] { "structural-import", "--gtfs", path, "--route-metadata", "O0843AAA0A", "--metadata-sha256", new string('0',64) };
            var code = await StructuralImportCommand.ExecuteAsync([.. args, "--dry-run"], Env, new StringWriter(), new StringWriter());
            Assert.NotEqual(0, code); Assert.Equal(0, reads);
            code = await StructuralImportCommand.ExecuteAsync([.. args, "--metadata-report", journal], Env, new StringWriter(), new StringWriter());
            Assert.NotEqual(0, code); Assert.Equal(0, reads); Assert.Equal("preserve-existing-journal", await File.ReadAllTextAsync(journal));
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

}
