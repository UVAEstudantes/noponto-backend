using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstruturaFinalUnitTests
{
    [Fact]
    public void Hash_EhDeterministico_ESeparaDerivados()
    {
        var line = new LineString([new(-43.2,-22.9), new(-43.19,-22.89)]) { SRID=4326 };
        var occurrences = new[] { new EstruturaHashOccurrence("A",1,0,.0,3),
            new EstruturaHashOccurrence("B",2,1,1500,4) };
        var first = EstruturaHash.Calcular(line, TopologiasPadrao.Linear, occurrences);
        var second = EstruturaHash.Calcular((LineString)line.Copy(), TopologiasPadrao.Linear, occurrences.Reverse());
        var changed = EstruturaHash.Calcular(line, TopologiasPadrao.Linear,
            [occurrences[0], occurrences[1] with { Lateral = 5 }]);
        Assert.Equal(64, first.Length); Assert.Equal(first, second); Assert.NotEqual(first, changed);
    }

    [Fact]
    public async Task DryRun_AceitaMultiplosPadroes_RepeatedStop_ECircular()
    {
        await using var db = InMemoryContext();
        var service = new EstruturaFinalRebuildService(db, new(), new());
        await using var feed = FeedSintetico();
        var result = await service.ExecutarAsync(feed, null, new());
        Assert.Equal(2, result.Padroes); // S1/S2 equivalentes convergem; o circular permanece distinto.
        Assert.Equal(1, result.SentidosComMultiplosPadroes);
        Assert.Equal(1, result.Circulares);
        Assert.Equal(0, result.Rejeitados);
        Assert.Equal(7, result.Ocorrencias); // ambas as candidatas equivalentes foram analisadas, não descartadas.
    }

    private static TransporteDbContext InMemoryContext()
    {
        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=localhost;Database=not-opened", x => x.UseNetTopologySuite()).Options;
        return new(options);
    }

    internal static MemoryStream FeedSintetico(string route="R")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip,"routes.txt",$"route_id,route_short_name\n{route},838\n");
            Add(zip,"trips.txt",$"route_id,service_id,trip_id,direction_id,shape_id\n{route},D,T1,0,{route}S1\n{route},D,T2,0,{route}S2\n{route},D,T3,0,{route}S3\n");
            Add(zip,"stops.txt","stop_id,stop_name,stop_lat,stop_lon\nA,A,-22.900,-43.200\nB,B,-22.895,-43.195\nC,C,-22.890,-43.190\n");
            Add(zip,"stop_times.txt","trip_id,stop_id,stop_sequence,shape_dist_traveled\nT1,A,1,0\nT1,B,2,700\nT2,A,1,0\nT2,B,2,700\nT3,A,1,0\nT3,C,2,700\nT3,A,3,1400\n");
            Add(zip,"shapes.txt",$"shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled\n{route}S1,-22.900,-43.200,1,0\n{route}S1,-22.895,-43.195,2,700\n{route}S2,-22.900,-43.200,1,0\n{route}S2,-22.895,-43.195,2,700\n{route}S3,-22.900,-43.200,1,0\n{route}S3,-22.890,-43.190,2,700\n{route}S3,-22.900,-43.200,3,1400\n");
        }
        stream.Position=0; return stream;
    }
    private static void Add(ZipArchive zip,string name,string text)
    { using var writer=new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
}

