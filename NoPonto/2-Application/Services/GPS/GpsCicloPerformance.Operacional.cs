using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

// Índices fixos: não são labels vindas de GPS, IDs ou configuração.
internal enum ContadorFunilOperacional
{
    Decisoes, Normal, BaselineNova, BaselineAposFinalizada, BaselineMudanca,
    AdocaoCandidato, CandidatoEspera, ProtecaoCircular, Recuperacao,
    EsperaFinalizada, IdentidadeRetida,
    EstadoAnteriorAtiva, EstadoAnteriorPossivelFim, EstadoAnteriorFinalizada,
    NormalTransicoes, NormalAvaliada, ProjecaoMaior, ProjecaoIgual, ProjecaoMenor,
    ComparacaoIncompativel, CursorAvancou, CursorPermaneceu, CursorIncompativel,
    ComAlvo, SemAlvo, SemAlvoAtiva, SemAlvoPossivelFim, ZeroCruzamentos, ComCruzamentos, OcorrenciasSelecionadas,
    OcorrenciasSuprimidasMensuraveis,
    SelecionadoPassagem, SelecionadoInicio, SelecionadoFim, SelecionadoOutro,
    CommitPassagem, CommitInicio, CommitFim, CommitOutro, CommitEventosForaEscopoLegado,
    RetryClaim, RetryNaoProcessado, ProjecaoPinadaAdotada,
    Quantidade
}

internal enum ResultadoFunilOperacional
{
    Created, Updated, ItineraryChanged, RejectedOlderOrEqual, InvalidState,
    InfrastructureFailure, Conflict, InvalidSequence, OccurrenceNotFromItinerary,
    NuloSemMatching, NaoProcessadoPendencia, Outro, Quantidade
}

internal sealed partial class GpsCicloPerformance
{
    private const int Origens = 2;
    private const int Modais = 3; // BUS, BRT, outro/ausente.
    private const int CamposPredecessor = 9;
    private readonly long[] _funil = new long[Origens * (int)ContadorFunilOperacional.Quantidade];
    private readonly long[] _resultadosOperacionais = new long[Origens * (int)ResultadoFunilOperacional.Quantidade];
    private readonly long[] _predecessores = new long[Origens * Modais * CamposPredecessor];
    private static readonly string[] NomesFunil = Enum.GetNames<ContadorFunilOperacional>();
    private static readonly string[] NomesResultado = Enum.GetNames<ResultadoFunilOperacional>();
    private static readonly string[] NomesOrigem = ["nova", "retry"];
    private static readonly string[] NomesModal = ["bus", "brt", "outro"];
    private static readonly string[] NomesPredecessor = ["avaliados", "valido", "ausente_invalido",
        "idade_ate40s", "idade_40a60s", "idade_60a180s", "idade_gt180s", "plausivel", "nao_plausivel"];

    internal long Funil(ContadorFunilOperacional contador, bool retry = false) =>
        Volatile.Read(ref _funil[(retry ? 1 : 0) * (int)ContadorFunilOperacional.Quantidade + (int)contador]);
    internal long ResultadoOperacional(ResultadoFunilOperacional resultado, bool retry = false) =>
        Volatile.Read(ref _resultadosOperacionais[(retry ? 1 : 0) * (int)ResultadoFunilOperacional.Quantidade + (int)resultado]);
    internal long Predecessor(int modal, int campo, bool retry = false) =>
        Volatile.Read(ref _predecessores[((retry ? 1 : 0) * Modais + modal) * CamposPredecessor + campo]);

    internal void RegistrarFunil(ContadorFunilOperacional contador, long quantidade = 1) =>
        Interlocked.Add(ref _funil[(GpsCommitPerformanceContext.IsRetry ? 1 : 0)
            * (int)ContadorFunilOperacional.Quantidade + (int)contador], quantidade);

