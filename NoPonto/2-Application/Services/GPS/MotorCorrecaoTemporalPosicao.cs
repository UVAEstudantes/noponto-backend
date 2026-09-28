namespace NoPonto.Application.GPS;

/// <summary>
/// Port puro e determinístico do avaliador Python da etapa 4.2.2. Não realiza I/O
/// e não possui integração com polling, viagem operacional, ETA ou SignalR.
/// </summary>
public sealed class MotorCorrecaoTemporalPosicao
{
    private readonly CorrecaoTemporalPosicaoOptions _opcoes;

    public MotorCorrecaoTemporalPosicao(CorrecaoTemporalPosicaoOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        if (!opcoes.Valida()) throw new ArgumentException("Configuração de correção temporal inválida.", nameof(opcoes));
        _opcoes = opcoes;
    }

    public ResultadoPosicaoCorrigida Corrigir(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        DateTimeOffset instanteAvaliacaoUtc,
        IPoliticaCorrecaoPosicao politica)
    {
        ArgumentNullException.ThrowIfNull(observacao);
        ArgumentNullException.ThrowIfNull(politica);

        var idadeOriginal = (instanteAvaliacaoUtc.ToUniversalTime()
            - observacao.TimestampGps.ToUniversalTime()).TotalSeconds;
        var idade = idadeOriginal is >= 0 ? idadeOriginal : 0;
        var movimento = estado?.EstadoMovimento ?? EstadoMovimentoPosicao.Indeterminado;

        if (!PosicaoValida(observacao.PosicaoOriginal))
            return Fallback(observacao, idade, EstrategiaCorrecaoPosicao.B0, movimento,
                politica.Versao, MotivoFallbackCorrecaoPosicao.PosicaoInvalida,
                QualidadeCorrecaoPosicao.EntradaInvalida);

        if (!FinitoPositivo(observacao.ComprimentoRotaMetros))
            return Fallback(observacao, idade, EstrategiaCorrecaoPosicao.B0, movimento,
                politica.Versao, MotivoFallbackCorrecaoPosicao.ComprimentoInvalido,
                QualidadeCorrecaoPosicao.EntradaInvalida);

        if (idadeOriginal < -_opcoes.FutureTimestampToleranceSeconds)
            return Fallback(observacao, idade, EstrategiaCorrecaoPosicao.B0, movimento,
                politica.Versao, MotivoFallbackCorrecaoPosicao.TimestampFuturo,
                QualidadeCorrecaoPosicao.EntradaInvalida);

        if (idade > _opcoes.MaxProjectionAgeSeconds)
            return Fallback(observacao, idade, EstrategiaCorrecaoPosicao.B0, movimento,
                politica.Versao, MotivoFallbackCorrecaoPosicao.IdadeForaDaFaixa,
                QualidadeCorrecaoPosicao.Stale);

        var estrategia = politica.Selecionar(observacao, estado, idade, _opcoes);
        return CorrigirComEstrategia(observacao, estado, idade, estrategia, politica.Versao);
    }

    public ResultadoPosicaoCorrigida CorrigirComEstrategia(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        DateTimeOffset instanteAvaliacaoUtc,
        EstrategiaCorrecaoPosicao estrategia) =>
        CorrigirComEstrategiaValidada(observacao, estado, instanteAvaliacaoUtc,
            estrategia, estrategia.ToString());

    private ResultadoPosicaoCorrigida CorrigirComEstrategiaValidada(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        DateTimeOffset instanteAvaliacaoUtc,
        EstrategiaCorrecaoPosicao estrategia,
        string versao)
    {
        ArgumentNullException.ThrowIfNull(observacao);
        var idadeOriginal = (instanteAvaliacaoUtc.ToUniversalTime()
            - observacao.TimestampGps.ToUniversalTime()).TotalSeconds;
        var idade = idadeOriginal is >= 0 ? idadeOriginal : 0;
        var movimento = estado?.EstadoMovimento ?? EstadoMovimentoPosicao.Indeterminado;

        if (!PosicaoValida(observacao.PosicaoOriginal))
            return Fallback(observacao, idade, estrategia, movimento, versao,
                MotivoFallbackCorrecaoPosicao.PosicaoInvalida, QualidadeCorrecaoPosicao.EntradaInvalida);
        if (!FinitoPositivo(observacao.ComprimentoRotaMetros))
            return Fallback(observacao, idade, estrategia, movimento, versao,
                MotivoFallbackCorrecaoPosicao.ComprimentoInvalido, QualidadeCorrecaoPosicao.EntradaInvalida);
        if (idadeOriginal < -_opcoes.FutureTimestampToleranceSeconds)
            return Fallback(observacao, idade, estrategia, movimento, versao,
                MotivoFallbackCorrecaoPosicao.TimestampFuturo, QualidadeCorrecaoPosicao.EntradaInvalida);
        if (idade > _opcoes.MaxProjectionAgeSeconds)
            return Fallback(observacao, idade, estrategia, movimento, versao,
                MotivoFallbackCorrecaoPosicao.IdadeForaDaFaixa, QualidadeCorrecaoPosicao.Stale);

        return CorrigirComEstrategia(observacao, estado, idade, estrategia, versao);
    }

