using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using System.Reflection;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class PostgresEstabilidadeTests
{
    private static PosicaoVeiculoDto[] Positions() => Enumerable.Range(0, 120)
        .Select(i => new PosicaoVeiculoDto { Ordem = "ESTABILIDADE_" + i,
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = .4,
            TimestampGps = DateTimeOffset.UtcNow }).ToArray();

    private sealed class Cache : IPosicaoVeiculoCacheRepository
    {
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem,
            PosicaoVeiculoDto posicao, DateTimeOffset ts, TimeSpan ativo, TimeSpan recente, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PosicaoVeiculoCacheResultado(PosicaoVeiculoCacheStatus.Accepted));
        }
    }

    private sealed class Repository : IViagemObservadaRepository
    {
        public readonly TaskCompletionSource Cheio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Liberar = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Ativos, Maximo, Chamadas, Concluidas;
        public bool Falhar;
        public async Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem,
            Guid id, DateTimeOffset ts, double p, CancellationToken ct)
        {
            Interlocked.Increment(ref Chamadas);
            var ativos = Interlocked.Increment(ref Ativos);
            int anterior;
            do { anterior = Volatile.Read(ref Maximo); }
            while (ativos > anterior && Interlocked.CompareExchange(ref Maximo, ativos, anterior) != anterior);
            if (ativos == 20) Cheio.TrySetResult();
            try
            {
                await Liberar.Task.WaitAsync(ct);
                await Task.Yield();
                if (Falhar && ordem == "ESTABILIDADE_0") throw new TimeoutException();
                return new(ViagemObservadaStatus.Updated);
            }
            finally { Interlocked.Decrement(ref Ativos); Interlocked.Increment(ref Concluidas); }
        }
    }

    private static GpsPollingService Polling(Repository repo) => new(null!, null!, null!,
        NullLogger<GpsPollingService>.Instance, null!, null!, null!, null!, null!,
        new Cache(), new ViagemObservadaService(repo, NullLogger<ViagemObservadaService>.Instance));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Polling_Lote120_Limita20_AguardaTodosMesmoComFalha(bool falhar)
    {
        var repo = new Repository { Falhar = falhar };
        var polling = Polling(repo);
        var positions = Positions();
        var task = polling.ConfirmarLoteAsync(positions, TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(180), new GpsPollingOptions().GrauParalelismoViagemObservada, default);
        try
        {
            await repo.Cheio.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(20, repo.Chamadas);
            Assert.Equal(20, repo.Maximo);
            Assert.False(task.IsCompleted);
        }
        finally { repo.Liberar.TrySetResult(); }
        var results = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(120, repo.Concluidas);
        Assert.Equal(0, repo.Ativos);
        Assert.Equal(20, repo.Maximo);
        Assert.All(results, r => Assert.True(r.Resultado.Aceito));
        Assert.Equal(positions.Select(p => p.Ordem), results.Select(r => r.Posicao.Ordem));
        await polling.ConfirmarLoteAsync(positions, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), 20, default);
        Assert.Equal(240, repo.Concluidas);
    }

    [Fact]
    public async Task Polling_Cancelamento_DrenaOperacoesELiberaPermissoes()
    {
        var repo = new Repository();
        using var cts = new CancellationTokenSource();
        var polling = Polling(repo);
        var task = polling.ConfirmarLoteAsync(Positions(), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), 20, cts.Token);
        try { await repo.Cheio.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { cts.Cancel(); repo.Liberar.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, repo.Ativos);
        await polling.ConfirmarLoteAsync(Positions(), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), 20, default);
        Assert.Equal(0, repo.Ativos);
    }

    [Fact]
    public async Task DataSource_DiReal_EfCompartilhaSingletonEntreScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AdicionarPostgresCompartilhado("Host=localhost;Database=teste;Username=teste;Password=teste");
        services.AddSingleton<IGpsItinerarioRepository, GpsItinerarioRepository>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var source = provider.GetRequiredService<NpgsqlDataSource>();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var context2 = scope2.ServiceProvider.GetRequiredService<TransporteDbContext>();
        Assert.NotSame(context1, context2);
        Assert.Same(context1, scope1.ServiceProvider.GetRequiredService<TransporteDbContext>());
        foreach (var context in new[] { context1, context2 })
        {
            var extension = context.GetService<IDbContextOptions>().Extensions.Single(e => e.GetType().Name == "NpgsqlOptionsExtension");
            Assert.Same(source, extension.GetType().GetProperty("DataSource")!.GetValue(extension));
        }
        Assert.Same(source, scope2.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        var repository = provider.GetRequiredService<IGpsItinerarioRepository>();
        Assert.Same(repository, scope2.ServiceProvider.GetRequiredService<IGpsItinerarioRepository>());
        Assert.Same(source, typeof(GpsItinerarioRepository).GetField("_dataSource",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository));
    }
}
