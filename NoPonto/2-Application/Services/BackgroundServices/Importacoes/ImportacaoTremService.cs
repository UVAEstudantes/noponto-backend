using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class ImportacaoTremService
{
    private const string NomeModalTrem = "Trem";
    private const string PrefixoParada = "TREM-";
    private const string ConsorcioSuperVia = "SuperVia";

    private static readonly Dictionary<string, (string Nome, string Cor)> RamaisConhecidos = new()
    {
        ["deodoro"] = ("Ramal Deodoro", "#ba0c2f"),
        ["santa_cruz"] = ("Ramal Santa Cruz", "#64a70b"),
        ["japeri"] = ("Ramal Japeri", "#92c1e9"),
        ["saracuruna"] = ("Ramal Saracuruna", "#de7c00"),
        ["belford_roxo"] = ("Ramal Belford Roxo", "#5c068c"),
        ["paracambi"] = ("Extensão Paracambi", "#92c1e9"),
        ["vila_inhomirim"] = ("Extensão Vila Inhomirim", "#de7c00"),
        ["guapimirim"] = ("Extensão Guapimirim", "#de7c00"),
    };

    private static readonly Dictionary<string, string> ArcGisParaBranch = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Deodoro"] = "deodoro",
        ["Santa Cruz"] = "santa_cruz",
        ["Japeri"] = "japeri",
        ["Saracuruna"] = "saracuruna",
        ["Belford Roxo"] = "belford_roxo",
        ["Paracambi"] = "paracambi",
        ["Vila Inhomirim"] = "vila_inhomirim",
        ["Guapimirim"] = "guapimirim",
    };

    // Mapeamento manual para estações cujo nome na SuperVia diverge do ArcGIS.
    // Chave  = nome normalizado da SuperVia
    // Valor  = nome normalizado do ArcGIS (ou prefixo que identifica a estação)
    private static readonly Dictionary<string, string> AliasEstacoes = new(StringComparer.OrdinalIgnoreCase)
    {

        ["OSWALDO CRUZ"] = "OSVALDO CRUZ",
        ["PAVUNA/SAO JOAO DE MERITI"] = "PAVUNA",
        ["ESTACAO OLIMPICA DE ENGENHO DE DENTRO"] = "ENGENHO DE DENTRO",
        ["PREFEITO BENTO RIBEIRO"] = "BENTO RIBEIRO",
        ["MOCIDADE/PADRE MIGUEL"] = "PADRE MIGUEL",
        ["BENJAMIM DO MONTE"] = "BENJAMIN DO MONTE",
        ["SURUI"] = "SURURU",
        ["JORORO"] = "JORARA",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImportacaoTremService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ImportacaoTremService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ImportacaoTremService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Ponto de entrada ──────────────────────────────────────────────────────

    public async Task ImportarTudoAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Iniciando importação completa da SuperVia...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var http = _httpClientFactory.CreateClient("arcgis-trem");

        var modalId = await GarantirModalTremAsync(db, ct);

        var estacoesSupervia = await BuscarEstacoesSuperViaAsync(http, ct);
        var coordenadasArcgis = await BuscarCoordenadasArcGisAsync(http, ct);
        var geometriasArcgis = await BuscarGeometriasArcGisAsync(http, ct);

        _logger.LogInformation(
            "Dados recebidos — SuperVia: {sv} entradas | ArcGIS coords: {ac} | ArcGIS geom: {ag} ramais",
            estacoesSupervia.Count, coordenadasArcgis.Count, geometriasArcgis.Count);

        // Deduplicar: a API retorna a mesma estação várias vezes (uma por ramal).
        var estacoesUnicas = estacoesSupervia
            .Where(e => !string.IsNullOrWhiteSpace(e.IdStation))
            .GroupBy(e => e.IdStation!)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "Estações únicas após deduplicação: {n} (eram {total})",
            estacoesUnicas.Count, estacoesSupervia.Count);

        var paradaIdPorEstacao = await ImportarEstacoesAsync(db, estacoesUnicas, coordenadasArcgis, ct);

        await ImportarRamaisAsync(db, modalId, estacoesSupervia, geometriasArcgis, paradaIdPorEstacao, ct);

        sw.Stop();
        _logger.LogInformation("Importação SuperVia concluída em {s:F1}s.", sw.Elapsed.TotalSeconds);
    }

    // ── Busca de dados ────────────────────────────────────────────────────────

    private async Task<List<SuperViaEstacaoDto>> BuscarEstacoesSuperViaAsync(
        HttpClient http, CancellationToken ct)
    {
        var url = _configuration["SUPERVIA:STATIONS_URL"]
               ?? "https://www.supervia.com.br/api/stations/?locale=pt-br";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "pt-BR,pt;q=0.9");
            request.Headers.Referrer = new Uri(
                "https://www.supervia.com.br/sua-viagem-e-servicos/conheca-as-estacoes/deodoro/?id_branch=santa_cruz");

            var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation(
                "SuperVia status: {status} | body: {body}",
                response.StatusCode,
                body[..Math.Min(body.Length, 300)]);

            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<List<SuperViaEstacaoDto>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao buscar estações da SuperVia.");
            return [];
        }
    }

    private async Task<Dictionary<string, (double Lon, double Lat)>> BuscarCoordenadasArcGisAsync(
        HttpClient http, CancellationToken ct)
    {
        var url = _configuration["ARCGIS:TREM:ESTACOES:BASE_URL"]
               ?? "https://pgeo3.rio.rj.gov.br/arcgis/rest/services/Transporte_Trafego/Transporte_publico/MapServer/16/query?outFields=*&where=1%3D1&f=geojson";

        try
        {
            var json = await http.GetStringAsync(url, ct);
            var fc = JsonSerializer.Deserialize<ArcGisEstacaoCollection>(json, JsonOpts);

            return fc?.Features?
                .Where(f => !string.IsNullOrWhiteSpace(f.Properties?.Nome)
                         && f.Geometry?.Coordinates?.Length >= 2)
                .ToDictionary(
                    f => NormalizarNome(f.Properties!.Nome!),
                    f => (f.Geometry!.Coordinates![0], f.Geometry.Coordinates[1]),
                    StringComparer.OrdinalIgnoreCase)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao buscar coordenadas ArcGIS.");
            return [];
        }
    }

    private async Task<Dictionary<string, ArcGisRamalFeature>> BuscarGeometriasArcGisAsync(
        HttpClient http, CancellationToken ct)
    {
        var url = _configuration["ARCGIS:TREM:RAMAIS:BASE_URL"]
               ?? "https://pgeo3.rio.rj.gov.br/arcgis/rest/services/Transporte_Trafego/Transporte_publico/MapServer/15/query?outFields=*&where=1%3D1&f=geojson";

        try
        {
            var json = await http.GetStringAsync(url, ct);
            var fc = JsonSerializer.Deserialize<ArcGisRamalCollection>(json, JsonOpts);

            return fc?.Features?
                .Where(f => !string.IsNullOrWhiteSpace(f.Properties?.Ramal)
                         && ArcGisParaBranch.ContainsKey(f.Properties!.Ramal!.Trim()))
                .ToDictionary(
                    f => ArcGisParaBranch[f.Properties!.Ramal!.Trim()],
                    f => f,
                    StringComparer.OrdinalIgnoreCase)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao buscar geometrias ArcGIS.");
            return [];
        }
    }

    // ── Importação de estações ────────────────────────────────────────────────

    private async Task<Dictionary<string, Guid>> ImportarEstacoesAsync(
        TransporteDbContext db,
        List<SuperViaEstacaoDto> estacoesUnicas,
        Dictionary<string, (double Lon, double Lat)> coordenadas,
        CancellationToken ct)
    {
        var codigosExistentes = await db.Paradas
            .Where(p => p.Codigo.StartsWith(PrefixoParada))
            .Select(p => new { p.Id, p.Codigo })
            .ToDictionaryAsync(p => p.Codigo, p => p.Id, ct);

        var paradaIdPorEstacao = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var criadas = 0;
        var atualizadas = 0;

        foreach (var estacao in estacoesUnicas)
        {
            if (string.IsNullOrWhiteSpace(estacao.Title)) continue;

            var nome = estacao.Title.Trim();
            var codigoBd = $"{PrefixoParada}{estacao.IdStation!.ToUpperInvariant()}";

            var coords = ResolverCoordenadas(nome, coordenadas);

            if (coords.Lon == 0 && coords.Lat == 0)
            {
                _logger.LogWarning("Coordenadas não encontradas para '{nome}' — pulando.", nome);
                continue;
            }

            var localizacao = new Point(coords.Lon, coords.Lat) { SRID = 4326 };

            if (codigosExistentes.TryGetValue(codigoBd, out var idExistente))
            {
                var parada = await db.Paradas.FindAsync([idExistente], ct);
                if (parada is not null)
                {
                    parada.Nome = nome;
                    parada.Localizacao = localizacao;
                    atualizadas++;
                    paradaIdPorEstacao[estacao.IdStation!] = parada.Id;
                }
            }
            else
            {
                var nova = new Parada
                {
                    Id = Guid.NewGuid(),
                    Codigo = codigoBd,
                    Nome = nome,
                    Localizacao = localizacao,
                };
                db.Paradas.Add(nova);
                paradaIdPorEstacao[estacao.IdStation!] = nova.Id;
                criadas++;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Estações — criadas: {c} | atualizadas: {a}", criadas, atualizadas);

        return paradaIdPorEstacao;
    }

    /// <summary>
    /// Resolve as coordenadas de uma estação tentando, em ordem:
    ///   1. Nome exato normalizado
    ///   2. Alias manual (AliasEstacoes)
    ///   3. Prefixo: primeiro token do nome normalizado contido em alguma chave ArcGIS
    /// </summary>
    private (double Lon, double Lat) ResolverCoordenadas(
        string nomeOriginal,
        Dictionary<string, (double Lon, double Lat)> coordenadas)
    {
        var nomeNorm = NormalizarNome(nomeOriginal);

        // 1. Exato
        if (coordenadas.TryGetValue(nomeNorm, out var c1)) return c1;

        // 2. Alias manual
        if (AliasEstacoes.TryGetValue(nomeNorm, out var alias)
            && coordenadas.TryGetValue(alias, out var c2))
            return c2;

        // 3. Prefixo: usa a primeira palavra do nome normalizado SuperVia
        //    e verifica se alguma chave ArcGIS começa com ela
        var primeiraPalavra = nomeNorm.Split(' ', '/')[0];
        if (primeiraPalavra.Length >= 4) // evita matches em palavras muito curtas
        {
            foreach (var (key, val) in coordenadas)
            {
                if (key.StartsWith(primeiraPalavra, StringComparison.OrdinalIgnoreCase))
                    return val;
            }
        }

        return default;
    }

    // ── Importação de ramais ──────────────────────────────────────────────────

    private async Task ImportarRamaisAsync(
        TransporteDbContext db,
        Guid modalId,
        List<SuperViaEstacaoDto> todasEstacoes,
        Dictionary<string, ArcGisRamalFeature> geometrias,
        Dictionary<string, Guid> paradaIdPorEstacao,
        CancellationToken ct)
    {
        var estacoesPorRamal = new Dictionary<string, List<SuperViaEstacaoDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var estacao in todasEstacoes)
        {
            if (estacao.StationXBranch is null) continue;
            foreach (var branch in estacao.StationXBranch)
            {
                if (string.IsNullOrWhiteSpace(branch.IdBranch)) continue;
                if (!estacoesPorRamal.TryGetValue(branch.IdBranch, out var lista))
                {
                    lista = [];
                    estacoesPorRamal[branch.IdBranch] = lista;
                }
                // Deduplicar: ignora se a estação já foi adicionada neste ramal
                if (lista.Any(e => e.IdStation == estacao.IdStation)) continue;
                lista.Add(estacao);
            }
        }

        foreach (var (branchId, info) in RamaisConhecidos)
        {
            if (!estacoesPorRamal.TryGetValue(branchId, out var estacoesDoRamal)
                || estacoesDoRamal.Count == 0)
            {
                _logger.LogWarning("Ramal '{ramal}' sem estações — pulando.", branchId);
                continue;
            }

            // Ordem pelo weight do branch dentro de cada estação (Central → terminal)
            var estacoesOrdenadas = estacoesDoRamal
                .OrderBy(e => e.StationXBranch!
                    .FirstOrDefault(b => b.IdBranch == branchId)?.Weight ?? 999)
                .ToList();

            // Nome dos sentidos baseado nas estações terminais do ramal
            var nomeTerminal = estacoesOrdenadas.Last().Title?.Trim() ?? "Terminal";
            var nomeCentral = estacoesOrdenadas.First().Title?.Trim() ?? "Central do Brasil";

            // ── Linha ─────────────────────────────────────────────────────────
            var codigo = $"TREM-{branchId.ToUpperInvariant()}";
            var linha = await db.Linhas.FirstOrDefaultAsync(l => l.Codigo == codigo, ct);

            if (linha is null)
            {
                linha = new Linha
                {
                    Id = Guid.NewGuid(),
                    Codigo = codigo,
                    Nome = info.Nome,
                    ModalId = modalId,
                    TipoRota = "trem",
                    Consorcio = ConsorcioSuperVia,
                };
                db.Linhas.Add(linha);
            }
            else
            {
                linha.Nome = info.Nome;
                linha.ModalId = modalId;
                linha.Consorcio = ConsorcioSuperVia;
            }

            await db.SaveChangesAsync(ct);

            // Monta geometria base (sentido Central → terminal)
            var geometriaIda = ObterOuCriarGeometria(branchId, geometrias);

            // Geometria volta: coordenadas invertidas
            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
            var coordsInvertidas = geometriaIda.Coordinates.Reverse().ToArray();
            var geometriaVolta = factory.CreateLineString(coordsInvertidas);

            // ── Sentido Central → Terminal (ex: "Santa Cruz") ─────────────────
            await GarantirSentidoEItinerarioAsync(
                db, linha.Id, nomeTerminal,
                estacoesOrdenadas, branchId, paradaIdPorEstacao,
                geometriaIda, ct);

            // ── Sentido Terminal → Central (ex: "Central do Brasil") ──────────
            var estacoesVolta = Enumerable.Reverse(estacoesOrdenadas).ToList();
            await GarantirSentidoEItinerarioAsync(
                db, linha.Id, nomeCentral,
                estacoesVolta, branchId, paradaIdPorEstacao,
                geometriaVolta, ct);
        }
    }

    private async Task GarantirSentidoEItinerarioAsync(
        TransporteDbContext db,
        Guid linhaId,
        string nomeSentido,
        List<SuperViaEstacaoDto> estacoesOrdenadas,
        string branchId,
        Dictionary<string, Guid> paradaIdPorEstacao,
        LineString geometria,
        CancellationToken ct)
    {
        // ── Sentido ───────────────────────────────────────────────────────────
        var sentido = await db.Sentidos
            .FirstOrDefaultAsync(s => s.LinhaId == linhaId && s.Nome == nomeSentido, ct);

        if (sentido is null)
        {
            sentido = new Sentido { Id = Guid.NewGuid(), LinhaId = linhaId, Nome = nomeSentido };
            db.Sentidos.Add(sentido);
            await db.SaveChangesAsync(ct);
        }

        // ── Itinerário ────────────────────────────────────────────────────────
        var itinerario = await db.Itinerarios
            .FirstOrDefaultAsync(i => i.SentidoId == sentido.Id, ct);

        if (itinerario is null)
        {
            itinerario = new Itinerario
            {
                Id = Guid.NewGuid(),
                SentidoId = sentido.Id,
                DistanciaMetros = geometria.Length, // comprimento aproximado em graus
                Geometria = geometria,
            };
            db.Itinerarios.Add(itinerario);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Atualiza geometria se já existia (re-importação)
            itinerario.Geometria = geometria;
            await db.SaveChangesAsync(ct);
        }

        // ── ParadaItinerario ──────────────────────────────────────────────────
        await VincularParadasAoItinerarioAsync(
            db, itinerario.Id, estacoesOrdenadas, branchId, paradaIdPorEstacao, nomeSentido, ct);
    }

    private LineString ObterOuCriarGeometria(
        string branchId,
        Dictionary<string, ArcGisRamalFeature> geometrias)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);

        if (!geometrias.TryGetValue(branchId, out var feat) || feat.Geometry is null)
        {
            _logger.LogWarning("Sem geometria ArcGIS para '{ramal}' — usando LineString mínima.", branchId);
            // Dois pontos distintos próximos ao centro do Rio como fallback seguro
            return factory.CreateLineString([
                new Coordinate(-43.1731, -22.9035),
                new Coordinate(-43.1730, -22.9034),
            ]);
        }

        var geo = ConverterGeometria(feat.Geometry);
        if (geo is null)
        {
            _logger.LogWarning("Falha ao converter geometria para '{ramal}' — usando fallback.", branchId);
            return factory.CreateLineString([
                new Coordinate(-43.1731, -22.9035),
                new Coordinate(-43.1730, -22.9034),
            ]);
        }

        return geo is LineString ls ? ls
             : geo is MultiLineString mls ? AchatarMultiLineString(mls)
             : (LineString)geo;
    }

    private async Task VincularParadasAoItinerarioAsync(
        TransporteDbContext db,
        Guid itinerarioId,
        List<SuperViaEstacaoDto> estacoesOrdenadas,
        string branchId,
        Dictionary<string, Guid> paradaIdPorEstacao,
        string nomeSentido,
        CancellationToken ct)
    {
        // Remove vínculos antigos (idempotente)
        var existentes = await db.ParadasItinerario
            .Where(pi => pi.ItinerarioId == itinerarioId)
            .ToListAsync(ct);

        if (existentes.Count > 0)
        {
            db.ParadasItinerario.RemoveRange(existentes);
            await db.SaveChangesAsync(ct);
        }

        var estacoesComParada = estacoesOrdenadas
            .Where(e => !string.IsNullOrWhiteSpace(e.IdStation)
                     && paradaIdPorEstacao.ContainsKey(e.IdStation!))
            .ToList();

        if (estacoesComParada.Count == 0)
        {
            _logger.LogWarning(
                "Ramal '{ramal}' sentido '{s}' — nenhuma estação com parada mapeada.", branchId, nomeSentido);
            return;
        }

        var total = estacoesComParada.Count;
        var novos = new List<ParadaItinerario>(total);

        for (var i = 0; i < total; i++)
        {
            var paradaId = paradaIdPorEstacao[estacoesComParada[i].IdStation!];
            var posicao = total > 1 ? (double)i / (total - 1) : 0.0;

            novos.Add(new ParadaItinerario
            {
                Id = Guid.NewGuid(),
                ItinerarioId = itinerarioId,
                ParadaId = paradaId,
                Ordem = i + 1,
                PosicaoLinha = posicao,
                DistanciaMetros = 0,
            });
        }

        db.ParadasItinerario.AddRange(novos);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ramal '{ramal}' sentido '{s}' — {n} paradas vinculadas.", branchId, nomeSentido, novos.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> GarantirModalTremAsync(TransporteDbContext db, CancellationToken ct)
    {
        var modalId = await db.Modais
            .Where(m => m.Nome == NomeModalTrem)
            .Select(m => m.Id)
            .FirstOrDefaultAsync(ct);

        if (modalId != Guid.Empty) return modalId;

        var modal = new Modal { Id = Guid.NewGuid(), Nome = NomeModalTrem };
        db.Modais.Add(modal);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Modal '{nome}' criado.", NomeModalTrem);
        return modal.Id;
    }

    private static LineString AchatarMultiLineString(MultiLineString mls)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var linhas = mls.Geometries
            .OfType<LineString>()
            .Where(l => l.NumPoints > 0)
            .ToList();

        if (linhas.Count == 0)
            return factory.CreateLineString([]);

        var usados = new HashSet<int>();
        var coordsOrdenados = new List<Coordinate>(linhas[0].Coordinates);
        usados.Add(0);

        while (usados.Count < linhas.Count)
        {
            var ultimo = coordsOrdenados[^1];
            var melhorIdx = -1;
            var melhorDist = double.MaxValue;
            var inverter = false;

            for (int i = 0; i < linhas.Count; i++)
            {
                if (usados.Contains(i)) continue;

                var coords = linhas[i].Coordinates;
                var inicio = coords[0];
                var fim = coords[^1];

                var distInicio = Distancia(ultimo, inicio);
                if (distInicio < melhorDist)
                {
                    melhorDist = distInicio;
                    melhorIdx = i;
                    inverter = false;
                }

                var distFim = Distancia(ultimo, fim);
                if (distFim < melhorDist)
                {
                    melhorDist = distFim;
                    melhorIdx = i;
                    inverter = true;
                }
            }

            if (melhorIdx < 0)
                break;

            var candidatos = linhas[melhorIdx].Coordinates;
            if (inverter)
                Array.Reverse(candidatos);

            if (candidatos.Length > 0 && candidatos[0].Equals2D(ultimo))
                coordsOrdenados.AddRange(candidatos.Skip(1));
            else
                coordsOrdenados.AddRange(candidatos);

            usados.Add(melhorIdx);
        }

        return factory.CreateLineString(coordsOrdenados.ToArray());
    }

    private static double Distancia(Coordinate a, Coordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Geometry? ConverterGeometria(ArcGisGeometry? geo)
    {
        if (geo is null) return null;

        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);

        try
        {
            if (geo.Type == "LineString" && geo.CoordinatesRaw.HasValue)
            {
                var pts = JsonSerializer.Deserialize<double[][]>(
                    geo.CoordinatesRaw.Value.GetRawText(), JsonOpts);
                if (pts is null) return null;
                return factory.CreateLineString(
                    pts.Select(c => new Coordinate(c[0], c[1])).ToArray());
            }

            if (geo.Type == "MultiLineString" && geo.CoordinatesRaw.HasValue)
            {
                var linhas = JsonSerializer.Deserialize<double[][][]>(
                    geo.CoordinatesRaw.Value.GetRawText(), JsonOpts);
                if (linhas is null) return null;
                return factory.CreateMultiLineString(
                    linhas.Select(l => factory.CreateLineString(
                        l.Select(c => new Coordinate(c[0], c[1])).ToArray())).ToArray());
            }
        }
        catch { /* retorna null */ }

        return null;
    }

    private static string NormalizarNome(string nome) =>
        nome.Trim().ToUpperInvariant()
            .Replace("Ã", "A").Replace("Á", "A").Replace("À", "A").Replace("Â", "A")
            .Replace("É", "E").Replace("Ê", "E")
            .Replace("Í", "I")
            .Replace("Ó", "O").Replace("Ô", "O")
            .Replace("Ú", "U")
            .Replace("Ú", "U")
            .Replace("Ç", "C");

    // ── DTOs SuperVia ─────────────────────────────────────────────────────────

    private sealed class SuperViaEstacaoDto
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("id_station")] public string? IdStation { get; init; }
        [JsonPropertyName("is_terminal")] public bool IsTerminal { get; init; }
        [JsonPropertyName("is_transfer")] public bool IsTransfer { get; init; }
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("station_x_branch")] public List<StationBranchDto>? StationXBranch { get; init; }
    }

    private sealed class StationBranchDto
    {
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("id_branch")] public string? IdBranch { get; init; }
        [JsonPropertyName("color")] public string? Color { get; init; }
    }

    // ── DTOs ArcGIS estações ──────────────────────────────────────────────────

    private sealed class ArcGisEstacaoCollection
    {
        [JsonPropertyName("features")] public List<ArcGisEstacaoFeature>? Features { get; init; }
    }

    private sealed class ArcGisEstacaoFeature
    {
        [JsonPropertyName("geometry")] public ArcGisPonto? Geometry { get; init; }
        [JsonPropertyName("properties")] public ArcGisEstacaoProps? Properties { get; init; }
    }

    private sealed class ArcGisPonto
    {
        [JsonPropertyName("coordinates")] public double[]? Coordinates { get; init; }
    }

    private sealed class ArcGisEstacaoProps
    {
        [JsonPropertyName("nome")] public string? Nome { get; init; }
    }

    // ── DTOs ArcGIS ramais ────────────────────────────────────────────────────

    private sealed class ArcGisRamalCollection
    {
        [JsonPropertyName("features")] public List<ArcGisRamalFeature>? Features { get; init; }
    }

    private sealed class ArcGisRamalFeature
    {
        [JsonPropertyName("geometry")] public ArcGisGeometry? Geometry { get; init; }
        [JsonPropertyName("properties")] public ArcGisRamalProps? Properties { get; init; }
    }

    private sealed class ArcGisGeometry
    {
        [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
        [JsonPropertyName("coordinates")] public JsonElement? CoordinatesRaw { get; init; }
    }

    private sealed class ArcGisRamalProps
    {
        [JsonPropertyName("ramal")] public string? Ramal { get; init; }
        [JsonPropertyName("st_length(shape)")] public double Comprimento { get; init; }
    }
}