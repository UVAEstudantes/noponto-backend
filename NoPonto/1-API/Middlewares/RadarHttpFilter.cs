using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using NoPonto.Application.Radar;
using NoPonto.API.Controllers;

namespace NoPonto.API.Middlewares;

/// <summary>Gate exclusivo do Radar, executado antes de binding e validação de modelos.</summary>
public sealed class RadarHttpFilter(IOptions<RadarOptions> options, RadarRateGate gate)
    : IAsyncResourceFilter, IAsyncExceptionFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!options.Value.Enabled) { context.Result = new NotFoundResult(); return; }
        var registration = context.HttpContext.Request.Method == "POST"
            && context.ActionDescriptor.RouteValues["action"] == nameof(RadarController.Registrar);
        if (!gate.Permitir(context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", registration))
        {
            context.HttpContext.Response.Headers.RetryAfter = registration ? "600" : "60";
            context.Result = Error(429, "limite_de_requisicoes");
            return;
        }
        await next();
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is RadarException e)
        {
            if (e.Status == 401) context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"radar\"";
            if (e.Status == 429) context.HttpContext.Response.Headers.RetryAfter = e.Codigo == "limite_de_registros" ? "86400" : "60";
            context.Result = Error(e.Status, e.Codigo);
            context.ExceptionHandled = true;
        }
        return Task.CompletedTask;
    }

    private static ObjectResult Error(int status, string code) => new(new ProblemDetails
    {
        Status = status, Title = code, Extensions = { ["code"] = code }
    }) { StatusCode = status };
}