    private ResultadoPosicaoCorrigida CorrigirComEstrategia(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        double idade,
        EstrategiaCorrecaoPosicao estrategia,
        string versao)
    {
        estado = EstadoCompativelComObservacao(estado, observacao) ? estado : null;
        var movimento = estado?.EstadoMovimento ?? EstadoMovimentoPosicao.Indeterminado;
        if (estrategia == EstrategiaCorrecaoPosicao.B0)
            return Sucesso(observacao, idade, estrategia, 0, movimento, versao);

        var velocidade = SelecionarVelocidade(observacao, estado, idade, estrategia);
        if (!VelocidadeValida(velocidade))
        {
            var aquecendo = estrategia is EstrategiaCorrecaoPosicao.B2Mediana
                or EstrategiaCorrecaoPosicao.B2Recencia
                or EstrategiaCorrecaoPosicao.B3Mediana
                or EstrategiaCorrecaoPosicao.B3Conservador
                or EstrategiaCorrecaoPosicao.B3Adaptativo;
            return Fallback(observacao, idade, estrategia, movimento, versao,
                MotivoFallbackCorrecaoPosicao.VelocidadeIndisponivelOuInvalida,
                aquecendo ? QualidadeCorrecaoPosicao.Aquecendo : QualidadeCorrecaoPosicao.Fallback);
        }

        return Sucesso(observacao, idade, estrategia, velocidade!.Value, movimento, versao);
    }

    public ResultadoAtualizacaoEstadoCausal AtualizarEstado(
        EstadoCausalPosicao? anterior,
        ObservacaoPosicaoTemporal atual)
    {
        ArgumentNullException.ThrowIfNull(atual);
        var contexto = Contexto(atual);
        var amostras = anterior?.Amostras.ToList() ?? [];
        var sinais = anterior?.SinaisParada.ToList() ?? [];
        var reiniciado = anterior is null;
        MotivoDescontinuidadeCausal? motivo = anterior is null
            ? MotivoDescontinuidadeCausal.EstadoAusente : null;
        double? deslocamento = null;

        var motivoEntrada = MotivoEntradaInvalida(atual);
        if (motivoEntrada is not null)
        {
            amostras.Clear();
            sinais.Clear();
            reiniciado = true;
            motivo = motivoEntrada;
        }
        else if (anterior is not null)
        {
            motivo = ContextoIncompativel(anterior, atual);
            if (motivo is not null)
            {
                amostras.Clear();
                sinais.Clear();
                reiniciado = true;
            }
            else
            {
                var dt = (atual.TimestampGps - anterior.UltimoTimestampGps).TotalSeconds;
                if (dt > _opcoes.CausalWindowSeconds)
                {
                    amostras.Clear();
                    sinais.Clear();
                    reiniciado = true;
                    motivo = MotivoDescontinuidadeCausal.JanelaExcedida;
                }

                if (dt > 0)
                {
                    deslocamento = (atual.PosicaoOriginal - anterior.UltimaPosicao)
                        * atual.ComprimentoRotaMetros;
                }

                if (!reiniciado)
                {
                    var par = VelocidadeDoPar(anterior, atual, dt);
                    if (par.Velocidade is { } velocidade)
                        amostras.Add(new(atual.TimestampGps, velocidade));
                    else if (par.Motivo is not null)
                        motivo = par.Motivo;
                }
            }
        }

        var limite = atual.TimestampGps.AddSeconds(-_opcoes.CausalWindowSeconds);
        amostras.RemoveAll(a => a.TimestampGps < limite);
        if (amostras.Count > _opcoes.MaxCausalSamples)
            amostras.RemoveRange(0, amostras.Count - _opcoes.MaxCausalSamples);

        var sinalParado = VelocidadeValida(atual.VelocidadeInstantaneaKmh)
            && atual.VelocidadeInstantaneaKmh < _opcoes.StopSpeedKmh
            && deslocamento is >= 0
            && deslocamento <= _opcoes.StopDisplacementMeters;
        sinais.Add(sinalParado);
        if (sinais.Count > _opcoes.StopConfirmationObservations)
            sinais.RemoveRange(0, sinais.Count - _opcoes.StopConfirmationObservations);

        var movimento = sinais.Count == _opcoes.StopConfirmationObservations && sinais.All(x => x)
            ? EstadoMovimentoPosicao.Parado
            : VelocidadeValida(atual.VelocidadeInstantaneaKmh)
                && atual.VelocidadeInstantaneaKmh >= _opcoes.StopSpeedKmh
                || deslocamento > _opcoes.StopDisplacementMeters
                    ? EstadoMovimentoPosicao.Movimento
                    : EstadoMovimentoPosicao.Indeterminado;

        var estado = new EstadoCausalPosicao(contexto, atual.TimestampGps,
            atual.PosicaoOriginal, atual.ComprimentoRotaMetros,
            amostras.ToArray(), sinais.ToArray(), movimento);
        return new(estado, reiniciado, motivo);
    }

