using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsPinnedVersionProjectionPostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly GpsPadraoRepository _repository;

    public GpsPinnedVersionProjectionPostgisTests(PostgisGpsFixture db)
    {
        _db = db;
        _repository = new(db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
    }

    [Fact]
    public async Task PointerAParaB_GlobalUsaB_EVersaoPinadaAContinuaProjetavelComCadeiaValidada()
    {
        var linha = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        var padrao = Guid.NewGuid();
        var versaoA = Guid.NewGuid();
        var versaoB = Guid.NewGuid();
        var outraLinha = Guid.NewGuid();
        var outroSentido = Guid.NewGuid();
        var outroPadrao = Guid.NewGuid();
        var versaoOutroPadrao = Guid.NewGuid();
        await using (var seed = _db.DataSource.CreateCommand("""
            INSERT INTO "Linhas" VALUES (@linha,'PIN-A-B');
            INSERT INTO "Linhas" VALUES (@outra_linha,'PIN-OUTRA');
            INSERT INTO "Sentidos" VALUES (@sentido,@linha);
            INSERT INTO "Sentidos" VALUES (@outro_sentido,@outra_linha);
            INSERT INTO "PadroesOperacionais" ("Id","SentidoId","VersaoAtualId")
                VALUES (@padrao,@sentido,@versao_a);
            INSERT INTO "PadroesOperacionais" ("Id","SentidoId","VersaoAtualId")
                VALUES (@outro_padrao,@outro_sentido,@versao_outro_padrao);
            INSERT INTO "PadroesVersoes" ("Id","PadraoOperacionalId","Geometria","Topologia") VALUES
                (@versao_a,@padrao,ST_GeomFromText('LINESTRING(-43.21 -22.91,-43.19 -22.91)',4326),'LINEAR'),
                (@versao_b,@padrao,ST_GeomFromText('LINESTRING(-43.21 -22.9098,-43.19 -22.9098)',4326),'LINEAR'),
                (@versao_outro_padrao,@outro_padrao,ST_GeomFromText('LINESTRING(-43.21 -22.91,-43.19 -22.91)',4326),'LINEAR');
            UPDATE "PadroesOperacionais" SET "VersaoAtualId"=@versao_b WHERE "Id"=@padrao;
            """))
        {
            seed.Parameters.AddWithValue("linha", linha);
            seed.Parameters.AddWithValue("sentido", sentido);
            seed.Parameters.AddWithValue("padrao", padrao);
            seed.Parameters.AddWithValue("versao_a", versaoA);
            seed.Parameters.AddWithValue("versao_b", versaoB);
            seed.Parameters.AddWithValue("outra_linha", outraLinha);
            seed.Parameters.AddWithValue("outro_sentido", outroSentido);
            seed.Parameters.AddWithValue("outro_padrao", outroPadrao);
            seed.Parameters.AddWithValue("versao_outro_padrao", versaoOutroPadrao);
            await seed.ExecuteNonQueryAsync();
        }

        var global = await _repository.BuscarEnriquecimentoAsync(
            "PIN-A-B", -22.9098, -43.20, 90, 250);
        Assert.NotNull(global);
        Assert.Equal(versaoB, global.PadraoVersaoId);

        var direcionado = await _repository.BuscarEnriquecimentoDoPadraoAsync(
            "PIN-A-B", versaoA, -22.91, -43.20, 90, 250, faixa: new(.40, .60));
        Assert.Equal(StatusBuscaPadrao.Found, direcionado.Status);
        Assert.Equal(versaoA, direcionado.Rota!.PadraoVersaoId);
        Assert.Equal(padrao, direcionado.Rota.PadraoOperacionalId);
        Assert.Equal(sentido, direcionado.Rota.SentidoId);
        Assert.Equal(linha, direcionado.Rota.LinhaId);
        Assert.InRange(direcionado.Rota.PosicaoNaRota, .49, .51);

        var pedido = new SolicitacaoProjecaoOperacional(
            versaoA, .45, 300, padrao, sentido, linha);
        var combinado = await _repository.BuscarMatchingCombinadoAsync(
            "PIN-A-B", null, -22.91, -43.20, 90, 250, null, pedido);
        Assert.Equal(versaoB, combinado.Global.Rota!.PadraoVersaoId);
        Assert.Equal(StatusProjecaoOperacional.Encontrada, combinado.Operacional!.Status);
        Assert.Equal(versaoA, combinado.Operacional.Projecao!.PadraoVersaoId);
        Assert.InRange(combinado.Operacional.Projecao.PosicaoNaRota, .49, .51);

        var lote = await _repository.BuscarCombinadosEmLoteAsync([
            new("pin", "PIN-A-B", null, -22.91, -43.20, 90, 250, null, pedido)
        ]);
        var resultadoLote = Assert.Single(lote.Resultados).Resultado;
        Assert.Equal(versaoB, resultadoLote.Global.Rota!.PadraoVersaoId);
        Assert.Equal(versaoA, resultadoLote.Operacional!.Projecao!.PadraoVersaoId);

        await AssertInelegivel(pedido with { PadraoOperacionalId = Guid.NewGuid() });
        await AssertInelegivel(pedido with { SentidoId = Guid.NewGuid() });
        await AssertInelegivel(pedido with { LinhaId = Guid.NewGuid() });
        await AssertInelegivel(pedido with { PadraoVersaoId = versaoOutroPadrao });
        await AssertInelegivel(pedido with { PadraoVersaoId = Guid.NewGuid() });
    }

    private async Task AssertInelegivel(SolicitacaoProjecaoOperacional pedido)
    {
        var resultado = await _repository.BuscarMatchingCombinadoAsync(
            "PIN-A-B", null, -22.91, -43.20, 90, 250, null, pedido);
        Assert.Equal(StatusProjecaoOperacional.Inelegivel, resultado.Operacional!.Status);
        Assert.Null(resultado.Operacional.Projecao);
    }
}
