using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public enum ArcGisRegularModo { DryRun, Persistir }

public sealed record ArcGisRegularRelatorio(
    int FeaturesEntrada,
    int FeaturesRegulares,
    int FeaturesIgnoradasModal,
    int FeaturesDirecaoInvalida,
    int Linhas,
    int Sentidos,
    int Padroes,
    int LinhasCriadas,
    int LinhasReutilizadas,
    int SentidosCriados,
    int SentidosReutilizados,
    int PadroesCriados,
    int PadroesReutilizados,
    int VersoesCriadas,
    int VersoesReutilizadas,
    int VersoesPublicadas,
    IReadOnlyDictionary<string, int> TiposDia,
    IReadOnlyList<string> Avisos);

internal sealed record ArcGisRegularPadrao(
    string Servico,
    string Direcao,
    string HashEstrutural,
    string ChaveSugerida,
    NetTopologySuite.Geometries.LineString Geometria,
    string Topologia,
    double ComprimentoMetros,
    IReadOnlyList<long> FeatureIds,
    IReadOnlyList<string> TiposDia,
    IReadOnlyList<string> Destinos,
    IReadOnlyList<string> Consorcios);

internal sealed record ArcGisRegularPlano(
    IReadOnlyList<ArcGisRegularPadrao> Padroes,
    int FeaturesEntrada,
    int FeaturesRegulares,
    int FeaturesIgnoradasModal,
    int FeaturesDirecaoInvalida,
    IReadOnlyDictionary<string, int> TiposDia,
    IReadOnlyList<string> Avisos);

/// <summary>
/// Materializa somente a estrutura regular nativa do ArcGIS no modelo final.
/// Esta fase não cria ocorrências, não publica versões e não depende de GTFS.
/// </summary>
public sealed class ArcGisEstruturalRegularService(TransporteDbContext db)
{
    public const string FonteCodigo = "DADOS_RIO_ARCGIS_SPPO";
    public const string AlgoritmoVersao = "ARCGIS_REGULAR_FINAL_V1";
    private const string IdentidadeLinha = "SERVICO";
    private const string IdentidadeSentido = "DIRECAO";
    private const string IdentidadePadraoFeature = "ARCGIS_FEATURE_ID";

