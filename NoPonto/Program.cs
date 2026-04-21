using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NoPonto.API.Middlewares;
using NoPonto.Application.Interfaces;
using NoPonto.Application.Services;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using System.Reflection;

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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NoPonto API",
        Version = "v1",
        Description = "API para consulta e importação de dados de transporte público (linhas, sentidos, itinerários, paradas e POIs)."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    options.TagActionsBy(api =>
    {
        var controller = api.ActionDescriptor.RouteValues["controller"];
        return [string.IsNullOrWhiteSpace(controller) ? "Outros" : controller];
    });
});

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

builder.Services.AddScoped<ILinhaRepository, LinhaRepository>();
builder.Services.AddScoped<ISentidoRepository, SentidoRepository>();
builder.Services.AddScoped<IItinerarioRepository, ItinerarioRepository>();
builder.Services.AddScoped<IParadaRepository, ParadaRepository>();
builder.Services.AddScoped<IPoiRepository, PoiRepository>();
builder.Services.AddScoped<IModalRepository, ModalRepository>();

builder.Services.AddScoped<ILinhaService, LinhaService>();
builder.Services.AddScoped<ISentidoService, SentidoService>();
builder.Services.AddScoped<IItinerarioService, ItinerarioService>();
builder.Services.AddScoped<IParadaService, ParadaService>();
builder.Services.AddScoped<IPoiService, PoiService>();
builder.Services.AddScoped<IModalService, ModalService>();

builder.Services.AddHttpClient<ArcGisClientService>();
builder.Services.AddScoped<ImportacaoParadasService>();
builder.Services.AddScoped<RelacionarParadasItinerariosService>();
builder.Services.AddScoped<RelacionarParadasJob>();
builder.Services.AddHostedService<ImportacaoItinerariosService>();

var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "NoPonto API v1");
    options.DocumentTitle = "NoPonto API - Documentação";
});

app.UseCors("CorsPadrao");

app.MapControllers();

app.MapGet("/", () => "Hello World!");

app.Run();
