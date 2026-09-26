using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public enum GtfsEstruturalStatus { Concluida, NoOp }

public sealed record GtfsEstruturalResultado(
    GtfsEstruturalStatus Status,
    Guid ImportacaoId,
    GtfsEstruturalRelatorio Relatorio);

public sealed record GtfsEstruturalRelatorio(
    int RoutesLidas,
    int TripsLidos,
    int ShapesLidos,
    int StopsLidos,
    int PadroesEstruturais,
    int PadroesProcessaveis,
    int GtfsAutoritativo,
    int GtfsInsuficiente,
    int Ambiguo,
    int PadraoIdentidadeAmbigua,
    int LinhasCriadas,
    int LinhasReutilizadas,
    int SentidosCriados,
    int SentidosReutilizados,
    int ParadasCriadas,
    int ParadasReutilizadas,
    int VersoesCandidatasCriadas,
    int OcorrenciasCriadas,
    int RepeatedStopsDetectados,
    int LoopsDetectados,
    IReadOnlyList<string> Warnings,
    long DuracaoMs);

public sealed class GtfsEstruturalV22Service(
    TransporteDbContext db,
    GtfsFeedParser parser,
    GtfsProjecaoService projector)
{
    public const string FonteCodigo = "SMTR_GTFS";
    public const string AlgoritmoVersao = "V2.2_GTFS_1";
    private const string ChavePadraoUnico = "GTFS_ROUTE_DIRECTION_DEFAULT";

    public async Task<GtfsEstruturalResultado> ImportarAsync(
        Stream zipStream,
        string? versaoFonte = null,
        string? rawUri = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        var inicio = Stopwatch.StartNew();
        var bytes = await LerTudoAsync(zipStream, ct);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var fonte = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == FonteCodigo, ct);
        if (fonte is null)
        {
            fonte = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = FonteCodigo, Nome = "GTFS oficial SMTR" };
            db.FontesEstruturais.Add(fonte);
            await db.SaveChangesAsync(ct);
        }

        var anterior = await db.ImportacoesEstruturais.AsNoTracking()
            .Where(x => x.FonteEstruturalId == fonte.Id && x.ConteudoHash == hash
                && x.AlgoritmoVersao == AlgoritmoVersao && x.Status == StatusImportacaoEstrutural.Concluida)
            .OrderByDescending(x => x.ConcluidaEmUtc).FirstOrDefaultAsync(ct);
        if (anterior is not null)
            return new(GtfsEstruturalStatus.NoOp, anterior.Id,
                JsonSerializer.Deserialize<GtfsEstruturalRelatorio>(anterior.Relatorio)
                    ?? throw new InvalidDataException("Relatório da importação concluída é inválido."));

        var importacao = new ImportacaoEstrutural
        {
            Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
            Status = StatusImportacaoEstrutural.EmProcessamento,
            IniciadaEmUtc = DateTimeOffset.UtcNow, VersaoFonte = versaoFonte,
            ConteudoHash = hash, RawUri = rawUri, AlgoritmoVersao = AlgoritmoVersao,
            Relatorio = "{}"
        };
        db.ImportacoesEstruturais.Add(importacao);
        await db.SaveChangesAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var feed = parser.Parse(input);
            var warnings = new List<string>();
            var counters = new Contadores();
            var modal = await ObterModalOnibusAsync(ct);

            var lines = await ObterLinhasAsync(feed, fonte, modal, counters, ct);
            var directions = await ObterSentidosAsync(feed, fonte, lines, counters, ct);
            var stops = await ObterParadasAsync(feed, fonte, counters, ct);

            var groups = feed.Padroes.GroupBy(x => (x.RouteId, x.DirectionId)).ToArray();
            foreach (var group in groups)
            {
                if (group.Count() != 1)
                {
                    counters.Ambiguos += group.Count();
                    counters.IdentidadeAmbigua++;
                    warnings.Add($"PADRAO_IDENTIDADE_AMBIGUA:{group.Key.RouteId}:{group.Key.DirectionId}:{group.Count()}");
                    continue;
                }

                var source = group.Single();
                if (source.Ocorrencias.Count < 3 || source.Shape.Count < 2)
                {
                    counters.Insuficientes++;
                    warnings.Add($"GTFS_INSUFICIENTE:{source.RouteId}:{source.DirectionId}:{source.Ocorrencias.Count}");
                    continue;
                }

                var direction = directions[(source.RouteId, source.DirectionId)];
                var pattern = await db.PadroesOperacionais
                    .SingleOrDefaultAsync(x => x.SentidoId == direction.Id && x.Chave == ChavePadraoUnico, ct);
                if (pattern is null)
                {
                    pattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id,
                        Chave = ChavePadraoUnico, TipoServico = "REGULAR" };
                    db.PadroesOperacionais.Add(pattern);
                    await db.SaveChangesAsync(ct);
                }

                var geometry = new LineString(source.Shape.ToArray()) { SRID = 4326 };
                var transient = new Itinerario { Id = Guid.Empty, SentidoId = direction.Id, Geometria = geometry };
                var projection = projector.Projetar(source, transient, stops);
                if (projection.Motivos.Count > 0)
                {
                    counters.Ambiguos++;
                    warnings.AddRange(projection.Motivos.Select(x => $"{source.RouteId}:{source.DirectionId}:{x}"));
                    continue;
                }

                var number = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == pattern.Id)
                    .Select(x => (int?)x.Numero).MaxAsync(ct) ?? 0) + 1;
                var version = new PadraoVersao
                {
                    Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id, Numero = number,
                    Geometria = geometry,
                    DistanciaMetros = ComprimentoMetros(source.Shape),
                    Topologia = EhLoop(source) ? TopologiasPadrao.Circular : TopologiasPadrao.Linear,
                    HashEstrutural = EstruturaHash.Calcular(geometry,
                        EhLoop(source) ? TopologiasPadrao.Circular : TopologiasPadrao.Linear,
                        projection.Ocorrencias.Select(x => new EstruturaHashOccurrence(x.ParadaCodigo,
                            x.Ordem, x.PosicaoLinha, x.PosicaoLinha * ComprimentoMetros(source.Shape), x.DistanciaMetros))),
                    MetodoConstrucao = "GTFS_AUTORITATIVO", Confianca = 1,
                    AlgoritmoVersao = AlgoritmoVersao,
                    ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
                    Relatorio = JsonSerializer.Serialize(new { source.RouteId, source.DirectionId, source.ShapeId,
                        source.QuantidadeTrips, RepeatedStops = Repeticoes(source), Loop = EhLoop(source) }),
                    CriadaEmUtc = DateTimeOffset.UtcNow
                };
                db.PadroesVersoes.Add(version);
                foreach (var occurrence in projection.Ocorrencias)
                    db.OcorrenciasParadasPadroes.Add(new OcorrenciaParadaPadrao
                    {
                        Id = Guid.NewGuid(), PadraoVersaoId = version.Id, ParadaId = occurrence.ParadaId,
                        Ordem = occurrence.Ordem, SourceSequence = occurrence.SourceStopSequence,
                        PosicaoTracado = occurrence.PosicaoLinha,
                        DistanciaAcumuladaMetros = occurrence.PosicaoLinha * version.ComprimentoMetros,
                        DistanciaDaLinhaMetros = occurrence.DistanciaMetros,
                        SourceShapeDistTraveledMetros = occurrence.SourceShapeDistTraveledMetros
                    });
                foreach (var role in new[] { PapeisImportacaoPadrao.Membership, PapeisImportacaoPadrao.Geometria,
                    PapeisImportacaoPadrao.Paradas, PapeisImportacaoPadrao.Metadados })
                    db.PadroesVersoesImportacoes.Add(new() { PadraoVersaoId = version.Id,
                        ImportacaoEstruturalId = importacao.Id, Papel = role });

                counters.Autoritativos++;
                counters.Versoes++;
                counters.Ocorrencias += projection.Ocorrencias.Count;
                counters.Repeated += Repeticoes(source);
                if (EhLoop(source)) counters.Loops++;
            }

            await db.SaveChangesAsync(ct);
            inicio.Stop();
            var report = new GtfsEstruturalRelatorio(feed.Routes.Count, feed.Trips.Count,
                feed.Shapes.Select(x => x.ShapeId).Distinct().Count(), feed.Stops.Count, feed.Padroes.Count,
                counters.Autoritativos, counters.Autoritativos, counters.Insuficientes, counters.Ambiguos,
                counters.IdentidadeAmbigua, counters.LinhasCriadas, counters.LinhasReutilizadas,
                counters.SentidosCriados, counters.SentidosReutilizados, counters.ParadasCriadas,
                counters.ParadasReutilizadas, counters.Versoes, counters.Ocorrencias, counters.Repeated,
                counters.Loops, warnings, inicio.ElapsedMilliseconds);
            importacao.Status = StatusImportacaoEstrutural.Concluida;
            importacao.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            importacao.Relatorio = JsonSerializer.Serialize(report);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(GtfsEstruturalStatus.Concluida, importacao.Id, report);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var failed = await db.ImportacoesEstruturais.SingleAsync(x => x.Id == importacao.Id, CancellationToken.None);
            failed.Status = StatusImportacaoEstrutural.Falhou;
            failed.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            failed.Relatorio = JsonSerializer.Serialize(new { Erro = ex.GetType().Name, ex.Message });
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Modal> ObterModalOnibusAsync(CancellationToken ct)
    {
        var modal = await db.Modais.SingleOrDefaultAsync(x => x.Nome == "Ônibus", ct);
        if (modal is not null) return modal;
        modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        db.Modais.Add(modal); await db.SaveChangesAsync(ct); return modal;
    }

    private async Task<Dictionary<string, Linha>> ObterLinhasAsync(GtfsFeed feed, FonteEstrutural fonte,
        Modal modal, Contadores c, CancellationToken ct)
    {
        var result = new Dictionary<string, Linha>(StringComparer.OrdinalIgnoreCase);
        var existing = (await db.LinhasIdentidadesExternas.Include(x => x.Linha)
            .Where(x => x.FonteEstruturalId == fonte.Id && x.Tipo == "ROUTE_ID").ToArrayAsync(ct))
            .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
        foreach (var route in feed.Routes)
        {
            if (existing.TryGetValue(route.RouteId, out var identity)) { result[route.RouteId] = identity.Linha; c.LinhasReutilizadas++; continue; }
            var line = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = route.RouteShortName,
                Nome = route.RouteShortName };
            db.Linhas.Add(line);
            db.LinhasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), LinhaId = line.Id,
                FonteEstruturalId = fonte.Id, Tipo = "ROUTE_ID", ExternalId = route.RouteId });
            result[route.RouteId] = line; c.LinhasCriadas++;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private async Task<Dictionary<(string Route, string Direction), Sentido>> ObterSentidosAsync(
        GtfsFeed feed, FonteEstrutural fonte, IReadOnlyDictionary<string, Linha> lines,
        Contadores c, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), Sentido>();
        var existing = (await db.SentidosIdentidadesExternas.Include(x => x.Sentido)
            .Where(x => x.FonteEstruturalId == fonte.Id && x.Tipo == "GTFS_DIRECTION").ToArrayAsync(ct))
            .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct())
        {
            var external = $"{item.RouteId}:{item.DirectionId}";
            if (existing.TryGetValue(external, out var identity)) { result[item] = identity.Sentido; c.SentidosReutilizados++; continue; }
            var direction = new Sentido { Id = Guid.NewGuid(), LinhaId = lines[item.RouteId].Id,
                Nome = $"Direção {item.DirectionId}" };
            db.Sentidos.Add(direction);
            db.SentidosIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), SentidoId = direction.Id,
                FonteEstruturalId = fonte.Id, Tipo = "GTFS_DIRECTION", ExternalId = external });
            result[item] = direction; c.SentidosCriados++;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private async Task<Dictionary<string, Parada>> ObterParadasAsync(GtfsFeed feed, FonteEstrutural fonte,
        Contadores c, CancellationToken ct)
    {
        var result = new Dictionary<string, Parada>(StringComparer.OrdinalIgnoreCase);
        var existing = (await db.ParadasIdentidadesExternas.Include(x => x.Parada)
            .Where(x => x.FonteEstruturalId == fonte.Id && x.Tipo == "STOP_ID").ToArrayAsync(ct))
            .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
        foreach (var source in feed.Stops)
        {
            if (existing.TryGetValue(source.StopId, out var identity)) { result[source.StopId] = identity.Parada; c.ParadasReutilizadas++; continue; }
            var stop = new Parada { Id = Guid.NewGuid(), Codigo = source.StopId, Nome = source.StopName,
                Localizacao = new Point(source.Longitude, source.Latitude) { SRID = 4326 } };
            db.Paradas.Add(stop);
            db.ParadasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), ParadaId = stop.Id,
                FonteEstruturalId = fonte.Id, Tipo = "STOP_ID", ExternalId = source.StopId });
            result[source.StopId] = stop; c.ParadasCriadas++;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private static async Task<byte[]> LerTudoAsync(Stream input, CancellationToken ct)
    { using var buffer = new MemoryStream(); await input.CopyToAsync(buffer, ct); return buffer.ToArray(); }
    private static int Repeticoes(GtfsPadrao p) => p.Ocorrencias.Count - p.Ocorrencias.Select(x => x.StopId).Distinct().Count();
    private static bool EhLoop(GtfsPadrao p) => p.Ocorrencias.Count > 1 && p.Ocorrencias[0].StopId == p.Ocorrencias[^1].StopId;
    private static double ComprimentoMetros(IReadOnlyList<Coordinate> shape)
    {
        const double radius = 6_371_008.8; var total = 0d;
        for (var i=1;i<shape.Count;i++) { var a=shape[i-1];var b=shape[i];var p1=a.Y*Math.PI/180;var p2=b.Y*Math.PI/180;
            var dp=(b.Y-a.Y)*Math.PI/180;var dl=(b.X-a.X)*Math.PI/180;var h=Math.Sin(dp/2)*Math.Sin(dp/2)+Math.Cos(p1)*Math.Cos(p2)*Math.Sin(dl/2)*Math.Sin(dl/2);total+=2*radius*Math.Asin(Math.Min(1,Math.Sqrt(h))); }
        return total;
    }

    private sealed class Contadores
    {
        public int Autoritativos, Insuficientes, Ambiguos, IdentidadeAmbigua;
        public int LinhasCriadas, LinhasReutilizadas, SentidosCriados, SentidosReutilizados;
        public int ParadasCriadas, ParadasReutilizadas, Versoes, Ocorrencias, Repeated, Loops;
    }
}
