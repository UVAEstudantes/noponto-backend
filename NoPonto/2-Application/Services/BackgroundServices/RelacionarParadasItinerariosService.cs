using System.Diagnostics;
using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class RelacionarParadasItinerariosService
{
    private readonly TransporteDbContext _contexto;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RelacionarParadasItinerariosService> _logger;

    public RelacionarParadasItinerariosService(
        TransporteDbContext contexto,
        IConfiguration configuration,
        ILogger<RelacionarParadasItinerariosService> logger)
    {
        _contexto      = contexto;
        _configuration = configuration;
        _logger        = logger;
    }

    // ── Todos os itinerários (ônibus SPPO) ────────────────────────────────────

    public async Task ExecutarRelacionamentoAsync(CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        _logger.LogInformation("Iniciando relacionamento de paradas com itinerários");

        var config                      = LerConfiguracoes();
        var totalGeralRelacoesCriadas   = 0;
        var totalItinerariosProcessados = 0;

        var itinerarioIds = await _contexto.Itinerarios
            .AsNoTracking()
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        foreach (var itinerarioId in itinerarioIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalItinerariosProcessados++;

            var candidatos = await BuscarCandidatosSppoAsync(itinerarioId, config, cancellationToken);

            _logger.LogInformation(
                "Itinerário {id} — candidatos brutos: {qtd}", itinerarioId, candidatos.Count);

            if (candidatos.Count == 0) continue;

            var paradasSelecionadas = FiltrarEOrdenarSppo(candidatos, config);

            _logger.LogInformation(
                "Itinerário {id} — paradas após filtro: {qtd} (descartadas: {desc})",
                itinerarioId, paradasSelecionadas.Count,
                candidatos.Count - paradasSelecionadas.Count);

            if (paradasSelecionadas.Count == 0) continue;

            var criadas = await SalvarRelacoesNovasAsync(
                itinerarioId, paradasSelecionadas, config.TamanhoLote, cancellationToken);

            _logger.LogInformation("Itinerário {id} — relações criadas: {qtd}", itinerarioId, criadas);
            totalGeralRelacoesCriadas += criadas;
        }

        cronometro.Stop();
        _logger.LogInformation("Total itinerários processados: {total}", totalItinerariosProcessados);
        _logger.LogInformation("Total relações criadas: {total}",        totalGeralRelacoesCriadas);
        _logger.LogInformation(
            "Tempo total: {s}s",
            cronometro.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    // ── BRT ───────────────────────────────────────────────────────────────────

    public async Task ExecutarRelacionamentoPorModalAsync(
        string nomeModal,
        CancellationToken cancellationToken = default)
    {
        // BRT tem lógica própria — simples e direta
        if (string.Equals(nomeModal, "BRT", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutarRelacionamentoBrtAsync(cancellationToken);
            return;
        }

        // Outros modais futuros podem ser adicionados aqui
        _logger.LogWarning("Modal '{modal}' não tem estratégia de relacionamento definida.", nomeModal);
    }

    private async Task ExecutarRelacionamentoBrtAsync(CancellationToken cancellationToken)
    {
        var cronometro = Stopwatch.StartNew();
        _logger.LogInformation("Iniciando relacionamento BRT...");

        var config = LerConfiguracoes();

        // Para BRT usamos raio maior — as paradas ficam exatamente sobre a geometria
        // e não há risco de contaminação pois filtramos por prefixo "BRT-" no SQL.
        var raioBrt = LerDoubleOpcional("RELACIONAMENTO:BRT:RAIO_METROS", 200.0);

        var itinerarioIds = await _contexto.Itinerarios
            .AsNoTracking()
            .Where(i => i.Sentido.Linha.Modal.Nome == "BRT")
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("BRT — {total} itinerários encontrados.", itinerarioIds.Count);

        var totalRelacoes   = 0;
        var totalItinerarios = 0;

        foreach (var itinerarioId in itinerarioIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalItinerarios++;

            var paradas = await BuscarParadasBrtAsync(itinerarioId, raioBrt, cancellationToken);

            _logger.LogInformation(
                "BRT itinerário {id} — {qtd} paradas encontradas.", itinerarioId, paradas.Count);

            if (paradas.Count == 0) continue;

            var criadas = await SalvarRelacoesNovasAsync(
                itinerarioId, paradas, config.TamanhoLote, cancellationToken);

            _logger.LogInformation(
                "BRT itinerário {id} — {qtd} relações criadas.", itinerarioId, criadas);

            totalRelacoes += criadas;
        }

        cronometro.Stop();
        _logger.LogInformation(
            "Relacionamento BRT concluído — {it} itinerários, {rel} relações, {s:F2}s.",
            totalItinerarios, totalRelacoes, cronometro.Elapsed.TotalSeconds);
    }

    // ── Por itinerário individual (debug/calibração) ──────────────────────────

    public async Task<ResultadoItinerario> ExecutarParaItinerarioAsync(
        Guid itinerarioId,
        CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var config     = LerConfiguracoes();

        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return ResultadoItinerario.NaoEncontrado(itinerarioId);

        var candidatos        = await BuscarCandidatosSppoAsync(itinerarioId, config, cancellationToken);
        var paradasSelecionadas = FiltrarEOrdenarSppo(candidatos, config);

        var criadas = await SalvarRelacoesNovasAsync(
            itinerarioId, paradasSelecionadas, config.TamanhoLote, cancellationToken);

        cronometro.Stop();

        return new ResultadoItinerario
        {
            ItinerarioId       = itinerarioId,
            Encontrado         = true,
            CandidatosBrutos   = candidatos.Count,
            ParadasDescartadas = candidatos.Count - paradasSelecionadas.Count,
            RelacoesCriadas    = criadas,
            TempoMs            = (long)cronometro.Elapsed.TotalMilliseconds
        };
    }

    public sealed class ResultadoItinerario
    {
        public Guid ItinerarioId       { get; init; }
        public bool Encontrado         { get; init; }
        public int  CandidatosBrutos   { get; init; }
        public int  ParadasDescartadas { get; init; }
        public int  RelacoesCriadas    { get; init; }
        public long TempoMs            { get; init; }

        public static ResultadoItinerario NaoEncontrado(Guid id) => new()
        {
            ItinerarioId = id,
            Encontrado   = false
        };
    }

    // ── SQL BRT: simples e direto ─────────────────────────────────────────────
    //
    // Lógica BRT:
    //   1. Pega todas as paradas com prefixo "BRT-" dentro do raio do itinerário
    //   2. Ordena pela posição na rota (ST_LineLocatePoint)
    //   3. Elimina duplicatas geográficas (mesma estação física, IDs diferentes)
    //
    // Não usa matching por vértice nem filtro perpendicular — desnecessário
    // pois as paradas BRT ficam exatamente sobre a geometria do corredor.

    private async Task<List<ParadaCandidato>> BuscarParadasBrtAsync(
        Guid itinerarioId,
        double raioMetros,
        CancellationToken cancellationToken)
    {
        var resultados = new List<ParadaCandidato>();
        var conexao    = _contexto.Database.GetDbConnection();
        var deveFechar = conexao.State != ConnectionState.Open;

        if (deveFechar)
            await conexao.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conexao.CreateCommand();

            cmd.CommandText = @"
SELECT
    p.""Id""                                                                AS ""ParadaId"",
    ST_LineLocatePoint(i.""Geometria"", p.""Localizacao"")                  AS ""PosicaoLinha"",
    ST_Distance(p.""Localizacao""::geography, i.""Geometria""::geography)   AS ""DistanciaMetros"",
    ST_Y(p.""Localizacao"")                                                 AS ""Latitude"",
    ST_X(p.""Localizacao"")                                                 AS ""Longitude""
FROM ""Paradas"" p
CROSS JOIN ""Itinerarios"" i
WHERE i.""Id"" = @itinerarioId
  AND p.""Codigo"" LIKE 'BRT-%'
  AND ST_DWithin(
        p.""Localizacao""::geography,
        i.""Geometria""::geography,
        @raioMetros
      )
ORDER BY ""PosicaoLinha"" ASC;";

            AddParam(cmd, "@itinerarioId", itinerarioId);
            AddParam(cmd, "@raioMetros",   raioMetros);

            await using var leitor = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await leitor.ReadAsync(cancellationToken))
            {
                resultados.Add(new ParadaCandidato
                {
                    ParadaId               = leitor.GetFieldValue<Guid>(0),
                    PosicaoLinha           = leitor.GetDouble(1),
                    DistanciaVerticeMetros = leitor.GetDouble(2),
                    DistanciaLinhaMetros   = leitor.GetDouble(2),
                    DistanciaPerp          = 0,
                    Score                  = 1,
                    Latitude               = leitor.GetDouble(3),
                    Longitude              = leitor.GetDouble(4),
                });
            }
        }
        finally
        {
            if (deveFechar)
                await conexao.CloseAsync();
        }

        // Elimina duplicatas geográficas (mesma estação física com IDs diferentes)
        // Mantém a primeira encontrada (menor distância à rota = melhor)
        return EliminarDuplicatasGeograficas(resultados, distanciaMinMetros: 100.0);
    }

    // ── SQL SPPO: algoritmo original com matching por vértice ─────────────────

    private async Task<List<ParadaCandidato>> BuscarCandidatosSppoAsync(
        Guid itinerarioId,
        Configuracoes config,
        CancellationToken cancellationToken)
    {
        var resultados = new List<ParadaCandidato>();
        var conexao    = _contexto.Database.GetDbConnection();
        var deveFechar = conexao.State != ConnectionState.Open;

        if (deveFechar)
            await conexao.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conexao.CreateCommand();

            cmd.CommandText = @"
WITH vertices AS (
    SELECT
        (dp).geom    AS ""Vertice"",
        (dp).path[1] AS ""IndiceVertice""
    FROM ""Itinerarios"" i
    CROSS JOIN ST_DumpPoints(i.""Geometria"") dp
    WHERE i.""Id"" = @itinerarioId
),
paradas_candidatas AS (
    SELECT
        p.""Id""          AS ""ParadaId"",
        p.""Localizacao"" AS ""Loc""
    FROM ""Paradas"" p
    CROSS JOIN ""Itinerarios"" i
    WHERE i.""Id"" = @itinerarioId
      AND p.""Codigo"" NOT LIKE 'BRT-%'
      AND p.""Codigo"" NOT LIKE 'TREM-%'
      AND ST_DWithin(
            p.""Localizacao""::geography,
            i.""Geometria""::geography,
            @distanciaMaxima
          )
),
pares AS (
    SELECT
        pc.""ParadaId"",
        pc.""Loc"",
        v.""IndiceVertice"",
        v.""Vertice"",
        ST_Distance(pc.""Loc""::geography, v.""Vertice""::geography) AS ""Dist""
    FROM paradas_candidatas pc
    CROSS JOIN vertices v
),
melhor_vertice_por_parada AS (
    SELECT DISTINCT ON (""ParadaId"")
        ""ParadaId"",
        ""Loc"",
        ""IndiceVertice"",
        ""Vertice"",
        ""Dist"" AS ""DistanciaVerticeMetros""
    FROM pares
    ORDER BY ""ParadaId"", ""Dist"" ASC
),
melhor_parada_por_vertice AS (
    SELECT DISTINCT ON (""IndiceVertice"")
        ""ParadaId"",
        ""Loc"",
        ""IndiceVertice"",
        ""DistanciaVerticeMetros""
    FROM melhor_vertice_por_parada
    WHERE ""DistanciaVerticeMetros"" <= @distanciaMaxima
    ORDER BY ""IndiceVertice"", ""DistanciaVerticeMetros"" ASC
),
enriquecido AS (
    SELECT
        m.""ParadaId"",
        m.""DistanciaVerticeMetros"",
        ST_LineLocatePoint(i.""Geometria"", m.""Loc"")                          AS ""PosicaoLinha"",
        ST_Distance(m.""Loc""::geography, i.""Geometria""::geography)           AS ""DistanciaLinhaMetros"",
        ST_ClosestPoint(i.""Geometria"", m.""Loc"")                             AS ""PontoProj"",
        ST_LineInterpolatePoint(
            i.""Geometria"",
            LEAST(ST_LineLocatePoint(i.""Geometria"", m.""Loc"") + 0.001, 1.0)
        )                                                                        AS ""PontoAdiante"",
        m.""Loc"",
        ST_Y(m.""Loc"")                                                         AS ""Latitude"",
        ST_X(m.""Loc"")                                                         AS ""Longitude""
    FROM melhor_parada_por_vertice m
    CROSS JOIN ""Itinerarios"" i
    WHERE i.""Id"" = @itinerarioId
)
SELECT
    ""ParadaId"",
    ""PosicaoLinha"",
    ""DistanciaVerticeMetros"",
    ""DistanciaLinhaMetros"",
    ABS(
        (ST_X(""PontoAdiante"") - ST_X(""PontoProj"")) * (ST_Y(""Loc"") - ST_Y(""PontoProj""))
      - (ST_Y(""PontoAdiante"") - ST_Y(""PontoProj"")) * (ST_X(""Loc"") - ST_X(""PontoProj""))
    ) / NULLIF(ST_Distance(""PontoProj"", ""PontoAdiante""), 0) AS ""DistanciaPerp"",
    ""Latitude"",
    ""Longitude""
FROM enriquecido
ORDER BY ""PosicaoLinha"" ASC;";

            AddParam(cmd, "@itinerarioId",    itinerarioId);
            AddParam(cmd, "@distanciaMaxima", config.DistanciaMaximaMetros);

            await using var leitor = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await leitor.ReadAsync(cancellationToken))
            {
                resultados.Add(new ParadaCandidato
                {
                    ParadaId               = leitor.GetFieldValue<Guid>(0),
                    PosicaoLinha           = leitor.GetDouble(1),
                    DistanciaVerticeMetros = leitor.GetDouble(2),
                    DistanciaLinhaMetros   = leitor.GetDouble(3),
                    DistanciaPerp          = leitor.IsDBNull(4) ? 0.0 : leitor.GetDouble(4),
                    Latitude               = leitor.GetDouble(5),
                    Longitude              = leitor.GetDouble(6),
                });
            }
        }
        finally
        {
            if (deveFechar)
                await conexao.CloseAsync();
        }

        return resultados;
    }

    // ── Filtragem SPPO ────────────────────────────────────────────────────────

    private List<ParadaCandidato> FiltrarEOrdenarSppo(
        List<ParadaCandidato> candidatos,
        Configuracoes config)
    {
        var melhoresPorParada = candidatos
            .GroupBy(c => c.ParadaId)
            .Select(g => g.OrderBy(c => c.DistanciaVerticeMetros).First())
            .ToList();

        var selecionados = new List<ParadaCandidato>();

        foreach (var candidato in melhoresPorParada.OrderBy(c => c.PosicaoLinha))
        {
            var ehTerminal = candidato.PosicaoLinha < config.LimiteTerminalInicio
                          || candidato.PosicaoLinha > config.LimiteTerminalFim;

            var distanciaLimite = ehTerminal
                ? config.DistanciaMaximaMetros * config.FatorRaioTerminal
                : config.DistanciaMaximaMetros;

            if (candidato.DistanciaVerticeMetros > distanciaLimite)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: vértice {dist:F1}m > limite {lim:F1}m (terminal={t})",
                    candidato.ParadaId, candidato.DistanciaVerticeMetros, distanciaLimite, ehTerminal);
                continue;
            }

            if (candidato.DistanciaPerp > config.DistanciaPerpMaxMetros)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: perp {perp:F1}m > {max:F1}m",
                    candidato.ParadaId, candidato.DistanciaPerp, config.DistanciaPerpMaxMetros);
                continue;
            }

            var scoreVertice       = 1.0 - (candidato.DistanciaVerticeMetros / config.DistanciaMaximaMetros);
            var scorePerpendicular = 1.0 - Math.Min(candidato.DistanciaPerp / config.DistanciaPerpMaxMetros, 1.0);
            candidato.Score        = config.PesoDistancia * scoreVertice
                                   + config.PesoPerpendicular * scorePerpendicular;

            if (candidato.Score < config.ScoreMinimo)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: score {s:F3} < mínimo {m:F3}",
                    candidato.ParadaId, candidato.Score, config.ScoreMinimo);
                continue;
            }

            selecionados.Add(candidato);
        }

        return FiltrarSequenciaConsistente(selecionados, config.SaltoMaximoPosicao);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Remove paradas a menos de <paramref name="distanciaMinMetros"/> metros
    /// de outra já aceita, mantendo a que aparece primeiro (menor PosicaoLinha).
    /// </summary>
    private List<ParadaCandidato> EliminarDuplicatasGeograficas(
        List<ParadaCandidato> paradas,
        double distanciaMinMetros)
    {
        // 1 grau ≈ 111 320 m — suficiente para distâncias < 500 m
        var limiteGraus = distanciaMinMetros / 111_320.0;
        var aceitas     = new List<ParadaCandidato>();

        foreach (var candidato in paradas)
        {
            var muitoProximo = aceitas.Any(a =>
            {
                var dLat = a.Latitude  - candidato.Latitude;
                var dLon = a.Longitude - candidato.Longitude;
                return Math.Sqrt(dLat * dLat + dLon * dLon) < limiteGraus;
            });

            if (muitoProximo)
            {
                _logger.LogDebug(
                    "BRT — parada {id} eliminada como duplicata geográfica (< {d}m).",
                    candidato.ParadaId, distanciaMinMetros);
                continue;
            }

            aceitas.Add(candidato);
        }

        return aceitas;
    }

    private List<ParadaCandidato> FiltrarSequenciaConsistente(
        List<ParadaCandidato> paradas,
        double saltoMaximo)
    {
        var resultado       = new List<ParadaCandidato>();
        var posicaoAnterior = -1.0;

        foreach (var parada in paradas)
        {
            if (parada.PosicaoLinha < posicaoAnterior)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: regressão {ant:F4} → {atual:F4}",
                    parada.ParadaId, posicaoAnterior, parada.PosicaoLinha);
                continue;
            }

            if (posicaoAnterior >= 0 && (parada.PosicaoLinha - posicaoAnterior) > saltoMaximo)
            {
                _logger.LogWarning(
                    "Parada {id}: salto grande {ant:F4} → {atual:F4} (>{max:F4})",
                    parada.ParadaId, posicaoAnterior, parada.PosicaoLinha, saltoMaximo);
            }

            resultado.Add(parada);
            posicaoAnterior = parada.PosicaoLinha;
        }

        return resultado;
    }

    // ── Persistência ──────────────────────────────────────────────────────────

    private async Task<int> SalvarRelacoesNovasAsync(
        Guid itinerarioId,
        List<ParadaCandidato> paradasSelecionadas,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        var paradaIds = paradasSelecionadas.Select(p => p.ParadaId).ToList();

        var jaRelacionadas = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ItinerarioId == itinerarioId && paradaIds.Contains(r.ParadaId))
            .Select(r => r.ParadaId)
            .ToListAsync(cancellationToken);

        var jaRelacionadasSet = new HashSet<Guid>(jaRelacionadas);
        var relacoesNovas     = new List<ParadaItinerario>();
        var ordem             = 0;

        foreach (var parada in paradasSelecionadas)
        {
            ordem++;
            if (!jaRelacionadasSet.Add(parada.ParadaId))
                continue;

            relacoesNovas.Add(new ParadaItinerario
            {
                Id              = Guid.NewGuid(),
                ParadaId        = parada.ParadaId,
                ItinerarioId    = itinerarioId,
                Ordem           = ordem,
                PosicaoLinha    = parada.PosicaoLinha,
                DistanciaMetros = parada.DistanciaVerticeMetros
            });
        }

        return await SalvarEmLotesAsync(relacoesNovas, tamanhoLote, cancellationToken);
    }

    private async Task<int> SalvarEmLotesAsync(
        List<ParadaItinerario> relacoes,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (relacoes.Count == 0) return 0;

        var total = 0;

        for (var i = 0; i < relacoes.Count; i += tamanhoLote)
        {
            var lote = relacoes.Skip(i).Take(tamanhoLote).ToList();
            _contexto.ParadasItinerario.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();
            total += lote.Count;
        }

        return total;
    }

    // ── Configurações ─────────────────────────────────────────────────────────

    private Configuracoes LerConfiguracoes()
    {
        return new Configuracoes
        {
            DistanciaMaximaMetros  = LerDouble("RELACIONAMENTO:DISTANCIA_MAXIMA_METROS",
                                         "RELACIONAMENTO__DISTANCIA_MAXIMA_METROS"),
            DistanciaPerpMaxMetros = LerDoubleOpcional("RELACIONAMENTO:DISTANCIA_PERP_MAX_METROS", 15.0),
            FatorRaioTerminal      = LerDoubleOpcional("RELACIONAMENTO:FATOR_RAIO_TERMINAL", 0.6),
            LimiteTerminalInicio   = LerDoubleOpcional("RELACIONAMENTO:LIMITE_TERMINAL_INICIO", 0.03),
            LimiteTerminalFim      = LerDoubleOpcional("RELACIONAMENTO:LIMITE_TERMINAL_FIM", 0.97),
            ScoreMinimo            = LerDoubleOpcional("RELACIONAMENTO:SCORE_MINIMO", 0.4),
            PesoDistancia          = LerDoubleOpcional("RELACIONAMENTO:PESO_DISTANCIA", 0.5),
            PesoPerpendicular      = LerDoubleOpcional("RELACIONAMENTO:PESO_PERPENDICULAR", 0.5),
            SaltoMaximoPosicao     = LerDoubleOpcional("RELACIONAMENTO:SALTO_MAXIMO_POSICAO", 0.20),
            TamanhoLote            = LerInt("IMPORT:BATCH_SIZE", "IMPORT__BATCH_SIZE")
        };
    }

    private double LerDouble(string chave, string mensagemErro)
    {
        var valor = _configuration[chave];
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
            return r;
        throw new InvalidOperationException($"Variável {mensagemErro} não configurada ou inválida.");
    }

    private double LerDoubleOpcional(string chave, double padrao)
    {
        var valor = _configuration[chave];
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
            return r;
        return padrao;
    }

    private int LerInt(string chave, string mensagemErro)
    {
        var valor = _configuration[chave];
        if (int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0)
            return r;
        throw new InvalidOperationException($"Variável {mensagemErro} não configurada ou inválida.");
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string nome, object valor)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = nome;
        p.Value         = valor;
        cmd.Parameters.Add(p);
    }

    // ── Tipos internos ────────────────────────────────────────────────────────

    private sealed class ParadaCandidato
    {
        public required Guid   ParadaId               { get; init; }
        public required double PosicaoLinha            { get; init; }
        public required double DistanciaVerticeMetros  { get; init; }
        public required double DistanciaLinhaMetros    { get; init; }
        public required double DistanciaPerp           { get; init; }
        public          double Score                   { get; set; }
        public          double Latitude                { get; init; }
        public          double Longitude               { get; init; }
    }

    private sealed class Configuracoes
    {
        public double DistanciaMaximaMetros   { get; init; }
        public double DistanciaPerpMaxMetros  { get; init; }
        public double FatorRaioTerminal       { get; init; }
        public double LimiteTerminalInicio    { get; init; }
        public double LimiteTerminalFim       { get; init; }
        public double ScoreMinimo             { get; init; }
        public double PesoDistancia           { get; init; }
        public double PesoPerpendicular       { get; init; }
        public double SaltoMaximoPosicao      { get; init; }
        public int    TamanhoLote             { get; init; }
    }
}