using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsStructuralHintShadowAuditTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ColetaManual_QuandoExplicitamenteHabilitada()
    {
        if (!Enabled("GPS_GTFS_HINT_AUDIT_ENABLED")) return;
        Assert.True(int.TryParse(Environment.GetEnvironmentVariable("GPS_GTFS_HINT_AUDIT_MINUTES"), out var minutes)
            && minutes is >= 1 and <= 10,
            "GPS_GTFS_HINT_AUDIT_MINUTES deve estar entre 1 e 10.");
        Assert.Equal("DISPOSABLE", Environment.GetEnvironmentVariable("GPS_GTFS_HINT_AUDIT_DATABASE_KIND"));
        var connectionString = Environment.GetEnvironmentVariable("GPS_GTFS_HINT_AUDIT_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "GPS_GTFS_HINT_AUDIT_CONNECTION deve apontar para uma cópia estrutural descartável.");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseNetTopologySuite();
        await using var dataSource = dataSourceBuilder.Build();
        await using (var gate = dataSource.CreateCommand("SELECT current_database(), pg_is_in_recovery()"))
        await using (var reader = await gate.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            output.WriteLine($"Banco estrutural de auditoria: {reader.GetString(0)}; recovery={reader.GetBoolean(1)}.");
        }

        using var http = new HttpClient
        {
            BaseAddress = new("https://its.mobilidade.rio/"),
            Timeout = TimeSpan.FromSeconds(45),
        };
        var client = new GpsDatarioClient(http, NullLogger<GpsDatarioClient>.Instance);
        var repository = new GpsPadraoRepository(dataSource, NullLogger<GpsPadraoRepository>.Instance);
        var enrichment = new GpsEnriquecimentoService(repository,
            Options.Create(new GpsPollingOptions()),
            Options.Create(new GpsMatchingBatchOptions { Enabled = true }),
            NullLogger<GpsEnriquecimentoService>.Instance);
        var resolver = new GpsStructuralHintResolver(new GpsStructuralHintLookup(dataSource));
        var aggregator = new GpsStructuralHintAuditAggregator();
        var routeShadow = new GpsRouteShadowAuditAggregator();
        var motionTracker = new GpsDiagnosticMotionTracker();
        var started = DateTimeOffset.UtcNow;

        for (var cycle = 1; cycle <= minutes; cycle++)
        {
            var collected = await GpsDiagnosticSourceCollector.CollectAsync(
                "DATARIO", cycle,
                ct => client.BuscarTodasPaginasAsync(new GpsDatarioQuery(Limit: 5000), ct));
            foreach (var failure in collected.Failures)
                aggregator.RecordFailure("SOURCE", failure.ErrorType,
                    failure.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (collected.Available)
                await ProcessCycleAsync(collected.Value!, enrichment, resolver, aggregator,
                    routeShadow, repository, dataSource, motionTracker);
            output.WriteLine($"Ciclo {cycle}/{minutes}: "
                + (collected.Available ? $"{collected.Value!.Data.Count} registros" : "fonte indisponível"));
            if (cycle < minutes) await Task.Delay(TimeSpan.FromMinutes(1));
        }

        var finished = DateTimeOffset.UtcNow;
        var report = aggregator.Build(started, finished);
        var root = FindProjectRoot();
        var directory = Path.Combine(root, "lab-output");
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "gps-gtfs-hints-shadow-audit.json");
        var markdownPath = Path.Combine(directory, "gps-gtfs-hints-shadow-audit.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(markdownPath, Markdown(report));
        var routeReport = routeShadow.Build(started, finished);
        await File.WriteAllTextAsync(Path.Combine(directory, "gps-route-shadow-enforced-audit.json"),
            JsonSerializer.Serialize(routeReport, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(directory, "gps-route-shadow-enforced-audit.md"),
            RouteShadowMarkdown(routeReport));
        output.WriteLine($"Relatórios: {jsonPath} e {markdownPath}");
    }

    private static async Task ProcessCycleAsync(
        GpsDatarioCollection collection,
        GpsEnriquecimentoService enrichment,
        IGpsStructuralHintResolver resolver,
        GpsStructuralHintAuditAggregator aggregator,
        GpsRouteShadowAuditAggregator routeShadow,
        GpsPadraoRepository repository,
        NpgsqlDataSource dataSource,
        GpsDiagnosticMotionTracker motionTracker)
    {
        var rawObservations = collection.Data.Select(DatarioGpsSource.Map)
            .OfType<GpsObservation>().ToArray();
        var observations = rawObservations
            .GroupBy(x => x.VehicleId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(y => y.GpsTimestamp).First()).ToArray();
        routeShadow.RecordNormalization(rawObservations.Length, observations.Length);
        var motion = observations.Select(motionTracker.Observe).ToArray();
        var inputs = observations.Select(x => new EntradaEnriquecimentoGps(
            GpsObservationMapper.ToPosition(x, x.Modal ?? "UNKNOWN"), null)).ToArray();
        var matched = await enrichment.EnriquecerLoteComContextoAsync(inputs, default, null);
        for (var index = 0; index < observations.Length; index++)
        {
            GpsStructuralHints hints;
            try
            {
                hints = await resolver.ResolveAsync(observations[index], default);
            }
            catch (Exception exception)
            {
                RecordDatabaseFailure(aggregator,"RESOLVER","STRUCTURAL_HINT_LOOKUP",exception);
                continue;
            }
            var diagnostic = GpsStructuralHintShadowEvaluator.Compare(
                observations[index], hints, matched[index].Posicao, observations[index].Modal);
            GpsStructuralHintAuditGeometry? geometry=null;
            if(diagnostic.DirectionStatus==GpsStructuralHintStatus.Mismatch
                || diagnostic.ShapeStatus==GpsStructuralHintStatus.Mismatch)
                try
                {
                    geometry=await ProbeGeometryAsync(dataSource,observations[index],hints.PadraoVersaoId,hints.SentidoId,
                        matched[index].Posicao?.PadraoVersaoId);
                }
                catch(Exception exception)
                {
                    RecordDatabaseFailure(aggregator,"DIAGNOSTIC_PROBE","SPATIAL_COMPARISON",exception);
                }
            aggregator.Record(observations[index],diagnostic,geometry);
            var routeResult = await GpsRouteShadowEnforcedEvaluator.EvaluateAsync(
                observations[index], hints, matched[index].Posicao,
                async (linhaId, cancellationToken) =>
                {
                    var codigo = await FindLineCodeAsync(dataSource, linhaId, cancellationToken);
                    if (codigo is null) return null;
                    var bearing = matched[index].Posicao.Bearing ?? observations[index].Bearing ?? 0;
                    return await repository.BuscarEnriquecimentoAsync(codigo,
                        observations[index].Latitude, observations[index].Longitude, bearing, 250,
                        cancellationToken);
                });
            if (routeResult.Eligible && routeResult.HintedLinhaId.HasValue)
            {
                EnriquecimentoRotaDto? baselineProjection = null;
                if (matched[index].Posicao.LinhaId is { } baselineLine
                    && matched[index].Posicao.PadraoVersaoId is { } baselineVersion)
                {
                    var baselineCode = await FindLineCodeAsync(dataSource, baselineLine, default);
                    if (baselineCode is not null)
                    {
                        var bearing = matched[index].Posicao.Bearing ?? observations[index].Bearing ?? 0;
                        var projected = await repository.BuscarEnriquecimentoDoPadraoAsync(
                            baselineCode, baselineVersion, observations[index].Latitude,
                            observations[index].Longitude, bearing, 250);
                        baselineProjection = projected.Rota;
                    }
                }
                routeResult = routeResult with
                {
                    Multipattern = await IsMultipatternAsync(
                        dataSource, routeResult.HintedLinhaId.Value, default),
                    BaselineDistanceMetres = baselineProjection?.DistanciaARotaMetros,
                    BaselineLocalBearing = baselineProjection?.BearingLocal,
                    DistanceDeltaMetres = baselineProjection is null
                        || routeResult.RestrictedDistanceMetres is null ? null
                        : routeResult.RestrictedDistanceMetres - baselineProjection.DistanciaARotaMetros,
                    LocalBearingDeltaDegrees = AngularDifference(
                        baselineProjection?.BearingLocal, routeResult.RestrictedLocalBearing)
                };
            }
            GpsGlobalNoCandidateProbe? globalProbe = null;
            if (matched[index].Diagnostico?.MotivoFinal == MotivoAusenciaLinhaGps.GlobalNoCandidate)
                globalProbe = await ProbeGlobalNoCandidateAsync(dataSource, observations[index],
                    matched[index].Posicao.Bearing, motion[index], default);
            routeShadow.Record(observations[index], routeResult, matched[index].Diagnostico, globalProbe);
        }
    }

    private static async Task<GpsGlobalNoCandidateProbe> ProbeGlobalNoCandidateAsync(
        NpgsqlDataSource dataSource, GpsObservation observation, double? trustedBearing,
        GpsDiagnosticMotionSnapshot motion, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH point AS (
              SELECT ST_SetSRID(ST_MakePoint(@lon,@lat),4326) AS geom
            ), line AS (
              SELECT "Id" FROM "Linhas" WHERE "Codigo"=@code
            ), published AS (
              SELECT pv."Topologia",pv."Geometria" FROM line l
              JOIN "Sentidos" s ON s."LinhaId"=l."Id"
              JOIN "PadroesOperacionais" po ON po."SentidoId"=s."Id"
              JOIN "PadroesVersoes" pv ON pv."Id"=po."VersaoAtualId"
            ), routes AS (
              SELECT * FROM published WHERE "Geometria" IS NOT NULL AND NOT ST_IsEmpty("Geometria")
            ), measured AS (
              SELECT "Topologia",
                ST_Distance("Geometria"::geography,point.geom::geography) AS distance,
                ABS(MOD((degrees(ST_Azimuth(
                  ST_LineInterpolatePoint("Geometria",GREATEST(0.0,ST_LineLocatePoint("Geometria",point.geom)-0.025))::geography,
                  ST_LineInterpolatePoint("Geometria",LEAST(1.0,ST_LineLocatePoint("Geometria",point.geom)+0.025))::geography
                ))-@bearing+540.0)::numeric,360.0)-180.0) AS bearing_diff
              FROM routes CROSS JOIN point
            )
            SELECT EXISTS(SELECT 1 FROM line),(SELECT COUNT(*)::int FROM published),
              COUNT(*)::int,MIN(distance),MIN(bearing_diff)::double precision,
              COALESCE(bool_or(distance<=250),false),COALESCE(bool_or(bearing_diff<80),false),
              COALESCE(bool_or(distance<=250 AND bearing_diff<80),false),
              COALESCE(bool_or("Topologia"='CIRCULAR'),false) FROM measured
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("lon", observation.Longitude);
        command.Parameters.AddWithValue("lat", observation.Latitude);
        command.Parameters.AddWithValue("code", observation.ServiceCode ?? observation.RouteId ?? "");
        command.Parameters.AddWithValue("bearing", trustedBearing ?? observation.Bearing ?? 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var lineExists = reader.GetBoolean(0);
        var published = reader.GetInt32(1);
        var valid = reader.GetInt32(2);
        double? distance = reader.IsDBNull(3) ? null : reader.GetDouble(3);
        double? bearing = reader.IsDBNull(4) ? null : reader.GetDouble(4);
        var distancePass = reader.GetBoolean(5);
        var bearingPass = reader.GetBoolean(6);
        var combinedPass = reader.GetBoolean(7);
        bool? circular = valid == 0 ? null : reader.GetBoolean(8);
        var cause = !lineExists ? "LINE_NOT_FOUND"
            : published == 0 ? "NO_PUBLISHED_PATTERN"
            : valid == 0 ? "NO_VALID_GEOMETRY"
            : !distancePass && !bearingPass ? "DISTANCE_AND_BEARING"
            : !distancePass ? "DISTANCE"
            : !bearingPass ? "BEARING"
            : !combinedPass ? "DISTANCE_BEARING_ON_DIFFERENT_PATTERNS"
            : "OTHER";
        var operationalContext = GpsGlobalNoCandidateDiagnosticClassifier.Classify(
            observation.ServiceCode, cause, distance, motion);
        return new(cause, lineExists, published, valid, distance, bearing, circular,
            operationalContext, motion.ReceivedSpeedKmh,
            motion.DisplacementSincePreviousMetres,
            motion.SecondsSinceLastSignificantMovement, motion.RepeatedCoordinate,
            motion.StationaryConsecutiveCycles);
    }

    private static double? AngularDifference(double? first, double? second)
    {
        if (!first.HasValue || !second.HasValue) return null;
        var difference = Math.Abs(first.Value - second.Value) % 360;
        return Math.Min(difference, 360 - difference);
    }

    private static async Task<bool> IsMultipatternAsync(
        NpgsqlDataSource dataSource, Guid linhaId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
              SELECT 1 FROM "PadroesOperacionais" po
              JOIN "Sentidos" s ON s."Id"=po."SentidoId"
              WHERE s."LinhaId"=@id
              GROUP BY po."SentidoId" HAVING COUNT(*) > 1)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", linhaId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<string?> FindLineCodeAsync(
        NpgsqlDataSource dataSource, Guid linhaId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT \"Codigo\" FROM \"Linhas\" WHERE \"Id\"=@id");
        command.Parameters.AddWithValue("id", linhaId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static string RouteShadowMarkdown(object report) =>
        "# Auditoria route_id shadow-enforced\n\n"
        + "Resultado bruto completo: `gps-route-shadow-enforced-audit.json`.\n\n"
        + "O resultado é exclusivamente diagnóstico: baseline não é substituído e nenhuma decisão "
        + "é publicada em Redis, viagem, outbox, SignalR, ETA ou ML.\n\n"
        + "Limitação arquitetural: o baseline atual já filtra por `CodigoLinha`; quando `route_id` "
        + "resolve para a mesma linha do `ServiceCode`, baseline e restricted tendem a consultar o "
        + "mesmo conjunto de candidatos.\n";

    private static void RecordDatabaseFailure(GpsStructuralHintAuditAggregator aggregator,string stage,
        string operation,Exception exception)
    {
        var postgres=exception as PostgresException;
        var message=(postgres?.MessageText??exception.Message).ReplaceLineEndings(" ");
        if(message.Length>160)message=message[..160];
        aggregator.RecordFailure(stage,exception.GetType().Name,null,postgres?.SqlState,operation,message);
    }

    private static async Task<GpsStructuralHintAuditGeometry?> ProbeGeometryAsync(
        NpgsqlDataSource dataSource, GpsObservation observation, Guid? hintedVersionId,Guid? hintedSentidoId,
        Guid? matchedVersionId)
    {
        const string sql = """
            WITH ponto AS (
              SELECT ST_SetSRID(ST_MakePoint(@longitude,@latitude),4326) AS geom
            ), rotas AS (
              SELECT 'hinted' AS tipo, pv."Geometria" AS geom, po."SentidoId" AS sentido_id
              FROM "PadroesVersoes" pv JOIN "PadroesOperacionais" po ON po."Id"=pv."PadraoOperacionalId"
              WHERE pv."Id"=@hinted OR (@hinted='00000000-0000-0000-0000-000000000000'::uuid
                AND po."SentidoId"=@hinted_sentido AND po."VersaoAtualId"=pv."Id")
              UNION ALL
              SELECT 'matched', pv."Geometria", po."SentidoId"
              FROM "PadroesVersoes" pv JOIN "PadroesOperacionais" po ON po."Id"=pv."PadraoOperacionalId"
              WHERE pv."Id"=@matched
            ), medidas_brutas AS (
              SELECT rotas.tipo, ST_LineLocatePoint(rotas.geom,ponto.geom) AS fracao,
                     ST_Distance(rotas.geom::geography,ponto.geom::geography) AS distancia,
                     LEAST(ST_Distance(ST_StartPoint(rotas.geom)::geography,ponto.geom::geography),
                           ST_Distance(ST_EndPoint(rotas.geom)::geography,ponto.geom::geography)) AS terminal,
                     rotas.geom,rotas.sentido_id
              FROM rotas CROSS JOIN ponto WHERE rotas.geom IS NOT NULL
            ), medidas AS (
              SELECT DISTINCT ON (tipo) * FROM medidas_brutas ORDER BY tipo,distancia
            )
            SELECT tipo,fracao,distancia,terminal,
              degrees(ST_Azimuth(
                ST_LineInterpolatePoint(geom,GREATEST(0,fracao-0.0001)),
                ST_LineInterpolatePoint(geom,LEAST(1,fracao+0.0001)))) AS bearing_local,sentido_id
            FROM medidas
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("longitude", observation.Longitude);
        command.Parameters.AddWithValue("latitude", observation.Latitude);
        command.Parameters.AddWithValue("hinted", hintedVersionId ?? Guid.Empty);
        command.Parameters.AddWithValue("hinted_sentido", hintedSentidoId ?? Guid.Empty);
        command.Parameters.AddWithValue("matched", matchedVersionId ?? Guid.Empty);
        double? hintedDistance=null,matchedDistance=null,fraction=null,localBearing=null,terminal=null;
        Guid? hintedSentido=null,matchedSentido=null;double? hintedBearing=null;
        await using var reader=await command.ExecuteReaderAsync();
        while(await reader.ReadAsync())
        {
            var matched=reader.GetString(0)=="matched";
            var rowFraction=reader.IsDBNull(1)?(double?)null:reader.GetDouble(1);
            var distance=reader.IsDBNull(2)?(double?)null:reader.GetDouble(2);
            if(matched){fraction=rowFraction;matchedDistance=distance;
                terminal=reader.IsDBNull(3)?null:reader.GetDouble(3);
                localBearing=reader.IsDBNull(4)?null:reader.GetDouble(4);
                matchedSentido=reader.IsDBNull(5)?null:reader.GetGuid(5);}
            else {hintedDistance=distance;hintedBearing=reader.IsDBNull(4)?null:reader.GetDouble(4);
                hintedSentido=reader.IsDBNull(5)?null:reader.GetGuid(5);}
        }
        double? difference=observation.Bearing.HasValue&&localBearing.HasValue
            ? Math.Min(Math.Abs(observation.Bearing.Value-localBearing.Value),
                360-Math.Abs(observation.Bearing.Value-localBearing.Value)) : null;
        double? hintedDifference=observation.Bearing.HasValue&&hintedBearing.HasValue
            ? Math.Min(Math.Abs(observation.Bearing.Value-hintedBearing.Value),
                360-Math.Abs(observation.Bearing.Value-hintedBearing.Value)) : null;
        return new(hintedDistance,matchedDistance,fraction,localBearing,difference,terminal,
            hintedSentido,matchedSentido,hintedBearing,hintedDifference);
    }

    private static string Markdown(GpsStructuralHintAuditReport report)
    {
        var text = new StringBuilder()
            .AppendLine("# Auditoria shadow dos GTFS structural hints")
            .AppendLine()
            .AppendLine($"- Schema: `{report.SchemaVersion}`")
            .AppendLine($"- Duração: {report.DurationSeconds:F1}s")
            .AppendLine($"- Observações processadas: {report.Totals.ObservationsProcessed}")
            .AppendLine($"- Com algum hint: {report.Totals.ObservationsWithAnyStructuralHint}")
            .AppendLine($"- Falhas totais: {report.Totals.TotalFailures}")
            .AppendLine($"- Falhas do resolver: {report.Totals.ResolverFailures}")
            .AppendLine()
            .AppendLine("## Dimensões")
            .AppendLine()
            .AppendLine("| Dimensão | Match | Mismatch | Unavailable | Ambiguous | Conflict | Stale | Concordância | Evidência |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var dimension in new[] { report.Route, report.Direction, report.Shape, report.Trip })
            text.AppendLine($"| {dimension.Dimension} | {Count(dimension, "MATCH")} | {Count(dimension, "MISMATCH")} | "
                + $"{Count(dimension, "UNAVAILABLE")} | {Count(dimension, "AMBIGUOUS")} | {Count(dimension, "CONFLICT")} | "
                + $"{Count(dimension, "STALE")} | {(dimension.AgreementRate?.ToString("P2") ?? "n/a")} | {dimension.Evidence} |");
        text.AppendLine().AppendLine("## Motivos de mismatch/conflito")
            .AppendLine();
        foreach (var reason in report.MismatchReasons.OrderByDescending(x => x.Value))
            text.AppendLine($"- `{reason.Key}`: {reason.Value}");
        text.AppendLine().AppendLine("## Mismatches de direção").AppendLine();
        foreach(var item in report.DirectionMismatches)
            text.AppendLine($"- {item.Source}/{item.Provider} route={item.RouteId} service={item.ServiceCode} "
                + $"direction={item.DirectionId}: {item.Count}; terminal={item.TerminalBucket}; hora={item.TimeBucketUtc}; "
                + $"bearing recebido/local/dif={item.ReceivedBearingAverage:F1}/{item.LocalBearingAverage:F1}/{item.AngularDifferenceAverage:F1}; "
                + $"dist hinted/chosen={item.HintedDistanceMetresAverage:F1}/{item.ChosenDistanceMetresAverage:F1}m");
        text.AppendLine().AppendLine("## Mismatches de shape (top 100)").AppendLine();
        foreach(var item in report.ShapeMismatches)
            text.AppendLine($"- {item.Provider} service={item.ServiceCode} route={item.RouteId} direction={item.DirectionId} "
                + $"shape={item.ShapeId} category={item.Category}: {item.Count}; terminal={item.TerminalBucket}; "
                + $"dist hinted/chosen={item.HintedDistanceMetresAverage:F1}/{item.ChosenDistanceMetresAverage:F1}m; "
                + $"bearing gps/hinted/chosen={item.ReceivedBearingAverage:F1}/{item.HintedLocalBearingAverage:F1}/{item.ChosenLocalBearingAverage:F1}; "
                + $"dif hinted/chosen={item.HintedAngularDifferenceAverage:F1}/{item.ChosenAngularDifferenceAverage:F1}");
        text.AppendLine().AppendLine("## Route conflicts/unresolved (limitado)").AppendLine();
        foreach(var category in report.RouteResolutionCases.GroupBy(x=>x.Reason).OrderBy(x=>x.Key))
        {
            text.AppendLine($"### {category.Key}").AppendLine();
            foreach(var item in category.OrderByDescending(x=>x.Count))
                text.AppendLine($"- {item.Source}/{item.Provider} route={item.RouteId} service={item.ServiceCode} "
                    + $"status={item.Status}: {item.Count}");
        }
        text.AppendLine().AppendLine("## Critérios explícitos")
            .AppendLine().AppendLine(report.DecisionThresholds.Notes)
            .AppendLine().AppendLine("## Metodologia").AppendLine();
        foreach (var note in report.MethodologicalNotes) text.AppendLine($"- {note}");
        return text.ToString();
    }

    private static long Count(GpsStructuralHintDimensionReport report, string status) =>
        report.Counts.GetValueOrDefault(status);

    private static bool Enabled(string name) => string.Equals(
        Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NoPonto.csproj")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Raiz do projeto NoPonto não encontrada.");
    }
}
