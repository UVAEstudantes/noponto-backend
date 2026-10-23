using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NoPonto.API.Controllers;

/// <summary>Segredo exclusivo de operador, configurado no servidor; nunca distribuir no mobile.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FormaPagamentoAdminAttribute : Attribute, IAuthorizationFilter
{
    public const string ConfigurationKey = "FORMAS_PAGAMENTO_ADMIN_SECRET";
    public const string HeaderName = "X-Formas-Pagamento-Admin-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length is < 32 or > 256)
        {
            context.Result = new ObjectResult(new ProblemDetails
                { Status = 503, Title = "Edição administrativa indisponível." }) { StatusCode = 503 };
            return;
        }

        var headers = context.HttpContext.Request.Headers;
        if (!headers.TryGetValue(HeaderName, out var values) || values.Count != 1
            || values[0] is not { Length: > 0 and <= 256 } supplied
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = "ApiKey realm=\"formas-pagamento\"";
            context.Result = new UnauthorizedObjectResult(new ProblemDetails
                { Status = 401, Title = "Credencial administrativa inválida." });
        }
    }
}
