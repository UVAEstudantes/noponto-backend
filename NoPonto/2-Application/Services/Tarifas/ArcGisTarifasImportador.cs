using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NoPonto.Data.Tarifas;
using NoPonto.Domain.Entities;
using NoPonto.Domain.Tarifas;

namespace NoPonto.Application.Tarifas;

public sealed record TarifaImportacaoItem(string Servico, string TipoRota, Guid? LinhaId, decimal? Valor, string Resultado);
public sealed record TarifaImportacaoRelatorio(bool DryRun, int Recebidos, int Validos,
    int Importados, int Atualizados, int Inalterados, int Ignorados, int Conflitantes,
    IReadOnlyList<TarifaImportacaoItem> Itens);

public static class ArcGisTarifasRegras
{
    public static bool TryParse(string? text, out decimal valor)
    {
        valor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("R$", StringComparison.Ordinal)) text = text[2..].Trim();
        if (!Regex.IsMatch(text, @"^[0-9]{1,8}([.,][0-9]{1,2})?$", RegexOptions.CultureInvariant)) return false;
        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out valor) && RegrasTarifarias.ValorValido(valor);
    }

    public static string Codigo(string? code) => (code ?? "").Trim().ToUpperInvariant();
    public static string Tipo(string? type) => (type ?? "").Trim().ToLowerInvariant();

    public static bool ContextoCompativel(Linha linha, string tipo)
    {
        var modal = (linha.Modal.Nome ?? "").Trim();
        var bus = string.Equals(modal, "Ônibus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modal, "Onibus", StringComparison.OrdinalIgnoreCase);
        // BRT pode ser tipo do modal Ônibus ou modal persistido; nunca o ID virtual do frontend.
        return tipo is "regular" or "frescao" ? bus && Tipo(linha.TipoRota) == tipo
            : tipo == "brt" && Tipo(linha.TipoRota) == "brt"
                && (bus || string.Equals(modal, "BRT", StringComparison.OrdinalIgnoreCase));
    }

    public static TarifaImportacaoRelatorio Planejar(IReadOnlyList<ArcGisTarifaFeature> features,
        IReadOnlyList<Linha> linhas, IReadOnlyList<Tarifa> tarifas, bool dryRun)
    {
        var itens = new List<TarifaImportacaoItem>();
        foreach (var group in features.GroupBy(x => (Servico: Codigo(x.Servico), Tipo: Tipo(x.TipoRota)))
            .OrderBy(x => x.Key.Servico, StringComparer.Ordinal).ThenBy(x => x.Key.Tipo, StringComparer.Ordinal))
        {
            var values = group.Select(x => TryParse(x.Tarifas, out var v) ? (decimal?)v : null)
                .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var key = group.Key;
            if (values.Length > 1)
            { itens.Add(new(key.Servico, key.Tipo, null, null, "CONFLITO_PRECOS")); continue; }
            // Uma feature inválida impede inferir que o preço vale para todos os itinerários.
            if (key.Servico.Length == 0 || values.Length == 0 || group.Any(x => !TryParse(x.Tarifas, out _)))
            { itens.Add(new(key.Servico, key.Tipo, null, null, "VALOR_INVALIDO_OU_INCOMPLETO")); continue; }
            var matches = linhas.Where(x => Codigo(x.Codigo) == key.Servico && ContextoCompativel(x, key.Tipo)).ToArray();
            if (matches.Length != 1)
            { itens.Add(new(key.Servico, key.Tipo, null, values[0], matches.Length == 0 ? "LINHA_NAO_ENCONTRADA" : "LINHA_AMBIGUA")); continue; }
            var linha = matches[0];
            var atual = tarifas.SingleOrDefault(x => x.LinhaId == linha.Id);
            var status = atual is null ? "IMPORTADO" : atual.Fonte == "MANUAL" ? "MANUAL_PRESERVADA"
                : atual.Fonte != "ARCGIS_SPPO" ? "FONTE_PRESERVADA"
                : atual.Valor == values[0] ? "INALTERADO" : "ATUALIZADO";
            itens.Add(new(key.Servico, key.Tipo, linha.Id, values[0], status));
        }
        return new(dryRun, features.Count, features.Count(x => TryParse(x.Tarifas, out _)),
            itens.Count(x => x.Resultado == "IMPORTADO"), itens.Count(x => x.Resultado == "ATUALIZADO"),
            itens.Count(x => x.Resultado == "INALTERADO"),
            itens.Count(x => x.Resultado is not ("IMPORTADO" or "ATUALIZADO" or "INALTERADO" or "CONFLITO_PRECOS")),
            itens.Count(x => x.Resultado == "CONFLITO_PRECOS"), itens);
    }
}

public sealed class ArcGisTarifasImportador(TransporteDbContext db, TarifasStore store, ArcGisTarifasClient client)
{
    public async Task<TarifaImportacaoRelatorio> ExecutarAsync(bool dryRun, CancellationToken ct = default)
    {
        // Baixa TODAS as páginas antes de qualquer transação/escrita; falha da fonte não persiste nada.
        var features = await client.BaixarAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (dryRun)
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY", ct);
        else
        {
            // Serializa importações e bloqueia DML manual apenas durante o apply.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '15s'", ct);
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Tarifas\" IN SHARE ROW EXCLUSIVE MODE", ct);
        }
        var linhas = await db.Linhas.AsNoTracking().Include(x => x.Modal).ToArrayAsync(ct);
        var tarifas = await db.Tarifas.AsNoTracking().ToArrayAsync(ct);
        var plano = ArcGisTarifasRegras.Planejar(features, linhas, tarifas, dryRun);
        if (!dryRun)
            foreach (var item in plano.Itens.Where(x => x.Resultado is "IMPORTADO" or "ATUALIZADO"))
                await store.SalvarAsync(null, item.LinhaId, item.Valor!.Value, "ARCGIS_SPPO", ct);
        await transaction.CommitAsync(ct);
        return plano;
    }
}
