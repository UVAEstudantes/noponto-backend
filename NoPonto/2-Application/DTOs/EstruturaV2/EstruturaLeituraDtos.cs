using NoPonto.Application.DTOs.Compartilhado;

namespace NoPonto.Application.DTOs.EstruturaV2;

public sealed record LinhaEstruturalResumoDto(Guid Id, string Codigo, string Nome,
    string TipoRota, string? Consorcio, Guid ModalId, string Modal);

public sealed record SentidoEstruturalResumoDto(Guid Id, Guid LinhaId, string Nome);

public sealed record PadraoOperacionalResumoDto(Guid Id, Guid SentidoId, string Chave,
    string TipoServico, string? NomePublico, Guid? VersaoAtualId);

public sealed record ParadaEstruturalDto(Guid Id, string Codigo, string Nome,
    double Latitude, double Longitude, Guid? ModalId, string TipoLocal,
    Guid? ParadaPaiId, string? Plataforma);

public sealed record GeometriaLinhaDto(string Tipo, IReadOnlyList<double[]> Coordenadas);

public sealed record OcorrenciaItinerarioDto(Guid OcorrenciaId, int Ordem, int? Volta,
    Guid ParadaId, string CodigoParada, string Nome, double Latitude, double Longitude,
    double PosicaoLinha, double? DistanciaMetros);

public sealed record ItinerarioPadraoVersaoDto(
    Guid LinhaId, string CodigoLinha, string NomeLinha,
    Guid SentidoId, string NomeSentido,
    Guid PadraoOperacionalId, string ChavePadrao, string TipoServico, string? NomePublico,
    Guid PadraoVersaoId, int NumeroVersao, string Topologia, double ComprimentoMetros,
    DateTimeOffset? PublicadoEmUtc, bool EhVersaoAtual,
    GeometriaLinhaDto Geometria, IReadOnlyList<OcorrenciaItinerarioDto> Ocorrencias);

public interface IEstruturaLeituraV2Repository
{
    Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(
        string? codigo, string? nome, int pagina, int tamanhoPagina, CancellationToken ct);
    Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string codigo, CancellationToken ct);
    Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(string codigoLinha, CancellationToken ct);
    Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(Guid sentidoId, CancellationToken ct);
    Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(Guid padraoVersaoId, CancellationToken ct);
    Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid paradaId, CancellationToken ct);
}
