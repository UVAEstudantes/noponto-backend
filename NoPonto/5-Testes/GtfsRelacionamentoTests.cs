using System.IO.Compression;
using System.Text;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsRelacionamentoTests
{
    private static readonly GeometryFactory Geo = new(new PrecisionModel(), 4326);

    [Fact]
    public void Parser_PreservaOcorrenciasZeroGapsEIdentidadeDeterministica()
    {
        using var zip = Feed(
            "trip_id,stop_id,stop_sequence,shape_dist_traveled\nT,A,0,0\nT,B,4,100\nT,A,9,200\n");
        var parser = new GtfsFeedParser();
        var first = Assert.Single(parser.Parse(zip).Padroes);
        zip.Position = 0;
        var second = Assert.Single(parser.Parse(zip).Padroes);

        Assert.Equal([0, 4, 9], first.Ocorrencias.Select(x => x.StopSequence));
        Assert.Equal(["A", "B", "A"], first.Ocorrencias.Select(x => x.StopId));
        Assert.Equal(first.PadraoExternoId, second.PadraoExternoId);
    }

    [Fact]
    public void Projecao_ConverteOrdemParaUmAteN_EPreservaSourceSequenceEDistancia()
    {
        var pattern = Pattern([("A",0,0d),("B",4,100d),("C",9,200d)]);
        var itinerary = Itinerary([(-43.0,-22.9),(-42.998,-22.9)]);
        var stops = Stops(("A",-43.0,-22.9),("B",-42.999,-22.9),("C",-42.998,-22.9));

        var result = new GtfsProjecaoService().Projetar(pattern, itinerary, stops);

        Assert.Empty(result.Motivos);
        Assert.Equal([1,2,3], result.Ocorrencias.Select(x => x.Ordem));
        Assert.Equal([0,4,9], result.Ocorrencias.Select(x => x.SourceStopSequence));
        Assert.Equal([0d,100d,200d], result.Ocorrencias.Select(x => x.SourceShapeDistTraveledMetros));
        Assert.All(result.Ocorrencias, x => Assert.InRange(x.DistanciaMetros, 0, .01));
    }

    [Fact]
    public void Projecao_MesmaParadaEmOrdensDiferentes_EhSuportadaEmLoopComProgressoFonte()
    {
        var pattern = Pattern([("A",0,0d),("B",1,100d),("A",2,200d)]);
        var itinerary = Itinerary([(-43.0,-22.9),(-42.999,-22.9),(-43.0,-22.9)]);
        var stops = Stops(("A",-43.0,-22.9),("B",-42.999,-22.9));

        var result = new GtfsProjecaoService().Projetar(pattern, itinerary, stops);

        Assert.Empty(result.Motivos);
        Assert.Equal([1,3], result.Ocorrencias.Where(x => x.ParadaCodigo=="A").Select(x => x.Ordem));
        Assert.Equal(result.Ocorrencias[0].ParadaId, result.Ocorrencias[2].ParadaId);
        Assert.True(result.Ocorrencias[2].PosicaoLinha > result.Ocorrencias[0].PosicaoLinha);
    }

    [Fact]
    public void Projecao_LoopSemProgressoFonte_EhAmbiguo()
    {
        var pattern = Pattern([("A",0,(double?)null),("B",1,null),("A",2,null)]);
        var result = new GtfsProjecaoService().Projetar(pattern,
            Itinerary([(-43.0,-22.9),(-42.999,-22.9),(-43.0,-22.9)]),
            Stops(("A",-43.0,-22.9),("B",-42.999,-22.9)));
        Assert.Contains(result.Motivos, x => x.StartsWith("PROJECAO_AMBIGUA"));
    }

    [Fact]
    public void ClassificacaoProjetor_RejeitaPadraoSparseStopAusenteDistanciaEProgressoInvalidos()
    {
        var service = new GtfsProjecaoService(); var itinerary = Itinerary([(-43.0,-22.9),(-42.998,-22.9)]);
        var stops = Stops(("A",-43.0,-22.9),("B",-42.999,-22.9),("C",-42.998,-22.9),("FAR",-42.9,-22.8));

        Assert.Contains(service.Projetar(Pattern([("A",0,0d),("B",1,1d)]),itinerary,stops).Motivos,x=>x.Contains("MENOS_DE_3"));
        Assert.Contains(service.Projetar(Pattern([("A",0,0d),("MISSING",1,1d),("C",2,2d)]),itinerary,stops).Motivos,x=>x.StartsWith("STOP_INEXISTENTE"));
        Assert.Contains(service.Projetar(Pattern([("A",0,0d),("B",1,2d),("C",2,1d)]),itinerary,stops).Motivos,x=>x=="SHAPE_DIST_TRAVELED_INVALIDO");
        Assert.Contains(service.Projetar(Pattern([("A",0,0d),("B",1,1d),("FAR",2,2d)]),itinerary,stops).Motivos,x=>x.StartsWith("DISTANCIA_ACIMA_LIMITE")||x.StartsWith("PROJECAO_SEQUENCIAL_INEXISTENTE"));
    }

    [Fact]
    public void Regressao838_IdentidadeEhCodigoENaoNome()
    {
        var itinerary0=Itinerary([(-43.604,-22.949),(-43.6035,-22.948)]);
        var itinerary1=Itinerary([(-43.6035,-22.948),(-43.604,-22.949)]);
        var stops=Stops(("D0-A",-43.604,-22.949),("5151O00281C9",-43.6038,-22.9486),("D0-Z",-43.6035,-22.948),
            ("D1-A",-43.6035,-22.948),("5151O00091C9",-43.6038,-22.9486),("D1-Z",-43.604,-22.949));
        var d0=new GtfsProjecaoService().Projetar(Pattern([("D0-A",0,0d),("5151O00281C9",1,50d),("D0-Z",2,100d)]),itinerary0,stops).Ocorrencias;
        var d1=new GtfsProjecaoService().Projetar(Pattern([("D1-A",0,0d),("5151O00091C9",1,50d),("D1-Z",2,100d)]),itinerary1,stops).Ocorrencias;
        Assert.Contains(d0,x=>x.ParadaCodigo=="5151O00281C9");
        Assert.DoesNotContain(d0,x=>x.ParadaCodigo=="5151O00091C9");
        Assert.Contains(d1,x=>x.ParadaCodigo=="5151O00091C9");
        Assert.DoesNotContain(d1,x=>x.ParadaCodigo=="5151O00281C9");
    }

    private static MemoryStream Feed(string stopTimes)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip,"routes.txt","route_id,route_short_name\nR,838\n");
            Add(zip,"trips.txt","route_id,service_id,trip_id,direction_id,shape_id\nR,S,T,0,SH\n");
            Add(zip,"stop_times.txt",stopTimes);
            Add(zip,"shapes.txt","shape_id,shape_pt_sequence,shape_pt_lat,shape_pt_lon,shape_dist_traveled\nSH,0,-22.9,-43,0\nSH,1,-22.9,-42.998,200\n");
            Add(zip,"stops.txt","stop_id,stop_name,stop_lat,stop_lon\nA,A,-22.9,-43\nB,B,-22.9,-42.999\nC,C,-22.9,-42.998\nMISSING,Missing,-22.9,-42.999\nFAR,Far,-22.8,-42.9\n");
        }
        stream.Position=0; return stream;
    }
    private static void Add(ZipArchive zip,string name,string value){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),Encoding.UTF8);writer.Write(value);}
    private static GtfsPadrao Pattern((string Code,int Sequence,double? Distance)[] stops) =>
        new("pattern","route","838","0","shape",stops.Select(x=>new GtfsOcorrencia(x.Code,x.Sequence,x.Distance)).ToArray(),[new(-43,-22.9),new(-42.998,-22.9)],1);
    private static Itinerario Itinerary((double X,double Y)[] points) => new(){Id=Guid.NewGuid(),SentidoId=Guid.NewGuid(),Geometria=Geo.CreateLineString(points.Select(x=>new Coordinate(x.X,x.Y)).ToArray())};
    private static Dictionary<string,Parada> Stops(params (string Code,double X,double Y)[] values) => values.ToDictionary(x=>x.Code,x=>new Parada{Id=Guid.NewGuid(),Codigo=x.Code,Nome=x.Code,Localizacao=Geo.CreatePoint(new Coordinate(x.X,x.Y))});
}
