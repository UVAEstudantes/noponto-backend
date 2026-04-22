using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Data.Interfaces;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class PopularPoisService
{
    private readonly TransporteDbContext _contexto;
    private readonly OverpassClient _overpass;
    private readonly IPoiRepository _poiRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PopularPoisService> _logger;

    public PopularPoisService(
        TransporteDbContext contexto,
        OverpassClient overpass,
        IPoiRepository poiRepository,
        IConfiguration configuration,
        ILogger<PopularPoisService> logger)
    {
        _contexto      = contexto;
        _overpass      = overpass;
        _poiRepository = poiRepository;
        _configuration = configuration;
        _logger        = logger;
    }

    // =========================================================================
    // Resultados públicos
    // =========================================================================

    public sealed class ResultadoParada
    {
        public Guid ParadaId        { get; init; }
        public bool Encontrada      { get; init; }
        public int  PoisCandidatos  { get; init; }
        public int  PoisDescartados { get; init; }
        public int  RelacoesCriadas { get; init; }
        public long TempoMs         { get; init; }

        public static ResultadoParada NaoEncontrada(Guid id) => new() { ParadaId = id, Encontrada = false };
    }

    public sealed class ResultadoItinerario
    {
        public Guid ItinerarioId         { get; init; }
        public bool Encontrado           { get; init; }
        public int  TotalParadas         { get; init; }
        public int  TotalPoisCandidatos  { get; init; }
        public int  TotalPoisDescartados { get; init; }
        public int  TotalRelacoesCriadas { get; init; }
        public long TempoMs              { get; init; }
    }

    // =========================================================================
    // FASE 1 — Importação OSM em tiles
    //
    // Divide o mapa em células de ~3km × 3km e faz UMA request Overpass por tile.
    // Para o Rio de Janeiro (~25km × 20km) gera ~60–80 tiles em vez de 800+
    // requests (uma por itinerário). Resultado: minutos em vez de horas.
    // =========================================================================
    public async Task ImportarPoisOsmAsync(CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        // Bounding box de todas as paradas cadastradas
        var bbox = await _contexto.Paradas
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Sul   = g.Min(p => p.Localizacao.Y),
                Norte = g.Max(p => p.Localizacao.Y),
                Oeste = g.Min(p => p.Localizacao.X),
                Leste = g.Max(p => p.Localizacao.X),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (bbox is null)
        {
            _logger.LogWarning("Nenhuma parada encontrada. Importação OSM cancelada.");
            return;
        }

        // Tile de 0.027° ≈ 3km — mantém cada query pequena e rápida
        const double tamTile = 0.027;

        var tiles = new List<(double Sul, double Oeste, double Norte, double Leste)>();
        for (var lat = bbox.Sul; lat < bbox.Norte; lat += tamTile)
        for (var lon = bbox.Oeste; lon < bbox.Leste; lon += tamTile)
            tiles.Add((lat, lon,
                       Math.Min(lat + tamTile, bbox.Norte),
                       Math.Min(lon + tamTile, bbox.Leste)));

        _logger.LogInformation(
            "Importação OSM — {qtd} tiles de {tam}° (~3km). Bbox: S{s:F4} O{o:F4} N{n:F4} L{l:F4}",
            tiles.Count, tamTile, bbox.Sul, bbox.Oeste, bbox.Norte, bbox.Leste);

        var totalNovos = 0;
        for (var i = 0; i < tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (sul, oeste, norte, leste) = tiles[i];
            var poisOsm = await _overpass.BuscarNaAreaAsync(sul, oeste, norte, leste, cancellationToken);

            // UpsertPoisAsync só insere os que ainda não existem (por nome+categoria na bbox)
            var salvos = await _poiRepository.UpsertPoisAsync(poisOsm, config.TamanhoLote, cancellationToken);
            totalNovos += salvos.Count;

            _logger.LogInformation(
                "Tile {i}/{total} — OSM: {osm} POIs retornados, {novos} no banco",
                i + 1, tiles.Count, poisOsm.Count, salvos.Count);
        }

        sw.Stop();
        _logger.LogInformation(
            "Importação OSM concluída. Tiles: {tiles}. Banco: {novos} POIs. Tempo: {s}s",
            tiles.Count, totalNovos,
            sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    // =========================================================================
    // FASE 2 — Matching local (sem nenhuma request HTTP)
    //
    // Lê os POIs já importados no banco e distribui para as paradas por distância.
    // 813 itinerários em ~2 minutos (contra horas com Overpass por itinerário).
    // =========================================================================
    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        var itinerarioIds = await _contexto.Itinerarios
            .AsNoTracking()
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "Matching POIs → paradas para {qtd} itinerários (sem Overpass)",
            itinerarioIds.Count);

        var totalRelacoes = 0;
        var concluidos    = 0;

        foreach (var itinerarioId in itinerarioIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resultado = await ExecutarParaItinerarioAsync(itinerarioId, cancellationToken);
            totalRelacoes += resultado.TotalRelacoesCriadas;
            concluidos++;

            if (concluidos % 50 == 0)
                _logger.LogInformation("Matching: {ok}/{total}", concluidos, itinerarioIds.Count);
        }

        sw.Stop();
        _logger.LogInformation(
            "Matching concluído. Relações criadas: {rel}. Tempo: {s}s",
            totalRelacoes,
            sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    // =========================================================================
    // Matching de um único itinerário (usado pelo endpoint individual e pelo ExecutarAsync)
    // =========================================================================
    public async Task<ResultadoItinerario> ExecutarParaItinerarioAsync(
        Guid itinerarioId, CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        var paradas = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ItinerarioId == itinerarioId)
            .OrderBy(r => r.Ordem)
            .Select(r => new
            {
                r.ParadaId,
                r.Ordem,
                Lat = r.Parada.Localizacao.Y,
                Lon = r.Parada.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        if (paradas.Count == 0)
            return new ResultadoItinerario { ItinerarioId = itinerarioId, Encontrado = false };

        // Busca POIs do banco dentro da bbox do itinerário — SEM Overpass
        var margem = config.DistanciaMaximaMetros / 111_000.0;
        var sul    = paradas.Min(p => p.Lat) - margem;
        var norte  = paradas.Max(p => p.Lat) + margem;
        var oeste  = paradas.Min(p => p.Lon) - margem;
        var leste  = paradas.Max(p => p.Lon) + margem;

        var poisNaArea = await _contexto.Pois
            .AsNoTracking()
            .Where(p =>
                p.Localizacao.Y >= sul   && p.Localizacao.Y <= norte &&
                p.Localizacao.X >= oeste && p.Localizacao.X <= leste)
            .Select(p => new { p.Id, Lat = p.Localizacao.Y, Lon = p.Localizacao.X })
            .ToListAsync(cancellationToken);

        if (poisNaArea.Count == 0)
            return new ResultadoItinerario
            {
                ItinerarioId = itinerarioId,
                Encontrado   = true,
                TotalParadas = paradas.Count,
                TempoMs      = (long)sw.Elapsed.TotalMilliseconds
            };

        // Carrega relações já existentes de uma vez para evitar queries por POI
        var paradaIds    = paradas.Select(p => p.ParadaId).ToList();
        var poiIds       = poisNaArea.Select(p => p.Id).ToList();
        var jaExistentes = await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => paradaIds.Contains(r.ParadaId) && poiIds.Contains(r.PoiId))
            .Select(r => new { r.Id, r.PoiId, r.ParadaId, r.DistanciaMetros })
            .ToListAsync(cancellationToken);

        var existentesPorPoi = jaExistentes
            .GroupBy(r => r.PoiId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.DistanciaMetros).First());

        // Para cada POI: qual parada do itinerário está mais próxima dentro do raio?
        var melhorParadaPorPoi = new Dictionary<Guid, (Guid paradaId, double distancia)>();

        foreach (var poi in poisNaArea)
        {
            var melhorDist   = double.MaxValue;
            var melhorParada = Guid.Empty;

            foreach (var parada in paradas)
            {
                var dist = HaversineMetros(parada.Lat, parada.Lon, poi.Lat, poi.Lon);
                if (dist < melhorDist && dist <= config.DistanciaMaximaMetros)
                {
                    melhorDist   = dist;
                    melhorParada = parada.ParadaId;
                }
            }

            if (melhorParada != Guid.Empty)
                melhorParadaPorPoi[poi.Id] = (melhorParada, melhorDist);
        }

        var novasRelacoes      = new List<PoiParada>();
        var relacoesPraRemover = new List<Guid>();
        var descartados        = poisNaArea.Count - melhorParadaPorPoi.Count;

        foreach (var (poiId, (paradaId, distancia)) in melhorParadaPorPoi)
        {
            if (existentesPorPoi.TryGetValue(poiId, out var existente))
            {
                if (existente.ParadaId == paradaId) continue;
                if (existente.DistanciaMetros <= distancia) continue;
                relacoesPraRemover.Add(existente.Id);
            }

            novasRelacoes.Add(new PoiParada
            {
                Id              = Guid.NewGuid(),
                PoiId           = poiId,
                ParadaId        = paradaId,
                DistanciaMetros = distancia
            });
        }

        if (relacoesPraRemover.Count > 0)
            await _contexto.PoiParadas
                .Where(r => relacoesPraRemover.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);

        await _poiRepository.InserirRelacaoEmLoteAsync(novasRelacoes, config.TamanhoLote, cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Itinerário {id} — paradas: {p}, candidatos: {c}, descartados: {d}, relações: {r}",
            itinerarioId, paradas.Count, poisNaArea.Count, descartados, novasRelacoes.Count);

        return new ResultadoItinerario
        {
            ItinerarioId         = itinerarioId,
            Encontrado           = true,
            TotalParadas         = paradas.Count,
            TotalPoisCandidatos  = poisNaArea.Count,
            TotalPoisDescartados = descartados,
            TotalRelacoesCriadas = novasRelacoes.Count,
            TempoMs              = (long)sw.Elapsed.TotalMilliseconds
        };
    }

    // =========================================================================
    // Parada individual — útil para debug/calibração (ainda usa Overpass)
    // =========================================================================
    public async Task<ResultadoParada> ExecutarParaParadaAsync(
        Guid paradaId, CancellationToken cancellationToken = default)
    {
        var config = LerConfiguracoes();

        var existe = await _contexto.Paradas
            .AsNoTracking()
            .AnyAsync(p => p.Id == paradaId, cancellationToken);

        if (!existe)
            return ResultadoParada.NaoEncontrada(paradaId);

        return await ProcessarParadaAsync(paradaId, config, cancellationToken);
    }

    private async Task<ResultadoParada> ProcessarParadaAsync(
        Guid paradaId, Configuracoes config, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var parada = await _contexto.Paradas
            .AsNoTracking()
            .Where(p => p.Id == paradaId)
            .Select(p => new { p.Id, Lat = p.Localizacao.Y, Lon = p.Localizacao.X })
            .FirstOrDefaultAsync(cancellationToken);

        if (parada is null)
            return ResultadoParada.NaoEncontrada(paradaId);

        var margem = config.DistanciaMaximaMetros / 111_000.0;

        var poisOsm = await _overpass.BuscarNaAreaAsync(
            parada.Lat - margem, parada.Lon - margem,
            parada.Lat + margem, parada.Lon + margem,
            cancellationToken);

        if (poisOsm.Count == 0)
            return new ResultadoParada { ParadaId = paradaId, Encontrada = true };

        var poisSalvos = await _poiRepository.UpsertPoisAsync(poisOsm, config.TamanhoLote, cancellationToken);

        var (criadas, descartados) = await RelacionarComParadaAsync(
            paradaId, parada.Lat, parada.Lon, poisSalvos, config, cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Parada {id} — candidatos: {c}, descartados: {d}, relações: {r}",
            paradaId, poisSalvos.Count, descartados, criadas);

        return new ResultadoParada
        {
            ParadaId        = paradaId,
            Encontrada      = true,
            PoisCandidatos  = poisSalvos.Count,
            PoisDescartados = descartados,
            RelacoesCriadas = criadas,
            TempoMs         = (long)sw.Elapsed.TotalMilliseconds
        };
    }

    private async Task<(int criadas, int descartados)> RelacionarComParadaAsync(
        Guid paradaId, double lat, double lon,
        List<Poi> candidatos, Configuracoes config,
        CancellationToken cancellationToken)
    {
        var itinerarioIds = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .Select(r => r.ItinerarioId)
            .ToListAsync(cancellationToken);

        var jaNestaParada = await _poiRepository
            .BuscarPoisJaRelacionadosNaParadaAsync(paradaId, cancellationToken);

        var poiIds = candidatos.Select(p => p.Id).ToList();

        var relacoesConcorrentes = await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r =>
                poiIds.Contains(r.PoiId) &&
                _contexto.ParadasItinerario
                    .Where(pi => pi.ParadaId == r.ParadaId && itinerarioIds.Contains(pi.ItinerarioId))
                    .Any())
            .Select(r => new { r.Id, r.PoiId, r.ParadaId, r.DistanciaMetros })
            .ToListAsync(cancellationToken);

        var concorrentesPorPoi = relacoesConcorrentes
            .GroupBy(r => r.PoiId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.DistanciaMetros).First());

        var novasRelacoes      = new List<PoiParada>();
        var relacoesPraRemover = new List<Guid>();
        var descartados        = 0;

        foreach (var poi in candidatos)
        {
            var distancia = HaversineMetros(lat, lon, poi.Localizacao.Y, poi.Localizacao.X);

            if (distancia > config.DistanciaMaximaMetros) { descartados++; continue; }
            if (jaNestaParada.Contains(poi.Id)) continue;

            if (concorrentesPorPoi.TryGetValue(poi.Id, out var existente))
            {
                if (existente.DistanciaMetros <= distancia) { descartados++; continue; }
                relacoesPraRemover.Add(existente.Id);
            }

            novasRelacoes.Add(new PoiParada
            {
                Id              = Guid.NewGuid(),
                PoiId           = poi.Id,
                ParadaId        = paradaId,
                DistanciaMetros = distancia
            });
        }

        if (relacoesPraRemover.Count > 0)
            await _contexto.PoiParadas
                .Where(r => relacoesPraRemover.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);

        await _poiRepository.InserirRelacaoEmLoteAsync(novasRelacoes, config.TamanhoLote, cancellationToken);

        return (novasRelacoes.Count, descartados);
    }

    // =========================================================================
    // Auxiliar para o Worker
    // =========================================================================
    public async Task<List<Guid>> ListarItinerarioIdsAsync(CancellationToken cancellationToken = default)
    {
        return await _contexto.Itinerarios
            .AsNoTracking()
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    // =========================================================================
    // Haversine + Configurações
    // =========================================================================
    private static double HaversineMetros(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private Configuracoes LerConfiguracoes() => new()
    {
        DistanciaMaximaMetros = LerDoubleOpcional("POI__DISTANCIA_MAXIMA_METROS", 150.0),
        TamanhoLote           = LerInt("IMPORT__BATCH_SIZE")
    };

    private double LerDoubleOpcional(string chave, double padrao)
    {
        var valor = _configuration[chave];
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
            return r;
        return padrao;
    }

    private int LerInt(string chave)
    {
        var valor = _configuration[chave];
        if (int.TryParse(valor, out var r) && r > 0) return r;
        return 100;
    }

    private sealed class Configuracoes
    {
        public double DistanciaMaximaMetros { get; init; }
        public int    TamanhoLote           { get; init; }
    }
}