    public async Task<ArcGisRegularRelatorio> ExecutarAsync(
        ArcGisSppoSnapshot snapshot,
        ArcGisRegularModo modo = ArcGisRegularModo.DryRun,
        string? versaoFonte = null,
        CancellationToken ct = default)
    {
        var plano = Planejar(snapshot);
        var report = CriarRelatorio(plano);
        if (modo == ArcGisRegularModo.DryRun)
            return report;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var fonte = await ObterFonteAsync(ct);
        var hashConteudo = ArcGisSppoSnapshotClient.HashSnapshot(snapshot.Features
            .Where(EhRegular).OrderBy(x => x.Fid));
        var anterior = await db.ImportacoesEstruturais.AsNoTracking().FirstOrDefaultAsync(x =>
            x.FonteEstruturalId == fonte.Id && x.ConteudoHash == hashConteudo
            && x.AlgoritmoVersao == AlgoritmoVersao
            && x.Status == StatusImportacaoEstrutural.Concluida, ct);
        if (anterior is not null)
        {
            await transaction.RollbackAsync(ct);
            return report with {
                LinhasReutilizadas = report.Linhas,
                SentidosReutilizados = report.Sentidos,
                PadroesReutilizados = report.Padroes,
                VersoesReutilizadas = report.Padroes
            };
        }

        var importacao = new ImportacaoEstrutural {
            Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
            Status = StatusImportacaoEstrutural.EmProcessamento,
            IniciadaEmUtc = DateTimeOffset.UtcNow,
            VersaoFonte = versaoFonte,
            ConteudoHash = hashConteudo,
            RawUri = snapshot.SourceUrl,
            AlgoritmoVersao = AlgoritmoVersao,
            Relatorio = "{}"
        };
        db.ImportacoesEstruturais.Add(importacao);
        await db.SaveChangesAsync(ct);

        try
        {
            var modal = await ObterModalOnibusAsync(ct);
            var linhas = new Dictionary<string, Linha>(StringComparer.OrdinalIgnoreCase);
            var sentidos = new Dictionary<(string Servico, string Direcao), Sentido>();
            var linhasCriadas = 0; var linhasReutilizadas = 0;
            var sentidosCriados = 0; var sentidosReutilizados = 0;
            var padroesCriados = 0; var padroesReutilizados = 0;
            var versoesCriadas = 0; var versoesReutilizadas = 0;

            foreach (var servico in plano.Padroes.Select(x => x.Servico)
                         .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            {
                var existente = await BuscarLinhaAsync(fonte.Id, servico, ct);
                var linha = existente.Linha;
                if (linha is null)
                {
                    linha = new Linha {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("line", FonteCodigo, servico),
                        Codigo = servico, Nome = servico, ModalId = modal.Id,
                        TipoRota = "regular",
                        Consorcio = ConsorcioUnico(plano.Padroes.Where(x =>
                            string.Equals(x.Servico, servico, StringComparison.OrdinalIgnoreCase)))
                    };
                    db.Linhas.Add(linha);
                    linhasCriadas++;
                }
                else
                {
                    linhasReutilizadas++;
                }
                if (!existente.TemIdentidade)
                    db.LinhasIdentidadesExternas.Add(new LinhaIdentidadeExterna {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("line-identity", FonteCodigo, IdentidadeLinha, servico),
                        LinhaId = linha.Id, FonteEstruturalId = fonte.Id,
                        Tipo = IdentidadeLinha, ExternalId = servico,
                        OrigemMapeamento = OrigensMapeamento.Fonte, Confianca = 1
                    });
                linhas[servico] = linha;
            }
            await db.SaveChangesAsync(ct);

            foreach (var chave in plano.Padroes.Select(x => (x.Servico, x.Direcao)).Distinct()
                         .OrderBy(x => x.Servico, StringComparer.Ordinal).ThenBy(x => x.Direcao, StringComparer.Ordinal))
            {
                var linha = linhas[chave.Servico];
                var externo = $"{chave.Servico}:{chave.Direcao}";
                var existente = await BuscarSentidoAsync(fonte.Id, linha.Id, externo, chave.Direcao, ct);
                var sentido = existente.Sentido;
                if (sentido is null)
                {
                    sentido = new Sentido {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("direction", FonteCodigo, externo),
                        LinhaId = linha.Id, Nome = $"Sentido {chave.Direcao}"
                    };
                    db.Sentidos.Add(sentido);
                    sentidosCriados++;
                }
                else
                {
                    sentidosReutilizados++;
                }
                if (!existente.TemIdentidade)
                    db.SentidosIdentidadesExternas.Add(new SentidoIdentidadeExterna {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("direction-identity", FonteCodigo, IdentidadeSentido, externo),
                        SentidoId = sentido.Id, FonteEstruturalId = fonte.Id,
                        Tipo = IdentidadeSentido, ExternalId = externo,
                        OrigemMapeamento = OrigensMapeamento.Fonte, Confianca = 1
                    });
                sentidos[chave] = sentido;
            }
            await db.SaveChangesAsync(ct);

            foreach (var item in plano.Padroes)
            {
                var sentido = sentidos[(item.Servico, item.Direcao)];
                var pattern = await BuscarPadraoAsync(fonte.Id, sentido.Id, item, ct);
                if (pattern is null)
                {
                    pattern = new PadraoOperacional {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("pattern", FonteCodigo,
                            item.Servico, item.Direcao, item.FeatureIds[0].ToString(CultureInfo.InvariantCulture)),
                        SentidoId = sentido.Id, Chave = item.ChaveSugerida,
                        TipoServico = "REGULAR",
                        NomePublico = string.Join(" / ", item.Destinos)
                    };
                    db.PadroesOperacionais.Add(pattern);
                    padroesCriados++;
                }
                else
                {
                    padroesReutilizados++;
                }
                foreach (var fid in item.FeatureIds)
                {
                    var external = fid.ToString(CultureInfo.InvariantCulture);
                    if (!await db.PadroesIdentidadesExternas.AnyAsync(x => x.FonteEstruturalId == fonte.Id
                            && x.Tipo == IdentidadePadraoFeature && x.ExternalId == external, ct))
                        db.PadroesIdentidadesExternas.Add(new PadraoIdentidadeExterna {
                            Id = EstruturaFinalRebuildService.DeterministicGuid("pattern-identity", FonteCodigo,
                                IdentidadePadraoFeature, external),
                            PadraoOperacionalId = pattern.Id, FonteEstruturalId = fonte.Id,
                            Tipo = IdentidadePadraoFeature, ExternalId = external,
                            OrigemMapeamento = OrigensMapeamento.Fonte, Confianca = 1
                        });
                }
                await db.SaveChangesAsync(ct);

                var version = await db.PadroesVersoes.SingleOrDefaultAsync(x =>
                    x.PadraoOperacionalId == pattern.Id && x.HashEstrutural == item.HashEstrutural, ct);
                if (version is null)
                {
                    var numero = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == pattern.Id)
                        .MaxAsync(x => (int?)x.Numero, ct) ?? 0) + 1;
                    version = new PadraoVersao {
                        Id = EstruturaFinalRebuildService.DeterministicGuid("version", pattern.Id.ToString("N"), item.HashEstrutural),
                        PadraoOperacionalId = pattern.Id, Numero = numero,
                        Geometria = (NetTopologySuite.Geometries.LineString)item.Geometria.Copy(),
                        Topologia = item.Topologia, ComprimentoMetros = item.ComprimentoMetros,
                        HashEstrutural = item.HashEstrutural,
                        MetodoConstrucao = "ARCGIS_NATIVO",
                        Confianca = .9, AlgoritmoVersao = AlgoritmoVersao,
                        ResultadoValidacao = ResultadosValidacaoPadrao.Pendente,
                        Relatorio = JsonSerializer.Serialize(new {
                            item.Servico, item.Direcao, item.FeatureIds,
                            item.TiposDia, item.Destinos, item.Consorcios,
                            SemOcorrencias = true
                        }),
                        CriadoEmUtc = DateTimeOffset.UtcNow
                    };
                    db.PadroesVersoes.Add(version);
                    versoesCriadas++;
                }
                else
                {
                    versoesReutilizadas++;
                }
                foreach (var papel in new[] { PapeisImportacaoPadrao.Membership,
                             PapeisImportacaoPadrao.Geometria, PapeisImportacaoPadrao.Metadados })
                    if (!await db.PadroesVersoesImportacoes.AnyAsync(x => x.PadraoVersaoId == version.Id
                            && x.ImportacaoEstruturalId == importacao.Id && x.Papel == papel, ct))
                        db.PadroesVersoesImportacoes.Add(new PadraoVersaoImportacao {
                            PadraoVersaoId = version.Id, ImportacaoEstruturalId = importacao.Id, Papel = papel
                        });
                await db.SaveChangesAsync(ct);
            }

            var final = report with {
                LinhasCriadas = linhasCriadas, LinhasReutilizadas = linhasReutilizadas,
                SentidosCriados = sentidosCriados, SentidosReutilizados = sentidosReutilizados,
                PadroesCriados = padroesCriados, PadroesReutilizados = padroesReutilizados,
                VersoesCriadas = versoesCriadas, VersoesReutilizadas = versoesReutilizadas,
                VersoesPublicadas = 0
            };
            importacao.Status = StatusImportacaoEstrutural.Concluida;
            importacao.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            importacao.Relatorio = JsonSerializer.Serialize(final);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return final;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal static ArcGisRegularPlano Planejar(ArcGisSppoSnapshot snapshot)
    {
        var regular = snapshot.Features.Where(EhRegular).ToArray();
        var valid = regular.Where(x => x.Direcao is "0" or "1").ToArray();
        var invalid = regular.Except(valid).ToArray();
        var patterns = valid.GroupBy(x => new {
                Servico = x.Servico.Trim().ToUpperInvariant(),
                Direcao = x.Direcao.Trim(),
                Hash = EstruturaHash.Calcular(x.Geometria,
                    x.Geometria.IsClosed ? TopologiasPadrao.Circular : TopologiasPadrao.Linear, [])
            })
            .Select(g => {
                var first = g.OrderBy(x => x.Fid).First();
                var topology = first.Geometria.IsClosed ? TopologiasPadrao.Circular : TopologiasPadrao.Linear;
                var featureIds = g.Select(x => x.Fid).Distinct().Order().ToArray();
                return new ArcGisRegularPadrao(g.Key.Servico, g.Key.Direcao, g.Key.Hash,
                    $"ARCGIS_FID_{featureIds[0]}", (NetTopologySuite.Geometries.LineString)first.Geometria.Copy(),
                    topology, Metric.Length(first.Geometria.Coordinates), featureIds,
                    g.Select(x => x.TipoDia).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
                    g.Select(x => x.Destino).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
                    g.Select(x => x.Consorcio).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
            })
            .OrderBy(x => x.Servico, StringComparer.Ordinal)
            .ThenBy(x => x.Direcao, StringComparer.Ordinal)
            .ThenBy(x => x.ChaveSugerida, StringComparer.Ordinal)
            .ToArray();
        var warnings = invalid.Select(x => $"DIRECAO_INVALIDA:{x.Servico}:{x.Direcao}:{x.Fid}")
            .Order(StringComparer.Ordinal).ToArray();
        return new(patterns, snapshot.Features.Count, regular.Length,
            snapshot.Features.Count - regular.Length, invalid.Length,
            regular.GroupBy(x => x.TipoDia).OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase), warnings);
    }

    private static ArcGisRegularRelatorio CriarRelatorio(ArcGisRegularPlano plano) => new(
        plano.FeaturesEntrada, plano.FeaturesRegulares, plano.FeaturesIgnoradasModal,
        plano.FeaturesDirecaoInvalida,
        plano.Padroes.Select(x => x.Servico).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        plano.Padroes.Select(x => (x.Servico, x.Direcao)).Distinct().Count(),
        plano.Padroes.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        plano.TiposDia, plano.Avisos);

    private static bool EhRegular(ArcGisSppoFeature feature) =>
        string.Equals(feature.TipoRota?.Trim(), "regular", StringComparison.OrdinalIgnoreCase);

    private async Task<FonteEstrutural> ObterFonteAsync(CancellationToken ct)
    {
        var source = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == FonteCodigo, ct);
        if (source is not null) return source;
        source = new FonteEstrutural {
            Id = EstruturaFinalRebuildService.DeterministicGuid("source", FonteCodigo),
            Codigo = FonteCodigo, Nome = "ArcGIS itinerários SPPO"
        };
        db.FontesEstruturais.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    private async Task<Modal> ObterModalOnibusAsync(CancellationToken ct)
    {
        var modal = await db.Modais.SingleOrDefaultAsync(x => x.Nome == "Ônibus", ct);
        if (modal is not null) return modal;
        modal = new Modal {
            Id = EstruturaFinalRebuildService.DeterministicGuid("modal", "ONIBUS"), Nome = "Ônibus"
        };
        db.Modais.Add(modal);
        await db.SaveChangesAsync(ct);
        return modal;
    }

    private async Task<(Linha? Linha, bool TemIdentidade)> BuscarLinhaAsync(
        Guid fonteId, string servico, CancellationToken ct)
    {
        var identity = await db.LinhasIdentidadesExternas.Include(x => x.Linha).SingleOrDefaultAsync(x =>
            x.FonteEstruturalId == fonteId && x.Tipo == IdentidadeLinha && x.ExternalId == servico, ct);
        if (identity is not null) return (identity.Linha, true);
        var byCode = await db.Linhas.Where(x => x.Codigo == servico).Take(2).ToArrayAsync(ct);
        if (byCode.Length > 1)
            throw new InvalidOperationException($"Código de linha ambíguo para ArcGIS: {servico}.");
        return (byCode.SingleOrDefault(), false);
    }

    private async Task<(Sentido? Sentido, bool TemIdentidade)> BuscarSentidoAsync(
        Guid fonteId, Guid linhaId, string external, string direcao, CancellationToken ct)
    {
        var identity = await db.SentidosIdentidadesExternas.Include(x => x.Sentido).SingleOrDefaultAsync(x =>
            x.FonteEstruturalId == fonteId && x.Tipo == IdentidadeSentido && x.ExternalId == external, ct);
        if (identity is not null)
        {
            if (identity.Sentido.LinhaId != linhaId)
                throw new InvalidOperationException($"Identidade de sentido ArcGIS aponta para outra linha: {external}.");
            return (identity.Sentido, true);
        }
        var expectedName = $"Sentido {direcao}";
        var candidates = await db.Sentidos.Where(x => x.LinhaId == linhaId && x.Nome == expectedName)
            .Take(2).ToArrayAsync(ct);
        if (candidates.Length > 1)
            throw new InvalidOperationException($"Sentido ambíguo para ArcGIS: {external}.");
        return (candidates.SingleOrDefault(), false);
    }

    private async Task<PadraoOperacional?> BuscarPadraoAsync(
        Guid fonteId, Guid sentidoId, ArcGisRegularPadrao item, CancellationToken ct)
    {
        var ids = item.FeatureIds.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
        var patterns = await db.PadroesIdentidadesExternas.Where(x => x.FonteEstruturalId == fonteId
                && x.Tipo == IdentidadePadraoFeature && ids.Contains(x.ExternalId))
            .Select(x => x.PadraoOperacional).Distinct().Take(2).ToArrayAsync(ct);
        if (patterns.Length > 1)
            throw new InvalidOperationException($"Features ArcGIS equivalentes apontam para padrões diferentes: {string.Join(',', ids)}.");
        if (patterns.Length == 1)
        {
            if (patterns[0].SentidoId != sentidoId)
                throw new InvalidOperationException($"Feature ArcGIS mudou de sentido estrutural: {string.Join(',', ids)}.");
            return patterns[0];
        }
        return await db.PadroesOperacionais.SingleOrDefaultAsync(x =>
            x.SentidoId == sentidoId && x.Chave == item.ChaveSugerida, ct);
    }

    private static string? ConsorcioUnico(IEnumerable<ArcGisRegularPadrao> patterns)
    {
        var values = patterns.SelectMany(x => x.Consorcios).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
