using DotNetEnv;
using Microsoft.EntityFrameworkCore;

Env.Load();

static string GetEnv(string key)
{
    var value = Environment.GetEnvironmentVariable(key);

    if (string.IsNullOrWhiteSpace(value))
        throw new Exception($"Variável de ambiente {key} não encontrada.");

    return value;
}

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    $"Host=localhost;" +
    $"Port={GetEnv("POSTGRES_PORT")};" +
    $"Database={GetEnv("POSTGRES_DB")};" +
    $"Username={GetEnv("POSTGRES_USER")};" +
    $"Password={GetEnv("POSTGRES_PASSWORD")}";

builder.Services.AddDbContext<TransporteDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        x => x.UseNetTopologySuite()
    )
);

var redisConnection =
    $"localhost:{Environment.GetEnvironmentVariable("REDIS_PORT")}";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
