using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public enum LegacyEstruturalStatus { Concluida, NoOp, DryRun }

public static class ClassificacoesLegacyV24
{
    public const string Aceito = "ACEITO_LEGADO";
    public const string JaCobertoV22 = "JA_COBERTO_POR_V22";
    public const string JaCobertoV23 = "JA_COBERTO_POR_V23";
    public const string JaImportadoV24 = "JA_IMPORTADO_V24";
    public const string SemParadas = "SEM_PARADAS_SUFICIENTES";
    public const string GeometriaInvalida = "GEOMETRIA_INVALIDA";
    public const string OrdemInvalida = "ORDEM_INVALIDA";
    public const string ProgressoRegressivo = "PROGRESSO_REGRESSIVO";
    public const string ParadaDistante = "PARADA_DISTANTE";
    public const string ProjecaoInvalida = "PROJECAO_INVALIDA";
    public const string AssociacaoAmbigua = "ASSOCIACAO_AMBIGUA";
    public const string Circular = "CIRCULAR_NAO_SUPORTADO";
    public const string Concorrencia = "CONCORRENCIA_DE_CANDIDATOS";
}

public sealed record LegacyEstruturalItem(
    Guid LinhaId, string CodigoLinha, Guid SentidoId, Guid ItinerarioId,
    Guid? PadraoOperacionalId, string Classificacao, IReadOnlyList<string> Motivos,
    int Ocorrencias, double? DistanciaMaximaMetros, Guid? VersaoCriadaId);

public sealed record LegacyEstruturalRelatorio(
    int PadroesAvaliados, int JaCobertosV22, int JaCobertosV23,
    int CandidatosAnalisados, int Aceitos, int Rejeitados,
    IReadOnlyDictionary<string, int> Classificacoes,
    IReadOnlyList<LegacyEstruturalItem> Itens, long DuracaoMs);

public sealed record LegacyEstruturalResultado(
    LegacyEstruturalStatus Status, Guid? ImportacaoId, string ConteudoHash,
    LegacyEstruturalRelatorio Relatorio);

public sealed class LegacyEstruturalV24Service(TransporteDbContext db, GtfsProjecaoService projector)
{
    public const string FonteCodigo = "LEGADO_INTERNO";
    public const string AlgoritmoVersao = "V2.4_LEGADO_1";
    public const string ChavePrefixo = "LEGADO_ITINERARIO:";
    public const double Confianca = .65;

