using System.Text;
using System.Text.RegularExpressions;

namespace NoPonto.Domain.Tarifas;

public static class RegrasTarifarias
{
    public const int NomeMaximo = 100;
    public static bool IconeValido(string? icone) => icone is null or
        "credit-card" or "wallet" or "banknote" or "landmark" or "smartphone" or "qr-code";

    public static bool CorValida(string? cor) => cor is null ||
        (cor.Length == 7 && cor[0] == '#' && cor.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false);

    public static bool ValorValido(decimal valor) =>
        valor >= 0 && valor <= 99999999.99m && decimal.Round(valor, 2) == valor;

    public static (string Nome, string Chave) NormalizarNome(string? nome)
    {
        if (nome is null || nome.Length > 1000) throw new ArgumentException("Nome inválido.");
        var display = Regex.Replace(nome.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
        var key = display.ToLowerInvariant().Normalize(NormalizationForm.FormC);
        if (display.Length is < 1 or > NomeMaximo || key.Length > NomeMaximo
            || display.Any(char.IsControl))
            throw new ArgumentException("Nome deve conter de 1 a 100 caracteres, sem controles.");
        return (display, key);
    }
}
