using NoPonto.Application.Constantes;
using NoPonto.Application.Exceptions;

namespace NoPonto.Application.Util;

public static class PaginacaoUtil
{
    public static void ValidarOuLancar(int page, int pageSize, int maximoPermitido)
    {
        if (page <= 0)
            throw new ValidationException(MensagemErro.PAGINA_INVALIDA);

        if (pageSize <= 0)
            throw new ValidationException(MensagemErro.TAMANHO_PAGINA_INVALIDO);

        if (pageSize > maximoPermitido)
            throw new ValidationException(MensagemErro.TamanhoPaginaAcimaDoMaximo(maximoPermitido));
    }
}
