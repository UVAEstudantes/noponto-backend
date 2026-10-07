namespace NoPonto.Application.GPS;

internal enum ContinuidadeCircular { Confiavel, Ambigua }

internal sealed record AncoraCircular(double Posicao, double Latitude, double Longitude,
    DateTimeOffset Timestamp, double Comprimento, double Bearing);

internal sealed record IntegridadeCircular(Guid ViagemId, Guid LinhaId, Guid SentidoId,
    Guid PadraoOperacionalId, Guid PadraoVersaoId, int VoltaConfirmada,
    ContinuidadeCircular Continuidade, AncoraCircular? Ancora,
    DateTimeOffset? PerdaEm = null, string? Motivo = null, CandidatoViagem? Recuperacao = null,
    int Contrato = 1)
{
    internal void Validar(ViagemOperacionalState estado)
    {
        var o = estado.Observada;
        if (Contrato != 1 || ViagemId != o.ViagemId || LinhaId != estado.LinhaId
            || SentidoId != estado.SentidoId || PadraoOperacionalId != o.PadraoOperacionalId
            || PadraoVersaoId != o.PadraoVersaoId || VoltaConfirmada != o.Volta
            || o.Topologia != "CIRCULAR" || !Enum.IsDefined(Continuidade)
            || (Continuidade == ContinuidadeCircular.Ambigua) != PerdaEm.HasValue
            || (Continuidade == ContinuidadeCircular.Ambigua) != !string.IsNullOrWhiteSpace(Motivo)
            || PerdaEm > o.TimestampUltimaAtualizacao || PerdaEm < o.TimestampObservacaoInicial)
            throw new FormatException("Integridade circular incompatível com a viagem.");
        if (Ancora is { } a && (!double.IsFinite(a.Posicao) || a.Posicao is < 0 or > 1
            || !GpsLeituraValidator.CoordenadaValida(a.Latitude, a.Longitude)
            || !double.IsFinite(a.Comprimento) || a.Comprimento <= 0
            || !double.IsFinite(a.Bearing) || a.Bearing is < 0 or >= 360
            || a.Timestamp < o.TimestampObservacaoInicial || a.Timestamp > o.TimestampUltimaAtualizacao))
            throw new FormatException("Âncora circular inválida.");
        if (Recuperacao is { } c && (Continuidade != ContinuidadeCircular.Ambigua
            || c.PadraoVersaoId == Guid.Empty || c.SentidoId == Guid.Empty || c.LinhaId == Guid.Empty
            || c.Timestamp < PerdaEm || c.Timestamp > o.TimestampUltimaAtualizacao
            || !double.IsFinite(c.Posicao) || c.Posicao is < 0 or > 1
            || c.LatitudeInicial is not { } lat || c.LongitudeInicial is not { } lon
            || !GpsLeituraValidator.CoordenadaValida(lat, lon)))
            throw new FormatException("Evidência de recuperação inválida.");
    }
}

/// <summary>Evidência relacional efêmera, calculada da geometria imutável no PostgreSQL.</summary>
public sealed record ProvaGeometricaCircular(bool GeometriaSimplesFechada, bool PontosEDirecaoCompativeis,
    double DistanciaDirigida, double DistanciaContraria, double Comprimento);

public static partial class ViagemOperacionalRegra
{
    internal static bool IdentidadeConfiavel(ViagemOperacionalState estado) =>
        estado.Integridade?.Continuidade != ContinuidadeCircular.Ambigua;

    private static bool MatchingConfiavel(EstruturaViagem e, PosicaoVeiculoDto gps, GpsPollingOptions opcoes) =>
        gps.MatchingOperacionalPlausivel && e.SentidoInequivoco
        && GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
        && gps.LinhaId == e.LinhaId && gps.SentidoId == e.SentidoId
        && gps.PadraoOperacionalId == e.PadraoOperacionalId && gps.PadraoVersaoId == e.PadraoVersaoId
        && gps.TopologiaPadrao == e.Topologia
        && gps.Bearing is { } b && double.IsFinite(b) && b is >= 0 and < 360
        && gps.Velocidade >= opcoes.VelocidadeMinimaBearingKmh
        && gps.ComprimentoRotaMetros is { } length && double.IsFinite(length) && length > 0;

    private static IntegridadeCircular NovoRegistro(ViagemOperacionalState s, AncoraCircular? a) =>
        new(s.Observada.ViagemId, s.LinhaId, s.SentidoId, s.Observada.PadraoOperacionalId,
            s.Observada.PadraoVersaoId, s.Observada.Volta, ContinuidadeCircular.Confiavel, a);

