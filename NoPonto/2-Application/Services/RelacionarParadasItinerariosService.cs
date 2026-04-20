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

        var distanciaMaximaMetros = LerDistanciaMaximaMetros();
        var tamanhoLote = LerTamanhoLote();
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

            var paradasProximas = await BuscarParadasProximasAsync(
                itinerarioId,
                distanciaMaximaMetros,
                cancellationToken);

            _logger.LogInformation(
                "Itinerário {id} - Paradas encontradas: {qtd}",
                itinerarioId,
                paradasProximas.Count);

            if (paradasProximas.Count == 0)
            {
                continue;
            }

            var paradaIds = paradasProximas
                .Select(parada => parada.ParadaId)
                .Distinct()
                .ToList();

            var paradasJaRelacionadas = await _contexto.ParadasItinerario
                .AsNoTracking()
                .Where(relacao => relacao.ItinerarioId == itinerarioId && paradaIds.Contains(relacao.ParadaId))
                .Select(relacao => relacao.ParadaId)
                .ToListAsync(cancellationToken);

            var paradaIdsRelacionadas = new HashSet<Guid>(paradasJaRelacionadas);
            var relacoesNovas = new List<ParadaItinerario>();

            var ordem = 0;

            foreach (var paradaProxima in paradasProximas.OrderBy(parada => parada.PosicaoLinha))
            {
                ordem++;

                if (!paradaIdsRelacionadas.Add(paradaProxima.ParadaId))
                {
                    continue;
                }

                relacoesNovas.Add(new ParadaItinerario
                {
                    Id = Guid.NewGuid(),
                    ParadaId = paradaProxima.ParadaId,
                    ItinerarioId = itinerarioId,
                    Ordem = ordem,
                    PosicaoLinha = paradaProxima.PosicaoLinha,
                    DistanciaMetros = paradaProxima.DistanciaMetros
                });
            }

            var criadasNoItinerario = await SalvarRelacoesEmLotesAsync(
                relacoesNovas,
                tamanhoLote,
                cancellationToken);

            _logger.LogInformation(
                "Itinerário {id} - Relações criadas: {qtd}",
                itinerarioId,
                criadasNoItinerario);

            totalGeralRelacoesCriadas += criadasNoItinerario;
        }

        cronometro.Stop();

        _logger.LogInformation("Total de itinerários processados: {total}", totalItinerariosProcessados);
        _logger.LogInformation("Total geral de relações criadas: {total}", totalGeralRelacoesCriadas);
        _logger.LogInformation(
            "Tempo total do relacionamento: {segundos} segundos",
            cronometro.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    private async Task<List<ParadaProximidadeResultado>> BuscarParadasProximasAsync(
        Guid itinerarioId,
        double distanciaMaximaMetros,
        CancellationToken cancellationToken)
    {
        var resultados = new List<ParadaProximidadeResultado>();
        var conexao = _contexto.Database.GetDbConnection();
        var deveFecharConexao = false;

        if (conexao.State != ConnectionState.Open)
        {
            await conexao.OpenAsync(cancellationToken);
            deveFecharConexao = true;
        }

        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = @"
SELECT
    p.""Id"" AS ""ParadaId"",
    ST_LineLocatePoint(i.""Geometria"", p.""Localizacao"") AS ""PosicaoLinha"",
    ST_Distance(p.""Localizacao""::geography, i.""Geometria""::geography) AS ""DistanciaMetros""
FROM ""Paradas"" p
INNER JOIN ""Itinerarios"" i ON i.""Id"" = @itinerarioId
WHERE ST_DWithin(p.""Localizacao""::geography, i.""Geometria""::geography, @distanciaMaxima)
ORDER BY ST_LineLocatePoint(i.""Geometria"", p.""Localizacao"") ASC;";

            var parametroItinerario = comando.CreateParameter();
            parametroItinerario.ParameterName = "@itinerarioId";
            parametroItinerario.Value = itinerarioId;
            comando.Parameters.Add(parametroItinerario);

            var parametroDistancia = comando.CreateParameter();
            parametroDistancia.ParameterName = "@distanciaMaxima";
            parametroDistancia.Value = distanciaMaximaMetros;
            comando.Parameters.Add(parametroDistancia);

            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);

            while (await leitor.ReadAsync(cancellationToken))
            {
                var paradaId = leitor.GetFieldValue<Guid>(0);
                var posicaoLinha = leitor.GetDouble(1);
                var distanciaMetros = leitor.GetDouble(2);

                resultados.Add(new ParadaProximidadeResultado
                {
                    ParadaId = paradaId,
                    PosicaoLinha = posicaoLinha,
                    DistanciaMetros = distanciaMetros
                });
            }
        }
        finally
        {
            if (deveFecharConexao)
            {
                await conexao.CloseAsync();
            }
        }

        return resultados;
    }

    private async Task<int> SalvarRelacoesEmLotesAsync(
        List<ParadaItinerario> relacoes,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        if (relacoes.Count == 0)
        {
            return 0;
        }

        var totalCriadas = 0;

        for (var indice = 0; indice < relacoes.Count; indice += tamanhoLote)
        {
            var lote = relacoes
                .Skip(indice)
                .Take(tamanhoLote)
                .ToList();

            _contexto.ParadasItinerario.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();

            totalCriadas += lote.Count;
        }

        return totalCriadas;
    }

    private double LerDistanciaMaximaMetros()
    {
        var valorConfigurado = _configuration["RELACIONAMENTO:DISTANCIA_MAXIMA_METROS"];

        if (double.TryParse(valorConfigurado, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor)
            && valor > 0)
        {
            return valor;
        }

        throw new InvalidOperationException("Variável RELACIONAMENTO__DISTANCIA_MAXIMA_METROS não configurada ou inválida.");
    }

    private int LerTamanhoLote()
    {
        var valorConfigurado = _configuration["IMPORT:BATCH_SIZE"];

        if (int.TryParse(valorConfigurado, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor)
            && valor > 0)
        {
            return valor;
        }

        throw new InvalidOperationException("Variável IMPORT__BATCH_SIZE não configurada ou inválida.");
    }

    private sealed class ParadaProximidadeResultado
    {
        public required Guid ParadaId { get; init; }
        public required double PosicaoLinha { get; init; }
        public required double DistanciaMetros { get; init; }
    }
}
