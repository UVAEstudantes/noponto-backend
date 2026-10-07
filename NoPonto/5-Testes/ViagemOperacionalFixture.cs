using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using NoPonto.Application.GPS;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemOperacionalFixture : IAsyncLifetime
{
    public string Schema { get; } = "viagem3_" + Guid.NewGuid().ToString("N");
    public Guid Linha { get; } = Guid.NewGuid();
    public Guid S1 { get; } = Guid.NewGuid();
    public Guid S2 { get; } = Guid.NewGuid();
    public Guid R1 { get; } = Guid.NewGuid();
    public Guid R2 { get; } = Guid.NewGuid();
    public Guid P1 { get; } = Guid.NewGuid();
    public Guid P2 { get; } = Guid.NewGuid();
    public Guid Stop { get; } = Guid.NewGuid();
    public Guid Historico { get; } = Guid.NewGuid();
    public Guid[] Occurrences { get; } = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    public ServiceProvider Provider { get; private set; } = null!;
    public NpgsqlDataSource Source => Provider.GetRequiredService<NpgsqlDataSource>();
    public ConnectionMultiplexer Redis { get; private set; } = null!;
    private NpgsqlDataSource _admin = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using(var command = _admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"")) await command.ExecuteNonQueryAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AdicionarPostgresCompartilhado(new NpgsqlConnectionStringBuilder(connection) { SearchPath = Schema + ",public" }.ConnectionString);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true});
        using var scope = Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var modal=Guid.NewGuid();
        context.Modais.Add(new(){Id=modal,Nome="Teste"});
        context.Linhas.Add(new(){Id=Linha,ModalId=modal,Codigo="VIAGEM3",Nome="Teste"});
        context.Sentidos.AddRange(new Sentido{Id=S1,LinhaId=Linha,Nome="Qualquer nome"},new Sentido{Id=S2,LinhaId=Linha,Nome="Sem parsing"});
        var p1 = new PadraoOperacional { Id=P1, Ativo=true, SentidoId=S1, Chave="VIAGEM3-1", TipoServico="TESTE" };
        var p2 = new PadraoOperacional { Id=P2, Ativo=true, SentidoId=S2, Chave="VIAGEM3-2", TipoServico="TESTE" };
        context.PadroesOperacionais.AddRange(p1,p2);
        await context.SaveChangesAsync();
        context.PadroesVersoes.AddRange(
            new PadraoVersao { Id=R1, PadraoOperacionalId=P1, Numero=1,
                Geometria=new LineString([new(-43.21,-22.9),new(-43.19,-22.9)]){SRID=4326},
                ComprimentoMetros=2000, HashEstrutural="r1", MetodoConstrucao="TESTE", AlgoritmoVersao="TESTE", Confianca=1,
                ResultadoValidacao=ResultadosValidacaoPadrao.Valida, Relatorio="{}", CriadoEmUtc=DateTimeOffset.UtcNow },
            new PadraoVersao { Id=R2, PadraoOperacionalId=P2, Numero=1,
                Geometria=new LineString([new(-43.19,-22.9),new(-43.21,-22.9)]){SRID=4326},
                ComprimentoMetros=2000, HashEstrutural="r2", MetodoConstrucao="TESTE", AlgoritmoVersao="TESTE", Confianca=1,
                ResultadoValidacao=ResultadosValidacaoPadrao.Valida, Relatorio="{}", CriadoEmUtc=DateTimeOffset.UtcNow });
        context.Paradas.Add(new(){Id=Stop,Codigo="TESTE",Nome="Mesmo nome repetido",Localizacao=new Point(-43.2,-22.9){SRID=4326}});
        for(var i=0;i<3;i++) context.OcorrenciasParadasPadroes.Add(new(){Id=Occurrences[i],PadraoVersaoId=R1,
            ParadaId=Stop,Ordem=i+1,PosicaoTracado=(i+1)*.2,DistanciaAcumuladaMetros=(i+1)*400,DistanciaDaLinhaMetros=0});
        context.OcorrenciasParadasPadroes.Add(new(){Id=Guid.NewGuid(),PadraoVersaoId=R2,
            ParadaId=Stop,Ordem=1,PosicaoTracado=.5,DistanciaAcumuladaMetros=1000,DistanciaDaLinhaMetros=0});
        await context.SaveChangesAsync();
        p1.VersaoAtualId=R1; p2.VersaoAtualId=R2;
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HistoricoPassagens" ("Id","Ativo","CreatedAt","Ordem","CodigoLinha","PadraoVersaoId","ParadaId",
                "PosicaoNaRota","DistanciaParadaMetros","TimestampGps","TimestampRegistro","VelocidadeInstantanea","HoraDia","DiaSemana")
            VALUES ({Historico},true,now(),'HISTORICO','VIAGEM3',{R1},{Stop},0.2,50,now(),now(),20,12,1)
            """);
        Redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");
    }

    public async Task DisposeAsync()
    {
        if(Redis is not null) await Redis.DisposeAsync();
        if(Provider is not null) await Provider.DisposeAsync();
        if(_admin is not null)
        {
            await using var command = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }
    }

    internal async Task<EstruturaViagem> CriarCircularAsync()
    {
        // Identidades e código exclusivos; não modificar/publicar versões compartilhadas.
        using var scope=Provider.CreateScope();
        var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var linha=Guid.NewGuid(); var sentido=Guid.NewGuid(); var padrao=Guid.NewGuid(); var versao=Guid.NewGuid();
        var modal=await context.Modais.Select(x=>x.Id).FirstAsync();
        var codigo="CIRC-"+linha.ToString("N")[..8];
        context.Linhas.Add(new(){Id=linha,ModalId=modal,Codigo=codigo,Nome="Circular isolada"});
        context.Sentidos.Add(new(){Id=sentido,LinhaId=linha,Nome="Circular dirigido"});
        var p=new PadraoOperacional{Id=padrao,Ativo=true,SentidoId=sentido,Chave=codigo,TipoServico="TESTE"};
        context.PadroesOperacionais.Add(p);
        await context.SaveChangesAsync();
        context.PadroesVersoes.Add(new(){Id=versao,PadraoOperacionalId=padrao,Numero=1,Topologia="CIRCULAR",
            Geometria=new LineString([new(-43.21,-22.9),new(-43.205,-22.9),new(-43.205,-22.895),
                new(-43.215,-22.895),new(-43.215,-22.9),new(-43.21,-22.9)]){SRID=4326},
            ComprimentoMetros=3000,HashEstrutural=versao.ToString("N"),MetodoConstrucao="TESTE",AlgoritmoVersao="TESTE",
            Confianca=1,ResultadoValidacao=ResultadosValidacaoPadrao.Valida,Relatorio="{}",CriadoEmUtc=DateTimeOffset.UtcNow});
        for(var i=1;i<=4;i++) context.OcorrenciasParadasPadroes.Add(new(){Id=Guid.NewGuid(),PadraoVersaoId=versao,
            ParadaId=Stop,Ordem=i,PosicaoTracado=i*.2,DistanciaAcumuladaMetros=i*600,DistanciaDaLinhaMetros=0});
        await context.SaveChangesAsync();
        p.VersaoAtualId=versao; await context.SaveChangesAsync();
        return new(versao,linha,sentido,codigo,true,padrao,"CIRCULAR");
    }

    internal async Task<(double Latitude,double Longitude,double Comprimento,double Bearing)> PontoCircularAsync(Guid versao,double posicao)
    {
        await using var command=Source.CreateCommand("""
            SELECT ST_Y(ST_LineInterpolatePoint("Geometria",@p)),ST_X(ST_LineInterpolatePoint("Geometria",@p)),
                ST_Length("Geometria"::geography),degrees(ST_Azimuth(
                    ST_LineInterpolatePoint("Geometria",greatest(0,@p-0.025))::geography,
                    ST_LineInterpolatePoint("Geometria",least(1,@p+0.025))::geography))
            FROM "PadroesVersoes" WHERE "Id"=@id
            """);
        command.Parameters.AddWithValue("id",versao); command.Parameters.AddWithValue("p",posicao);
        await using var reader=await command.ExecuteReaderAsync();
        if(!await reader.ReadAsync()) throw new InvalidOperationException("Versão circular de teste ausente.");
        return (reader.GetDouble(0),reader.GetDouble(1),reader.GetDouble(2),reader.GetDouble(3));
    }
}
