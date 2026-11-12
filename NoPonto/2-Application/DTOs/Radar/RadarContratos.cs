using System.ComponentModel.DataAnnotations;

namespace NoPonto.Application.Radar;

/// <summary>Contexto estrutural selecionado; todos os identificadores são conferidos no catálogo.</summary>
public sealed record RadarContexto(Guid LinhaId, Guid SentidoId, Guid PadraoOperacionalId, Guid PadraoVersaoId);

/// <summary>Configuração integral do acompanhamento. Revisão zero somente na ativação.</summary>
public sealed record RadarConfigurarRequest
{
    public Guid ParadaId { get; init; }
    [Required, MinLength(1), MaxLength(50)]
    public RadarContexto[] Contextos { get; init; } = [];
    [Required, MinLength(1), MaxLength(50)]
    public Guid[] LinhasSelecionadas { get; init; } = [];
    [Required, MaxLength(50)]
    public Guid[] LinhasPrioritarias { get; init; } = [];
    [Range(2, 15)] public int AntecedenciaMinutos { get; init; }
    [Range(0, long.MaxValue)] public long RevisaoEsperada { get; init; }
    public Guid? SessaoId { get; init; }
}

/// <summary>Credencial entregue somente no registro. Push ainda não é suportado.</summary>
public sealed record RadarRegistroResponse(Guid InstalacaoId, string Credencial, DateTimeOffset CredencialExpiraEm);

/// <summary>Metadados públicos da própria instalação; nunca inclui o verificador.</summary>
public sealed record RadarInstalacaoResponse(Guid InstalacaoId, DateTimeOffset CredencialExpiraEm, bool PushDisponivel = false);

/// <summary>Sessão corrente; consultas e reconfigurações preservam o prazo de uma hora.</summary>
public sealed record RadarSessaoResponse(Guid SessaoId, Guid InstalacaoId, Guid ParadaId,
    RadarContexto[] Contextos, Guid[] LinhasSelecionadas, Guid[] LinhasPrioritarias,
    int AntecedenciaMinutos, string Estado, DateTimeOffset CriadaEm, DateTimeOffset ExpiraEm, long Revisao);
