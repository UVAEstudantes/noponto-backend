using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;
using static NoPonto.Data.Repositories.EstruturaLeituraV2Repository;

namespace NoPonto.Tests;

public sealed class ParadasConsultaTests
{
    [Fact]
    public void NomeParcialIgnoraCaixaECodigoLinhaEhExato()
    {
        var f = Fixture();
        Assert.Equal(new[] { f.A }, Filtrar(f, nome: "  cAmPo  ").Select(x => x.Id));
        Assert.Equal(new[] { f.A, f.B }.Order(), Filtrar(f, codigo: " 838 ").Select(x => x.Id).Order());
        Assert.Empty(Filtrar(f, codigo: "83"));
        Assert.Empty(Filtrar(f, nome: "inexistente"));
    }

    [Fact]
    public void ModalESentidoUsamVinculosAtuaisOuModalDeclaradoSemInventarLinha()
    {
        var f = Fixture();
        Assert.Equal(new[] { f.A }, Filtrar(f, sentido: f.Ida).Select(x => x.Id));
        Assert.Equal(new[] { f.A, f.C }.Order(), Filtrar(f, modal: f.Trem).Select(x => x.Id).Order());
        Assert.Equal(new[] { f.A, f.B, f.SemLinha }.Order(), Filtrar(f, modal: f.Onibus).Select(x => x.Id).Order());
        Assert.Empty(Filtrar(f, sentido: Guid.NewGuid()));
        Assert.DoesNotContain(Filtrar(f, codigo: "838"), x => x.Id == f.Antiga);
    }

    [Fact]
    public void FiltrosCombinadosDevemCorresponderAoMesmoVinculoEPaginacaoEhEstavel()
    {
        var f = Fixture();
        Assert.Equal(new[] { f.A }, Filtrar(f, nome: "campo", codigo: "838", modal: f.Onibus, sentido: f.Ida).Select(x => x.Id));
        Assert.Empty(Filtrar(f, codigo: "838", modal: f.Trem));
        Assert.Empty(Filtrar(f, codigo: "838", sentido: f.SentidoTrem));
        var linha = Filtrar(f, codigo: "838");
        Assert.Equal(2, linha.Count());
        Assert.Equal(f.A, Assert.Single(PaginarParadas(linha, 1, 1)).Id);
        Assert.Equal(f.B, Assert.Single(PaginarParadas(linha, 2, 1)).Id);
        Assert.Empty(PaginarParadas(linha, 3, 1));
    }

    [Fact]
    public void RepeticoesEVersoesAntigasNaoDuplicamLinhasSentidosOuModais()
    {
        var f = Fixture();
        var atuais = OcorrenciasAtuais(f.Ocorrencias);
        var parada = Assert.Single(ProjetarParadas(f.Paradas.Where(x => x.Id == f.A)));
        var result = CompletarParada(parada, ProjetarVinculos(atuais.Where(x => x.ParadaId == f.A)));
        Assert.Equal(3, result.Linhas.Count);
        var linha = Assert.Single(result.Linhas, x => x.Codigo == "838");
        Assert.Equal(f.Ida, Assert.Single(linha.Sentidos).Id);
        Assert.Equal(2, result.Modais.Count);
        Assert.Equal(-22.9, result.Latitude);
        Assert.Equal(-43.2, result.Longitude);
        Assert.DoesNotContain(result.Linhas, x => x.Codigo == "ANTIGA");
        var antiga = Assert.Single(ProjetarParadas(f.Paradas.Where(x => x.Id == f.Antiga)));
        Assert.Empty(CompletarParada(antiga, ProjetarVinculos(atuais.Where(x => x.ParadaId == f.Antiga))).Linhas);
    }