    private static ViagemOperacionalState AtualizarAncoraCircular(ViagemOperacionalState s,
        EstruturaViagem e, PosicaoVeiculoDto gps, GpsPollingOptions? opcoes = null)
    {
        if (s.Observada.Topologia != "CIRCULAR") return s with { Integridade = null };
        if (!MatchingConfiavel(e, gps, opcoes ?? new()) || s.Candidato is not null)
            return s.Integridade is { } i && i.VoltaConfirmada != s.Observada.Volta
                ? s with { Integridade = NovoRegistro(s, null) } : s;
        return s with { Integridade = NovoRegistro(s, new(gps.PosicaoNaRota!.Value,
            gps.Latitude, gps.Longitude, gps.TimestampGps, gps.ComprimentoRotaMetros!.Value, gps.Bearing!.Value)) };
    }

    internal static bool WrapComprovado(ViagemOperacionalState s, EstruturaViagem e,
        PosicaoVeiculoDto gps, ProvaGeometricaCircular? prova, GpsPollingOptions opcoes)
    {
        if (s.Integridade?.Ancora is not { } a || prova is not { GeometriaSimplesFechada: true, PontosEDirecaoCompativeis: true }
            || e.PadraoVersaoId != s.Observada.PadraoVersaoId || e.PadraoOperacionalId != s.Observada.PadraoOperacionalId
            || e.LinhaId != s.LinhaId || e.SentidoId != s.SentidoId || e.Topologia != "CIRCULAR"
            || !MatchingConfiavel(e, gps, opcoes) || gps.PosicaoNaRota >= a.Posicao
            || gps.ComprimentoRotaMetros != a.Comprimento || gps.TimestampGps <= a.Timestamp
            || !double.IsFinite(prova.DistanciaDirigida) || !double.IsFinite(prova.DistanciaContraria)
            || !double.IsFinite(prova.Comprimento) || prova.Comprimento <= 0)
            return false;
        var segundos = (gps.TimestampGps - a.Timestamp).TotalSeconds;
        var budget = GpsEnriquecimentoService.OrcamentoProjecaoMetros(segundos, opcoes);
        // Caminho dirigido alcançável; regressão inversa e uma segunda volta inalcançáveis.
        // Movimento mínimo e margem de projeção são os parâmetros já usados no pipeline.
        return segundos <= JanelaCandidatoSegundos
            && prova.DistanciaDirigida >= MovimentoMinimoMetros
            && prova.DistanciaContraria > budget
            && prova.DistanciaDirigida <= budget
            && prova.DistanciaDirigida + prova.Comprimento > budget
            && HaversineMetros(a.Latitude, a.Longitude, gps.Latitude, gps.Longitude) >= MovimentoMinimoMetros
            && !GpsEnriquecimentoService.EhSaltoImplausivel(a.Latitude, a.Longitude,
                gps.Latitude, gps.Longitude, segundos, opcoes.VelocidadeMaximaKmh);
    }

