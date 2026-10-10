using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NoPonto.Data.Context;

/// <summary>Tooling EF isolado: não cria host, não inicia serviços nem lê credenciais normais.</summary>
public sealed class TransporteDesignTimeFactory : IDesignTimeDbContextFactory<TransporteDbContext>
{
    public TransporteDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("NOPONTO_EF_CONNECTION")
            ?? throw new InvalidOperationException("Defina NOPONTO_EF_CONNECTION explicitamente para o tooling EF.");
        return new(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, o => o.UseNetTopologySuite()).Options);
    }
}
