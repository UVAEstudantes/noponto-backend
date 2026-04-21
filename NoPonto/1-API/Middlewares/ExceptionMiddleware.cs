using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.Exceptions;

namespace NoPonto.API.Middlewares;

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await TratarExcecaoAsync(context, ex);
        }
    }

    private async Task TratarExcecaoAsync(HttpContext context, Exception exception)
    {
        var status = StatusCodes.Status500InternalServerError;
        var mensagem = "Ocorreu um erro interno no servidor.";

        switch (exception)
        {
            case NotFoundException:
                status = StatusCodes.Status404NotFound;
                mensagem = exception.Message;
                break;
            case BusinessException:
            case ValidationException:
                status = StatusCodes.Status400BadRequest;
                mensagem = exception.Message;
                break;
            default:
                _logger.LogError(exception, "Erro não tratado capturado pelo middleware global.");
                break;
        }

        if (status != StatusCodes.Status500InternalServerError)
        {
            _logger.LogWarning("Exceção de negócio capturada. status={status}, mensagem={mensagem}", status, mensagem);
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = status;

        var resposta = new ErroRespostaDTO
        {
            Status = status,
            Mensagem = mensagem,
            Timestamp = DateTime.UtcNow
        };

        await context.Response.WriteAsJsonAsync(resposta);
    }
}
