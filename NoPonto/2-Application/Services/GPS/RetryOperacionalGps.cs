using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class RetryOperacionalGpsOptions
{
    public int TtlSegundos { get; set; } = 180;
    public int BackoffSegundos { get; set; } = 20;
    public int MaxTentativas { get; set; } = 4;
    public int MaxPorVeiculo { get; set; } = 3;
    public int MaxGlobal { get; set; } = 2000;
    public int LotePorCiclo { get; set; } = 4;
    public int TimeoutTentativaSegundos { get; set; } = 3;
    public int LeaseSegundos { get; set; } = 60;
    public bool Valido() => TtlSegundos is >= 20 and <= 300 && BackoffSegundos > 0
        && MaxTentativas is >= 1 and <= 10 && MaxPorVeiculo is >= 1 and <= 10
        && MaxGlobal is >= 1 and <= 10000 && LotePorCiclo is >= 1 and <= 100
        && LeaseSegundos is >= 30 and <= 300 && TimeoutTentativaSegundos is >= 1 and <= 20;
}

public sealed record PendenciaOperacionalGps(string Id, PosicaoVeiculoDto Gps,
    DateTimeOffset CriadaEm, DateTimeOffset ExpiraEm, int Tentativas = 0,
    Guid? ViagemAnterior = null, DateTimeOffset? TimestampOperacionalAnterior = null,
    PosicaoVeiculoDto? Predecessor = null, bool AguardandoAnterior = false)
{
    public string Assinatura => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Gps.Ordem, Gps.CodigoLinha, Gps.TimestampGps,
            Gps.Latitude,Gps.Longitude,Gps.Bearing,Gps.Velocidade,Gps.LatitudeAnterior,
            Gps.LongitudeAnterior,Gps.TimestampAnterior,Gps.ModalFonte,Gps.ProvedorFonte,
            Predecessor = Predecessor is null ? null : new { Predecessor.Ordem,
                Predecessor.CodigoLinha,Predecessor.TimestampGps,Predecessor.Latitude,
                Predecessor.Longitude,Predecessor.Bearing,Predecessor.Velocidade,
                Predecessor.ModalFonte,Predecessor.ProvedorFonte } })));
}
public sealed record LeasePendenciaOperacional(PendenciaOperacionalGps Pendencia, string Token);

public interface IPendenciaOperacionalGpsStore
{
    Task<string> AdicionarAsync(PendenciaOperacionalGps pendencia, CancellationToken ct);
    Task<LeasePendenciaOperacional?> ClaimAsync(string ordem, DateTimeOffset agora, CancellationToken ct);
    Task<bool> ConcluirAsync(LeasePendenciaOperacional lease, CancellationToken ct);
    Task<bool> ReagendarAsync(LeasePendenciaOperacional lease, DateTimeOffset quando, CancellationToken ct);
    Task<bool> TemPendenciaAsync(string ordem, CancellationToken ct);
    Task<IReadOnlyList<string>> VeiculosElegiveisAsync(DateTimeOffset agora, CancellationToken ct);
}

internal interface IEnriquecimentoRetryOperacionalGps
{
    Task<ResultadoEnriquecimentoGps> RecalcularAsync(PendenciaOperacionalGps pendencia,
        ContextoOperacional? contexto, CancellationToken ct);
}

public interface IRetryOperacionalGps
{
    Task RegistrarAsync(PosicaoVeiculoDto gps, PosicaoVeiculoDto? predecessor, ContextoOperacional? contexto,
        CancellationToken ct, bool aguardandoAnterior = false);
    Task<bool> TemPendenciaAsync(string ordem, CancellationToken ct);
    Task RecuperarVeiculoAsync(string ordem, CancellationToken ct);
    Task ExecutarCicloAsync(CancellationToken ct);
}