    private (double? Velocidade, MotivoDescontinuidadeCausal? Motivo) VelocidadeDoPar(
        EstadoCausalPosicao anterior, ObservacaoPosicaoTemporal atual, double dt)
    {
        if (dt <= 0) return (null, MotivoDescontinuidadeCausal.TempoNaoPositivo);
        var delta = (atual.PosicaoOriginal - anterior.UltimaPosicao) * anterior.ComprimentoRotaMetros;
        if (!double.IsFinite(delta) || delta < 0)
            return (null, MotivoDescontinuidadeCausal.RegressaoPosicao);
        var velocidade = delta / dt * 3.6;
        return VelocidadeValida(velocidade)
            ? (velocidade, null)
            : (null, MotivoDescontinuidadeCausal.VelocidadeCausalInvalida);
    }

    private MotivoDescontinuidadeCausal? ContextoIncompativel(
        EstadoCausalPosicao anterior, ObservacaoPosicaoTemporal atual)
    {
        var a = anterior.Contexto;
        if (!string.Equals(a.Modal, atual.Modal, StringComparison.Ordinal)
            || !string.Equals(a.Provedor, atual.Provedor, StringComparison.Ordinal)
            || !string.Equals(a.Ordem, atual.Ordem, StringComparison.Ordinal))
            return MotivoDescontinuidadeCausal.IdentidadeDiferente;
        if (!string.Equals(a.CodigoLinha, atual.CodigoLinha, StringComparison.Ordinal))
            return MotivoDescontinuidadeCausal.LinhaDiferente;
        if (a.PadraoVersaoId == Guid.Empty || a.PadraoVersaoId != atual.PadraoVersaoId)
            return MotivoDescontinuidadeCausal.PadraoVersaoDiferente;
        if ((a.SentidoId.HasValue || atual.SentidoId.HasValue) && a.SentidoId != atual.SentidoId)
            return MotivoDescontinuidadeCausal.SentidoDiferente;
        if ((a.ViagemId.HasValue || atual.ViagemId.HasValue) && a.ViagemId != atual.ViagemId)
            return MotivoDescontinuidadeCausal.ViagemDiferente;
        return ComprimentosCompativeis(anterior.ComprimentoRotaMetros, atual.ComprimentoRotaMetros)
            ? null : MotivoDescontinuidadeCausal.ComprimentoIncompativel;
    }

    private double? SelecionarVelocidade(ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado, double idade, EstrategiaCorrecaoPosicao estrategia)
    {
        var causaisValidas = estado?.Amostras.Select(a => a.VelocidadeKmh)
            .Where(v => VelocidadeValida(v)).ToArray();
        var mediana = Mediana(causaisValidas);
        var recencia = MediaPonderadaRecencia(causaisValidas);
        var instantanea = observacao.VelocidadeInstantaneaKmh;
        var hibridas = new[] { instantanea, mediana }.Where(VelocidadeValida).Select(x => x!.Value).ToArray();
        var hibridaMediana = Mediana(hibridas);
        double? hibridaConservadora = hibridas.Length == 0 ? null : hibridas.Min();

        double? velocidade = estrategia switch
        {
            EstrategiaCorrecaoPosicao.B1 => instantanea,
            EstrategiaCorrecaoPosicao.B2Mediana => mediana,
            EstrategiaCorrecaoPosicao.B2Recencia => recencia,
            EstrategiaCorrecaoPosicao.B2Legacy => observacao.VelocidadeMediaLegacyKmh,
            EstrategiaCorrecaoPosicao.B3Mediana => hibridaMediana,
            EstrategiaCorrecaoPosicao.B3Conservador => hibridaConservadora,
            EstrategiaCorrecaoPosicao.B3Adaptativo => idade <= _opcoes.AdaptiveB1LimitSeconds
                ? instantanea : hibridaMediana,
            _ => null,
        };

        return estrategia is EstrategiaCorrecaoPosicao.B3Mediana
                or EstrategiaCorrecaoPosicao.B3Conservador
                or EstrategiaCorrecaoPosicao.B3Adaptativo
            && estado?.EstadoMovimento == EstadoMovimentoPosicao.Parado
                ? 0 : velocidade;
    }

