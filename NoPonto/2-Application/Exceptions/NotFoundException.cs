namespace NoPonto.Application.Exceptions;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string mensagem)
        : base(mensagem)
    {
    }
}
