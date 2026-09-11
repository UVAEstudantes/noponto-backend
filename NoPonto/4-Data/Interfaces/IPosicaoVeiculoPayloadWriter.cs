namespace NoPonto.Data.Interfaces;

/// <summary>
/// Grava atomicamente o payload de posição do veículo, condicionado à posse
/// do lock de escrita — o "fencing token" da Etapa 1.
///
/// A verificação de posse do lock ("veiculo:{ordem}:gps-lock" ainda contém
/// exatamente <paramref name="token"/>") e a gravação do payload acontecem
/// dentro da MESMA operação atômica no Redis. Não existe nenhuma janela
/// entre "verificar" e "escrever": se o lock não pertencer mais ao token
/// informado no exato instante da gravação, nada é escrito.
/// </summary>
public interface IPosicaoVeiculoPayloadWriter
{
    /// <summary>
    /// Tenta gravar <paramref name="json"/> em <paramref name="chave"/>,
    /// condicionado a <paramref name="chaveLock"/> ainda conter exatamente
    /// <paramref name="token"/>.
    /// Retorna true se a escrita foi confirmada; false se o fencing rejeitou
    /// a escrita (lock expirado, tomado por outro dono, ou inexistente) —
    /// nesse caso NADA é alterado em <paramref name="chave"/>.
    /// </summary>
    Task<bool> GravarComFencingAsync(
        string chave,
        string chaveLock,
        string token,
        string json,
        TimeSpan ttl,
        CancellationToken ct);
}