    private ResultadoPosicaoCorrigida Sucesso(ObservacaoPosicaoTemporal observacao, double idade,
        EstrategiaCorrecaoPosicao estrategia, double velocidade, EstadoMovimentoPosicao movimento,
        string versao)
    {
        var metros = velocidade / 3.6 * idade;
        var semClamp = observacao.PosicaoOriginal + metros / observacao.ComprimentoRotaMetros;
        var corrigida = Math.Min(1, Math.Max(observacao.PosicaoOriginal, semClamp));
        var clampada = semClamp > 1;
        return new(observacao.PosicaoOriginal, corrigida,
            (corrigida - observacao.PosicaoOriginal) * observacao.ComprimentoRotaMetros,
            idade, estrategia, velocidade, movimento, QualidadeCorrecaoPosicao.Disponivel,
            null, corrigida > observacao.PosicaoOriginal, clampada, versao);
    }

    private static ResultadoPosicaoCorrigida Fallback(ObservacaoPosicaoTemporal observacao,
        double idade, EstrategiaCorrecaoPosicao estrategia, EstadoMovimentoPosicao movimento,
        string versao, MotivoFallbackCorrecaoPosicao motivo, QualidadeCorrecaoPosicao qualidade)
    {
        double? original = PosicaoValida(observacao.PosicaoOriginal) ? observacao.PosicaoOriginal : null;
        return new(original, original, original.HasValue ? 0d : null, idade, estrategia, null,
            movimento, qualidade, motivo, false, false, versao);
    }

    private static ContextoCausalPosicao Contexto(ObservacaoPosicaoTemporal observacao) => new(
        observacao.Ordem, observacao.Modal, observacao.Provedor, observacao.CodigoLinha,
        observacao.PadraoVersaoId, observacao.SentidoId, observacao.ViagemId,
        observacao.OcorrenciaParadaPadraoId,
        observacao.LinhaId, observacao.Volta);

    private bool EstadoCompativelComObservacao(
        EstadoCausalPosicao? estado, ObservacaoPosicaoTemporal observacao)
    {
        if (estado is null || estado.Versao != 1
            || estado.UltimoTimestampGps != observacao.TimestampGps
            || estado.UltimaPosicao != observacao.PosicaoOriginal)
            return false;
        return ContextoIncompativel(estado, observacao) is null;
    }

    private static MotivoDescontinuidadeCausal? MotivoEntradaInvalida(
        ObservacaoPosicaoTemporal observacao)
    {
        if (!string.Equals(observacao.OrigemPosicao, TelemetriaMlContrato.OrigemReal,
                StringComparison.OrdinalIgnoreCase))
            return MotivoDescontinuidadeCausal.OrigemNaoReal;
        if (observacao.PadraoVersaoId == Guid.Empty)
            return MotivoDescontinuidadeCausal.MatchingAusente;
        if (!PosicaoValida(observacao.PosicaoOriginal))
            return MotivoDescontinuidadeCausal.PosicaoInvalida;
        if (!FinitoPositivo(observacao.ComprimentoRotaMetros))
            return MotivoDescontinuidadeCausal.ComprimentoInvalido;
        return null;
    }

    private bool ComprimentosCompativeis(double a, double b) =>
        FinitoPositivo(a) && FinitoPositivo(b)
        && Math.Abs(a - b) <= Math.Max(_opcoes.RouteLengthRelativeTolerance
            * Math.Max(Math.Abs(a), Math.Abs(b)), _opcoes.RouteLengthAbsoluteTolerance);

    private bool VelocidadeValida(double? valor) => valor.HasValue
        && double.IsFinite(valor.Value) && valor.Value is >= 0 && valor.Value <= _opcoes.MaxSpeedKmh;

    private static bool PosicaoValida(double valor) => double.IsFinite(valor) && valor is >= 0 and <= 1;
    private static bool FinitoPositivo(double valor) => double.IsFinite(valor) && valor > 0;

    private static double? Mediana(IEnumerable<double>? valores)
    {
        if (valores is null) return null;
        var ordenados = valores.Order().ToArray();
        if (ordenados.Length == 0) return null;
        var meio = ordenados.Length / 2;
        return ordenados.Length % 2 == 1
            ? ordenados[meio]
            : (ordenados[meio - 1] + ordenados[meio]) / 2;
    }

    private static double? MediaPonderadaRecencia(IEnumerable<double>? valores)
    {
        if (valores is null) return null;
        var array = valores.ToArray();
        if (array.Length == 0) return null;
        double soma = 0;
        long pesos = 0;
        for (var i = 0; i < array.Length; i++)
        {
            var peso = i + 1;
            soma += array[i] * peso;
            pesos += peso;
        }
        return soma / pesos;
    }
}
