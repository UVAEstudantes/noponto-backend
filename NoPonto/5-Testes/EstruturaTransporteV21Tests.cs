using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NoPonto.Data.Configuration;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstruturaTransporteV21Tests(EstruturaTransporteV21Fixture fixture)
    : IClassFixture<EstruturaTransporteV21Fixture>
{
    [Fact]
    public async Task Versao_AceitaParadaRepetidaEImportacoesComPapeisDiferentes()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        var paradaB = new Parada { Id = Guid.NewGuid(), Codigo = Guid.NewGuid().ToString("N"), Nome = "B",
            Localizacao = new Point(-43.18, -22.88) { SRID = 4326 } };
        var paradaC = new Parada { Id = Guid.NewGuid(), Codigo = Guid.NewGuid().ToString("N"), Nome = "C",
            Localizacao = new Point(-43.16, -22.86) { SRID = 4326 } };
        var outraImportacao = new ImportacaoEstrutural { Id = Guid.NewGuid(), FonteEstruturalId = data.Fonte.Id,
            Status = StatusImportacaoEstrutural.Concluida, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConteudoHash = Guid.NewGuid().ToString("N"), AlgoritmoVersao = "v1", Relatorio = "{}" };
        db.AddRange(paradaB, paradaC, outraImportacao);

        db.OcorrenciasParadasPadroes.AddRange(
            Ocorrencia(data.Versao.Id, data.Parada.Id, 1, 0),
            Ocorrencia(data.Versao.Id, paradaB.Id, 2, .3),
            Ocorrencia(data.Versao.Id, paradaC.Id, 3, .7),
            Ocorrencia(data.Versao.Id, data.Parada.Id, 4, 1));
        db.PadroesVersoesImportacoes.AddRange(
            new() { PadraoVersaoId = data.Versao.Id, ImportacaoEstruturalId = data.Importacao.Id,
                Papel = PapeisImportacaoPadrao.Membership },
            new() { PadraoVersaoId = data.Versao.Id, ImportacaoEstruturalId = outraImportacao.Id,
                Papel = PapeisImportacaoPadrao.Geometria });

        await db.SaveChangesAsync();

        Assert.Equal(4, await db.OcorrenciasParadasPadroes.CountAsync(x => x.PadraoVersaoId == data.Versao.Id));
        Assert.Equal(2, await db.PadroesVersoesImportacoes.CountAsync(x => x.PadraoVersaoId == data.Versao.Id));
    }

    [Fact]
    public async Task IdentidadeExterna_DuplicadaNaMesmaFonteETipo_ERejeitada()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        var outra = new Parada { Id = Guid.NewGuid(), Codigo = Guid.NewGuid().ToString("N"), Nome = "Outra",
            Localizacao = new Point(-43.19, -22.91) { SRID = 4326 } };
        db.Paradas.Add(outra);
        db.ParadasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), ParadaId = data.Parada.Id,
            FonteEstruturalId = data.Fonte.Id, Tipo = "stop_id", ExternalId = "MESMO-ID" });
        await db.SaveChangesAsync();

        db.ParadasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), ParadaId = outra.Id,
            FonteEstruturalId = data.Fonte.Id, Tipo = "stop_id", ExternalId = "MESMO-ID" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Ocorrencia_ComOrdemDuplicadaOuPosicaoInvalida_ERejeitada()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        db.OcorrenciasParadasPadroes.Add(Ocorrencia(data.Versao.Id, data.Parada.Id, 1, 0));
        await db.SaveChangesAsync();

        db.OcorrenciasParadasPadroes.Add(Ocorrencia(data.Versao.Id, data.Parada.Id, 1, .5));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.OcorrenciasParadasPadroes.Add(Ocorrencia(data.Versao.Id, data.Parada.Id, 2, 1.01));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task VersaoAtual_DeOutroPadrao_ERejeitadaPelaFkComposta()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var primeiro = await CriarEstruturaBaseAsync(db);
        var segundo = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = primeiro.Padrao.SentidoId,
            Chave = Guid.NewGuid().ToString("N"), TipoServico = "EXPRESSO" };
        var versaoSegundo = Versao(segundo.Id, 1);
        db.PadroesOperacionais.Add(segundo);
        db.PadroesVersoes.Add(versaoSegundo);
        await db.SaveChangesAsync();

        primeiro.Padrao.VersaoAtualId = versaoSegundo.Id;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Importacao_MesmoConteudo_PodeSerReprocessadaComNovoAlgoritmo()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        db.ImportacoesEstruturais.Add(new() { Id = Guid.NewGuid(), FonteEstruturalId = data.Fonte.Id,
            Status = StatusImportacaoEstrutural.Concluida, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConteudoHash = data.Importacao.ConteudoHash, AlgoritmoVersao = "v2", Relatorio = "{}" });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Importacao_Falha_PodeSerReexecutadaComMesmoConteudoEAlgoritmo()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        data.Importacao.Status = StatusImportacaoEstrutural.Falhou;
        await db.SaveChangesAsync();
        db.ImportacoesEstruturais.Add(new() { Id = Guid.NewGuid(), FonteEstruturalId = data.Fonte.Id,
            Status = StatusImportacaoEstrutural.EmProcessamento, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConteudoHash = data.Importacao.ConteudoHash, AlgoritmoVersao = data.Importacao.AlgoritmoVersao,
            Relatorio = "{}" });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Importacao_DuasConcluidasEquivalentes_ERejeitada()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        db.ImportacoesEstruturais.Add(new() { Id = Guid.NewGuid(), FonteEstruturalId = data.Fonte.Id,
            Status = StatusImportacaoEstrutural.Concluida, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConteudoHash = data.Importacao.ConteudoHash, AlgoritmoVersao = data.Importacao.AlgoritmoVersao,
            Relatorio = "{}" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task IdentidadesExternas_SuportamMultiplasFontesEIdentidadeDeSentido()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        var fonte2 = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = "F2_" + Guid.NewGuid().ToString("N"), Nome = "Fonte 2" };
        db.Add(fonte2);
        db.ParadasIdentidadesExternas.AddRange(
            new() { Id = Guid.NewGuid(), ParadaId = data.Parada.Id, FonteEstruturalId = data.Fonte.Id,
                Tipo = "stop_id", ExternalId = "ID-COMUM" },
            new() { Id = Guid.NewGuid(), ParadaId = data.Parada.Id, FonteEstruturalId = fonte2.Id,
                Tipo = "stop_id", ExternalId = "ID-COMUM" });
        db.SentidosIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), SentidoId = data.Padrao.SentidoId,
            FonteEstruturalId = data.Fonte.Id, Tipo = "direction_id", ExternalId = "0" });

        await db.SaveChangesAsync();
        Assert.Equal(2, await db.ParadasIdentidadesExternas.CountAsync(x => x.ParadaId == data.Parada.Id));
        Assert.True(await db.SentidosIdentidadesExternas.AnyAsync(x => x.SentidoId == data.Padrao.SentidoId));
    }

    [Fact]
    public async Task VersaoAtualValida_OrdemEmVersoesDiferentesEGeometria4326_SaoPermitidas()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        var segunda = Versao(data.Padrao.Id, 2);
        db.PadroesVersoes.Add(segunda);
        db.OcorrenciasParadasPadroes.AddRange(
            Ocorrencia(data.Versao.Id, data.Parada.Id, 1, 0),
            Ocorrencia(segunda.Id, data.Parada.Id, 1, 0));
        data.Padrao.VersaoAtualId = data.Versao.Id;

        await db.SaveChangesAsync();
        Assert.Equal(4326, data.Versao.Geometria.SRID);
        Assert.Equal(data.Versao.Id, (await db.PadroesOperacionais.SingleAsync(x => x.Id == data.Padrao.Id)).VersaoAtualId);
    }

    [Fact]
    public async Task Ocorrencia_ComDistanciaNegativa_ERejeitada()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var data = await CriarEstruturaBaseAsync(db);
        var ocorrencia = Ocorrencia(data.Versao.Id, data.Parada.Id, 1, 0);
        ocorrencia.DistanciaAcumuladaMetros = -1;
        db.OcorrenciasParadasPadroes.Add(ocorrencia);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Schema_PostgisPossuiGistNaGeometriaDoPadrao()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var sql = $"SELECT indexdef FROM pg_indexes WHERE schemaname = '{fixture.Schema}' " +
            "AND tablename = 'PadroesVersoes' AND indexdef ILIKE '%USING gist%';";
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await db.Database.OpenConnectionAsync();
        Assert.NotNull(await command.ExecuteScalarAsync());
    }

    private static OcorrenciaParadaPadrao Ocorrencia(Guid versao, Guid parada, int ordem, double posicao) =>
        new() { Id = Guid.NewGuid(), PadraoVersaoId = versao, ParadaId = parada, Ordem = ordem,
            SourceSequence = ordem - 1, PosicaoTracado = posicao };

    private static PadraoVersao Versao(Guid padrao, int numero) => new()
    {
        Id = Guid.NewGuid(), PadraoOperacionalId = padrao, Numero = numero,
        Geometria = new LineString([new(-43.2, -22.9), new(-43.1, -22.8)]) { SRID = 4326 },
        DistanciaMetros = 1000, MetodoConstrucao = "GTFS_AUTORITATIVO", Confianca = 1,
        AlgoritmoVersao = "v1", ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
        Relatorio = "{}", CriadaEmUtc = DateTimeOffset.UtcNow
    };

    private static async Task<BaseData> CriarEstruturaBaseAsync(TransporteDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Modal " + suffix };
        var linha = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = suffix, Nome = "Linha" };
        var sentido = new Sentido { Id = Guid.NewGuid(), LinhaId = linha.Id, Nome = "Ida" };
        var parada = new Parada { Id = Guid.NewGuid(), Codigo = suffix, Nome = "Parada",
            Localizacao = new Point(-43.2, -22.9) { SRID = 4326 } };
        var fonte = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = "F_" + suffix, Nome = "Fonte" };
        var importacao = new ImportacaoEstrutural { Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
            Status = StatusImportacaoEstrutural.Concluida, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConteudoHash = suffix, AlgoritmoVersao = "v1", Relatorio = "{}" };
        var padrao = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = sentido.Id,
            Chave = suffix, TipoServico = "REGULAR" };
        var versao = Versao(padrao.Id, 1);
        db.AddRange(modal, linha, sentido, parada, fonte, importacao, padrao, versao);
        await db.SaveChangesAsync();
        return new(modal, linha, sentido, parada, fonte, importacao, padrao, versao);
    }

    private sealed record BaseData(Modal Modal, Linha Linha, Sentido Sentido, Parada Parada,
        FonteEstrutural Fonte, ImportacaoEstrutural Importacao, PadraoOperacional Padrao, PadraoVersao Versao);
}

public sealed class EstruturaTransporteV21Fixture : IAsyncLifetime
{
    public string Schema { get; } = "estrutura_v21_" + Guid.NewGuid().ToString("N");
    public ServiceProvider Provider { get; private set; } = null!;
    private NpgsqlDataSource _admin = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using (var command = _admin.CreateCommand($"CREATE SCHEMA \"{Schema}\""))
            await command.ExecuteNonQueryAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AdicionarPostgresCompartilhado(
            new NpgsqlConnectionStringBuilder(connection) { SearchPath = Schema + ",public" }.ConnectionString);
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        await db.GetService<IMigrator>().MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (_admin is not null)
        {
            await using var command = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }
    }
}
