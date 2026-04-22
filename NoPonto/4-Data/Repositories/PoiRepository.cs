// NoPonto.Data.Repositories/PoiRepository.cs
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

    public async Task<PaginacaoRespostaDTO<PoiConsultaDTO>> ListarAsync(
        string? nome, int page, int pageSize, CancellationToken cancellationToken)
    {
        // Só POIs que estão relacionados a pelo menos uma parada
        var query = _contexto.Pois
            .AsNoTracking()
            .Where(p => p.PoiParadas.Any());

        if (!string.IsNullOrWhiteSpace(nome))
            query = query.Where(p => p.Nome.ToLower().Contains(nome.ToLower()));

        var total = await query.CountAsync(cancellationToken);
        var itens = await query
            .OrderBy(p => p.Nome)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PoiConsultaDTO
            {
                Id        = p.Id,
                Nome      = p.Nome,
                Categoria = p.Categoria,
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

    public async Task<List<PoiPorParadaDTO>> ListarPorParadaAsync(
        Guid paradaId, CancellationToken cancellationToken)
    {
        return await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .OrderBy(r => r.DistanciaMetros)
            .Select(r => new PoiPorParadaDTO
            {
                PoiId           = r.PoiId,
                Nome            = r.Poi.Nome,
                Categoria       = r.Poi.Categoria,
                Latitude        = r.Poi.Localizacao.Y,
                Longitude       = r.Poi.Localizacao.X,
                DistanciaMetros = r.DistanciaMetros
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PoiConsultaDTO>> ListarPorPontoAsync(
        double latitude, double longitude, double raioMetros, CancellationToken cancellationToken)
    {
        // Usa ST_DWithin via geography para distância real em metros,
        // com índice GIST. EF Core com Npgsql + NTS traduz Distance(...) corretamente
        // quando a coluna está configurada como geography.
        // Como o contexto usa geometry(Point,4326), fazemos o filtro em C# após
        // trazer candidatos por bbox e depois filtramos pelo Haversine exato.

        var margem = raioMetros / 111_000.0; // bbox aproximada em graus
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
                Lat = p.Localizacao.Y,
                Lon = p.Localizacao.X
            })
            .ToListAsync(cancellationToken);

        // Filtra pelo raio real (Haversine) e ordena por distância
        return candidatos
            .Select(p => new
            {
                Dto = new PoiConsultaDTO
                {
                    Id        = p.Id,
                    Nome      = p.Nome,
                    Categoria = p.Categoria,
                    Latitude  = p.Lat,
                    Longitude = p.Lon
                },
                Dist = HaversineMetros(latitude, longitude, p.Lat, p.Lon)
            })
            .Where(x => x.Dist <= raioMetros)
            .OrderBy(x => x.Dist)
            .Select(x => x.Dto)
            .ToList();
    }

    public async Task<List<Poi>> UpsertPoisAsync(
        IEnumerable<PoiImportadoDTO> importados,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        var lista = importados.ToList();
        var categorias = lista.Select(p => p.Categoria).Distinct().ToList();

        var existentes = await _contexto.Pois
            .Where(p => categorias.Contains(p.Categoria))
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

    public async Task<HashSet<Guid>> BuscarPoisJaRelacionadosNaParadaAsync(
        Guid paradaId, CancellationToken cancellationToken)
    {
        var ids = await _contexto.PoiParadas
            .AsNoTracking()
            .Where(r => r.ParadaId == paradaId)
            .Select(r => r.PoiId)
            .ToListAsync(cancellationToken);

        return new HashSet<Guid>(ids);
    }

    public async Task InserirRelacaoEmLoteAsync(
        IEnumerable<PoiParada> relacoes, int tamanhoLote, CancellationToken cancellationToken)
    {
        var lista = relacoes.ToList();
        for (var i = 0; i < lista.Count; i += tamanhoLote)
        {
            var lote = lista.Skip(i).Take(tamanhoLote).ToList();
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