namespace NoPonto.Application.GPS;

// Contrato interno do pipeline batch. Embora publico para atravessar a fronteira
// Application/Data, o estado e criado exclusivamente por um ExecutorMatchingGpsLote.
public enum CategoriaFalhaMatchingBatch
{
    Cancellation,
    Connectivity,
    Timeout,
    TransientPostgres,
    SqlOrSchema,
    DataOrMapping,
    Serialization,
    Unknown,
}

public enum AcaoFalhaMatchingBatch
{
    RecuperarChunk,
    ExecutarSonda,
    AbrirCircuito,
}

/// <summary>
/// Circuit breaker local a uma execucao batch. Nao e singleton, nao e persistido
/// e nunca e usado pelo caminho individual com a flag desligada.
/// </summary>
public sealed class MatchingBatchStageProtection
{
    private readonly object _sync = new();
    private readonly HashSet<TipoBatchMatching> _operacoesDegradadas = [];
    private bool _circuitoAberto;
    private bool _sondaExecutada;
    private bool _sondaConcluida;
    private int _probesSucesso;
    private int _probesFalha;
    private int _entradasPuladas;
    private int _comandosEvitados;
    private CategoriaFalhaMatchingBatch? _motivoCircuito;

    public bool CircuitoAberto { get { lock (_sync) return _circuitoAberto; } }
    public int ProbesExecutadas { get { lock (_sync) return _sondaExecutada ? 1 : 0; } }
    public int ProbesSucesso { get { lock (_sync) return _probesSucesso; } }
    public int ProbesFalha { get { lock (_sync) return _probesFalha; } }
    public int EntradasPuladas { get { lock (_sync) return _entradasPuladas; } }
    public int ComandosEvitados { get { lock (_sync) return _comandosEvitados; } }
    public int OperacoesDegradadas { get { lock (_sync) return _operacoesDegradadas.Count; } }
    public CategoriaFalhaMatchingBatch? MotivoCircuito { get { lock (_sync) return _motivoCircuito; } }

    public bool DevePular(TipoBatchMatching tipo)
    {
        lock (_sync) return _circuitoAberto || _operacoesDegradadas.Contains(tipo);
    }

    public void RegistrarPulo(int entradas)
    {
        lock (_sync)
        {
            _entradasPuladas += entradas;
            _comandosEvitados++;
        }
    }

    public AcaoFalhaMatchingBatch RegistrarFalha(
        TipoBatchMatching tipo, CategoriaFalhaMatchingBatch categoria)
    {
        lock (_sync)
        {
            if (_circuitoAberto) return AcaoFalhaMatchingBatch.AbrirCircuito;
            if (!EhCandidataInfraestrutura(categoria))
            {
                _operacoesDegradadas.Add(tipo);
                return AcaoFalhaMatchingBatch.RecuperarChunk;
            }

            // A sonda e unica por estagio. Uma nova evidencia forte depois dela
            // abre o circuito sem criar uma segunda tempestade de sondas.
            if (_sondaExecutada)
            {
                _circuitoAberto = true;
                _motivoCircuito = categoria;
                return AcaoFalhaMatchingBatch.AbrirCircuito;
            }

            _sondaExecutada = true;
            _motivoCircuito = categoria;
            return AcaoFalhaMatchingBatch.ExecutarSonda;
        }
    }

    public bool ConcluirSonda(TipoBatchMatching tipo, bool infraestrutura)
    {
        lock (_sync)
        {
            if (_sondaConcluida) return _circuitoAberto;
            _sondaConcluida = true;
            if (infraestrutura)
            {
                _circuitoAberto = true;
                _probesFalha++;
                return true;
            }

            _operacoesDegradadas.Add(tipo);
            _probesSucesso++;
            return false;
        }
    }

    private static bool EhCandidataInfraestrutura(CategoriaFalhaMatchingBatch categoria) =>
        categoria is CategoriaFalhaMatchingBatch.Connectivity or CategoriaFalhaMatchingBatch.Timeout;
}
