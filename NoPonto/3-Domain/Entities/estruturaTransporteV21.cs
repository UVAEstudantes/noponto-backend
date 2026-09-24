using NetTopologySuite.Geometries;

namespace NoPonto.Domain.Entities;

public sealed class FonteEstrutural : BaseEntity
{
    public string Codigo { get; set; } = null!;
    public string Nome { get; set; } = null!;
    public ICollection<ImportacaoEstrutural> Importacoes { get; set; } = [];
}

public sealed class ImportacaoEstrutural
{
    public Guid Id { get; set; }
    public Guid FonteEstruturalId { get; set; }
    public string Status { get; set; } = StatusImportacaoEstrutural.EmProcessamento;
    public DateTimeOffset IniciadaEmUtc { get; set; }
    public DateTimeOffset? ConcluidaEmUtc { get; set; }
    public string? VersaoFonte { get; set; }
    public string ConteudoHash { get; set; } = null!;
    public string? RawUri { get; set; }
    public string AlgoritmoVersao { get; set; } = null!;
    public string Relatorio { get; set; } = "{}";
    public FonteEstrutural FonteEstrutural { get; set; } = null!;
    public ICollection<PadraoVersaoImportacao> PadroesVersoes { get; set; } = [];
}

public sealed class LinhaIdentidadeExterna : BaseEntity
{
    public Guid LinhaId { get; set; }
    public Guid FonteEstruturalId { get; set; }
    public string Tipo { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string OrigemMapeamento { get; set; } = OrigensMapeamento.Fonte;
    public Linha Linha { get; set; } = null!;
    public FonteEstrutural FonteEstrutural { get; set; } = null!;
}

public sealed class SentidoIdentidadeExterna : BaseEntity
{
    public Guid SentidoId { get; set; }
    public Guid FonteEstruturalId { get; set; }
    public string Tipo { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string OrigemMapeamento { get; set; } = OrigensMapeamento.Fonte;
    public Sentido Sentido { get; set; } = null!;
    public FonteEstrutural FonteEstrutural { get; set; } = null!;
}

public sealed class ParadaIdentidadeExterna : BaseEntity
{
    public Guid ParadaId { get; set; }
    public Guid FonteEstruturalId { get; set; }
    public string Tipo { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string OrigemMapeamento { get; set; } = OrigensMapeamento.Fonte;
    public Parada Parada { get; set; } = null!;
    public FonteEstrutural FonteEstrutural { get; set; } = null!;
}

public sealed class PadraoOperacional : BaseEntity
{
    public Guid SentidoId { get; set; }
    public Guid? VersaoAtualId { get; set; }
    public string Chave { get; set; } = null!;
    public string TipoServico { get; set; } = null!;
    public string? NomePublico { get; set; }
    public Sentido Sentido { get; set; } = null!;
    public PadraoVersao? VersaoAtual { get; set; }
    public ICollection<PadraoVersao> Versoes { get; set; } = [];
    public ICollection<PadraoIdentidadeExterna> IdentidadesExternas { get; set; } = [];
}

public sealed class PadraoIdentidadeExterna : BaseEntity
{
    public Guid PadraoOperacionalId { get; set; }
    public Guid FonteEstruturalId { get; set; }
    public string Tipo { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string OrigemMapeamento { get; set; } = OrigensMapeamento.Fonte;
    public PadraoOperacional PadraoOperacional { get; set; } = null!;
    public FonteEstrutural FonteEstrutural { get; set; } = null!;
}

public sealed class PadraoVersao
{
    public Guid Id { get; set; }
    public Guid PadraoOperacionalId { get; set; }
    public int Numero { get; set; }
    public LineString Geometria { get; set; } = null!;
    public double DistanciaMetros { get; set; }
    public string MetodoConstrucao { get; set; } = null!;
    public double Confianca { get; set; }
    public string AlgoritmoVersao { get; set; } = null!;
    public string ResultadoValidacao { get; set; } = ResultadosValidacaoPadrao.Pendente;
    public string Relatorio { get; set; } = "{}";
    public DateTimeOffset CriadaEmUtc { get; set; }
    public DateTimeOffset? PublicadaEmUtc { get; set; }
    public PadraoOperacional PadraoOperacional { get; set; } = null!;
    public ICollection<OcorrenciaParadaPadrao> Ocorrencias { get; set; } = [];
    public ICollection<PadraoVersaoImportacao> Importacoes { get; set; } = [];
}

public sealed class PadraoVersaoImportacao
{
    public Guid PadraoVersaoId { get; set; }
    public Guid ImportacaoEstruturalId { get; set; }
    public string Papel { get; set; } = null!;
    public PadraoVersao PadraoVersao { get; set; } = null!;
    public ImportacaoEstrutural ImportacaoEstrutural { get; set; } = null!;
}

public sealed class OcorrenciaParadaPadrao
{
    public Guid Id { get; set; }
    public Guid PadraoVersaoId { get; set; }
    public Guid ParadaId { get; set; }
    public int Ordem { get; set; }
    public int? SourceSequence { get; set; }
    public double PosicaoTracado { get; set; }
    public double? DistanciaAcumuladaMetros { get; set; }
    public PadraoVersao PadraoVersao { get; set; } = null!;
    public Parada Parada { get; set; } = null!;
}

public sealed class OverrideOcorrenciaPadrao : BaseEntity
{
    public Guid PadraoOperacionalId { get; set; }
    public Guid ParadaId { get; set; }
    public string Acao { get; set; } = null!;
    public int? OrdemDesejada { get; set; }
    public string Justificativa { get; set; } = null!;
    public string CriadoPor { get; set; } = null!;
    public DateTimeOffset? RevalidadoEmUtc { get; set; }
    public DateTimeOffset? ObsoletoEmUtc { get; set; }
    public PadraoOperacional PadraoOperacional { get; set; } = null!;
    public Parada Parada { get; set; } = null!;
}

public static class StatusImportacaoEstrutural
{
    public const string EmProcessamento = "EM_PROCESSAMENTO";
    public const string Concluida = "CONCLUIDA";
    public const string Falhou = "FALHOU";
}

public static class OrigensMapeamento
{
    public const string Fonte = "FONTE";
    public const string Manual = "MANUAL";
}

public static class PapeisImportacaoPadrao
{
    public const string Membership = "MEMBERSHIP";
    public const string Geometria = "GEOMETRIA";
    public const string Paradas = "PARADAS";
    public const string Metadados = "METADADOS";
}

public static class ResultadosValidacaoPadrao
{
    public const string Pendente = "PENDENTE";
    public const string Valida = "VALIDA";
    public const string Rejeitada = "REJEITADA";
}

public static class AcoesOverrideOcorrencia
{
    public const string Incluir = "INCLUIR";
    public const string Excluir = "EXCLUIR";
    public const string Mover = "MOVER";
}
