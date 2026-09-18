namespace NoPonto.Application.GPS;

/// <summary>
/// Resultado da tentativa de gravação atômica (CAS) da posição de um veículo.
/// Distingue explicitamente "GPS não aceito por regra de negócio" de
/// "não foi possível decidir/gravar por falha de infraestrutura" — os dois
/// NUNCA devem ser tratados da mesma forma pelo chamador.
/// </summary>
public enum PosicaoVeiculoCacheStatus
{
    /// <summary>Gravação confirmada: esta é agora a posição vigente do veículo.</summary>
    Accepted,

    /// <summary>
    /// Rejeitada por regra de negócio: timestamp novo é mais antigo ou igual
    /// ao já armazenado. Redis funcionou normalmente; a decisão foi correta.
    /// </summary>
    RejectedOlderOrEqual,

    /// <summary>
    /// Não foi possível determinar/gravar por falha de infraestrutura (Redis
    /// indisponível, timeout, erro de script, falha de serialização, etc).
    /// NÃO deve ser tratado como rejeição de GPS.
    /// </summary>
    InfrastructureFailure,
}

public readonly record struct PosicaoVeiculoCacheResultado(PosicaoVeiculoCacheStatus Status)
{
    public bool Aceito => Status == PosicaoVeiculoCacheStatus.Accepted;

    public static readonly PosicaoVeiculoCacheResultado Accepted =
        new(PosicaoVeiculoCacheStatus.Accepted);

    public static readonly PosicaoVeiculoCacheResultado RejectedOlderOrEqual =
        new(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual);

    public static readonly PosicaoVeiculoCacheResultado InfrastructureFailure =
        new(PosicaoVeiculoCacheStatus.InfrastructureFailure);
}