/// <summary>Melhor esforço Redis, acionado pelo polling; não é ingress durável.</summary>
internal sealed class RetryOperacionalGpsService(IPendenciaOperacionalGpsStore store,
    ViagemObservadaService viagens, IEnriquecimentoRetryOperacionalGps enriquecimento,
    IOptions<RetryOperacionalGpsOptions> options, IOptions<GpsPollingOptions> gpsOptions,
    ILogger<RetryOperacionalGpsService> logger) : IRetryOperacionalGps
{
    internal Func<DateTimeOffset> Agora { get; init; } = () => DateTimeOffset.UtcNow;
    private long _ultimoErro;
    private int _restantes = options.Value.LotePorCiclo;

    // Limpar identidade derivada; JsonIgnore não é usado como prova recuperada.
    internal static PosicaoVeiculoDto Observacional(PosicaoVeiculoDto p) => p with
    {
        MatchingOperacionalPlausivel = false, ExigirPersistenciaDuravel = false,
        PadraoVersaoId = null, PadraoOperacionalId = null, LinhaId = null, SentidoId = null,
        TopologiaPadrao = null, PosicaoNaRota = null, ComprimentoRotaMetros = null,
        ProximaOcorrenciaParadaPadraoId = null,
    };

    public async Task RegistrarAsync(PosicaoVeiculoDto posicao, PosicaoVeiculoDto? predecessor,
        ContextoOperacional? contexto, CancellationToken ct, bool aguardandoAnterior = false)
    {
        var gps = Observacional(posicao);
        var now = Agora();
        var id = TelemetriaMlContrato.ObservacaoId(gps.ModalFonte, gps.ProvedorFonte, gps.Ordem, gps.TimestampGps);
        var o = contexto?.Observada;
        var p = new PendenciaOperacionalGps(id, gps, now,
            now.AddSeconds(options.Value.TtlSegundos), ViagemAnterior: o?.ViagemId,
            TimestampOperacionalAnterior: o?.TimestampUltimaAtualizacao,
            Predecessor: predecessor is null ? null : Observacional(predecessor), AguardandoAnterior: aguardandoAnterior);
        try
        {
            var resultado = await store.AdicionarAsync(p, ct);
            if (resultado != "EXISTENTE") logger.LogWarning("Pendência GPS {id}: {resultado}.", id, resultado);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Indisponivel(ex); }
    }

    public async Task<bool> TemPendenciaAsync(string ordem, CancellationToken ct)
    {
        try { return await store.TemPendenciaAsync(ordem, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Indisponivel(ex); return true; } // Mapa segue; operação não ultrapassa backlog desconhecido.
    }

    public async Task ExecutarCicloAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _restantes, options.Value.LotePorCiclo);
        try
        {
            foreach (var ordem in await store.VeiculosElegiveisAsync(Agora(), ct))
                await RecuperarVeiculoAsync(ordem, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Indisponivel(ex); }
    }

    public async Task RecuperarVeiculoAsync(string ordem, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref _restantes) < 0) return;
        try
        {
            // Um item por veículo/chamada: sem loop agressivo, ordem é definida pelo store.
            var lease = await store.ClaimAsync(ordem, Agora(), ct);
            if (lease is null) { Interlocked.Increment(ref _restantes); return; }
            var p = lease.Pendencia;
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutTentativaSegundos));
            var tentativaCt = limite.Token;
            string? descarte = null;
            try
            {
                if (Agora() >= p.ExpiraEm) descarte = "Expirada";
                else if (p.Tentativas >= options.Value.MaxTentativas) descarte = "LimiteTentativas";
                else if (!GpsLeituraValidator.TimestampValido(p.Gps.TimestampGps, Agora(), out _)
                    || (Agora()-p.Gps.TimestampGps).TotalSeconds > gpsOptions.Value.MaxIdadeGpsSegundos)
                    descarte = "TimestampInelegivel";
                if (descarte is not null) { await EncerrarAsync(lease, descarte, ct); return; }

                var duravel = await viagens.LerDuravelParaRetryAsync(ordem, tentativaCt);
                if (duravel?.Observada is { } d && d.TimestampUltimaAtualizacao >= p.Gps.TimestampGps)
                {
                    await EncerrarAsync(lease, d.TimestampUltimaAtualizacao == p.Gps.TimestampGps
                        ? "CommitDuravelReconhecido" : "SuperadaPorEstadoDuravelMaisNovo", ct);
                    return;
                }
                var contexto = await viagens.LerContextoParaRetryAsync(ordem, tentativaCt);
                if (contexto?.Observada is { } atual)
                {
                    if (atual.TimestampUltimaAtualizacao >= p.Gps.TimestampGps
                        || (p.ViagemAnterior is { } id && atual.ViagemId != id)
                        || (!p.AguardandoAnterior && p.TimestampOperacionalAnterior is { } ts && atual.TimestampUltimaAtualizacao != ts))
                    { await EncerrarAsync(lease, "ContextoAlteradoOuProgressoQuenteMaisNovo", ct); return; }
                }
                else if (p.ViagemAnterior is not null)
                { await EncerrarAsync(lease, "ContextoAnteriorPerdido", ct); return; }

                var gps = p.Gps;
                if (p.Predecessor is not { } anterior || anterior.Ordem != gps.Ordem
                    || anterior.TimestampGps != gps.TimestampAnterior
                    || anterior.Latitude != gps.LatitudeAnterior || anterior.Longitude != gps.LongitudeAnterior
                    || !gps.TemHistorico || gps.TimestampAnterior >= gps.TimestampGps
                    || (gps.TimestampGps - gps.TimestampAnterior!.Value).TotalSeconds > 180
                    || !GpsLeituraValidator.CoordenadaValida(gps.LatitudeAnterior!.Value, gps.LongitudeAnterior!.Value)
                    || !GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
                    || GpsEnriquecimentoService.EhSaltoImplausivel(gps.LatitudeAnterior.Value,
                        gps.LongitudeAnterior.Value, gps.Latitude, gps.Longitude,
                        (gps.TimestampGps - gps.TimestampAnterior.Value).TotalSeconds, gpsOptions.Value.VelocidadeMaximaKmh))
                { await EncerrarAsync(lease, "PredecessorFisicoInsuficienteOuImplausivel", ct); return; }

                logger.LogInformation("Retry operacional GPS {id}, tentativa {tentativa}.", p.Id, p.Tentativas + 1);
                var refeito = await enriquecimento.RecalcularAsync(p, contexto, tentativaCt);
                if (refeito.Posicao.Ordem != gps.Ordem || refeito.Posicao.TimestampGps != gps.TimestampGps
                    || refeito.Posicao.CodigoLinha != gps.CodigoLinha || refeito.Posicao.Latitude != gps.Latitude
                    || refeito.Posicao.Longitude != gps.Longitude)
                { await EncerrarAsync(lease, "ReenriquecimentoAlterouObservacao", ct); return; }
                if (!refeito.Posicao.MatchingOperacionalPlausivel)
                { await ReagendarAsync(lease, ct); return; } // Ausência de matching também pode ser infraestrutura.
                var resultado = await viagens.AtualizarAsync(refeito with
                { Posicao = refeito.Posicao with { ExigirPersistenciaDuravel = true } }, tentativaCt);
                if (resultado is { PersistidoDuravelmente: true })
                    await EncerrarAsync(lease, "RecuperacaoCommitDuravel", ct);
                else if (resultado?.Status is ViagemObservadaStatus.InvalidState
                    or ViagemObservadaStatus.InvalidSequence or ViagemObservadaStatus.OccurrenceNotFromItinerary
                    or ViagemObservadaStatus.ItineraryChanged)
                    await EncerrarAsync(lease, "RejeicaoOperacional:" + resultado.Value.Status, ct);
                else await ReagendarAsync(lease, ct); // Updated sem confirmação nunca é ACK.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Indisponivel(ex); await ReagendarAsync(lease, ct); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Indisponivel(ex); } // Lease expira se Redis caiu; pendência não vira sucesso.
    }

    private async Task EncerrarAsync(LeasePendenciaOperacional lease, string motivo, CancellationToken ct)
    {
        if (await store.ConcluirAsync(lease, ct))
            logger.LogInformation("Pendência operacional GPS {id} encerrada: {motivo}.", lease.Pendencia.Id, motivo);
    }
    private Task<bool> ReagendarAsync(LeasePendenciaOperacional lease, CancellationToken ct) =>
        store.ReagendarAsync(lease, Agora().AddSeconds(Math.Min(options.Value.TtlSegundos,
            options.Value.BackoffSegundos * Math.Pow(2, lease.Pendencia.Tentativas))), ct);
    private void Indisponivel(Exception ex)
    {
        var minuto = Agora().ToUnixTimeSeconds() / 60;
        if (Interlocked.Exchange(ref _ultimoErro, minuto) != minuto)
            logger.LogWarning(ex, "Retry operacional indisponível; mapa independente, recuperação de melhor esforço.");
    }
}