    private static DecisaoViagem? DecidirIntegridadeCircular(ViagemOperacionalState s, EstruturaViagem e,
        PosicaoVeiculoDto gps, TransicaoParadas baseline, Guid novaId, bool adocao,
        ProvaGeometricaCircular? prova, GpsPollingOptions opcoes)
    {
        if (s.Observada.Topologia != "CIRCULAR" || s.Estado == EstadoViagem.Finalizada) return null;
        var ambigua = !IdentidadeConfiavel(s);
        var cancelando = s.Candidato is not null && e.PadraoOperacionalId == s.Observada.PadraoOperacionalId
            && e.SentidoId == s.SentidoId && e.LinhaId == s.LinhaId;
        if (!ambigua && !cancelando) return null;
        if (baseline.Ultrapassadas.Count != 0 && ambigua)
            throw new InvalidOperationException("Integridade ambígua exige baseline sem passagens.");
        if (WrapComprovado(s, e, gps, prova, opcoes))
        {
            var volta = checked(s.Observada.Volta + 1);
            var recuperada = s with { Candidato = null, Estado = EstadoViagem.Ativa, ConfirmacoesPosTerminal = 0,
                Observada = s.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps,
                    PosicaoNaRotaConfirmada = gps.PosicaoNaRota!.Value,
                    UltimaOcorrenciaParadaPadraoId = baseline.UltimaId, UltimaParadaOrdem = baseline.UltimaOrdem,
                    OcorrenciaCursorId = baseline.UltimaId, OrdemCursor = baseline.UltimaOrdem == 0 ? null : baseline.UltimaOrdem,
                    Volta = volta, ProgressoAbsolutoMetros = (volta + gps.PosicaoNaRota.Value) * gps.ComprimentoRotaMetros!.Value },
                Integridade = null };
            return new(AtualizarAncoraCircular(recuperada, e, gps, opcoes), []);
        }
        if (!ambigua)
        {
            var a = s.Integridade?.Ancora;
            // Sem âncora, a idade INTEIRA da execução é um limite superior conservador;
            // não fabricar timestamp da última posição a partir do candidato.
            var length = a?.Comprimento ?? gps.ComprimentoRotaMetros ?? 0;
            var budget = GpsEnriquecimentoService.OrcamentoProjecaoMetros(
                (gps.TimestampGps - (a?.Timestamp ?? s.Observada.TimestampObservacaoInicial)).TotalSeconds, opcoes);
            var posicaoAncora = a?.Posicao ?? s.Observada.PosicaoNaRotaConfirmada;
            var delta = gps.PosicaoNaRota!.Value - posicaoAncora;
            var dirigido = (delta < 0 ? 1 + delta : delta) * length;
            // Nunca reabrir ocorrência já incorporada; posição sozinha não prova wrap.
            var risco = baseline.UltimaOrdem < s.Observada.UltimaParadaOrdem
                || e.PadraoVersaoId != s.Observada.PadraoVersaoId
                || length <= 0 || (a is not null && gps.ComprimentoRotaMetros != a.Comprimento)
                || (delta < 0 && dirigido <= budget)
                || budget >= length + dirigido;
            if (!risco) return null;
            var i = (s.Integridade ?? NovoRegistro(s, null)) with {
                Continuidade = ContinuidadeCircular.Ambigua, PerdaEm = gps.TimestampGps,
                Motivo = "ContagemCircularNaoComprovada", Recuperacao = null };
            return new(s with { Integridade = i, Candidato = null,
                Observada = s.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps } }, []);
        }
        var integridade = s.Integridade!;
        var confiavel = MatchingConfiavel(e, gps, opcoes);
        var c = integridade.Recuperacao;
        if (confiavel && c is not null && c.PadraoVersaoId == e.PadraoVersaoId
            && c.LinhaId == e.LinhaId && c.SentidoId == e.SentidoId
            && gps.TimestampGps > c.Timestamp
            && (gps.TimestampGps - c.Timestamp).TotalSeconds <= JanelaCandidatoSegundos
            && EvidenciaInicioSuficiente(c, gps)
            && !GpsEnriquecimentoService.EhSaltoImplausivel(c.LatitudeInicial!.Value, c.LongitudeInicial!.Value,
                gps.Latitude, gps.Longitude, (gps.TimestampGps - c.Timestamp).TotalSeconds, opcoes.VelocidadeMaximaKmh)
            && (gps.PosicaoNaRota!.Value - c.Posicao) * gps.ComprimentoRotaMetros!.Value
                <= GpsEnriquecimentoService.OrcamentoProjecaoMetros((gps.TimestampGps - c.Timestamp).TotalSeconds, opcoes))
        {
            var inicio = Decidir(null, e, gps, baseline with { Volta = 0, HouveWrap = false }, novaId, opcoes: opcoes);
            var fim = Evento(s, "ViagemFinalizada", gps.TimestampGps) with {
                OcorrenciaParadaPadraoId = null, MotivoFim = "PerdaContinuidadeCircular" };
            return new(inicio.Estado, new[] { fim }.Concat(inicio.Eventos).ToArray());
        }
        var recuperar = confiavel ? new CandidatoViagem(e.PadraoVersaoId, e.SentidoId, e.LinhaId,
            gps.TimestampGps, gps.PosicaoNaRota!.Value, gps.Latitude, gps.Longitude) : null;
        // Preservar primeira evidência consistente, sem prolongar uma janela vencida.
        if (confiavel && c is not null && c.PadraoVersaoId == e.PadraoVersaoId && c.LinhaId == e.LinhaId
            && c.SentidoId == e.SentidoId && (gps.TimestampGps - c.Timestamp).TotalSeconds <= JanelaCandidatoSegundos)
            recuperar = c;
        return new(s with { Integridade = integridade with { Recuperacao = recuperar }, Candidato = null,
            Observada = s.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps } }, []);
    }
}
