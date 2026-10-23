using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NoPonto.Application.DTOs.Tarifas;
using NoPonto.Application.Tarifas;
using NoPonto.Data.Tarifas;
using NoPonto.Domain.Entities;
using NoPonto.Domain.Tarifas;
using Xunit;

namespace NoPonto.Tests;

public sealed class FormaPagamentoAparenciaTests
{
    [Theory]
    [InlineData(null)] [InlineData("credit-card")] [InlineData("wallet")]
    [InlineData("banknote")] [InlineData("landmark")] [InlineData("smartphone")] [InlineData("qr-code")]
    public void CatalogoAceitaSomenteIconesDoFrontend(string? icone) => Assert.True(RegrasTarifarias.IconeValido(icone));

    [Theory]
    [InlineData(null)] [InlineData("#000000")] [InlineData("#FFFFFF")] [InlineData("#aB12cD")]
    public void CorOpcionalOuHexadecimal(string? cor) => Assert.True(RegrasTarifarias.CorValida(cor));

    [Theory]
    [InlineData("", null)] [InlineData("Wallet", null)] [InlineData("wallet ", null)]
    [InlineData("<svg>", null)] [InlineData("unknown", null)]
    [InlineData(null, "")] [InlineData(null, "red")] [InlineData(null, "123456")]
    [InlineData(null, "#123")] [InlineData(null, "#12345678")] [InlineData(null, "#GG0000")]
    [InlineData(null, "#123456\n")] [InlineData(null, " #123456")]
    [InlineData(null, "#１２３４５６")]
    public async Task CadastroInvalidoRetorna400AntesDeAcessarBanco(string? icone, string? cor)
    {
        using var db = Db();
        var service = new TarifaService(db, new TarifasStore(db));
        var error = await Assert.ThrowsAsync<TarifasException>(() => service.CriarFormaAsync("PIX", icone, cor));
        Assert.Equal(400, error.Status);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void ClienteAntigoENulosMantemContratoJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = JsonSerializer.Deserialize<FormaPagamentoRequest>("{\"nome\":\"Dinheiro\"}", options)!;
        Assert.Equal("Dinheiro", legacy.Nome); Assert.Null(legacy.Icone); Assert.Null(legacy.Cor);
        var id = Guid.NewGuid();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new FormaPagamentoResposta(id, legacy.Nome), options));
        Assert.Equal(id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Dinheiro", json.RootElement.GetProperty("nome").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("icone").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cor").ValueKind);
        var colored = new FormaPagamentoResposta(id, "PIX", "qr-code", "#aB12cD");
        var resolved = new TarifasResolvidasResposta(Guid.NewGuid(), null, new(null, null, null), [colored]);
        using var nested = JsonDocument.Parse(JsonSerializer.Serialize(resolved, options));
        var forma = nested.RootElement.GetProperty("formasPagamento")[0];
        Assert.Equal("qr-code", forma.GetProperty("icone").GetString());
        Assert.Equal("#aB12cD", forma.GetProperty("cor").GetString());
    }

    [Fact]
    public void ModeloSnapshotEMigrationSomenteDuasColunasNullable()
    {
        using var db = Db();
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(FormaPagamento))!;
        Assert.True(entity.FindProperty(nameof(FormaPagamento.Icone))!.IsNullable);
        Assert.Equal(20, entity.FindProperty(nameof(FormaPagamento.Icone))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(FormaPagamento.Cor))!.IsNullable);
        Assert.Equal(7, entity.FindProperty(nameof(FormaPagamento.Cor))!.GetMaxLength());
        var assembly = db.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(x => x.Key.EndsWith("_FormaPagamentoAparencia", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(entry.Value, db.Database.ProviderName!);
        Assert.Equal(2, migration.UpOperations.Count);
        Assert.Equal(new[] { "Cor", "Icone" }, migration.UpOperations.Cast<AddColumnOperation>().Select(x => x.Name).Order());
        Assert.All(migration.UpOperations, op =>
        {
            var column = Assert.IsType<AddColumnOperation>(op);
            Assert.Equal("FormasPagamento", column.Table);
            Assert.True(column.IsNullable); Assert.Null(column.DefaultValue); Assert.Null(column.DefaultValueSql);
        });
        Assert.Equal(2, migration.DownOperations.Count);
        Assert.All(migration.DownOperations, op => Assert.Equal("FormasPagamento", Assert.IsType<DropColumnOperation>(op).Table));
        var previous = assembly.Migrations.Keys.TakeWhile(x => x != entry.Key).Last();
        var sql = db.GetService<IMigrator>().GenerateScript(previous, entry.Key);
        Assert.Contains("ALTER TABLE \"FormasPagamento\" ADD \"Cor\" character varying(7)", sql);
        Assert.Contains("ALTER TABLE \"FormasPagamento\" ADD \"Icone\" character varying(20)", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DELETE ", sql); Assert.DoesNotContain("DROP ", sql);
    }

    private static TransporteDbContext Db() => new(new DbContextOptionsBuilder<TransporteDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_opened", x => x.UseNetTopologySuite()).Options);
}
