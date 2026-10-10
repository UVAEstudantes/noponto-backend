using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.GTFS;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed class GtfsDatarioPlanPersister(TransporteDbContext db) : IGtfsDatarioPlanPersister
{
    public const string SourceCode = "DATARIO_GTFS";
    public const string AlgorithmVersion = "DATARIO_GTFS_V1";

    public async Task<GtfsDatarioPersistenceReport> PersistAsync(
        GtfsDatarioImportPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Report.Errors.Count > 0) throw new InvalidDataException("Plano contém gates bloqueadores.");
        var watch = Stopwatch.StartNew();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var source = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == SourceCode, cancellationToken);
            if (source is null)
            {
                source = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = SourceCode, Nome = "GTFS Data.Rio" };
                db.FontesEstruturais.Add(source);
            }
            var contentHash = ContentHash(plan);
            var import = await db.ImportacoesEstruturais.SingleOrDefaultAsync(x =>
                x.FonteEstruturalId == source.Id && x.ConteudoHash == contentHash
                && x.AlgoritmoVersao == AlgorithmVersion
                && x.Status == StatusImportacaoEstrutural.Concluida, cancellationToken);
            if (import is null)
            {
                import = new ImportacaoEstrutural
                {
                    Id = Guid.NewGuid(), FonteEstruturalId = source.Id,
                    Status = StatusImportacaoEstrutural.EmProcessamento,
                    IniciadaEmUtc = DateTimeOffset.UtcNow,
                    ConteudoHash = contentHash, AlgoritmoVersao = AlgorithmVersion,
                    Relatorio = "{}"
                };
                db.ImportacoesEstruturais.Add(import);
            }
            var modal = await db.Modais.SingleOrDefaultAsync(x => x.Nome == "Ônibus", cancellationToken);
            if (modal is null) { modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" }; db.Modais.Add(modal); }
            await db.SaveChangesAsync(cancellationToken);

            var stopResult = await PersistStopsAsync(plan, source, cancellationToken);
            var lineResult = await PersistLinesAsync(plan, source, modal, cancellationToken);
            var directionResult = await PersistDirectionsAsync(plan, source, lineResult.Map, cancellationToken);
            var structural = await PersistPatternsAsync(plan, source, import, directionResult.Map, stopResult.Map, cancellationToken);
            import.Status = StatusImportacaoEstrutural.Concluida;
            import.ConcluidaEmUtc ??= DateTimeOffset.UtcNow;
            import.Relatorio = JsonSerializer.Serialize(new
            {
                Routes = plan.Feed.Routes.Count,
                Patterns = plan.Patterns.Count,
                Occurrences = plan.Patterns.Sum(x => x.Occurrences.Count)
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            watch.Stop();
            return new(import.Id, lineResult.Counts, directionResult.Counts, stopResult.Counts,
                structural.Patterns, structural.Versions, structural.Occurrences,
                watch.ElapsedMilliseconds, plan.Report.Warnings);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<(Dictionary<string, Parada> Map, GtfsDatarioEntityCounts Counts)> PersistStopsAsync(
        GtfsDatarioImportPlan plan, FonteEstrutural source, CancellationToken ct)
    {
        var codes = plan.Feed.Stops.Select(x => x.StopId).ToArray();
        var map = (await db.Paradas.Where(x => codes.Contains(x.Codigo)).ToArrayAsync(ct))
            .ToDictionary(x => x.Codigo, StringComparer.OrdinalIgnoreCase);
        var created = 0; var reused = map.Count; var updated = 0;
        foreach (var item in plan.Feed.Stops.OrderByDescending(x => x.LocationType == "1"))
        {
            if (!map.TryGetValue(item.StopId, out var stop))
            {
                stop = new Parada { Id = Guid.NewGuid(), Codigo = item.StopId, Nome = item.StopName,
                    Localizacao = new NetTopologySuite.Geometries.Point(item.Longitude, item.Latitude) { SRID = 4326 },
                    TipoLocal = item.LocationType == "1" ? TiposLocalParada.Estacao :
                        item.PlatformCode.Length > 0 ? TiposLocalParada.Plataforma : TiposLocalParada.Parada,
                    Plataforma = EmptyToNull(item.PlatformCode) };
                db.Paradas.Add(stop); map.Add(item.StopId, stop); created++;
            }
            else
            {
                var desiredType = item.LocationType == "1" ? TiposLocalParada.Estacao :
                    item.PlatformCode.Length > 0 ? TiposLocalParada.Plataforma : stop.TipoLocal;
                if (stop.Nome != item.StopName || stop.Plataforma != EmptyToNull(item.PlatformCode) || stop.TipoLocal != desiredType)
                { stop.Nome = item.StopName; stop.Plataforma = EmptyToNull(item.PlatformCode); stop.TipoLocal = desiredType; updated++; }
            }
        }
        await db.SaveChangesAsync(ct); // materializa IDs dos pais antes de ligar filhos
        foreach (var item in plan.Feed.Stops.Where(x => x.ParentStation.Length > 0))
        {
            if (!map.TryGetValue(item.ParentStation, out var parent))
                throw new InvalidDataException($"parent_station inexistente: {item.StopId}->{item.ParentStation}");
            var child = map[item.StopId];
            if (child.ParadaPaiId != parent.Id) { child.ParadaPaiId = parent.Id; updated++; }
        }
        var identities = (await db.ParadasIdentidadesExternas.Where(x => x.FonteEstruturalId == source.Id
            && x.Tipo == "STOP_ID" && codes.Contains(x.ExternalId)).ToArrayAsync(ct))
            .Select(x => x.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.ParadasIdentidadesExternas.AddRange(codes.Where(x => !identities.Contains(x)).Select(x => new ParadaIdentidadeExterna
        { Id = Guid.NewGuid(), ParadaId = map[x].Id, FonteEstruturalId = source.Id, Tipo = "STOP_ID", ExternalId = x }));
        await db.SaveChangesAsync(ct);
        return (map, new(created, reused, updated));
    }

    private async Task<(Dictionary<string, Linha> Map, GtfsDatarioEntityCounts Counts)> PersistLinesAsync(
        GtfsDatarioImportPlan plan, FonteEstrutural source, Modal modal, CancellationToken ct)
    {
        var routes = plan.Feed.Routes.ToDictionary(x => x.RouteId, StringComparer.OrdinalIgnoreCase);
        var codes = routes.Values.Select(x => x.RouteShortName)
            .Concat(plan.Crosswalk.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var byCode = (await db.Linhas.Where(x => codes.Contains(x.Codigo)).ToArrayAsync(ct))
            .ToDictionary(x => x.Codigo, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, Linha>(StringComparer.OrdinalIgnoreCase); var created=0;var reused=0;
        foreach (var route in routes.Values)
        {
            var mappedCode = plan.Crosswalk.FirstOrDefault(x => string.Equals(x.Value, route.RouteShortName,
                StringComparison.OrdinalIgnoreCase)).Key ?? route.RouteShortName;
            if (!byCode.TryGetValue(mappedCode, out var line))
            {
                line = new Linha { Id=Guid.NewGuid(), Codigo=route.RouteShortName,
                    Nome=route.RouteLongName.Length>0?route.RouteLongName:route.RouteShortName,
                    ModalId=await GtfsLinhaModal.ParaNovaLinhaAsync(db, route.RouteType, modal.Id, ct),
                    TipoRota=GtfsLinhaModal.TipoRota(route.RouteType) };
                db.Linhas.Add(line); byCode.Add(line.Codigo,line); created++;
            } else reused++;
            map[route.RouteId]=line;
        }
        await db.SaveChangesAsync(ct);
        var ext=routes.Keys.ToArray();
        var identities=(await db.LinhasIdentidadesExternas.Where(x=>x.FonteEstruturalId==source.Id&&x.Tipo=="ROUTE_ID"&&ext.Contains(x.ExternalId)).ToArrayAsync(ct))
            .Select(x=>x.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.LinhasIdentidadesExternas.AddRange(ext.Where(x=>!identities.Contains(x)).Select(x=>new LinhaIdentidadeExterna
        {Id=Guid.NewGuid(),LinhaId=map[x].Id,FonteEstruturalId=source.Id,Tipo="ROUTE_ID",ExternalId=x}));
        // Agency belongs to the versioned static source, not the commercial service type.
        // A route-scoped key avoids conflating multiple lines belonging to the same agency.
        var agencyKeys = routes.Values.Where(x => !string.IsNullOrWhiteSpace(x.AgencyId))
            .ToDictionary(x => x.RouteId, x => $"{x.RouteId}:{x.AgencyId}");
        var existingAgency = await db.LinhasIdentidadesExternas.Where(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_AGENCY"
            && agencyKeys.Values.Contains(x.ExternalId)).ToArrayAsync(ct);
        foreach (var route in agencyKeys)
        {
            var identity = existingAgency.SingleOrDefault(x => x.ExternalId == route.Value);
            if (identity is not null && identity.LinhaId != map[route.Key].Id)
                throw new InvalidDataException("Route agency identity belongs to another line.");
            if (identity is null) db.LinhasIdentidadesExternas.Add(new LinhaIdentidadeExterna
            { Id = Guid.NewGuid(), LinhaId = map[route.Key].Id, FonteEstruturalId = source.Id,
                Tipo = "ROUTE_AGENCY", ExternalId = route.Value });
        }
        var origins = routes.Values.Select(route => (Route:route, Origin:RealtimeOrigin(route) ?? "UNKNOWN")).ToArray();
        var originKeys = origins.Select(x => $"{x.Route.RouteId}:{x.Origin}").ToArray();
        var existingOrigins = await db.LinhasIdentidadesExternas.Where(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_ORIGIN_V1"
            && originKeys.Contains(x.ExternalId)).ToArrayAsync(ct);
        foreach (var item in origins)
        {
            var key = $"{item.Route.RouteId}:{item.Origin}";
            var identity = existingOrigins.SingleOrDefault(x => x.ExternalId == key);
            if (identity is not null && identity.LinhaId != map[item.Route.RouteId].Id)
                throw new InvalidDataException("Route origin identity belongs to another line.");
            if (identity is null) db.LinhasIdentidadesExternas.Add(new LinhaIdentidadeExterna
            { Id = Guid.NewGuid(), LinhaId = map[item.Route.RouteId].Id, FonteEstruturalId = source.Id,
                Tipo = "ROUTE_ORIGIN_V1", ExternalId = key });
        }
        await db.SaveChangesAsync(ct); return(map,new(created,reused));
    }

    private async Task<(Dictionary<(string,string), Sentido> Map, GtfsDatarioEntityCounts Counts)> PersistDirectionsAsync(
        GtfsDatarioImportPlan plan, FonteEstrutural source, IReadOnlyDictionary<string,Linha> lines, CancellationToken ct)
    {
        var keys=plan.Feed.Trips.Select(x=>(x.RouteId,x.DirectionId)).Distinct().ToArray();
        var externals=keys.Select(x=>$"{x.RouteId}:{x.DirectionId}").ToArray();
        var existing=(await db.SentidosIdentidadesExternas.Include(x=>x.Sentido)
            .Where(x=>x.FonteEstruturalId==source.Id&&x.Tipo=="GTFS_DIRECTION"&&externals.Contains(x.ExternalId)).ToArrayAsync(ct))
            .ToDictionary(x=>x.ExternalId,x=>x.Sentido,StringComparer.OrdinalIgnoreCase);
        var map=new Dictionary<(string,string),Sentido>();var created=0;var reused=0;
        foreach(var key in keys){var external=$"{key.RouteId}:{key.DirectionId}";
            if(!existing.TryGetValue(external,out var direction)){var names=plan.Feed.Trips.Where(x=>x.RouteId==key.RouteId&&x.DirectionId==key.DirectionId)
                    .Select(x=>x.TripHeadsign).Where(x=>x.Length>0).Distinct().ToArray();
                direction=new Sentido{Id=Guid.NewGuid(),LinhaId=lines[key.RouteId].Id,Nome=names.Length==1?names[0]:$"Direção {key.DirectionId}"};
                db.Sentidos.Add(direction);db.SentidosIdentidadesExternas.Add(new(){Id=Guid.NewGuid(),SentidoId=direction.Id,FonteEstruturalId=source.Id,Tipo="GTFS_DIRECTION",ExternalId=external});created++;}
            else reused++;map[key]=direction;}
        await db.SaveChangesAsync(ct);return(map,new(created,reused));
    }

    private async Task<(GtfsDatarioEntityCounts Patterns,GtfsDatarioEntityCounts Versions,GtfsDatarioEntityCounts Occurrences)> PersistPatternsAsync(
        GtfsDatarioImportPlan plan, FonteEstrutural source, ImportacaoEstrutural import,
        IReadOnlyDictionary<(string,string),Sentido> directions,
        IReadOnlyDictionary<string,Parada> stops, CancellationToken ct)
    {
        var keys=plan.Patterns.Select(x=>x.StructuralKey).ToArray();
        var identities=(await db.PadroesIdentidadesExternas.Include(x=>x.PadraoOperacional)
            .Where(x=>x.FonteEstruturalId==source.Id&&x.Tipo=="STRUCTURAL_KEY"&&keys.Contains(x.ExternalId)).ToArrayAsync(ct))
            .ToDictionary(x=>x.ExternalId,x=>x.PadraoOperacional,StringComparer.OrdinalIgnoreCase);
        var shapeIds=plan.Patterns.Select(x=>x.ShapeId).Distinct(StringComparer.Ordinal).ToArray();
        var shapeIdentities=(await db.PadroesIdentidadesExternas
            .Where(x=>x.FonteEstruturalId==source.Id&&x.Tipo=="SHAPE_ID"&&shapeIds.Contains(x.ExternalId)).ToArrayAsync(ct))
            .ToDictionary(x=>x.ExternalId,StringComparer.Ordinal);
        var pc=0;var pr=0;var vc=0;var vr=0;var oc=0;var oru=0;
        var versionIds = new HashSet<Guid>();
        foreach(var item in plan.Patterns)
        {
            if(!identities.TryGetValue(item.StructuralKey,out var pattern))
            { pattern=new PadraoOperacional{Id=Guid.NewGuid(),SentidoId=directions[(item.RouteId,item.DirectionId)].Id,
                Chave=item.StructuralKey,TipoServico="GTFS",NomePublico=item.Headsigns.FirstOrDefault()};
              db.PadroesOperacionais.Add(pattern);db.PadroesIdentidadesExternas.Add(new(){Id=Guid.NewGuid(),PadraoOperacionalId=pattern.Id,
                FonteEstruturalId=source.Id,Tipo="STRUCTURAL_KEY",ExternalId=item.StructuralKey});identities[item.StructuralKey]=pattern;pc++;await db.SaveChangesAsync(ct); }
            else pr++;
            if(shapeIdentities.TryGetValue(item.ShapeId,out var shapeIdentity))
            {
                if(shapeIdentity.PadraoOperacionalId!=pattern.Id)
                    throw new InvalidDataException($"SHAPE_ID associado a outro padrão: {item.ShapeId}.");
            }
            else
            {
                shapeIdentity=new PadraoIdentidadeExterna{Id=Guid.NewGuid(),PadraoOperacionalId=pattern.Id,
                    FonteEstruturalId=source.Id,Tipo="SHAPE_ID",ExternalId=item.ShapeId};
                db.PadroesIdentidadesExternas.Add(shapeIdentity);shapeIdentities[item.ShapeId]=shapeIdentity;
            }
            var version=await db.PadroesVersoes.SingleOrDefaultAsync(x=>x.PadraoOperacionalId==pattern.Id&&x.HashEstrutural==item.StructuralHash,ct);
            if(version is not null){vr++;versionIds.Add(version.Id);var count=await db.OcorrenciasParadasPadroes.CountAsync(x=>x.PadraoVersaoId==version.Id,ct);
                if(count!=item.Occurrences.Count)throw new InvalidDataException($"Versão idêntica com ocorrências inconsistentes: {item.StructuralKey}.");oru+=count;continue;}
            var number=(await db.PadroesVersoes.Where(x=>x.PadraoOperacionalId==pattern.Id).MaxAsync(x=>(int?)x.Numero,ct)??0)+1;
            version=new PadraoVersao{Id=Guid.NewGuid(),PadraoOperacionalId=pattern.Id,Numero=number,Geometria=item.Geometry,
                Topologia=item.Circular?TopologiasPadrao.Circular:TopologiasPadrao.Linear,ComprimentoMetros=item.ShapeDistanceMetres,
                HashEstrutural=item.StructuralHash,MetodoConstrucao="GTFS_DATARIO",Confianca=1,AlgoritmoVersao=AlgorithmVersion,
                ResultadoValidacao=ResultadosValidacaoPadrao.Valida,Relatorio=JsonSerializer.Serialize(new{item.RouteId,item.DirectionId,item.ShapeId,item.TripCount}),CriadoEmUtc=DateTimeOffset.UtcNow};
            db.PadroesVersoes.Add(version);versionIds.Add(version.Id);vc++;
            var max=item.ShapeDistanceMetres;
            db.OcorrenciasParadasPadroes.AddRange(item.Occurrences.Select((x,index)=>new OcorrenciaParadaPadrao{Id=Guid.NewGuid(),PadraoVersaoId=version.Id,
                ParadaId=stops[x.StopId].Id,Ordem=index+1,SourceSequence=x.StopSequence,
                PosicaoTracado=max>0&&x.ShapeDistTraveledMetros.HasValue?Math.Clamp(x.ShapeDistTraveledMetros.Value/max,0,1):0,
                DistanciaAcumuladaMetros=x.ShapeDistTraveledMetros??0,DistanciaDaLinhaMetros=0,SourceShapeDistTraveledMetros=x.ShapeDistTraveledMetros}));oc+=item.Occurrences.Count;
        }
        var roles = new[] { PapeisImportacaoPadrao.Membership, PapeisImportacaoPadrao.Geometria,
            PapeisImportacaoPadrao.Paradas, PapeisImportacaoPadrao.Metadados };
        var existingLinks = (await db.PadroesVersoesImportacoes
            .Where(x => x.ImportacaoEstruturalId == import.Id && versionIds.Contains(x.PadraoVersaoId))
            .Select(x => new { x.PadraoVersaoId, x.Papel }).ToArrayAsync(ct))
            .Select(x => (x.PadraoVersaoId, x.Papel)).ToHashSet();
        db.PadroesVersoesImportacoes.AddRange(versionIds.SelectMany(versionId => roles.Select(role => (versionId, role)))
            .Where(x => !existingLinks.Contains(x))
            .Select(x => new PadraoVersaoImportacao
            { PadraoVersaoId = x.versionId, ImportacaoEstruturalId = import.Id, Papel = x.role }));
        return(new(pc,pr),new(vc,vr),new(oc,oru));
    }

    private static string ContentHash(GtfsDatarioImportPlan plan)
    {
        var canonical = string.Join('\n', plan.Patterns.OrderBy(x => x.StructuralKey, StringComparer.Ordinal)
            .Select(x => $"{x.StructuralKey}|{x.StructuralHash}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? EmptyToNull(string value)=>value.Length==0?null:value;

    internal static string? RealtimeOrigin(GtfsRoute route)
    {
        // Publisher exceptions pinned to exact route + code + agency, not prefixes or all MOBI-Rio lines.
        var exception = (route.RouteId, route.RouteShortName, route.RouteType) is
            ("20000281130", "28", "700") or ("20000671130", "67", "700")
            or ("20000681130", "68", "700") or ("20000EXEC1110", "ESP01", "200");
        if (route.AgencyId == "20001")
        {
            if (route.RouteType == "702" || exception) return "BRT";
            return (route.RouteId, route.RouteShortName, route.RouteType) == ("O0634AAA0A", "634", "700")
                ? "BUS" : null;
        }
        if (route.AgencyId is "22002" or "22003" or "22004" or "22005")
            return route.RouteType is "700" or "200" ? "BUS" : null;
        return null;
    }
}