    internal void RegistrarResultadoOperacional(ViagemObservadaResultado? resultado,
        ResultadoFunilOperacional nulo = ResultadoFunilOperacional.NuloSemMatching)
    {
        var categoria = resultado?.Status switch
        {
            null => nulo,
            ViagemObservadaStatus.Created => ResultadoFunilOperacional.Created,
            ViagemObservadaStatus.Updated => ResultadoFunilOperacional.Updated,
            ViagemObservadaStatus.ItineraryChanged => ResultadoFunilOperacional.ItineraryChanged,
            ViagemObservadaStatus.RejectedOlderOrEqual => ResultadoFunilOperacional.RejectedOlderOrEqual,
            ViagemObservadaStatus.InvalidState => ResultadoFunilOperacional.InvalidState,
            ViagemObservadaStatus.InfrastructureFailure => ResultadoFunilOperacional.InfrastructureFailure,
            ViagemObservadaStatus.Conflict => ResultadoFunilOperacional.Conflict,
            ViagemObservadaStatus.InvalidSequence => ResultadoFunilOperacional.InvalidSequence,
            ViagemObservadaStatus.OccurrenceNotFromItinerary => ResultadoFunilOperacional.OccurrenceNotFromItinerary,
            _ => ResultadoFunilOperacional.Outro,
        };
        Interlocked.Increment(ref _resultadosOperacionais[(GpsCommitPerformanceContext.IsRetry ? 1 : 0)
            * (int)ResultadoFunilOperacional.Quantidade + (int)categoria]);
    }

