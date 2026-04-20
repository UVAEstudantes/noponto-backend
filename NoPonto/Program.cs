using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.Services;

Env.Load();

static string GetEnv(string key)
{
    var value = Environment.GetEnvironmentVariable(key);

    if (string.IsNullOrWhiteSpace(value))
        throw new Exception($"Variável de ambiente {key} não encontrada.");

    return value;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var corsOrigins = builder.Configuration.GetSection("CORS:ORIGINS").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPadrao", policy =>
    {
        if (corsOrigins.Length > 0)
        {
            policy
                .WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();

            return;
        }

        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

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

builder.Services.AddHttpClient<ArcGisClientService>();
builder.Services.AddScoped<ImportacaoParadasService>();
builder.Services.AddScoped<RelacionarParadasItinerariosService>();
builder.Services.AddScoped<RelacionarParadasJob>();
builder.Services.AddHostedService<ImportacaoItinerariosService>();

var app = builder.Build();

app.UseCors("CorsPadrao");

app.MapControllers();

app.MapGet("/", () => "Hello World!");

app.Run();