    [Fact]
    public void ConsultaEProjecoesTraduzemParaSqlPostgresSemConexao()
    {
        using var db = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=localhost;Database=not-opened", x => x.UseNetTopologySuite()).Options);
        var atuais = OcorrenciasAtuais(db.OcorrenciasParadasPadroes.AsNoTracking());
        var query = FiltrarParadas(db.Paradas.AsNoTracking(), atuais, "campo", Guid.NewGuid(), "838", Guid.NewGuid());
        var sql = ProjetarParadas(PaginarParadas(query, 2, 20)).ToQueryString();
        Assert.Contains("EXISTS", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Contains("VersaoAtualId", sql);
        Assert.Contains("SELECT DISTINCT", ProjetarVinculos(atuais.Where(x => x.ParadaId == Guid.NewGuid())).Distinct().ToQueryString());
        var apenasLinha = FiltrarParadas(db.Paradas.AsNoTracking(), atuais, null, null, "838", null);
        Assert.Contains("EXISTS", ProjetarParadas(PaginarParadas(apenasLinha, 1, 50)).ToQueryString());
        var apenasModal = FiltrarParadas(db.Paradas.AsNoTracking(), atuais, null, Guid.NewGuid(), null, null);
        Assert.Contains("EXISTS", ProjetarParadas(PaginarParadas(apenasModal, 1, 50)).ToQueryString());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static IQueryable<Parada> Filtrar(Dados f, string? nome = null, Guid? modal = null,
        string? codigo = null, Guid? sentido = null) =>
        FiltrarParadas(f.Paradas, OcorrenciasAtuais(f.Ocorrencias), nome, modal, codigo, sentido);

    private sealed record Dados(IQueryable<Parada> Paradas, IQueryable<OcorrenciaParadaPadrao> Ocorrencias,
        Guid A, Guid B, Guid C, Guid Antiga, Guid SemLinha, Guid Onibus, Guid Trem, Guid Ida, Guid SentidoTrem);

    private static Dados Fixture()
    {
        var bus = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        var rail = new Modal { Id = Guid.NewGuid(), Nome = "Trem" };
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        Parada Stop(string nome, Modal? modal = null) => new() { Id = Guid.NewGuid(), Codigo = nome, Nome = nome,
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43.2, -22.9)), ModalId = modal?.Id, Modal = modal };
        var a = Stop("Campo Grande", bus); var b = Stop("Rua Teste"); var c = Stop("Central");
        var old = Stop("Antiga"); var isolated = Stop("Cadastro", bus);
        var ida = Guid.NewGuid(); var railDirection = Guid.NewGuid();
        var rows = new List<OcorrenciaParadaPadrao>();
        var lines = new Dictionary<string, Linha>();
        var directions = new Dictionary<Guid, Sentido>();
        void Add(string codigo, Modal modal, Guid directionId, Parada stop, bool current = true, int repeat = 1)
        {
            if (!lines.TryGetValue(codigo, out var line))
                lines[codigo] = line = new Linha { Id = Guid.NewGuid(), Codigo = codigo, Nome = "Linha " + codigo, ModalId = modal.Id, Modal = modal };
            if (!directions.TryGetValue(directionId, out var direction))
                directions[directionId] = direction = new Sentido { Id = directionId, Nome = "Sentido " + codigo, LinhaId = line.Id, Linha = line };
            var pattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id, Sentido = direction, VersaoAtualId = Guid.NewGuid() };
            var version = new PadraoVersao { Id = current ? pattern.VersaoAtualId!.Value : Guid.NewGuid(), PadraoOperacionalId = pattern.Id, PadraoOperacional = pattern };
            for (var i = 0; i < repeat; i++) rows.Add(new() { Id = Guid.NewGuid(), ParadaId = stop.Id, Parada = stop,
                PadraoVersaoId = version.Id, PadraoVersao = version, Ordem = i + 1 });
        }
        Add("838", bus, ida, a, repeat: 3);
        Add("838", bus, Guid.NewGuid(), b);
        Add("884", bus, Guid.NewGuid(), a);
        Add("T1", rail, railDirection, a);
        Add("T1", rail, railDirection, c);
        Add("838", bus, ida, old, current: false);
        Add("ANTIGA", bus, Guid.NewGuid(), a, current: false);
        return new(new[] { a, b, c, old, isolated }.AsQueryable(), rows.AsQueryable(),
            a.Id, b.Id, c.Id, old.Id, isolated.Id, bus.Id, rail.Id, ida, railDirection);
    }
}