    internal void RegistrarPredecessor(PosicaoVeiculoDto gps)
    {
        var modal = gps.ModalFonte is "BUS" or "ONIBUS" ? 0 : gps.ModalFonte == "BRT" ? 1 : 2;
        var offset = ((GpsCommitPerformanceContext.IsRetry ? 1 : 0) * Modais + modal) * CamposPredecessor;
        Interlocked.Increment(ref _predecessores[offset]);
        var valido = gps.TemHistorico && gps.TimestampAnterior < gps.TimestampGps
            && GpsLeituraValidator.CoordenadaValida(gps.LatitudeAnterior!.Value, gps.LongitudeAnterior!.Value)
            && GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude);
        Interlocked.Increment(ref _predecessores[offset + (valido ? 1 : 2)]);
        if (valido)
        {
            var idade = (gps.TimestampGps - gps.TimestampAnterior!.Value).TotalSeconds;
            Interlocked.Increment(ref _predecessores[offset + (idade <= 40 ? 3 : idade <= 60 ? 4 : idade <= 180 ? 5 : 6)]);
        }
        Interlocked.Increment(ref _predecessores[offset + (gps.MatchingOperacionalPlausivel ? 7 : 8)]);
    }

    internal void RegistrarEventosSelecionados(IReadOnlyList<EventoViagem> eventos) => RegistrarEventos(eventos, false);
    internal void RegistrarEventosCommit(IReadOnlyList<EventoViagem> eventos)
    {
        RegistrarEventos(eventos, true);
        if (!ReferenceEquals(GpsCommitPerformanceContext.Current, this))
            RegistrarFunil(ContadorFunilOperacional.CommitEventosForaEscopoLegado, eventos.Count);
    }
    private void RegistrarEventos(IReadOnlyList<EventoViagem> eventos, bool commit)
    {
        // Listas já pertencem à decisão produtiva; nenhuma cópia ou serialização por GPS.
        for (var i = 0; i < eventos.Count; i++)
            RegistrarFunil(eventos[i].Tipo switch
            {
                "PassagemParada" => commit ? ContadorFunilOperacional.CommitPassagem : ContadorFunilOperacional.SelecionadoPassagem,
                "ViagemIniciada" => commit ? ContadorFunilOperacional.CommitInicio : ContadorFunilOperacional.SelecionadoInicio,
                "ViagemFinalizada" => commit ? ContadorFunilOperacional.CommitFim : ContadorFunilOperacional.SelecionadoFim,
                _ => commit ? ContadorFunilOperacional.CommitOutro : ContadorFunilOperacional.SelecionadoOutro,
            });
    }

    internal void RegistrarDecisaoRegra(ViagemOperacionalState? anterior, EstruturaViagem estrutura,
        PosicaoVeiculoDto gps, TransicaoParadas transicao, DecisaoViagem decisao, bool adocao)
    {
        var ramo = anterior is null ? ContadorFunilOperacional.BaselineNova
            : anterior.Estado == EstadoViagem.Finalizada
                ? decisao.Estado.Observada.ViagemId != anterior.Observada.ViagemId
                    ? ContadorFunilOperacional.BaselineAposFinalizada : ContadorFunilOperacional.EsperaFinalizada
            : !ViagemOperacionalRegra.IdentidadeConfiavel(anterior)
                ? ViagemOperacionalRegra.IdentidadeConfiavel(decisao.Estado)
                    ? ContadorFunilOperacional.Recuperacao : ContadorFunilOperacional.ProtecaoCircular
            : !ViagemOperacionalRegra.IdentidadeConfiavel(decisao.Estado) ? ContadorFunilOperacional.ProtecaoCircular
            : anterior.Observada.Volta != decisao.Estado.Observada.Volta && anterior.Candidato is not null
                ? ContadorFunilOperacional.Recuperacao
            : adocao || anterior.Candidato is not null ? ContadorFunilOperacional.AdocaoCandidato
            : estrutura.LinhaId != anterior.LinhaId || estrutura.SentidoId != anterior.SentidoId
                || estrutura.PadraoVersaoId != anterior.Observada.PadraoVersaoId
                ? ContadorFunilOperacional.IdentidadeRetida : ContadorFunilOperacional.Normal;
        RegistrarRamo(anterior, ramo, decisao);
        if (adocao && transicao.Ultrapassadas.Count > 0)
            RegistrarFunil(ContadorFunilOperacional.OcorrenciasSuprimidasMensuraveis, transicao.Ultrapassadas.Count);
        if (ramo != ContadorFunilOperacional.Normal || anterior is null) return;
        RegistrarFunil(ContadorFunilOperacional.NormalTransicoes);
        RegistrarFunil(transicao.Proxima is null ? ContadorFunilOperacional.SemAlvo : ContadorFunilOperacional.ComAlvo);
        if (transicao.Proxima is null)
            RegistrarFunil(anterior.Estado == EstadoViagem.PossivelFim
                ? ContadorFunilOperacional.SemAlvoPossivelFim : ContadorFunilOperacional.SemAlvoAtiva);
        RegistrarFunil(transicao.Ultrapassadas.Count == 0 ? ContadorFunilOperacional.ZeroCruzamentos : ContadorFunilOperacional.ComCruzamentos);
        RegistrarFunil(ContadorFunilOperacional.OcorrenciasSelecionadas, transicao.Ultrapassadas.Count);

        var obs = anterior.Observada;
        var atual = decisao.Estado.Observada;
        // Não comparar frações de outra geometria/volta ou cursor de outra cadeia.
        var compativel = obs.PadraoOperacionalId != Guid.Empty
            && obs.PadraoOperacionalId == estrutura.PadraoOperacionalId
            && obs.Topologia == estrutura.Topologia && obs.Volta == atual.Volta && !transicao.HouveWrap
            && gps.PadraoVersaoId == obs.PadraoVersaoId
            && gps.ComprimentoRotaMetros is { } length && double.IsFinite(length) && length > 0
            && double.IsFinite(obs.PosicaoNaRotaConfirmada)
            && (obs.OcorrenciaCursorId == Guid.Empty || obs.OcorrenciaCursorId == obs.UltimaOcorrenciaParadaPadraoId)
            && (obs.OrdemCursor is null || obs.OrdemCursor == obs.UltimaParadaOrdem)
            && (transicao.Proxima is null || transicao.Proxima.PadraoVersaoId == obs.PadraoVersaoId);
        if (!compativel)
        {
            RegistrarFunil(ContadorFunilOperacional.ComparacaoIncompativel);
            return;
        }
        RegistrarFunil(ContadorFunilOperacional.NormalAvaliada);
        RegistrarFunil(atual.PosicaoNaRotaConfirmada > obs.PosicaoNaRotaConfirmada ? ContadorFunilOperacional.ProjecaoMaior
            : atual.PosicaoNaRotaConfirmada < obs.PosicaoNaRotaConfirmada ? ContadorFunilOperacional.ProjecaoMenor
            : ContadorFunilOperacional.ProjecaoIgual);
        RegistrarFunil(atual.UltimaParadaOrdem > obs.UltimaParadaOrdem ? ContadorFunilOperacional.CursorAvancou
            : atual.UltimaParadaOrdem == obs.UltimaParadaOrdem && atual.UltimaOcorrenciaParadaPadraoId == obs.UltimaOcorrenciaParadaPadraoId
                ? ContadorFunilOperacional.CursorPermaneceu : ContadorFunilOperacional.CursorIncompativel);
    }

    internal void RegistrarRamo(ViagemOperacionalState? anterior, ContadorFunilOperacional ramo, DecisaoViagem decisao)
    {
        RegistrarFunil(ContadorFunilOperacional.Decisoes);
        RegistrarFunil(ramo);
        if (anterior is not null)
            RegistrarFunil(anterior.Estado switch
            {
                EstadoViagem.Ativa => ContadorFunilOperacional.EstadoAnteriorAtiva,
                EstadoViagem.PossivelFim => ContadorFunilOperacional.EstadoAnteriorPossivelFim,
                _ => ContadorFunilOperacional.EstadoAnteriorFinalizada,
            });
        RegistrarEventosSelecionados(decisao.Eventos);
    }

    private readonly string _cycleId = Guid.NewGuid().ToString("N");
    internal string CycleId => _cycleId;
    private static readonly string[] Temas = ["resultados", "decisoes", "movimento", "passagens", "predecessor_nova", "predecessor_retry"];

    internal void EmitirFunilOperacional(ILogger logger)
    {
        for (var parte = 0; parte < Temas.Length; parte++)
            logger.LogInformation("Funil GPS: cycle_id={cycle_id} inicio_utc={inicio_utc:o} parte={parte} partes={partes} tema={tema} contadores={contadores}",
                _cycleId, Inicio.ToUniversalTime(), parte + 1, Temas.Length, Temas[parte], FormatarParteFunil(parte));
    }

    // Mantém a visão completa para inspeção/testes; a emissão produtiva é segmentada.
    internal string FormatarFunilOperacional() => string.Join(" ", Enumerable.Range(0, Temas.Length).Select(FormatarParteFunil));

    private string FormatarParteFunil(int parte)
    {
        var texto = new StringBuilder(2_000);
        for (var origem = 0; origem < Origens; origem++)
        {
            var retry = origem == 1;
            if (parte == 0)
                for (var i = 0; i < (int)ResultadoFunilOperacional.Quantidade; i++)
                    Campo($"op_{NomesOrigem[origem]}_resultado_{NomesResultado[i]}", ResultadoOperacional((ResultadoFunilOperacional)i, retry));
            for (var i = 0; i < (int)ContadorFunilOperacional.Quantidade; i++)
            {
                var contador = (ContadorFunilOperacional)i;
                var grupo = contador is ContadorFunilOperacional.RetryClaim or ContadorFunilOperacional.RetryNaoProcessado ? 0
                    : contador <= ContadorFunilOperacional.EstadoAnteriorFinalizada || contador == ContadorFunilOperacional.ProjecaoPinadaAdotada ? 1
                    : contador <= ContadorFunilOperacional.OcorrenciasSuprimidasMensuraveis ? 2 : 3;
                if (parte == grupo)
                    Campo($"op_{NomesOrigem[origem]}_{NomesFunil[i]}", Funil(contador, retry));
            }
            if (parte == 4 + origem)
                for (var modal = 0; modal < Modais; modal++)
                    for (var campo = 0; campo < CamposPredecessor; campo++)
                        Campo($"op_{NomesOrigem[origem]}_{NomesModal[modal]}_predecessor_{NomesPredecessor[campo]}", Predecessor(modal, campo, retry));
        }
        return texto.ToString();
        void Campo(string nome, long valor)
        {
            if (texto.Length != 0) texto.Append(' ');
            texto.Append(nome).Append('=').Append(valor.ToString(CultureInfo.InvariantCulture));
        }
    }
}
