namespace NoPonto.Application.GPS;

internal enum StatusMudancaOperacional
{
    Continuidade, PrimeiraEvidencia, CandidatoMantido, CandidatoSubstituido,
    CandidatoCancelado, MudancaConfirmada, EvidenciaRejeitadaOuInsuficiente
}

internal sealed record AvaliacaoMudancaOperacional(StatusMudancaOperacional Status,
    CandidatoViagem? Candidato, string Motivo, EstruturaViagem? EstruturaConfirmada = null);

public static partial class ViagemOperacionalRegra
{
    internal static AvaliacaoMudancaOperacional? AvaliarMudancaSeHabilitada(GpsPollingOptions opcoes,
        ViagemOperacionalState? anterior, EstruturaViagem? estrutura, PosicaoVeiculoDto gps) =>
        !opcoes.MudancaOperacionalHabilitada || anterior?.Estado is not (EstadoViagem.Ativa or EstadoViagem.PossivelFim)
            ? null : AvaliarMudancaOperacional(anterior, estrutura, gps,
                estrutura is null ? StatusBuscaPadrao.NotEligible : StatusBuscaPadrao.Found,
                gps.MatchingOperacionalPlausivel, opcoes);

    internal static DecisaoViagem? AplicarAvaliacaoMudanca(ViagemOperacionalState anterior,
        AvaliacaoMudancaOperacional avaliacao, PosicaoVeiculoDto gps,
        TransicaoParadas? baseline = null, Guid novaId = default)
    {
        if (avaliacao.Status == StatusMudancaOperacional.MudancaConfirmada)
        {
            if (baseline is null || baseline.Status != ViagemObservadaStatus.Updated
                || baseline.Ultrapassadas.Count != 0 || baseline.Volta != 0
                || avaliacao.EstruturaConfirmada is null || novaId == Guid.Empty)
                throw new InvalidOperationException("Confirmação exige baseline novo válido.");
            var inicio = Decidir(null, avaliacao.EstruturaConfirmada, gps, baseline, novaId);
            // Fim lógico no instante da confirmação; não afirmar passagem/chegada a uma parada.
            var fim = Evento(anterior, "ViagemFinalizada", gps.TimestampGps) with { OcorrenciaParadaPadraoId = null };
            return new(inicio.Estado, new[] { fim }.Concat(inicio.Eventos).ToArray());
        }
        if (avaliacao.Status is StatusMudancaOperacional.PrimeiraEvidencia
            or StatusMudancaOperacional.CandidatoMantido or StatusMudancaOperacional.CandidatoSubstituido)
            return new(anterior with { Candidato = avaliacao.Candidato,
                Observada = anterior.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps } }, []);
        return null; // Continuidade normal ou rejeição sem mudança: fluxo anterior.
    }

    /// <summary>
    /// Avaliação pura usada pelo fluxo protegido pela flag. matchingPlausivel deve refletir
    /// a validação espacial/temporal do matching observacional, e não a projeção no padrão antigo.
    /// O chamador controla timestamps/candidato sob CAS; nenhum estado/evento é escrito aqui.
    /// </summary>
    internal static AvaliacaoMudancaOperacional AvaliarMudancaOperacional(
        ViagemOperacionalState anterior, EstruturaViagem? estrutura, PosicaoVeiculoDto gps,
        StatusBuscaPadrao statusMatching, bool matchingPlausivel, GpsPollingOptions opcoes)
    {
        var obs = anterior.Observada;
        var candidato = anterior.Candidato;
        AvaliacaoMudancaOperacional Rejeitar(string motivo) =>
            new(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, candidato, motivo);

        if (anterior.Estado is not (EstadoViagem.Ativa or EstadoViagem.PossivelFim))
            return Rejeitar("Fase não elegível.");
        if (gps.TimestampGps <= obs.TimestampUltimaAtualizacao
            || (candidato is not null && gps.TimestampGps <= candidato.Timestamp))
            return Rejeitar("Observação duplicada ou antiga.");
        if (statusMatching != StatusBuscaPadrao.Found || !matchingPlausivel || estrutura is null)
            return Rejeitar("Matching ausente, indisponível ou implausível.");
        if (gps.Ordem != obs.OrdemVeiculo || obs.ViagemId == Guid.Empty
            || anterior.LinhaId == Guid.Empty || anterior.SentidoId == Guid.Empty
            || obs.PadraoOperacionalId == Guid.Empty || obs.PadraoVersaoId == Guid.Empty
            || estrutura.LinhaId == Guid.Empty || estrutura.SentidoId == Guid.Empty
            || estrutura.PadraoOperacionalId == Guid.Empty || estrutura.PadraoVersaoId == Guid.Empty
            || string.IsNullOrWhiteSpace(estrutura.CodigoLinha)
            || gps.CodigoLinha != estrutura.CodigoLinha || gps.LinhaId != estrutura.LinhaId
            || gps.SentidoId != estrutura.SentidoId || gps.PadraoOperacionalId != estrutura.PadraoOperacionalId
            || gps.PadraoVersaoId != estrutura.PadraoVersaoId || gps.TopologiaPadrao != estrutura.Topologia
            || estrutura.Topologia is not ("LINEAR" or "CIRCULAR")
            || !GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
            || gps.PosicaoNaRota is not { } p || !double.IsFinite(p) || p is < 0 or > 1
            || gps.ComprimentoRotaMetros is not { } comprimento || !double.IsFinite(comprimento) || comprimento <= 0
            || !double.IsFinite(opcoes.VelocidadeMaximaKmh) || opcoes.VelocidadeMaximaKmh <= 0
            || !double.IsFinite(opcoes.ToleranciaProjecaoMetros) || opcoes.ToleranciaProjecaoMetros < 0)
            return Rejeitar("Identidade, posição ou parâmetros inválidos.");

        var mesmaOperacao = estrutura.LinhaId == anterior.LinhaId && estrutura.SentidoId == anterior.SentidoId
            && estrutura.PadraoOperacionalId == obs.PadraoOperacionalId && estrutura.Topologia == obs.Topologia;
        if (mesmaOperacao)
            return new(candidato is null ? StatusMudancaOperacional.Continuidade : StatusMudancaOperacional.CandidatoCancelado,
                null, "Operação original; versão geométrica não define outra execução.");
        if (!estrutura.SentidoInequivoco)
            return new(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, null, "Sentido ambíguo; evidência candidata invalidada.");
        if (estrutura.LinhaId == anterior.LinhaId && estrutura.SentidoId == anterior.SentidoId)
            return new(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, null,
                "Diferença apenas de padrão/topologia: política de nova execução não definida.");

        CandidatoViagem Novo() => new(estrutura.PadraoVersaoId, estrutura.SentidoId, estrutura.LinhaId,
            gps.TimestampGps, p, gps.Latitude, gps.Longitude);
        if (candidato is null)
            return new(StatusMudancaOperacional.PrimeiraEvidencia, Novo(), "Primeira observação divergente elegível.");
        var identidadeConsistente = candidato.PadraoVersaoId == estrutura.PadraoVersaoId
            && candidato.SentidoId == estrutura.SentidoId && candidato.LinhaId == estrutura.LinhaId;
        if (!identidadeConsistente || (gps.TimestampGps - candidato.Timestamp).TotalSeconds > JanelaCandidatoSegundos)
            return new(StatusMudancaOperacional.CandidatoSubstituido, Novo(),
                identidadeConsistente ? "Candidato vencido; evidência reiniciada." : "Identidade candidata mudou.");
        if (candidato.Timestamp < obs.TimestampObservacaoInicial
            || candidato.LatitudeInicial is not { } lat || candidato.LongitudeInicial is not { } lon
            || !GpsLeituraValidator.CoordenadaValida(lat, lon)
            || !double.IsFinite(candidato.Posicao) || candidato.Posicao is < 0 or > 1)
            return new(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, null, "Candidato anterior inválido.");

        var segundos = (gps.TimestampGps - candidato.Timestamp).TotalSeconds;
        // Frações de versões diferentes nunca são comparadas. Regressão circular não confirma troca.
        var progresso = (p - candidato.Posicao) * comprimento;
        if (GpsEnriquecimentoService.EhSaltoImplausivel(lat, lon, gps.Latitude, gps.Longitude,
                segundos, opcoes.VelocidadeMaximaKmh)
            || Math.Abs(progresso) > GpsEnriquecimentoService.OrcamentoProjecaoMetros(segundos, opcoes))
            return new(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, null, "Salto entre evidências implausível.");
        if (!EvidenciaInicioSuficiente(candidato, gps))
            return new(StatusMudancaOperacional.CandidatoMantido, candidato, "Movimento/progresso ainda insuficiente.");
        return new(StatusMudancaOperacional.MudancaConfirmada, candidato,
            "Duas evidências consistentes com movimento e progresso suficientes.", estrutura);
    }
}
