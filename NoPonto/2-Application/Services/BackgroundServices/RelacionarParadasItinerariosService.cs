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
        _contexto = contexto;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ExecutarRelacionamentoAsync(CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        _logger.LogInformation("Iniciando relacionamento de paradas com itinerários");

        var config = LerConfiguracoes();
        var totalGeralRelacoesCriadas = 0;
        var totalItinerariosProcessados = 0;

        var itinerarioIds = await _contexto.Itinerarios
            .AsNoTracking()
            .Select(itinerario => itinerario.Id)
            .ToListAsync(cancellationToken);

        foreach (var itinerarioId in itinerarioIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalItinerariosProcessados++;

            // Busca candidatos com raio generoso + métricas de qualidade vindas do SQL
            var candidatos = await BuscarCandidatosAsync(itinerarioId, config, cancellationToken);

            _logger.LogInformation(
                "Itinerário {id} - Candidatos brutos: {qtd}",
                itinerarioId,
                candidatos.Count);

            if (candidatos.Count == 0)
                continue;

            // Filtra e ordena por qualidade em C# (leve, dados já vieram do banco)
            var paradasSelecionadas = FiltrarEOrdenar(candidatos, config);

            _logger.LogInformation(
                "Itinerário {id} - Paradas após filtro: {qtd} (descartadas: {desc})",
                itinerarioId,
                paradasSelecionadas.Count,
                candidatos.Count - paradasSelecionadas.Count);

            if (paradasSelecionadas.Count == 0)
                continue;

            var paradaIds = paradasSelecionadas.Select(p => p.ParadaId).ToList();

            var paradasJaRelacionadas = await _contexto.ParadasItinerario
                .AsNoTracking()
                .Where(r => r.ItinerarioId == itinerarioId && paradaIds.Contains(r.ParadaId))
                .Select(r => r.ParadaId)
                .ToListAsync(cancellationToken);

            var jaRelacionadas = new HashSet<Guid>(paradasJaRelacionadas);
            var relacoesNovas = new List<ParadaItinerario>();
            var ordem = 0;

            foreach (var parada in paradasSelecionadas)
            {
                ordem++;

                if (!jaRelacionadas.Add(parada.ParadaId))
                    continue;

                relacoesNovas.Add(new ParadaItinerario
                {
                    Id = Guid.NewGuid(),
                    ParadaId = parada.ParadaId,
                    ItinerarioId = itinerarioId,
                    Ordem = ordem,
                    PosicaoLinha = parada.PosicaoLinha,
                    DistanciaMetros = parada.DistanciaVerticeMetros
                });
            }

            var criadas = await SalvarRelacoesEmLotesAsync(relacoesNovas, config.TamanhoLote, cancellationToken);

            _logger.LogInformation("Itinerário {id} - Relações criadas: {qtd}", itinerarioId, criadas);
            totalGeralRelacoesCriadas += criadas;
        }

        cronometro.Stop();

        _logger.LogInformation("Total de itinerários processados: {total}", totalItinerariosProcessados);
        _logger.LogInformation("Total geral de relações criadas: {total}", totalGeralRelacoesCriadas);
        _logger.LogInformation(
            "Tempo total: {segundos}s",
            cronometro.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    public async Task<ResultadoItinerario> ExecutarParaItinerarioAsync(
        Guid itinerarioId,
        CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        var existe = await _contexto.Itinerarios
            .AsNoTracking()
            .AnyAsync(i => i.Id == itinerarioId, cancellationToken);

        if (!existe)
            return ResultadoItinerario.NaoEncontrado(itinerarioId);

        var candidatos = await BuscarCandidatosAsync(itinerarioId, config, cancellationToken);
        var paradasSelecionadas = FiltrarEOrdenar(candidatos, config);

        var paradaIds = paradasSelecionadas.Select(p => p.ParadaId).ToList();

        var jaRelacionadas = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ItinerarioId == itinerarioId && paradaIds.Contains(r.ParadaId))
            .Select(r => r.ParadaId)
            .ToListAsync(cancellationToken);

        var jaRelacionadasSet = new HashSet<Guid>(jaRelacionadas);
        var relacoesNovas = new List<ParadaItinerario>();
        var ordem = 0;

        foreach (var parada in paradasSelecionadas)
        {
            ordem++;
            if (!jaRelacionadasSet.Add(parada.ParadaId))
                continue;

            relacoesNovas.Add(new ParadaItinerario
            {
                Id = Guid.NewGuid(),
                ParadaId = parada.ParadaId,
                ItinerarioId = itinerarioId,
                Ordem = ordem,
                PosicaoLinha = parada.PosicaoLinha,
                DistanciaMetros = parada.DistanciaVerticeMetros
            });
        }

        var criadas = await SalvarRelacoesEmLotesAsync(relacoesNovas, config.TamanhoLote, cancellationToken);
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
        public Guid   ItinerarioId       { get; init; }
        public bool   Encontrado         { get; init; }
        public int    CandidatosBrutos   { get; init; }
        public int    ParadasDescartadas { get; init; }
        public int    RelacoesCriadas    { get; init; }
        public long   TempoMs            { get; init; }

        public static ResultadoItinerario NaoEncontrado(Guid id) => new()
        {
            ItinerarioId = id,
            Encontrado   = false
        };
    }

    // -------------------------------------------------------------------------
    // SQL: busca candidatos com métricas de qualidade já calculadas no PostGIS
    // -------------------------------------------------------------------------
    private async Task<List<ParadaCandidato>> BuscarCandidatosAsync(
        Guid itinerarioId,
        Configuracoes config,
        CancellationToken cancellationToken)
    {
        var resultados = new List<ParadaCandidato>();
        var conexao = _contexto.Database.GetDbConnection();
        var deveFechar = conexao.State != ConnectionState.Open;

        if (deveFechar)
            await conexao.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conexao.CreateCommand();

            // Estratégia: matching exclusivo vértice ↔ parada
            //
            // Problema anterior: duas paradas frente a frente (ida/volta) podiam
            // compartilhar o mesmo vértice como "mais próximo", pois a distância
            // entre elas é menor que o raio. Ambas passavam no filtro.
            //
            // Solução em duas etapas dentro do SQL:
            //
            //   1. Para cada PARADA  → encontra o vértice mais próximo  (melhor_vertice_por_parada)
            //   2. Para cada VÉRTICE → dentre todas as paradas que o elegeram,
            //                          fica apenas a mais próxima            (melhor_parada_por_vertice)
            //
            // Resultado: cada vértice "pertence" a no máximo uma parada.
            // Paradas do lado oposto da rua elegem o mesmo vértice mas perdem
            // para a parada que de fato está mais próxima dele.

            cmd.CommandText = @"
WITH vertices AS (
    -- Vértices reais do itinerário (coordenadas GPS coletadas em campo)
    SELECT
        (dp).geom       AS ""Vertice"",
        (dp).path[1]    AS ""IndiceVertice""
    FROM ""Itinerarios"" i
    CROSS JOIN ST_DumpPoints(i.""Geometria"") dp
    WHERE i.""Id"" = @itinerarioId
),
paradas_candidatas AS (
    -- Paradas dentro do raio máximo (usa índice GIST — pré-filtro barato)
    SELECT
        p.""Id""            AS ""ParadaId"",
        p.""Localizacao""   AS ""Loc""
    FROM ""Paradas"" p
    CROSS JOIN ""Itinerarios"" i
    WHERE i.""Id"" = @itinerarioId
      AND ST_DWithin(
            p.""Localizacao""::geography,
            i.""Geometria""::geography,
            @distanciaMaxima
          )
),
pares AS (
    -- Produto cartesiano paradas × vértices com distância entre cada par
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
    -- Passo 1: para cada parada, qual é o vértice mais próximo?
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
    -- Passo 2: para cada vértice, qual parada está mais perto?
    -- Isso resolve o empate entre paradas frente a frente que elegeram o mesmo vértice.
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
    -- Calcula métricas de posição e perpendicularidade só para os vencedores
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
        m.""Loc""
    FROM melhor_parada_por_vertice m
    CROSS JOIN ""Itinerarios"" i
    WHERE i.""Id"" = @itinerarioId
)
SELECT
    ""ParadaId"",
    ""PosicaoLinha"",
    ""DistanciaVerticeMetros"",
    ""DistanciaLinhaMetros"",

    -- Componente perpendicular: detecta paradas no lado oposto da via
    ABS(
        (ST_X(""PontoAdiante"") - ST_X(""PontoProj"")) * (ST_Y(""Loc"") - ST_Y(""PontoProj""))
      - (ST_Y(""PontoAdiante"") - ST_Y(""PontoProj"")) * (ST_X(""Loc"") - ST_X(""PontoProj""))
    ) / NULLIF(ST_Distance(""PontoProj"", ""PontoAdiante""), 0)                 AS ""DistanciaPerp""

FROM enriquecido
ORDER BY ""PosicaoLinha"" ASC;";

            AddParam(cmd, "@itinerarioId", itinerarioId);
            AddParam(cmd, "@distanciaMaxima", config.DistanciaMaximaMetros);

            await using var leitor = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await leitor.ReadAsync(cancellationToken))
            {
                // col 0: ParadaId
                // col 1: PosicaoLinha
                // col 2: DistanciaVerticeMetros  ← critério principal (vértice GPS real)
                // col 3: DistanciaLinhaMetros    ← usado no score como métrica secundária
                // col 4: DistanciaPerp           ← detecta lado oposto da rua
                var distanciaPerp = leitor.IsDBNull(4) ? 0.0 : leitor.GetDouble(4);

                resultados.Add(new ParadaCandidato
                {
                    ParadaId                = leitor.GetFieldValue<Guid>(0),
                    PosicaoLinha            = leitor.GetDouble(1),
                    DistanciaVerticeMetros  = leitor.GetDouble(2),
                    DistanciaLinhaMetros    = leitor.GetDouble(3),
                    DistanciaPerp           = distanciaPerp
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

    // -------------------------------------------------------------------------
    // Filtragem em memória: aplica regras de qualidade e monta sequência final
    // -------------------------------------------------------------------------
    private List<ParadaCandidato> FiltrarEOrdenar(
        List<ParadaCandidato> candidatos,
        Configuracoes config)
    {
        // 1. Deduplica: para cada ParadaId, mantém apenas o candidato
        //    com menor distância (pode aparecer duplicado se a linha passar perto 2x)
        var melhoresPorParada = candidatos
            .GroupBy(c => c.ParadaId)
            .Select(g => g.OrderBy(c => c.DistanciaVerticeMetros).First())
            .ToList();

        var selecionados = new List<ParadaCandidato>();

        foreach (var candidato in melhoresPorParada.OrderBy(c => c.PosicaoLinha))
        {
            var ehTerminal = candidato.PosicaoLinha < config.LimiteTerminalInicio
                          || candidato.PosicaoLinha > config.LimiteTerminalFim;

            // Critério principal: vértice GPS real mais próximo deve estar dentro do raio.
            // Terminais usam raio reduzido para evitar explosão de candidatos.
            var distanciaLimite = ehTerminal
                ? config.DistanciaMaximaMetros * config.FatorRaioTerminal
                : config.DistanciaMaximaMetros;

            if (candidato.DistanciaVerticeMetros > distanciaLimite)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: vértice mais próximo {dist:F1}m > limite {lim:F1}m (terminal={terminal})",
                    candidato.ParadaId, candidato.DistanciaVerticeMetros, distanciaLimite, ehTerminal);
                continue;
            }

            // Filtro perpendicular: descarta paradas do lado oposto da rua.
            if (candidato.DistanciaPerp > config.DistanciaPerpMaxMetros)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: distância perpendicular {perp:F1}m > {max:F1}m",
                    candidato.ParadaId, candidato.DistanciaPerp, config.DistanciaPerpMaxMetros);
                continue;
            }

            // Score composto (0.0–1.0):
            //   60% baseado no vértice GPS real (critério físico mais confiável)
            //   40% baseado no alinhamento lateral (penaliza lado oposto da rua)
            var scoreVertice       = 1.0 - (candidato.DistanciaVerticeMetros / config.DistanciaMaximaMetros);
            var scorePerpendicular = 1.0 - Math.Min(candidato.DistanciaPerp / config.DistanciaPerpMaxMetros, 1.0);
            candidato.Score = config.PesoDistancia * scoreVertice
                            + config.PesoPerpendicular * scorePerpendicular;

            if (candidato.Score < config.ScoreMinimo)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: score {score:F3} < mínimo {min:F3}",
                    candidato.ParadaId, candidato.Score, config.ScoreMinimo);
                continue;
            }

            selecionados.Add(candidato);
        }

        // 5. Validação de sequência monótona:
        //    Remove paradas que "regridem" na linha (possível falso positivo)
        return FiltrarSequenciaConsistente(selecionados, config.SaltoMaximoPosicao);
    }

    private List<ParadaCandidato> FiltrarSequenciaConsistente(
        List<ParadaCandidato> paradas,
        double saltoMaximo)
    {
        var resultado = new List<ParadaCandidato>();
        var posicaoAnterior = -1.0;

        foreach (var parada in paradas) // já vem ordenado por PosicaoLinha
        {
            if (parada.PosicaoLinha < posicaoAnterior)
            {
                _logger.LogDebug(
                    "Parada {id} descartada: regressão de posição {ant:F4} → {atual:F4}",
                    parada.ParadaId, posicaoAnterior, parada.PosicaoLinha);
                continue;
            }

            if (posicaoAnterior >= 0 && (parada.PosicaoLinha - posicaoAnterior) > saltoMaximo)
            {
                _logger.LogWarning(
                    "Parada {id}: salto grande de posição {ant:F4} → {atual:F4} (>{max:F4})",
                    parada.ParadaId, posicaoAnterior, parada.PosicaoLinha, saltoMaximo);
                // Mantém — pode ser lacuna legítima (ex: trecho sem paradas)
            }

            resultado.Add(parada);
            posicaoAnterior = parada.PosicaoLinha;
        }

        return resultado;
    }

    // -------------------------------------------------------------------------
    // Persistência em lotes (sem alterações de lógica)
    // -------------------------------------------------------------------------
    private async Task<int> SalvarRelacoesEmLotesAsync(
        List<ParadaItinerario> relacoes,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (relacoes.Count == 0)
            return 0;

        var totalCriadas = 0;

        for (var i = 0; i < relacoes.Count; i += tamanhoLote)
        {
            var lote = relacoes.Skip(i).Take(tamanhoLote).ToList();
            _contexto.ParadasItinerario.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();
            totalCriadas += lote.Count;
        }

        return totalCriadas;
    }

    // -------------------------------------------------------------------------
    // Configurações
    // -------------------------------------------------------------------------
    private Configuracoes LerConfiguracoes()
    {
        return new Configuracoes
        {
            DistanciaMaximaMetros    = LerDouble("RELACIONAMENTO:DISTANCIA_MAXIMA_METROS",
                                           "RELACIONAMENTO__DISTANCIA_MAXIMA_METROS"),

            // Distância perpendicular máxima aceita.
            // ~metade da largura de uma via de mão dupla urbana (3,5m por faixa × 2 = 7m)
            // Paradas do outro lado da rua costumam ter DistanciaPerp > 12m.
            // Valor padrão: 15m. Ajuste para baixo (10m) em corredores exclusivos,
            // para cima (20m) em vias muito largas.
            DistanciaPerpMaxMetros   = LerDoubleOpcional("RELACIONAMENTO:DISTANCIA_PERP_MAX_METROS", 15.0),

            // Nos terminais (primeiros/últimos X% da rota), o raio efetivo
            // é reduzido por este fator para evitar explosão de candidatos.
            // 0.6 = usa 60% do raio normal no terminal.
            FatorRaioTerminal        = LerDoubleOpcional("RELACIONAMENTO:FATOR_RAIO_TERMINAL", 0.6),

            // Define o que é "terminal": posição < 3% ou > 97% da linha.
            LimiteTerminalInicio     = LerDoubleOpcional("RELACIONAMENTO:LIMITE_TERMINAL_INICIO", 0.03),
            LimiteTerminalFim        = LerDoubleOpcional("RELACIONAMENTO:LIMITE_TERMINAL_FIM", 0.97),

            // Score mínimo para aceitar a parada (0.0–1.0).
            // 0.4 é conservador; aumente para 0.5–0.6 se ainda houver falsos positivos.
            ScoreMinimo              = LerDoubleOpcional("RELACIONAMENTO:SCORE_MINIMO", 0.4),

            // Pesos do score composto (devem somar 1.0)
            PesoDistancia            = LerDoubleOpcional("RELACIONAMENTO:PESO_DISTANCIA", 0.5),
            PesoPerpendicular        = LerDoubleOpcional("RELACIONAMENTO:PESO_PERPENDICULAR", 0.5),

            // Salto máximo de PosicaoLinha entre paradas consecutivas antes de logar aviso.
            // 0.20 = 20% da linha. Não descarta, só avisa.
            SaltoMaximoPosicao       = LerDoubleOpcional("RELACIONAMENTO:SALTO_MAXIMO_POSICAO", 0.20),

            TamanhoLote              = LerInt("IMPORT:BATCH_SIZE", "IMPORT__BATCH_SIZE")
        };
    }

    private double LerDouble(string chave, string mensagemErro)
    {
        var valor = _configuration[chave];
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;
        throw new InvalidOperationException($"Variável {mensagemErro} não configurada ou inválida.");
    }

    private double LerDoubleOpcional(string chave, double padrao)
    {
        var valor = _configuration[chave];
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;
        return padrao;
    }

    private int LerInt(string chave, string mensagemErro)
    {
        var valor = _configuration[chave];
        if (int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;
        throw new InvalidOperationException($"Variável {mensagemErro} não configurada ou inválida.");
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string nome, object valor)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = nome;
        p.Value = valor;
        cmd.Parameters.Add(p);
    }

    // -------------------------------------------------------------------------
    // Tipos internos
    // -------------------------------------------------------------------------
    private sealed class ParadaCandidato
    {
        public required Guid   ParadaId               { get; init; }
        public required double PosicaoLinha            { get; init; }
        public required double DistanciaVerticeMetros  { get; init; }  // vértice GPS real mais próximo
        public required double DistanciaLinhaMetros    { get; init; }  // linha interpolada (score secundário)
        public required double DistanciaPerp           { get; init; }
        public          double Score                   { get; set; }
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