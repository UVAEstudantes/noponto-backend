using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoPonto.Application.GTFS;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsRebuildTests(ViagemOperacionalFixture fixture) : IClassFixture<ViagemOperacionalFixture>
{
    [Fact]
    public async Task Rebuild_PreservaLegadoHistorico_CriaStop_Repeticao_IdempotenciaERollback()
    {
        using var scope=fixture.Provider.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        db.Paradas.Add(new(){Id=Guid.NewGuid(),Codigo="OUTRO",Nome="Manal",Localizacao=new NetTopologySuite.Geometries.Point(-43.561906,-22.902687){SRID=4326}});
        await db.SaveChangesAsync();
        var viagem=Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "HistoricoPassagens" SET "ViagemId"={viagem}, "ParadaItinerarioId"={fixture.Occurrences[0]},
                "SentidoId"={fixture.S1}, "TimestampPassagem"=now() WHERE "Id"={fixture.Legacy}
            """);
        var plan=Plan(fixture.R1, fixture.Stop);
        var service=new GtfsParadaItinerarioRebuildService(db);

        var first=await service.ExecutarAsync([plan]);
        Assert.NotNull(first.ImportacaoId); Assert.Equal(1,first.ParadasCriadas); Assert.Equal(3,first.RelacoesCriadas);
        db.ChangeTracker.Clear();
        var legacy=await db.ParadasItinerario.Where(x=>fixture.Occurrences.Contains(x.Id)).ToArrayAsync();
        Assert.All(legacy,x=>Assert.False(x.Ativo)); Assert.All(legacy,x=>Assert.Equal(first.ImportacaoId,x.SubstituidaPorImportacaoId));
        var historical=await db.HistoricoPassagens.Include(x=>x.ParadaItinerario).SingleAsync(x=>x.Id==fixture.Legacy);
        Assert.Equal(fixture.Occurrences[0],historical.ParadaItinerarioId); Assert.NotNull(historical.ParadaItinerario); Assert.False(historical.ParadaItinerario!.Ativo);
        var active=await db.ParadasItinerario.Include(x=>x.Parada).Where(x=>x.Ativo&&x.ItinerarioId==fixture.R1).OrderBy(x=>x.Ordem).ToArrayAsync();
        Assert.Equal(["TESTE","5144O00069C9","TESTE"],active.Select(x=>x.Parada.Codigo));
        Assert.Equal(1,await db.Paradas.CountAsync(x=>x.Codigo=="5144O00069C9"));
        Assert.All(active,x=>{Assert.Equal(FontesParadaItinerario.Gtfs,x.Fonte);Assert.DoesNotContain(x.Id,fixture.Occurrences);});

        var second=await service.ExecutarAsync([plan]); Assert.Null(second.ImportacaoId); Assert.Equal(1,second.ItinerariosSemAlteracao);
        Assert.Equal(3,await db.ParadasItinerario.CountAsync(x=>x.Ativo&&x.ItinerarioId==fixture.R1));

        db.ParadasItinerario.Add(new(){Id=Guid.NewGuid(),ItinerarioId=fixture.R1,ParadaId=fixture.Stop,Ordem=1,PosicaoLinha=.1,Ativo=false});
        await db.SaveChangesAsync(); // mesma ordem inativa é permitida pelo índice parcial
        await Assert.ThrowsAsync<DbUpdateException>(async()=>{db.ParadasItinerario.Add(new(){Id=Guid.NewGuid(),ItinerarioId=fixture.R1,ParadaId=fixture.Stop,Ordem=1,PosicaoLinha=.1,Ativo=true});await db.SaveChangesAsync();});
        db.ChangeTracker.Clear();

        Assert.Equal(1,await service.RollbackAsync(first.ImportacaoId!.Value)); db.ChangeTracker.Clear();
        Assert.All(await db.ParadasItinerario.Where(x=>fixture.Occurrences.Contains(x.Id)).ToArrayAsync(),x=>Assert.True(x.Ativo));
        Assert.All(await db.ParadasItinerario.Where(x=>x.ImportacaoId==first.ImportacaoId).ToArrayAsync(),x=>Assert.False(x.Ativo));

        var x=await service.ExecutarAsync([plan]); db.ChangeTracker.Clear();
        var yPlan=plan with { Ocorrencias=plan.Ocorrencias.Select(o=>o with { PosicaoLinha=o.PosicaoLinha+.01 }).ToArray() };
        var y=await service.ExecutarAsync([yPlan]); Assert.NotEqual(x.ImportacaoId,y.ImportacaoId); db.ChangeTracker.Clear();
        Assert.Equal(1,await service.RollbackAsync(y.ImportacaoId!.Value)); db.ChangeTracker.Clear();
        var restoredX=await db.ParadasItinerario.Where(r=>r.Ativo&&r.ItinerarioId==fixture.R1).ToArrayAsync();
        Assert.All(restoredX,r=>Assert.Equal(x.ImportacaoId,r.ImportacaoId));
        Assert.Equal(1,await service.RollbackAsync(x.ImportacaoId!.Value));
    }

    private static GtfsDryRunItem Plan(Guid itinerary,Guid existingStop) => new(Guid.NewGuid(),"838",Guid.NewGuid(),"Magarça",itinerary,
        ClassificacaoGtfs.GtfsAutoritativo,"838:0",3,3,2,1,1,0,0,5,[],
        [new(existingStop,"TESTE",1,1,0,.1,1),new(Guid.NewGuid(),"5144O00069C9",2,2,1047.09,.2,2),new(existingStop,"TESTE",3,3,1200,.3,1)],
        [new("5144O00069C9","Manal",-22.902687,-43.561906)]);
}
