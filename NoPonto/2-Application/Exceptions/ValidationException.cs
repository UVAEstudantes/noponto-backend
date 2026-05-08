namespace NoPonto.Application.Exceptions;

public sealed class ValidationException : Exception
{
    public ValidationException(string mensagem)
        : base(mensagem)
    {
    }
}
