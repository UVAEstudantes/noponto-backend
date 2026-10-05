using Microsoft.AspNetCore.Mvc;
using NoPonto.API.Controllers;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstruturaLeituraV2ControllerTests
{
    [Fact]
    public void ContratoLinha_BrtMantemModalRealETipoRotaExplicito()
    {
        var linhaId = Guid.NewGuid();
        var modalOnibusId = Guid.NewGuid();
        var dto = new LinhaEstruturalResumoDto(linhaId, "B42", "Transcarioca",
            "brt", "BRT Rio", modalOnibusId, "Ônibus");

        Assert.Equal(linhaId, dto.LinhaId);
        Assert.Equal(modalOnibusId, dto.ModalId);
        Assert.Equal("Ônibus", dto.Modal);
        Assert.Equal("brt", dto.TipoRota);
    }

    [Fact]
    public async Task LinhaComum_ListaESentidoComUmPadraoPublicado()
    {
        var repo = Scenario();
        var linhas = new EstruturaLinhasController(repo);
        var pagina = Assert.IsType<OkObjectResult>(await linhas.Listar("006", null, null,
            null, null, 1, 20)).Value;
        var linha = Assert.Single(Assert.IsType<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>>(pagina).Itens);
        Assert.Equal("006", linha.Codigo);
        Assert.Equal(repo.LinhaId, linha.LinhaId);
        var sentidos = Assert.IsAssignableFrom<IReadOnlyList<SentidoEstruturalResumoDto>>(
            Assert.IsType<OkObjectResult>(await linhas.Sentidos("006", default)).Value);
        var padroes = Assert.IsAssignableFrom<IReadOnlyList<PadraoOperacionalResumoDto>>(
            Assert.IsType<OkObjectResult>(await new EstruturaSentidosController(repo)
                .Padroes(Assert.Single(sentidos).Id, default)).Value);
        Assert.NotNull(Assert.Single(padroes).VersaoAtualId);
    }

    [Fact]
    public async Task BuscaEncaminhaFiltrosEstruturaisAoRepositorio()
    {
        var repo = Scenario();
        var modalId = Guid.NewGuid();
        await new EstruturaLinhasController(repo).Listar(null, "deodoro", modalId,
            "train", "brt", 2, 15);
        Assert.Equal(("deodoro", modalId, "train", "brt"), repo.LastSearch);
    }

    [Fact]
    public async Task LinhaMultipattern_RetornaTodosSemEscolhaArbitraria()
    {
        var repo = Scenario(multipattern: true);
        var result = Assert.IsType<OkObjectResult>(await new EstruturaSentidosController(repo)
            .Padroes(repo.SentidoId, default));
        var padroes = Assert.IsAssignableFrom<IReadOnlyList<PadraoOperacionalResumoDto>>(result.Value);
        Assert.Equal(2, padroes.Count);
        Assert.All(padroes, x => Assert.NotNull(x.VersaoAtualId));
    }

    [Fact]
    public async Task CodigoComercialEhExato_E866NaoEhSv866()
    {
        var repo = Scenario(); var controller = new EstruturaLinhasController(repo);
        var linha = Assert.IsType<LinhaEstruturalResumoDto>(
            Assert.IsType<OkObjectResult>(await controller.Buscar("866", default)).Value);
        Assert.Equal("866", linha.Codigo);
        Assert.IsType<NotFoundResult>(await controller.Buscar("SV866", default));
    }

    [Fact]
    public async Task LinhaInexistenteRetorna404()
        => Assert.IsType<NotFoundResult>(await new EstruturaLinhasController(Scenario())
            .Buscar("INEXISTENTE", default));

    [Fact]
    public async Task Circular_PreservaOrdemExtremosEParadaRepetidaComoOcorrencias()
    {
        var repo = Scenario(circular: true);
        var result = Assert.IsType<OkObjectResult>(await new PadroesVersoesController(repo)
            .Itinerario(repo.VersaoId, default));
        var dto = Assert.IsType<ItinerarioPadraoVersaoDto>(result.Value);
        Assert.Equal("CIRCULAR", dto.Topologia);
        Assert.Equal(new[] { 1, 2, 3 }, dto.Ocorrencias.Select(x => x.Ordem));
        Assert.Equal(dto.Ocorrencias[0].ParadaId, dto.Ocorrencias[2].ParadaId);
        Assert.NotEqual(dto.Ocorrencias[0].OcorrenciaId, dto.Ocorrencias[2].OcorrenciaId);
        Assert.Equal(0, dto.Ocorrencias[0].PosicaoLinha);
        Assert.Equal(1, dto.Ocorrencias[^1].PosicaoLinha);
    }

    [Fact]
    public async Task VersaoInexistenteRetorna404()
        => Assert.IsType<NotFoundResult>(await new PadroesVersoesController(Scenario())
            .Itinerario(Guid.NewGuid(), default));

    [Fact]
    public async Task ParadaRetornaDetalhesSemMutacao()
    {
        var repo = Scenario();
        var result = Assert.IsType<OkObjectResult>(await new EstruturaParadasController(repo)
            .Buscar(repo.ParadaId, default));
        Assert.Equal("P001", Assert.IsType<ParadaEstruturalDto>(result.Value).Codigo);
        Assert.Equal(0, repo.MutationCount);
    }

    private static FakeRepository Scenario(bool multipattern = false, bool circular = false)
        => new(multipattern, circular);

    private sealed class FakeRepository : IEstruturaLeituraV2Repository
    {
        public Guid LinhaId { get; } = Guid.NewGuid();
        public Guid SentidoId { get; } = Guid.NewGuid();
        public Guid PadraoId { get; } = Guid.NewGuid();
        public Guid VersaoId { get; } = Guid.NewGuid();
        public Guid ParadaId { get; } = Guid.NewGuid();
        public int MutationCount { get; private set; }
        public (string? Nome, Guid? ModalId, string? TipoRota, string? ExcluirTipoRota) LastSearch { get; private set; }
        private readonly bool _multipattern;
        private readonly bool _circular;
        public FakeRepository(bool multipattern, bool circular)
        { _multipattern = multipattern; _circular = circular; }

        private LinhaEstruturalResumoDto Linha => new(LinhaId, "006", "Castelo - Silvestre",
            "regular", null, Guid.NewGuid(), "Ônibus");
        public Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(
            string? codigo, string? nome, Guid? modalId, string? tipoRota,
            string? excluirTipoRota, int pagina, int tamanhoPagina, CancellationToken ct)
        {
            LastSearch = (nome, modalId, tipoRota, excluirTipoRota);
            return Task.FromResult(new PaginacaoRespostaDTO<LinhaEstruturalResumoDto>
            { Pagina = pagina, TamanhoPagina = tamanhoPagina, TotalRegistros = 1,
                TotalPaginas = 1, Itens = [Linha] });
        }
        public Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string codigo, CancellationToken ct)
            => Task.FromResult<LinhaEstruturalResumoDto?>(codigo is "006" or "866"
                ? Linha with { Codigo = codigo } : null);
        public Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(string codigoLinha, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SentidoEstruturalResumoDto>?>(codigoLinha == "006"
                ? [new(SentidoId, LinhaId, "IDA")] : null);
        public Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(Guid sentidoId, CancellationToken ct)
        {
            if (sentidoId != SentidoId) return Task.FromResult<IReadOnlyList<PadraoOperacionalResumoDto>?>(null);
            List<PadraoOperacionalResumoDto> result =
                [new(PadraoId, SentidoId, "principal", "regular", null, VersaoId)];
            if (_multipattern) result.Add(new(Guid.NewGuid(), SentidoId, "variante", "regular", null, Guid.NewGuid()));
            return Task.FromResult<IReadOnlyList<PadraoOperacionalResumoDto>?>(result);
        }
        public Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(Guid padraoVersaoId, CancellationToken ct)
        {
            if (padraoVersaoId != VersaoId) return Task.FromResult<ItinerarioPadraoVersaoDto?>(null);
            var repeated = ParadaId;
            IReadOnlyList<OcorrenciaItinerarioDto> occurrences =
            [
                new(Guid.NewGuid(), 1, null, repeated, "P001", "Terminal", -22.9, -43.2, 0, 0),
                new(Guid.NewGuid(), 2, null, Guid.NewGuid(), "P002", "Intermediária", -22.91, -43.21, .5, 1000),
                new(Guid.NewGuid(), 3, null, repeated, "P001", "Terminal", -22.9, -43.2, 1, 2000)
            ];
            return Task.FromResult<ItinerarioPadraoVersaoDto?>(new(LinhaId, "006", "Linha",
                SentidoId, "IDA", PadraoId, "principal", "regular", null, VersaoId, 1,
                _circular ? "CIRCULAR" : "LINEAR", 2000, DateTimeOffset.UtcNow, true,
                new("LineString", [[-43.2, -22.9], [-43.21, -22.91]]), occurrences));
        }
        public Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid paradaId, CancellationToken ct)
            => Task.FromResult<ParadaEstruturalDto?>(paradaId == ParadaId
                ? new(ParadaId, "P001", "Terminal", -22.9, -43.2, null, "PARADA", null, null) : null);
    }
}