    public async Task<LegacyEstruturalResultado> ExecutarAsync(bool dryRun = true,
        CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        var snapshot = await CarregarSnapshotAsync(ct);
        var hash = CalcularHash(snapshot.Itinerarios, snapshot.Relacoes);
        var analyses = Analisar(snapshot);
        if (dryRun)
            return new(LegacyEstruturalStatus.DryRun, null, hash,
                CriarRelatorio(analyses, timer.ElapsedMilliseconds));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ImportacaoEstrutural? importacao = null;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext('V2.4_LEGADO_1'))", ct);
            var fonte = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == FonteCodigo, ct);
            if (fonte is null)
            {
                fonte = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = FonteCodigo,
                    Nome = "Estrutura operacional legada interna" };
                db.FontesEstruturais.Add(fonte);
                await db.SaveChangesAsync(ct);
            }
            var anterior = await db.ImportacoesEstruturais.AsNoTracking().Where(x =>
                    x.FonteEstruturalId == fonte.Id && x.ConteudoHash == hash
                    && x.AlgoritmoVersao == AlgoritmoVersao
                    && x.Status == StatusImportacaoEstrutural.Concluida)
                .OrderByDescending(x => x.ConcluidaEmUtc).FirstOrDefaultAsync(ct);
            if (anterior is not null)
            {
                await transaction.RollbackAsync(ct);
                return new(LegacyEstruturalStatus.NoOp, anterior.Id, hash,
                    JsonSerializer.Deserialize<LegacyEstruturalRelatorio>(anterior.Relatorio)
                    ?? throw new InvalidDataException("Relatório V2.4 concluído é inválido."));
            }

            importacao = new ImportacaoEstrutural { Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
                Status = StatusImportacaoEstrutural.EmProcessamento, IniciadaEmUtc = DateTimeOffset.UtcNow,
                VersaoFonte = hash, ConteudoHash = hash, AlgoritmoVersao = AlgoritmoVersao,
                Relatorio = "{}" };
            db.ImportacoesEstruturais.Add(importacao);
            await db.SaveChangesAsync(ct);

            foreach (var analysis in analyses.Where(x => x.Classificacao == ClassificacoesLegacyV24.Aceito))
            {
                var key = ChavePrefixo + analysis.Itinerario.Id.ToString("N");
                var pattern = await db.PadroesOperacionais.SingleOrDefaultAsync(x =>
                    x.SentidoId == analysis.Itinerario.SentidoId && x.Chave == key, ct);
                if (pattern is null)
                {
                    pattern = new PadraoOperacional { Id = Guid.NewGuid(),
                        SentidoId = analysis.Itinerario.SentidoId, Chave = key,
                        TipoServico = "LEGADO", NomePublico = null };
                    db.PadroesOperacionais.Add(pattern);
                    await db.SaveChangesAsync(ct);
                }
                var candidateHash = HashCandidato(analysis.Itinerario, analysis.Relacoes);
                var existingReports = await db.PadroesVersoes.AsNoTracking().Where(x =>
                        x.PadraoOperacionalId == pattern.Id && x.AlgoritmoVersao == AlgoritmoVersao)
                    .Select(x => x.Relatorio).ToArrayAsync(ct);
                if (existingReports.Any(x => RelatorioTemHash(x, candidateHash)))
                {
                    analysis.PadraoOperacionalId = pattern.Id;
                    analysis.Reject(ClassificacoesLegacyV24.JaImportadoV24);
                    continue;
                }
                var number = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == pattern.Id)
                    .Select(x => (int?)x.Numero).MaxAsync(ct) ?? 0) + 1;
                var version = new PadraoVersao { Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id,
                    Numero = number, Geometria = (LineString)analysis.Itinerario.Geometria.Copy(),
                    DistanciaMetros = ComprimentoMetros(analysis.Itinerario.Geometria),
                    MetodoConstrucao = "LEGADO_INTERNO_VALIDADO", Confianca = Confianca,
                    AlgoritmoVersao = AlgoritmoVersao,
                    ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
                    Relatorio = JsonSerializer.Serialize(new { analysis.Itinerario.Id,
                        analysis.Itinerario.SentidoId, Classificacao = analysis.Classificacao,
                        analysis.DistanciaMaximaMetros, ConteudoHash = candidateHash }),
                    CriadaEmUtc = DateTimeOffset.UtcNow };
                db.PadroesVersoes.Add(version);
                foreach (var relation in analysis.Relacoes.OrderBy(x => x.Ordem).ThenBy(x => x.Id))
                    db.OcorrenciasParadasPadroes.Add(new() { Id = Guid.NewGuid(),
                        PadraoVersaoId = version.Id, ParadaId = relation.ParadaId,
                        Ordem = relation.Ordem, SourceSequence = relation.SourceStopSequence,
                        PosicaoTracado = relation.PosicaoLinha,
                        DistanciaAcumuladaMetros = relation.DistanciaMetros });
                foreach (var role in new[] { PapeisImportacaoPadrao.Membership,
                    PapeisImportacaoPadrao.Geometria, PapeisImportacaoPadrao.Paradas,
                    PapeisImportacaoPadrao.Metadados })
                    db.PadroesVersoesImportacoes.Add(new() { PadraoVersaoId = version.Id,
                        ImportacaoEstruturalId = importacao.Id, Papel = role });
                analysis.PadraoOperacionalId = pattern.Id;
                analysis.VersaoCriadaId = version.Id;
            }
            await db.SaveChangesAsync(ct);
            timer.Stop();
            var report = CriarRelatorio(analyses, timer.ElapsedMilliseconds);
            importacao.Status = StatusImportacaoEstrutural.Concluida;
            importacao.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            importacao.Relatorio = JsonSerializer.Serialize(report);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(LegacyEstruturalStatus.Concluida, importacao.Id, hash, report);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            await RegistrarFalhaAsync(hash, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task RegistrarFalhaAsync(string hash, Exception ex, CancellationToken ct)
    {
        var fonte = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == FonteCodigo, ct);
        if (fonte is null)
        {
            fonte = new() { Id = Guid.NewGuid(), Codigo = FonteCodigo,
                Nome = "Estrutura operacional legada interna" };
            db.FontesEstruturais.Add(fonte);
        }
        db.ImportacoesEstruturais.Add(new() { Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
            Status = StatusImportacaoEstrutural.Falhou, IniciadaEmUtc = DateTimeOffset.UtcNow,
            ConcluidaEmUtc = DateTimeOffset.UtcNow, VersaoFonte = hash, ConteudoHash = hash,
            AlgoritmoVersao = AlgoritmoVersao,
            Relatorio = JsonSerializer.Serialize(new { Erro = ex.GetType().Name, ex.Message }) });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Snapshot> CarregarSnapshotAsync(CancellationToken ct)
    {
        var itineraries = await db.Itinerarios.AsNoTracking().Where(x => x.Ativo)
            .Include(x => x.Sentido).ThenInclude(x => x.Linha).OrderBy(x => x.Id).ToArrayAsync(ct);
        var ids = itineraries.Select(x => x.Id).ToArray();
        var relations = await db.ParadasItinerario.AsNoTracking()
            .Where(x => x.Ativo && ids.Contains(x.ItinerarioId)).Include(x => x.Parada)
            .OrderBy(x => x.ItinerarioId).ThenBy(x => x.Ordem).ThenBy(x => x.Id).ToArrayAsync(ct);
        var directionIds = itineraries.Select(x => x.SentidoId).Distinct().ToArray();
        var covered = await db.PadroesVersoes.AsNoTracking().Where(x =>
                directionIds.Contains(x.PadraoOperacional.SentidoId)
                && x.ResultadoValidacao == ResultadosValidacaoPadrao.Valida
                && (x.AlgoritmoVersao == GtfsEstruturalV22Service.AlgoritmoVersao
                    || x.AlgoritmoVersao == ArcGisEstruturalV23Service.AlgoritmoVersao))
            .Select(x => new { x.PadraoOperacional.SentidoId, x.AlgoritmoVersao }).ToArrayAsync(ct);
        return new(itineraries, relations,
            covered.Where(x => x.AlgoritmoVersao == GtfsEstruturalV22Service.AlgoritmoVersao)
                .Select(x => x.SentidoId).ToHashSet(),
            covered.Where(x => x.AlgoritmoVersao == ArcGisEstruturalV23Service.AlgoritmoVersao)
                .Select(x => x.SentidoId).ToHashSet());
    }

    private List<Analysis> Analisar(Snapshot snapshot)
    {
        var analyses = snapshot.Itinerarios.Select(itinerary =>
        {
            var relations = snapshot.Relacoes.Where(x => x.ItinerarioId == itinerary.Id).ToArray();
            if (snapshot.CobertosV23.Contains(itinerary.SentidoId))
                return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.JaCobertoV23);
            if (snapshot.CobertosV22.Contains(itinerary.SentidoId))
                return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.JaCobertoV22);
            return Avaliar(itinerary, relations, projector);
        }).ToList();
        foreach (var ambiguous in analyses.Where(x => x.Classificacao == ClassificacoesLegacyV24.Aceito)
            .GroupBy(x => x.Itinerario.SentidoId).Where(x => x.Count() > 1))
            foreach (var item in ambiguous)
                item.Reject(ClassificacoesLegacyV24.Concorrencia);
        return analyses;
    }

    internal static Analysis Avaliar(Itinerario itinerary,
        IReadOnlyList<ParadaItinerario> relations, GtfsProjecaoService projector)
    {
        if (itinerary.Sentido is null || itinerary.Sentido.Linha is null
            || itinerary.SentidoId == Guid.Empty || itinerary.Sentido.LinhaId == Guid.Empty)
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.AssociacaoAmbigua);
        var line = itinerary.Geometria;
        if (line is null || line.NumPoints < 2 || line.IsEmpty || ComprimentoMetros(line) <= 0)
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.GeometriaInvalida);
        var ordered = relations.OrderBy(x => x.Ordem).ThenBy(x => x.Id).ToArray();
        if (ordered.Length < 3)
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.SemParadas);
        if (ordered.Any(x => x.Ordem <= 0) || ordered.Select(x => x.Ordem).Distinct().Count() != ordered.Length)
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.OrdemInvalida);
        if (line.IsClosed || ordered[0].ParadaId == ordered[^1].ParadaId)
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.Circular);
        if (ordered.Any(x => !double.IsFinite(x.PosicaoLinha) || x.PosicaoLinha is < 0 or > 1)
            || !NaoDecrescente(ordered.Select(x => x.PosicaoLinha)))
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.ProgressoRegressivo);
        if (ordered.Any(x => x.Parada is null || x.Parada.Localizacao is null
            || x.Parada.Localizacao.IsEmpty))
            return Analysis.Rejected(itinerary, relations, ClassificacoesLegacyV24.ProjecaoInvalida);

        var occurrences = ordered.Select(x => new GtfsOcorrencia(x.ParadaId.ToString("N"),
            x.Ordem, x.DistanciaMetros)).ToArray();
        var source = new GtfsPadrao(itinerary.Id.ToString("N"), itinerary.SentidoId.ToString("N"),
            itinerary.Sentido?.Linha?.Codigo ?? itinerary.SentidoId.ToString("N"),
            itinerary.SentidoId.ToString("N"), itinerary.Id.ToString("N"), occurrences,
            line.Coordinates, 1);
        var stops = ordered.GroupBy(x => x.ParadaId).ToDictionary(x => x.Key.ToString("N"),
            x => x.First().Parada);
        var projection = projector.Projetar(source, itinerary, stops);
        if (projection.Motivos.Count > 0)
        {
            var classification = projection.Motivos.Any(x => x.StartsWith("DISTANCIA_ACIMA_LIMITE", StringComparison.Ordinal))
                ? ClassificacoesLegacyV24.ParadaDistante : ClassificacoesLegacyV24.ProjecaoInvalida;
            return Analysis.Rejected(itinerary, relations, classification, projection.Motivos);
        }
        return new(itinerary, relations, ClassificacoesLegacyV24.Aceito, [],
            projection.Ocorrencias.Max(x => x.DistanciaMetros));
    }

    private static LegacyEstruturalRelatorio CriarRelatorio(IReadOnlyList<Analysis> analyses, long elapsed)
    {
        var items = analyses.Select(x => new LegacyEstruturalItem(x.Itinerario.Sentido.LinhaId,
            x.Itinerario.Sentido.Linha.Codigo, x.Itinerario.SentidoId, x.Itinerario.Id,
            x.PadraoOperacionalId, x.Classificacao, x.Motivos, x.Relacoes.Count,
            x.DistanciaMaximaMetros, x.VersaoCriadaId)).ToArray();
        return new(items.Length,
            items.Count(x => x.Classificacao == ClassificacoesLegacyV24.JaCobertoV22),
            items.Count(x => x.Classificacao == ClassificacoesLegacyV24.JaCobertoV23),
            items.Count(x => x.Classificacao != ClassificacoesLegacyV24.JaCobertoV22
                && x.Classificacao != ClassificacoesLegacyV24.JaCobertoV23),
            items.Count(x => x.Classificacao == ClassificacoesLegacyV24.Aceito),
            items.Count(x => x.Classificacao != ClassificacoesLegacyV24.Aceito
                && x.Classificacao != ClassificacoesLegacyV24.JaCobertoV22
                && x.Classificacao != ClassificacoesLegacyV24.JaCobertoV23
                && x.Classificacao != ClassificacoesLegacyV24.JaImportadoV24),
            items.GroupBy(x => x.Classificacao).ToDictionary(x => x.Key, x => x.Count()), items, elapsed);
    }

    private static string CalcularHash(IEnumerable<Itinerario> itineraries,
        IEnumerable<ParadaItinerario> relations)
    {
        var byItinerary = relations.GroupBy(x => x.ItinerarioId).ToDictionary(x => x.Key,
            x => x.OrderBy(y => y.Ordem).ThenBy(y => y.Id).ToArray());
        var canonical = new StringBuilder(AlgoritmoVersao);
        foreach (var itinerary in itineraries.OrderBy(x => x.Id))
        {
            canonical.Append('|').Append(itinerary.Id).Append('|').Append(itinerary.SentidoId);
            foreach (var c in itinerary.Geometria?.Coordinates ?? [])
                canonical.Append('|').Append(c.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(c.Y.ToString("R", CultureInfo.InvariantCulture));
            foreach (var relation in byItinerary.GetValueOrDefault(itinerary.Id) ?? [])
                canonical.Append('|').Append(relation.Ordem)
                    .Append(':').Append(relation.ParadaId).Append(':')
                    .Append(relation.PosicaoLinha.ToString("R", CultureInfo.InvariantCulture))
                    .Append(':').Append(relation.SourceStopSequence?.ToString(CultureInfo.InvariantCulture) ?? "")
                    .Append(':').Append(relation.Parada.Localizacao.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(relation.Parada.Localizacao.Y.ToString("R", CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string HashCandidato(Itinerario itinerary, IEnumerable<ParadaItinerario> relations)
    {
        var text = itinerary.SentidoId + "|" + string.Join(';', itinerary.Geometria.Coordinates.Select(x =>
            FormattableString.Invariant($"{x.X:R},{x.Y:R}"))) + '|' + string.Join(';',
            relations.OrderBy(x => x.Ordem).Select(x => FormattableString.Invariant(
                $"{x.Ordem}:{x.ParadaId}:{x.PosicaoLinha:R}:{x.SourceStopSequence}:{x.Parada.Localizacao.X:R},{x.Parada.Localizacao.Y:R}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool RelatorioTemHash(string report, string hash)
    {
        try
        {
            using var json = JsonDocument.Parse(report);
            return json.RootElement.TryGetProperty("ConteudoHash", out var value)
                && string.Equals(value.GetString(), hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static double ComprimentoMetros(LineString line)
    {
        const double radius = 6_371_008.8; var total = 0d;
        for (var i = 1; i < line.NumPoints; i++)
        {
            var a = line.GetCoordinateN(i - 1); var b = line.GetCoordinateN(i);
            var p1 = a.Y * Math.PI / 180; var p2 = b.Y * Math.PI / 180;
            var dp = (b.Y-a.Y)*Math.PI/180; var dl = (b.X-a.X)*Math.PI/180;
            var h = Math.Sin(dp/2)*Math.Sin(dp/2)+Math.Cos(p1)*Math.Cos(p2)*Math.Sin(dl/2)*Math.Sin(dl/2);
            total += 2*radius*Math.Asin(Math.Min(1,Math.Sqrt(h)));
        }
        return total;
    }

    private static bool NaoDecrescente(IEnumerable<double> values)
    {
        var first = true; var last = 0d;
        foreach (var value in values) { if (!first && value < last) return false; first = false; last = value; }
        return true;
    }

    private sealed record Snapshot(Itinerario[] Itinerarios, ParadaItinerario[] Relacoes,
        HashSet<Guid> CobertosV22, HashSet<Guid> CobertosV23);

    internal sealed class Analysis(Itinerario itinerary, IReadOnlyList<ParadaItinerario> relations,
        string classification, IReadOnlyList<string> reasons, double? maxDistance = null)
    {
        public Itinerario Itinerario { get; } = itinerary;
        public IReadOnlyList<ParadaItinerario> Relacoes { get; } = relations;
        public string Classificacao { get; private set; } = classification;
        public IReadOnlyList<string> Motivos { get; private set; } = reasons;
        public double? DistanciaMaximaMetros { get; } = maxDistance;
        public Guid? PadraoOperacionalId { get; set; }
        public Guid? VersaoCriadaId { get; set; }
        public void Reject(string reason) { Classificacao = reason; Motivos = [reason]; }
        public static Analysis Rejected(Itinerario i, IReadOnlyList<ParadaItinerario> r,
            string reason, IReadOnlyList<string>? details = null) => new(i, r, reason, details ?? [reason]);
    }
}
