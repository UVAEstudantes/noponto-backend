using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

internal sealed record CandidatoEstadoCausal(
    ObservacaoPosicaoTemporal Observacao,
    long? ExpectedTimestampMs,
    EstadoCausalPosicao Estado,
    CorrecaoTemporalPosicaoOptions? OpcoesEfetivas = null);

internal sealed record PreparacaoEstadoCausal(
    IReadOnlyDictionary<string, CandidatoEstadoCausal> Candidatos)
{
    public static PreparacaoEstadoCausal Vazia { get; } =
        new(new Dictionary<string, CandidatoEstadoCausal>(StringComparer.OrdinalIgnoreCase));
}

public sealed class CorrecaoTemporalPosicaoCoordinator(
    IEstadoCausalPosicaoRepository repository,
    IOptionsMonitor<CorrecaoTemporalPosicaoOptions> options,
    EstadoCausalPosicaoMetrics metrics,
    ILogger<CorrecaoTemporalPosicaoCoordinator> logger)
{
    public bool Enabled => options.CurrentValue.Enabled;

    internal async Task<PreparacaoEstadoCausal> PrepararAsync(
        IReadOnlyCollection<PosicaoVeiculoDto> posicoes,
        CancellationToken ct)
    {
        var opcoes = options.CurrentValue;
        if (!opcoes.Enabled) return PreparacaoEstadoCausal.Vazia;

        var observacoes = posicoes.Select(CriarObservacao)
            .Where(x => x is not null)
            .Cast<ObservacaoPosicaoTemporal>()
            .GroupBy(x => x.Ordem, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(o => o.TimestampGps).First())
            .ToArray();
        if (observacoes.Length == 0) return PreparacaoEstadoCausal.Vazia;

        try
        {
            var leituras = await repository.LerLoteAsync(
                observacoes.Select(x => x.Ordem).ToArray(), opcoes.StateBatchSize, ct);
            var motor = new MotorCorrecaoTemporalPosicao(opcoes);
            var candidatos = new Dictionary<string, CandidatoEstadoCausal>(
                observacoes.Length, StringComparer.OrdinalIgnoreCase);

            foreach (var observacao in observacoes)
            {
                if (!leituras.TryGetValue(observacao.Ordem, out var leitura)
                    || leitura.Status is EstadoCausalLeituraStatus.InfrastructureFailure
                        or EstadoCausalLeituraStatus.InvalidState
                        or EstadoCausalLeituraStatus.VersionUnsupported)
                    continue;

                var anterior = leitura.Status == EstadoCausalLeituraStatus.Hit
                    ? leitura.Estado : null;
                var atualizado = motor.AtualizarEstado(anterior, observacao);
                if (atualizado.Reiniciado) metrics.RegistrarReset();
                if (anterior is null || atualizado.Estado.Amostras.Count == 0)
                    metrics.RegistrarWarming();
                candidatos[observacao.Ordem] = new(
                    observacao, leitura.TimestampMs, atualizado.Estado, opcoes);
            }
            return new(candidatos);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RegistrarRedisFailure();
            logger.LogDebug(ex, "Falha fail-open na preparação do estado causal Redis.");
            return PreparacaoEstadoCausal.Vazia;
        }
    }

    internal async Task<IReadOnlyDictionary<string, CandidatoEstadoCausal>> PersistirAceitosAsync(
        PreparacaoEstadoCausal preparacao,
        IReadOnlyCollection<PosicaoVeiculoDto> aceitos,
        CancellationToken ct)
    {
        var opcoes = options.CurrentValue;
        var persistidos = new Dictionary<string, CandidatoEstadoCausal>(StringComparer.OrdinalIgnoreCase);
        if (!opcoes.Enabled || preparacao.Candidatos.Count == 0 || aceitos.Count == 0) return persistidos;

        var timestampsAceitos = aceitos.ToDictionary(
            x => x.Ordem, x => x.TimestampGps.ToUnixTimeMilliseconds(),
            StringComparer.OrdinalIgnoreCase);
        var pendentes = preparacao.Candidatos.Values
            .Where(x => timestampsAceitos.TryGetValue(x.Observacao.Ordem, out var ts)
                && ts == x.Observacao.TimestampGps.ToUnixTimeMilliseconds())
            .ToArray();
        if (pendentes.Length == 0) return persistidos;

        try
        {
            for (var tentativa = 0; pendentes.Length > 0; tentativa++)
            {
                var commits = pendentes.Select(x => new EstadoCausalCommit(
                    x.Observacao.Ordem, x.ExpectedTimestampMs,
                    x.Observacao.TimestampGps.ToUnixTimeMilliseconds(), x.Estado)).ToArray();
                var resultados = await repository.TentarAtualizarLoteAsync(
                    commits, opcoes.StateBatchSize, TimeSpan.FromSeconds(opcoes.StateTtlSeconds), ct);
                if (opcoes.ShadowEnabled)
                {
                    var aceitosNestaTentativa = resultados
                        .Where(x => x.Status == EstadoCausalCommitStatus.Accepted)
                        .Select(x => x.Ordem).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var candidato in pendentes.Where(x => aceitosNestaTentativa.Contains(x.Observacao.Ordem)))
                        persistidos[candidato.Observacao.Ordem] = candidato;
                }
                if (tentativa >= opcoes.StateConflictRetryCount) break;

                var conflitos = resultados
                    .Where(x => x.Status == EstadoCausalCommitStatus.Conflict)
                    .Select(x => x.Ordem).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (conflitos.Count == 0) break;

                var observacoes = pendentes.Where(x => conflitos.Contains(x.Observacao.Ordem))
                    .ToDictionary(x => x.Observacao.Ordem, x => x.Observacao,
                        StringComparer.OrdinalIgnoreCase);
                var leituras = await repository.LerLoteAsync(
                    conflitos.ToArray(), opcoes.StateBatchSize, ct);
                var motor = new MotorCorrecaoTemporalPosicao(opcoes);
                var recalculados = new List<CandidatoEstadoCausal>(conflitos.Count);
                foreach (var ordem in conflitos)
                {
                    if (!leituras.TryGetValue(ordem, out var leitura)
                        || leitura.Status is not (EstadoCausalLeituraStatus.Hit
                            or EstadoCausalLeituraStatus.Miss))
                        continue;
                    var observacao = observacoes[ordem];
                    var atualizado = motor.AtualizarEstado(leitura.Estado, observacao);
                    recalculados.Add(new(observacao, leitura.TimestampMs, atualizado.Estado, opcoes));
                }
                pendentes = recalculados.ToArray();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RegistrarRedisFailure();
            logger.LogDebug(ex, "Falha fail-open na persistência do estado causal Redis.");
        }
        return persistidos;
    }

    private static ObservacaoPosicaoTemporal? CriarObservacao(PosicaoVeiculoDto posicao)
    {
        if (posicao.ItinerarioId is not { } itinerario || itinerario == Guid.Empty
            || posicao.PosicaoNaRota is not { } posicaoNaRota
            || posicao.ComprimentoRotaMetros is not { } comprimento
            || !double.IsFinite(posicaoNaRota) || !double.IsFinite(comprimento)
            || posicaoNaRota is < 0 or > 1 || comprimento <= 0)
            return null;

        var modal = posicao.ModalFonte ?? string.Empty;
        var provedor = posicao.ProvedorFonte ?? string.Empty;
        return new(
            TelemetriaMlContrato.ObservacaoId(modal, provedor, posicao.Ordem, posicao.TimestampGps),
            posicao.Ordem, modal, provedor, posicao.CodigoLinha, itinerario,
            null, null, posicao.TimestampGps, posicaoNaRota, comprimento,
            posicao.Velocidade, posicao.VelocidadeMedia, TelemetriaMlContrato.OrigemReal);
    }
}
