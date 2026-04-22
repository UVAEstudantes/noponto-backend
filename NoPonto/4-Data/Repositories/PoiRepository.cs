using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.Pois;
using NoPonto.Data.Interfaces;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed class PoiRepository : IPoiRepository
{
    private readonly TransporteDbContext _contexto;
    private static readonly GeometryFactory _factory =
        new GeometryFactory(new PrecisionModel(), 4326);

    public PoiRepository(TransporteDbContext contexto)
    {
        _contexto = contexto;
    }

    // -------------------------------------------------------------------------
    // Listagem geral paginada — só POIs com pelo menos uma relação PoiParada
    // -------------------------------------------------------------------------
    public async Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(
        string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _contexto.Pois
            .AsNoTracking()
            .Where(p => p.PoiParadas.Any());

        if (!string.IsNullOrWhiteSpace(nome))
            query = query.Where(p => p.Nome.ToLower().Contains(nome.ToLower()));

        var total = await query.CountAsync(cancellationToken);
        var itens = await query
            .OrderBy(p => p.Prioridade)
            .ThenBy(p => p.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PoiConsultaDTO
            {
                Id        = p.Id,
                Nome      = p.Nome,
                Categoria = p.Categoria,
                Prioridade = p.Prioridade,
                Latitude  = p.Localizacao.Y,
                Longitude = p.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        return new PaginacaoRespostaDTO<PoiConsultaDTO>
        {
            Pagina         = page,
            TamanhoPagina  = pageSize,
            TotalRegistros = total,
            TotalPaginas   = (int)Math.Ceiling(total / (double)pageSize),
            Itens          = itens
        };
    }

    // -------------------------------------------------------------------------
    // POIs de uma parada — ordenados por prioridade e depois por distância
    // -------------------------------------------------------------------------
    public async Task<List<PoiPorParadaDTO>> ListarPorParadaAsync(
        Guid paradaId, CancellationToken cancellationToken)
    {
        return await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .OrderBy(r => r.Poi.Prioridade)
            .ThenBy(r => r.DistanciaMetros)
            .Select(r => new PoiPorParadaDTO
            {
                PoiId           = r.PoiId,
                ParadaId        = r.ParadaId,
                Nome            = r.Poi.Nome,
                Categoria       = r.Poi.Categoria,
                Prioridade      = r.Poi.Prioridade,
                Latitude        = r.Poi.Localizacao.Y,
                Longitude       = r.Poi.Localizacao.X,
                DistanciaMetros = r.DistanciaMetros
            })
            .ToListAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // POIs de um itinerário — com ordem da parada na sequência da linha
    // -------------------------------------------------------------------------
    public async Task<List<PoiPorItinerarioDTO>> ListarPorItinerarioAsync(
        Guid itinerarioId, CancellationToken cancellationToken)
    {
        return await _contexto.PoiParadas
            .AsNoTracking()
            .Where(pp =>
                _contexto.ParadasItinerario
                    .Any(pi => pi.ParadaId == pp.ParadaId && pi.ItinerarioId == itinerarioId))
            .Select(pp => new PoiPorItinerarioDTO
            {
                PoiId       = pp.PoiId,
                ParadaId    = pp.ParadaId,
                OrdemParada = _contexto.ParadasItinerario
                    .Where(pi => pi.ParadaId == pp.ParadaId && pi.ItinerarioId == itinerarioId)
                    .Select(pi => pi.Ordem)
                    .FirstOrDefault(),
                NomeParada      = pp.Parada.Nome,
                Nome            = pp.Poi.Nome,
                Categoria       = pp.Poi.Categoria,
                Prioridade      = pp.Poi.Prioridade,
                Latitude        = pp.Poi.Localizacao.Y,
                Longitude       = pp.Poi.Localizacao.X,
                DistanciaMetros = pp.DistanciaMetros
            })
            .ToListAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Contagem de POIs por itinerário — para diagnóstico de cobertura
    // -------------------------------------------------------------------------
    public async Task<List<PoiContagemPorItinerarioDTO>> ListarContagemPorItinerarioAsync(
        CancellationToken cancellationToken)
    {
        // Busca todos os itinerários com seus dados de linha/sentido
        var itinerarios = await _contexto.Itinerarios
            .AsNoTracking()
            .Select(i => new
            {
                i.Id,
                LinhaId    = i.Sentido.LinhaId,
                NomeLinha  = i.Sentido.Linha.Nome,
                SentidoId  = i.SentidoId,
                NomeSentido = i.Sentido.Nome
            })
            .ToListAsync(cancellationToken);

        // Paradas de cada itinerário
        var paradaPorItinerario = await _contexto.ParadasItinerario
            .AsNoTracking()
            .Select(pi => new { pi.ItinerarioId, pi.ParadaId })
            .ToListAsync(cancellationToken);

        // Contagem de PoiParadas por ParadaId
        var contagemPorParada = await _contexto.PoiParadas
            .AsNoTracking()
            .GroupBy(pp => pp.ParadaId)
            .Select(g => new { ParadaId = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);

        // Agrupa em memória: soma os POIs de todas as paradas de cada itinerário
        var totalPorParada = contagemPorParada.ToDictionary(x => x.ParadaId, x => x.Total);

        var paradasPorItinerario = paradaPorItinerario
            .GroupBy(x => x.ItinerarioId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ParadaId).ToList());

        return itinerarios
            .Select(i =>
            {
                var paradas = paradasPorItinerario.GetValueOrDefault(i.Id, []);
                var total   = paradas.Sum(pid => totalPorParada.GetValueOrDefault(pid, 0));

                return new PoiContagemPorItinerarioDTO
                {
                    ItinerarioId = i.Id,
                    LinhaId      = i.LinhaId,
                    NomeLinha    = i.NomeLinha,
                    SentidoId    = i.SentidoId,
                    NomeSentido  = i.NomeSentido,
                    TotalPois    = total
                };
            })
            .ToList();
    }

    // -------------------------------------------------------------------------
    // POIs próximos a um ponto — busca por bbox + Haversine exato em memória
    // -------------------------------------------------------------------------
    public async Task<List<PoiConsultaDTO>> ListarPorPontoAsync(
        double latitude, double longitude, double raioMetros, CancellationToken cancellationToken)
    {
        var margem = raioMetros / 111_000.0;
        var lonMin = longitude - margem;
        var lonMax = longitude + margem;
        var latMin = latitude  - margem;
        var latMax = latitude  + margem;

        var candidatos = await _contexto.Pois
            .AsNoTracking()
            .Where(p =>
                p.PoiParadas.Any() &&
                p.Localizacao.X >= lonMin && p.Localizacao.X <= lonMax &&
                p.Localizacao.Y >= latMin && p.Localizacao.Y <= latMax)
            .Select(p => new
            {
                p.Id,
                p.Nome,
                p.Categoria,
                p.Prioridade,
                Lat = p.Localizacao.Y,
                Lon = p.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        return candidatos
            .Select(p => new
            {
                Dto = new PoiConsultaDTO
                {
                    Id         = p.Id,
                    Nome       = p.Nome,
                    Categoria  = p.Categoria,
                    Prioridade = p.Prioridade,
                    Latitude   = p.Lat,
                    Longitude  = p.Lon
                },
                Dist = HaversineMetros(latitude, longitude, p.Lat, p.Lon)
            })
            .Where(x => x.Dist <= raioMetros)
            .OrderBy(x => x.Dist)
            .Select(x => x.Dto)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Upsert de POIs — busca existentes por bbox para não carregar o banco todo
    // -------------------------------------------------------------------------
    public async Task<List<Poi>> UpsertPoisAsync(
        IEnumerable<PoiImportadoDTO> importados,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        var lista = importados.ToList();
        if (lista.Count == 0) return [];

        var latMin = lista.Min(p => p.Latitude)  - 0.001;
        var latMax = lista.Max(p => p.Latitude)  + 0.001;
        var lonMin = lista.Min(p => p.Longitude) - 0.001;
        var lonMax = lista.Max(p => p.Longitude) + 0.001;

        var existentes = await _contexto.Pois
            .Where(p =>
                p.Localizacao.Y >= latMin && p.Localizacao.Y <= latMax &&
                p.Localizacao.X >= lonMin && p.Localizacao.X <= lonMax)
            .ToListAsync(cancellationToken);

        var existentesPorChave = existentes
            .ToDictionary(p => ChavePoi(p.Nome, p.Categoria));

        var novos    = new List<Poi>();
        var resultado = new List<Poi>();

        foreach (var dto in lista)
        {
            var chave = ChavePoi(dto.Nome, dto.Categoria);

            if (existentesPorChave.TryGetValue(chave, out var existente))
            {
                resultado.Add(existente);
                continue;
            }

            var novo = new Poi
            {
                Id          = Guid.NewGuid(),
                Nome        = dto.Nome,
                Categoria   = dto.Categoria,
                Prioridade  = dto.Prioridade,
                Localizacao = _factory.CreatePoint(new Coordinate(dto.Longitude, dto.Latitude))
            };

            novos.Add(novo);
            existentesPorChave[chave] = novo;
            resultado.Add(novo);
        }

        for (var i = 0; i < novos.Count; i += tamanhoLote)
        {
            var lote = novos.Skip(i).Take(tamanhoLote).ToList();
            _contexto.Pois.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();
        }

        return resultado;
    }

    // -------------------------------------------------------------------------
    // Auxiliares de relação PoiParada
    // -------------------------------------------------------------------------
    public async Task<HashSet<Guid>> BuscarPoisJaRelacionadosNaParadaAsync(
        Guid paradaId, CancellationToken cancellationToken)
    {
        var ids = await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .Select(r => r.PoiId)
            .ToListAsync(cancellationToken);

        return [..ids];
    }

    public async Task InserirRelacaoEmLoteAsync(
        IEnumerable<PoiParada> relacoes, int tamanhoLote, CancellationToken cancellationToken)
    {
        var lista = relacoes.ToList();
        if (lista.Count == 0) return;

        // Filtra duplicatas que já existem no banco (índice único garante isso,
        // mas evitar o erro de constraint é mais limpo)
        var paradaIds = lista.Select(r => r.ParadaId).Distinct().ToList();
        var jaExistentes = await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => paradaIds.Contains(r.ParadaId))
            .Select(r => new { r.PoiId, r.ParadaId })
            .ToListAsync(cancellationToken);

        var chaveExistentes = jaExistentes
            .Select(r => $"{r.PoiId}|{r.ParadaId}")
            .ToHashSet();

        var novas = lista
            .Where(r => !chaveExistentes.Contains($"{r.PoiId}|{r.ParadaId}"))
            .ToList();

        if (novas.Count == 0) return;

        for (var i = 0; i < novas.Count; i += tamanhoLote)
        {
            var lote = novas.Skip(i).Take(tamanhoLote).ToList();
            _contexto.PoiParadas.AddRange(lote);
            await _contexto.SaveChangesAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();
        }
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

    private static string ChavePoi(string nome, string categoria) =>
        $"{categoria}|{nome.Trim().ToLowerInvariant()}";
}