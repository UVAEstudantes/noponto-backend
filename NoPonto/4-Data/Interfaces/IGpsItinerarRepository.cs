namespace NoPonto.Application.GPS;

/// <summary>
/// Repositório especializado em queries PostGIS para o subsistema de GPS em tempo real.
/// Separado dos repositórios de domínio para isolar as queries geoespaciais de alto desempenho.
/// </summary>
public interface IGpsPadraoRepository
{
    /// <summary>
    /// Primeira versao experimental do matching set-based. Nao e usada pelo fluxo
    /// de producao; mantem as operacoes separadas para permitir prova diferencial.
    /// </summary>
    Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    // Sobrecargas usadas somente pelo executor batch com a flag ON. Os contratos
    // antigos continuam sendo o caminho normal para consumidores existentes e fakes.
    Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        BuscarGlobaisEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

    Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        BuscarCombinadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

    Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        BuscarDirecionadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

    /// <summary>
    /// Executa em um comando o matching global e o matching de continuidade do
    /// padrao anterior dentro de uma faixa valida.
    /// </summary>
    Task<ResultadoMatchingCombinado> BuscarMatchingCombinadoAsync(
        string codigoLinha, Guid? padraoVersaoAnteriorId, double latitude, double longitude,
        double bearing, double distanciaMaximaMetros, FaixaProjecao? faixa,
        SolicitacaoProjecaoOperacional? projecaoOperacional = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reavalia um padrão na linha/GPS/bearing atuais, distinguindo inelegibilidade de falha.</summary>
    Task<ResultadoBuscaPadrao> BuscarEnriquecimentoDoPadraoAsync(
        string codigoLinha, Guid padraoVersaoId, double latitude, double longitude,
        double bearing, double distanciaMaximaMetros, CancellationToken cancellationToken = default,
        FaixaProjecao? faixa = null);

    /// <summary>
    /// Para um veículo em (latitude, longitude) numa determinada linha, retorna:
    /// - O padrão (ida ou volta) mais próximo ao veículo;
    /// - A posição na rota (0.0 → 1.0) via ST_LineLocatePoint;
    /// - O comprimento total da rota em metros;
    /// - A próxima parada à frente do veículo;
    /// - A distância até essa parada.
    ///
    /// Retorna null quando:
    ///   - A linha não tem padrãos cadastrados;
    ///   - O veículo está a mais de <paramref name="distanciaMaximaMetros"/> da rota;
    ///   - A query falha por qualquer motivo.
    /// </summary>
    Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna a geometria GeoJSON de um padrão para o frontend usar
    /// em interpolação local (dead-reckoning com Turf.js).
    /// </summary>
    Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid padraoVersaoId,
        CancellationToken cancellationToken = default);
}

// Contrato do pipeline GPS; não faz parte dos DTOs HTTP.
public enum StatusBuscaPadrao { Found, NotEligible, InfrastructureFailure }

// Frações globais da mesma LineString; o orçamento temporal pertence ao service.
public readonly record struct FaixaProjecao(double Min, double Max)
{
    public bool Valida => double.IsFinite(Min) && double.IsFinite(Max)
        && Min >= 0 && Max <= 1 && Min < Max;
}

public sealed class ResultadoBuscaPadrao
{
    public StatusBuscaPadrao Status { get; }
    public EnriquecimentoRotaDto? Rota { get; }
    private ResultadoBuscaPadrao(StatusBuscaPadrao status, EnriquecimentoRotaDto? rota = null)
        => (Status, Rota) = (status, rota);
    public static ResultadoBuscaPadrao Found(EnriquecimentoRotaDto rota)
        => new(StatusBuscaPadrao.Found, rota ?? throw new ArgumentNullException(nameof(rota)));
    public static ResultadoBuscaPadrao NotEligible() => new(StatusBuscaPadrao.NotEligible);
    public static ResultadoBuscaPadrao InfrastructureFailure() => new(StatusBuscaPadrao.InfrastructureFailure);
}

