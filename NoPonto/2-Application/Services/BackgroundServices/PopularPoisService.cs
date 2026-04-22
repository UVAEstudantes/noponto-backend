// NoPonto.Application.Services/PopularPoisService.cs
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
        _contexto       = contexto;
        _overpass       = overpass;
        _poiRepository  = poiRepository;
        _configuration  = configuration;
        _logger         = logger;
    }

    // -------------------------------------------------------------------------
    // Resultado público
    // -------------------------------------------------------------------------
    public sealed class ResultadoParada
    {
        public Guid   ParadaId        { get; init; }
        public bool   Encontrada      { get; init; }
        public int    PoisCandidatos  { get; init; }
        public int    PoisDescartados { get; init; }
        public int    RelacoesCriadas { get; init; }
        public long   TempoMs         { get; init; }

        public static ResultadoParada NaoEncontrada(Guid id) => new()
        {
            ParadaId   = id,
            Encontrada = false
        };
    }

    // -------------------------------------------------------------------------
    // Processa todas as paradas que têm pelo menos um itinerário associado
    // -------------------------------------------------------------------------
    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        var itinerarioIds = await _contexto.Itinerarios
            .AsNoTracking()
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Iniciando população de POIs para {qtd} itinerários", itinerarioIds.Count);

        var totalRelacoes = 0;
        foreach (var itinerarioId in itinerarioIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultado = await ExecutarParaItinerarioAsync(itinerarioId, cancellationToken);
            totalRelacoes += resultado.TotalRelacoesCriadas;

            // Pausa entre itinerários para não bater rate limit da Overpass
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        sw.Stop();
        _logger.LogInformation(
            "População concluída. Total relações: {rel}. Tempo: {s}s",
            totalRelacoes,
            sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    // -------------------------------------------------------------------------
    // Processa uma parada específica (exposto para o endpoint individual)
    // -------------------------------------------------------------------------
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

    // PopularPoisService.cs — novo método público
    public async Task<ResultadoItinerario> ExecutarParaItinerarioAsync(
        Guid itinerarioId, CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var config = LerConfiguracoes();

        // Busca paradas do itinerário já com localização, ordenadas pela sequência
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
            return new ResultadoItinerario
            {
                ItinerarioId = itinerarioId,
                Encontrado   = false
            };

        // Uma única bbox cobrindo todas as paradas + margem do raio configurado
        var margem = config.DistanciaMaximaMetros / 111_000.0;
        var sul    = paradas.Min(p => p.Lat) - margem;
        var norte  = paradas.Max(p => p.Lat) + margem;
        var oeste  = paradas.Min(p => p.Lon) - margem;
        var leste  = paradas.Max(p => p.Lon) + margem;

        // Uma única request Overpass para o itinerário inteiro
        var poisOsm = await _overpass.BuscarNaAreaAsync(sul, oeste, norte, leste, cancellationToken);

        if (poisOsm.Count == 0)
        {
            sw.Stop();
            return new ResultadoItinerario
            {
                ItinerarioId = itinerarioId,
                Encontrado   = true,
                TotalParadas = paradas.Count,
                TempoMs      = (long)sw.Elapsed.TotalMilliseconds
            };
        }

        // Upsert dos POIs na tabela (uma única passagem)
        var poisSalvos = await _poiRepository.UpsertPoisAsync(poisOsm, config.TamanhoLote, cancellationToken);

        // Distribui POIs para paradas em memória:
        // Para cada POI, encontra a parada mais próxima dentro do raio.
        // Garante que cada POI aparece ligado a no máximo uma parada neste itinerário.
        var paradaIds   = paradas.Select(p => p.ParadaId).ToList();
        var poiIds      = poisSalvos.Select(p => p.Id).ToList();

        // Carrega relações já existentes para todas as paradas deste itinerário de uma vez
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

        foreach (var poi in poisSalvos)
        {
            var melhorDistancia = double.MaxValue;
            Guid melhorParadaId = Guid.Empty;

            foreach (var parada in paradas)
            {
                var dist = HaversineMetros(parada.Lat, parada.Lon, poi.Localizacao.Y, poi.Localizacao.X);
                if (dist < melhorDistancia && dist <= config.DistanciaMaximaMetros)
                {
                    melhorDistancia = dist;
                    melhorParadaId  = parada.ParadaId;
                }
            }

            if (melhorParadaId != Guid.Empty)
                melhorParadaPorPoi[poi.Id] = (melhorParadaId, melhorDistancia);
        }

        var novasRelacoes      = new List<PoiParada>();
        var relacoesPraRemover = new List<Guid>();
        var descartados        = poisSalvos.Count - melhorParadaPorPoi.Count;

        foreach (var (poiId, (paradaId, distancia)) in melhorParadaPorPoi)
        {
            // Já existe relação para este POI em alguma parada deste itinerário?
            if (existentesPorPoi.TryGetValue(poiId, out var existente))
            {
                if (existente.ParadaId == paradaId)
                    continue; // já está na parada certa

                if (existente.DistanciaMetros <= distancia)
                    continue; // a relação existente já é mais próxima

                // Esta parada é mais próxima — substitui
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
            "Itinerário {id} — paradas: {p}, POIs candidatos: {c}, descartados: {d}, relações criadas: {r}",
            itinerarioId, paradas.Count, poisSalvos.Count, descartados, novasRelacoes.Count);

        return new ResultadoItinerario
        {
            ItinerarioId         = itinerarioId,
            Encontrado           = true,
            TotalParadas         = paradas.Count,
            TotalPoisCandidatos  = poisSalvos.Count,
            TotalPoisDescartados = descartados,
            TotalRelacoesCriadas = novasRelacoes.Count,
            TempoMs              = (long)sw.Elapsed.TotalMilliseconds
        };
    }

    // Adiciona junto com ResultadoParada no mesmo arquivo
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

    // -------------------------------------------------------------------------
    // Núcleo: processa uma parada
    // -------------------------------------------------------------------------
    private async Task<ResultadoParada> ProcessarParadaAsync(
        Guid paradaId, Configuracoes config, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var parada = await _contexto.Paradas
            .AsNoTracking()
            .Where(p => p.Id == paradaId)
            .Select(p => new { p.Id, p.Localizacao })
            .FirstOrDefaultAsync(cancellationToken);

        if (parada is null)
            return ResultadoParada.NaoEncontrada(paradaId);

        var lat = parada.Localizacao.Y;
        var lon = parada.Localizacao.X;

        // Bounding box quadrada em graus ao redor da parada
        var margem = config.DistanciaMaximaMetros / 111_000.0;

        var poisOsm = await _overpass.BuscarNaAreaAsync(
            sul:   lat - margem,
            oeste: lon - margem,
            norte: lat + margem,
            leste: lon + margem,
            cancellationToken);

        if (poisOsm.Count == 0)
            return new ResultadoParada { ParadaId = paradaId, Encontrada = true };

        // Upsert na tabela Pois (sem duplicar por nome+categoria)
        var poisSalvos = await _poiRepository.UpsertPoisAsync(poisOsm, config.TamanhoLote, cancellationToken);

        // Filtra pelo raio real (Haversine) e garante unicidade por itinerário
        var (criadas, descartados) = await RelacionarComParadaAsync(
            paradaId, lat, lon, poisSalvos, config, cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Parada {id} — candidatos: {c}, descartados: {d}, relações criadas: {r}",
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

    // -------------------------------------------------------------------------
    // Relacionamento POI → Parada
    //
    // Regra de unicidade por itinerário:
    //   Um POI deve aparecer ligado a no máximo uma parada por itinerário —
    //   a parada mais próxima dele. Para garantir isso:
    //
    //   1. Busca os itinerários que passam por esta parada.
    //   2. Para cada POI candidato, verifica se já existe uma relação
    //      com qualquer outra parada do mesmo itinerário.
    //   3. Se existir e a distância anterior for menor, descarta.
    //      Se esta parada for mais próxima, remove a relação anterior e cria aqui.
    // -------------------------------------------------------------------------
    private async Task<(int criadas, int descartados)> RelacionarComParadaAsync(
        Guid paradaId,
        double lat,
        double lon,
        List<Poi> candidatos,
        Configuracoes config,
        CancellationToken cancellationToken)
    {
        // Itinerários que passam por esta parada
        var itinerarioIds = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .Select(r => r.ItinerarioId)
            .ToListAsync(cancellationToken);

        // POIs já ligados a esta parada (para não duplicar)
        var jaNestaParada = await _poiRepository.BuscarPoisJaRelacionadosNaParadaAsync(paradaId, cancellationToken);

        // Para cada itinerário que passa aqui, quais POIs já têm relação com OUTRA parada?
        // Estrutura: dicionário poiId → (paradaId existente, distância existente)
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
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.DistanciaMetros).First()); // pega a mais próxima já existente

        var novasRelacoes      = new List<PoiParada>();
        var relacoesPraRemover = new List<Guid>(); // ids de PoiParada a substituir
        var descartados        = 0;

        foreach (var poi in candidatos)
        {
            // Distância real em metros desta parada até o POI
            var distancia = HaversineMetros(lat, lon, poi.Localizacao.Y, poi.Localizacao.X);

            if (distancia > config.DistanciaMaximaMetros)
            {
                descartados++;
                continue;
            }

            // Já está ligado a esta mesma parada — pula
            if (jaNestaParada.Contains(poi.Id))
                continue;

            // Verifica se já existe relação com outra parada do mesmo itinerário
            if (concorrentesPorPoi.TryGetValue(poi.Id, out var existente))
            {
                if (existente.DistanciaMetros <= distancia)
                {
                    // A parada existente já é mais próxima — descarta
                    descartados++;
                    continue;
                }

                // Esta parada é mais próxima — remove a relação anterior
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

        // Remove relações substituídas
        if (relacoesPraRemover.Count > 0)
        {
            await _contexto.PoiParadas
                .Where(r => relacoesPraRemover.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await _poiRepository.InserirRelacaoEmLoteAsync(novasRelacoes, config.TamanhoLote, cancellationToken);

        return (novasRelacoes.Count, descartados);
    }

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

    // -------------------------------------------------------------------------
    // Configurações
    // -------------------------------------------------------------------------
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