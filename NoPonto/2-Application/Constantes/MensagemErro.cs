namespace NoPonto.Application.Constantes;

public static class MensagemErro
{
    public const string LINHA_NAO_ENCONTRADA = "Essa linha não foi encontrada.";
    public const string SENTIDO_NAO_ENCONTRADO = "Esse sentido não foi encontrado.";
    public const string ITINERARIO_NAO_ENCONTRADO = "Esse itinerário não foi encontrado.";
    public const string PARADA_NAO_ENCONTRADA = "Essa parada não foi encontrada.";

    public const string PAGINA_INVALIDA = "O parâmetro page deve ser maior que zero.";
    public const string TAMANHO_PAGINA_INVALIDO = "O parâmetro pageSize deve ser maior que zero.";

    public static string TamanhoPaginaAcimaDoMaximo(int maximoPermitido)
        => $"O parâmetro pageSize deve ser menor ou igual a {maximoPermitido}.";
}