// Contrato interno do pipeline de matching; nao faz parte dos contratos HTTP.
public sealed record ResultadoMatchingCombinado(
    ResultadoBuscaPadrao Global,
    ResultadoBuscaPadrao Anterior,
    ResultadoProjecaoOperacional? Operacional = null);

// Contratos experimentais exclusivos do pipeline interno. InputId e a unica
// correlacao entre entrada e saida; nenhuma operacao depende da ordem do PostgreSQL.
// Bearing null formaliza o mesmo resultado final do runtime atual, que nao chama
// o repository nesse caso: inelegivel, sem comando PostgreSQL e nunca falha de infraestrutura.
public sealed record EntradaMatchingGlobalLote(
    string InputId, string CodigoLinha, double Latitude, double Longitude,
    double? Bearing, double DistanciaMaximaMetros);

public sealed record EntradaMatchingCombinadoLote(
    string InputId, string CodigoLinha, Guid? PadraoVersaoAnteriorId,
    double Latitude, double Longitude, double? Bearing, double DistanciaMaximaMetros,
    FaixaProjecao? Faixa, SolicitacaoProjecaoOperacional? ProjecaoOperacional = null);

public sealed record EntradaMatchingDirecionadoLote(
    string InputId, string CodigoLinha, Guid PadraoVersaoId,
    double Latitude, double Longitude, double? Bearing, double DistanciaMaximaMetros,
    FaixaProjecao? Faixa = null);

public sealed record ResultadoMatchingGlobalLote(string InputId, ResultadoBuscaPadrao Global);
public sealed record ResultadoMatchingCombinadoLote(string InputId, ResultadoMatchingCombinado Resultado);
public sealed record ResultadoMatchingDirecionadoLote(string InputId, ResultadoBuscaPadrao Direcionado);

public enum TipoBatchMatching { GlobalSimples, Combinado, Direcionado }
public enum OrigemComandoMatchingLote { Batch, FallbackIndividual }

public sealed record MetricaComandoMatchingLote(
    TipoBatchMatching Tipo,
    OrigemComandoMatchingLote Origem,
    int TamanhoBatch,
    TimeSpan Duracao);

public sealed record MetricasMatchingLote(
    int MatchingBatchOperations,
    IReadOnlyList<MetricaComandoMatchingLote> Comandos)
{
    public int MatchingCommandsPostgres => Comandos.Count;
    public int MatchingBatchCommandsPostgres =>
        Comandos.Count(x => x.Origem == OrigemComandoMatchingLote.Batch);
    public int MatchingFallbackCommandsPostgres =>
        Comandos.Count(x => x.Origem == OrigemComandoMatchingLote.FallbackIndividual);
    public IReadOnlyList<int> MatchingBatchSize => Comandos
        .Where(x => x.Origem == OrigemComandoMatchingLote.Batch)
        .Select(x => x.TamanhoBatch).ToArray();
    // Soma do tempo dos comandos batch, nao wall-clock da fase nem tempo de fallback.
    public TimeSpan MatchingBatchDuration => TimeSpan.FromTicks(Comandos
        .Where(x => x.Origem == OrigemComandoMatchingLote.Batch)
        .Sum(x => x.Duracao.Ticks));
    public int GlobalSimpleBatches => Comandos.Count(x => x.Tipo == TipoBatchMatching.GlobalSimples
        && x.Origem == OrigemComandoMatchingLote.Batch);
    public int CombinedBatches => Comandos.Count(x => x.Tipo == TipoBatchMatching.Combinado
        && x.Origem == OrigemComandoMatchingLote.Batch);
    public int DirectedBatches => Comandos.Count(x => x.Tipo == TipoBatchMatching.Direcionado
        && x.Origem == OrigemComandoMatchingLote.Batch);
}

public sealed record ResultadoMatchingLote<T>(
    IReadOnlyList<T> Resultados,
    MetricasMatchingLote Metricas);