public sealed class EstruturaFinalPostgisTests(EstruturaFinalFixture fixture) : IClassFixture<EstruturaFinalFixture>
{
    [Fact]
    public async Task RebuildDuasVezes_NaoDuplica_PublicacaoUsaCas_EVersaoFicaImutavel()
    {
        await using var scope=fixture.Provider.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var service=new EstruturaFinalRebuildService(db,new(),new());
        var beforePatterns=await db.PadroesOperacionais.CountAsync();
        var beforeVersions=await db.PadroesVersoes.CountAsync();
        var beforeOccurrences=await db.OcorrenciasParadasPadroes.CountAsync();
        await using var firstFeed=EstruturaFinalUnitTests.FeedSintetico();
        var first=await service.ExecutarAsync(firstFeed,null,new(EstruturaRebuildModo.Persistir));
        Assert.Equal(2,first.VersoesCriadas); // S1/S2 equivalentes compartilham versão; S3 é circular.
        Assert.Equal(beforePatterns+2,await db.PadroesOperacionais.CountAsync());
        Assert.Equal(beforeVersions+2,await db.PadroesVersoes.CountAsync());
        Assert.Equal(beforeOccurrences+5,await db.OcorrenciasParadasPadroes.CountAsync());
        Assert.Equal(3,await db.PadroesIdentidadesExternas.CountAsync(x=>x.Tipo=="SHAPE_ID" && x.ExternalId.StartsWith("RS")));
        Assert.All(await db.OcorrenciasParadasPadroes.GroupBy(x=>x.PadraoVersaoId)
            .Select(x=>x.OrderBy(y=>y.Ordem).Select(y=>y.DistanciaAcumuladaMetros).ToArray()).ToArrayAsync(),
            values=>Assert.Equal(values.Order().ToArray(),values));
        Assert.Contains(await db.PadroesVersoes.ToArrayAsync(),x=>x.Topologia==TopologiasPadrao.Circular);

        var counts=(await db.PadroesOperacionais.CountAsync(),await db.PadroesVersoes.CountAsync(),
            await db.OcorrenciasParadasPadroes.CountAsync());
        var hashes=await db.PadroesVersoes.OrderBy(x=>x.HashEstrutural).Select(x=>x.HashEstrutural).ToArrayAsync();
        await using var secondFeed=EstruturaFinalUnitTests.FeedSintetico();
        var second=await service.ExecutarAsync(secondFeed,null,new(EstruturaRebuildModo.Persistir));
        Assert.Equal(0,second.VersoesCriadas);
        Assert.Equal(counts,(await db.PadroesOperacionais.CountAsync(),await db.PadroesVersoes.CountAsync(),
            await db.OcorrenciasParadasPadroes.CountAsync()));
        Assert.Equal(hashes,await db.PadroesVersoes.OrderBy(x=>x.HashEstrutural).Select(x=>x.HashEstrutural).ToArrayAsync());

        var version=await db.PadroesVersoes.FirstAsync();
        await service.PublicarAsync(version.PadraoOperacionalId,version.Id,null);
        await Assert.ThrowsAsync<PostgresException>(async()=>{
            await db.Database.ExecuteSqlInterpolatedAsync($$"""UPDATE "PadroesVersoes" SET "Confianca"=.5 WHERE "Id"={{version.Id}}"""); });
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>service.PublicarAsync(
            version.PadraoOperacionalId,version.Id,Guid.NewGuid()));
    }

    [Fact]
    public async Task Schema_AceitaHistoricoAntigoNulo_EImpedePointerCruzado()
    {
        await using var scope=fixture.Provider.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        Assert.True(db.Model.FindEntityType(typeof(HistoricoPassagem))!.FindProperty("PadraoVersaoId")!.IsNullable);
        Assert.True(db.Model.FindEntityType(typeof(HistoricoPassagem))!.FindProperty("OcorrenciaParadaPadraoId")!.IsNullable);
        if (!await db.PadroesOperacionais.AnyAsync())
        {
            await using var feed=EstruturaFinalUnitTests.FeedSintetico("SCHEMA");
            await new EstruturaFinalRebuildService(db,new(),new()).ExecutarAsync(feed,null,
                new(EstruturaRebuildModo.Persistir));
        }
        var patterns=await db.PadroesOperacionais.Include(x=>x.Versoes).ToArrayAsync();
        Assert.True(patterns.Length>=2);
        await Assert.ThrowsAsync<PostgresException>(async()=>await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PadroesOperacionais" SET "VersaoAtualId"={patterns[1].Versoes.Single().Id}
            WHERE "Id"={patterns[0].Id}
            """));
    }
}

public sealed class EstruturaFinalFixture : IAsyncLifetime
{
    public string Schema { get; }="estrutura_final_"+Guid.NewGuid().ToString("N");
    public ServiceProvider Provider { get; private set; }=null!;
    private NpgsqlDataSource _admin=null!;
    public async Task InitializeAsync()
    {
        var connection=Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin=NpgsqlDataSource.Create(connection);
        await using(var command=_admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"")) await command.ExecuteNonQueryAsync();
        var services=new ServiceCollection(); services.AddLogging();
        services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(connection){SearchPath=Schema+",public"}.ConnectionString);
        Provider=services.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true});
        await using var scope=Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().GetService<IMigrator>().MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        if(Provider is not null) await Provider.DisposeAsync();
        if(_admin is not null){await using var command=_admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync(); await _admin.DisposeAsync();}
    }
}
