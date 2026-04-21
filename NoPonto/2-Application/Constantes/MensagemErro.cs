namespace NoPonto.Application.Constantes;

public static class MensagemErro
{
    public const string LINHA_NAO_ENCONTRADA = "Essa linha não foi encontrada.";
    public const string SENTIDO_NAO_ENCONTRADO = "Esse sentido não foi encontrado.";
    public const string ITINERARIO_NAO_ENCONTRADO = "Esse itinerário não foi encontrado.";
    public const string PARADA_NAO_ENCONTRADA = "Essa parada não foi encontrada.";

    public const string PAGINA_INVALIDA = "O parâmetro page deve ser maior que zero.";
    public const string TAMANHO_PAGINA_INVALIDO = "O parâmetro pageSize deve ser maior que zero.";
    public const string LATITUDE_INVALIDA = "O parâmetro lat deve estar entre -90 e 90.";
    public const string LONGITUDE_INVALIDA = "O parâmetro lng deve estar entre -180 e 180.";
    public const string RAIO_INVALIDO = "O parâmetro raio deve ser maior que zero.";

    public static string TamanhoPaginaAcimaDoMaximo(int maximoPermitido)
        => $"O parâmetro pageSize deve ser menor ou igual a {maximoPermitido}.";

    public static string RaioAcimaDoMaximo(double maximoPermitido)
        => $"O parâmetro raio deve ser menor ou igual a {maximoPermitido}.";
}
