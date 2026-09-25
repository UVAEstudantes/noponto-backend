using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using Npgsql;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>PostgreSQL e a autoridade; Redis recebe somente uma projecao descartavel.</summary>
public sealed class ViagemOperacionalRepository(IConnectionMultiplexer redis, NpgsqlDataSource source,
    IOptions<GpsPollingOptions> options,
    ILogger<ViagemOperacionalRepository> logger) : IViagemObservadaRepository
{
    public const string Stream = "noponto:viagem:eventos";
    internal static readonly TimeSpan ProjectionTtl = TimeSpan.FromHours(24);
    internal string StreamKey { get; init; } = Stream;
    internal Func<Task>? BeforeDurableProjectionAsync { get; init; }
    internal Func<Task>? AfterDurableStateWriteAsync { get; init; }
    internal Func<Task>? AfterDurableOutboxWriteAsync { get; init; }
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    private long _nextRedisWarningAt;
    private int _suppressedRedisWarnings;
    private const long RedisWarningWindowMs = 60_000;

    public Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem, Guid itinerarioId,
        DateTimeOffset timestampGps, double posicaoNaRota, CancellationToken ct) =>
        Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.InvalidState));

    public async Task<ContextoOperacional?> LerContextoAsync(string ordem, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ordem)) return null;
        var performance = GpsCommitPerformanceContext.Current;
        try
        {
            var values = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(
                ViagemOperacionalRedisScript.Read, [ViagemObservadaRepository.ChaveVeiculoViagem(ordem)]);
            if (values is { Length: > 0 })
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < values.Length; i += 2)
                    map[(string)values[i]!] = (string)values[i + 1]!;
                if (long.TryParse(map.GetValueOrDefault(ViagemOperacionalRedisScript.DurableVersion),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                    && version > 0
                    && long.TryParse(map.GetValueOrDefault(ViagemOperacionalRedisScript.DurableCheckpoint),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var checkpointTicks))
                {
                    var state = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names
                        .ToDictionary(n => n, n => map[n]), ordem);
                    performance?.RegistrarViagemRedisContextHit();
                    return new(["postgres", version.ToString(CultureInfo.InvariantCulture)],
                        state.Observada, state, version,
                        new DateTimeOffset(checkpointTicks, TimeSpan.Zero));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Contexto Redis de {ordem} inválido ou indisponível; usando PostgreSQL.", ordem);
        }

        performance?.RegistrarViagemPgFallbackRead();
        await using var connection = await source.OpenConnectionAsync(ct);
        performance?.RegistrarViagemPgRead();
        var durable = await LerDuravelAsync(connection, null, ordem, false, ct);
        if (durable is null) return new([], null, null);
        await ProjetarRedisAsync(ordem, durable.Estado, [], durable.Versao, durable.AtualizadoEmUtc, ct);
        return Contexto(durable);
    }

    public Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps, CancellationToken ct) =>
        TentarAtualizarInternoAsync(gps, null, ResultadoProjecaoOperacional.NaoSolicitada(), false, ct);

    public Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps,
        ContextoOperacional? contexto, ResultadoProjecaoOperacional projecao, CancellationToken ct) =>
        TentarAtualizarInternoAsync(gps, contexto, projecao, true, ct);

    private async Task<ViagemObservadaResultado> TentarAtualizarInternoAsync(
        PosicaoVeiculoDto gps, ContextoOperacional? contexto,
        ResultadoProjecaoOperacional projecao, bool snapshotFornecido, CancellationToken ct)
    {
        if (gps.ItinerarioId is not { } itinerary || itinerary == Guid.Empty || gps.PosicaoNaRota is not { } p
            || !double.IsFinite(p) || p is < 0 or > 1 || string.IsNullOrWhiteSpace(gps.Ordem)
            || string.IsNullOrWhiteSpace(gps.CodigoLinha)
            || !GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
            || !GpsLeituraValidator.TimestampValido(gps.TimestampGps, DateTimeOffset.UtcNow, out _))
            return new(ViagemObservadaStatus.InvalidState);

        try
        {
            if (!snapshotFornecido)
            {
                contexto = await LerContextoAsync(gps.Ordem, ct);
                snapshotFornecido = true;
            }
            if (snapshotFornecido && contexto is null)
                return new(ViagemObservadaStatus.Conflict);
            var previous = contexto?.Estado;
            var observed = previous?.Observada;
            if (observed is not null && gps.TimestampGps <= observed.TimestampUltimaAtualizacao)
                return new(ViagemObservadaStatus.RejectedOlderOrEqual, observed);

            var gpsOperacional = gps;
            var usandoProjecao = false;
            var divergente = previous?.Estado is EstadoViagem.Ativa or EstadoViagem.PossivelFim
                && observed!.ItinerarioId != itinerary;
            if (divergente && projecao.Status != StatusProjecaoOperacional.NaoSolicitada)
            {
                if (projecao.Status == StatusProjecaoOperacional.FalhaInfraestrutura)
                    return new(ViagemObservadaStatus.InfrastructureFailure, observed);
                if (projecao.Status != StatusProjecaoOperacional.Encontrada
                    || projecao.Projecao is not { } op || op.ItinerarioId != observed!.ItinerarioId
                    || !double.IsFinite(op.PosicaoNaRota) || op.PosicaoNaRota is < 0 or > 1
                    || op.PosicaoNaRota < observed.PosicaoNaRotaConfirmada
                    || !double.IsFinite(op.ComprimentoRotaMetros) || op.ComprimentoRotaMetros <= 0)
                    return Divergencia(observed!);
                gpsOperacional = gps with { CodigoLinha = previous!.CodigoLinha,
                    ItinerarioId = previous.Observada.ItinerarioId, PosicaoNaRota = op.PosicaoNaRota,
                    ComprimentoRotaMetros = op.ComprimentoRotaMetros };
                itinerary = op.ItinerarioId; p = op.PosicaoNaRota; usandoProjecao = true;
            }
            if (previous?.Estado == EstadoViagem.Ativa && observed!.ItinerarioId != itinerary)
                return Divergencia(observed);

            await using var connection = await source.OpenConnectionAsync(ct);
            EstruturaViagem? structure;
            if (usandoProjecao && previous is not null)
                structure = new(previous.Observada.ItinerarioId, previous.LinhaId,
                    previous.SentidoId, previous.CodigoLinha, true);
            else
            {
                GpsCommitPerformanceContext.Current?.RegistrarViagemPgRead();
                structure = await EstruturaAsync(connection, null, gpsOperacional, ct);
            }
            if (structure is null) return new(ViagemObservadaStatus.InvalidSequence);
            var baseline = previous is null || previous.Estado == EstadoViagem.Finalizada
                || previous.Observada.ItinerarioId != itinerary;
            GpsCommitPerformanceContext.Current?.RegistrarViagemPgRead();
            var transition = await OcorrenciaParadaRepository.BuscarTransicaoNaConexaoAsync(connection, null,
                itinerary, previous is null || previous.Observada.ItinerarioId != itinerary
                    ? p : previous.Observada.PosicaoNaRotaConfirmada, p,
                baseline ? Guid.Empty : previous!.Observada.UltimaParadaItinerarioId,
                baseline ? 0 : previous!.Observada.UltimaParadaOrdem, baseline, ct);
            if (transition.Status != ViagemObservadaStatus.Updated) return new(transition.Status);
            var decision = ViagemOperacionalRegra.Decidir(previous, structure, gpsOperacional,
                transition, Guid.NewGuid());
            if (previous is not null && transition.Ultrapassadas.Count > 0)
                GpsCommitPerformanceContext.Current?.RegistrarCatchupPassagens(
                    transition.Ultrapassadas.Count,
                    gpsOperacional.TimestampGps - previous.Observada.TimestampUltimaAtualizacao);
            foreach (var evento in decision.Eventos) EventoViagemValidator.Validar(evento);
            var checkpointSeconds = options.Value.CheckpointViagemSegundos;
            var checkpointInterval = checkpointSeconds <= 0
                ? TimeSpan.Zero : TimeSpan.FromSeconds(checkpointSeconds);
            var motivo = PersistenciaViagemOperacional.DevePersistirDuravelmente(previous,
                decision.Estado, decision.Eventos, contexto?.UltimoCheckpointUtc,
                UtcNow(), checkpointInterval);
            if (motivo == MotivoPersistenciaViagem.Nenhum)
            {
                try
                {
                    var hot = await TentarProjetarQuenteAsync(gps.Ordem, previous!, decision.Estado,
                        VersaoContexto(contexto), ct);
                    if (!hot)
                    {
                        var atual = await LerContextoAsync(gps.Ordem, ct);
                        return atual?.Observada is { } observadaAtual
                            && gps.TimestampGps <= observadaAtual.TimestampUltimaAtualizacao
                            ? new(ViagemObservadaStatus.RejectedOlderOrEqual, observadaAtual)
                            : new(ViagemObservadaStatus.Conflict, atual?.Observada ?? previous!.Observada);
                    }
                    GpsCommitPerformanceContext.Current?.RegistrarViagemDurableWriteSkipped();
                    return new(ViagemObservadaStatus.Updated, decision.Estado.Observada)
                    {
                        OcorrenciasUltrapassadas = [], ProximaOcorrenciaOperacional = transition.Proxima
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    LogRedisUnavailable(ex,
                        "Redis quente indisponível; persistindo checkpoints conservadores.");
                    motivo = MotivoPersistenciaViagem.Checkpoint;
                }
            }

            await using var transaction = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);
            GpsCommitPerformanceContext.Current?.RegistrarViagemTransacao();
            // Lock por veiculo cobre inclusive a primeira insercao, sem lock global.
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@ordem, 0))", connection, transaction))
            {
                advisory.Parameters.AddWithValue("ordem", gps.Ordem);
                await advisory.ExecuteNonQueryAsync(ct);
                GpsCommitPerformanceContext.Current?.RegistrarViagemAdvisoryLock();
            }

            var durable = await LerDuravelAsync(connection, transaction, gps.Ordem, true, ct);
            GpsCommitPerformanceContext.Current?.RegistrarViagemPgRead();
            if (snapshotFornecido && VersaoContexto(contexto) != (durable?.Versao ?? 0))
                return durable?.Estado.Observada is { } atualDuravel
                    && gps.TimestampGps <= atualDuravel.TimestampUltimaAtualizacao
                    ? new(ViagemObservadaStatus.RejectedOlderOrEqual, atualDuravel)
                    : new(ViagemObservadaStatus.Conflict, durable?.Estado.Observada ?? contexto?.Observada);
            var encoded = ViagemOperacionalCodec.Encode(decision.Estado);
            _ = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(encoded)
                .ToDictionary(x => x.First, x => x.Second), gps.Ordem);
            foreach (var evento in decision.Eventos) EventoViagemValidator.Validar(evento);
            var nextVersion = (durable?.Versao ?? 0) + 1;
            await GravarEstadoAsync(connection, transaction, gps.Ordem, encoded, nextVersion, ct);
            if (AfterDurableStateWriteAsync is not null)
                await AfterDurableStateWriteAsync();
            await InserirOutboxAsync(connection, transaction, decision.Eventos, ct);
            if (AfterDurableOutboxWriteAsync is not null)
                await AfterDurableOutboxWriteAsync();
            await transaction.CommitAsync(ct);
            var checkpointUtc = UtcNow();
            GpsCommitPerformanceContext.Current?.RegistrarViagemDurableWrite(
                motivo == MotivoPersistenciaViagem.Checkpoint, decision.Eventos.Count);
            if (BeforeDurableProjectionAsync is not null)
                await BeforeDurableProjectionAsync();
            await ProjetarRedisAsync(gps.Ordem, decision.Estado, decision.Eventos,
                nextVersion, checkpointUtc, ct);
            var status = durable is null ? ViagemObservadaStatus.Created : ViagemObservadaStatus.Updated;
            return new(status, decision.Estado.Observada)
            {
                OcorrenciasUltrapassadas = status == ViagemObservadaStatus.Updated && !baseline
                    && previous?.Estado != EstadoViagem.Finalizada ? transition.Ultrapassadas : [],
                ProximaOcorrenciaOperacional = transition.Proxima,
            };
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Estado operacional invalido de {ordem}; nenhuma alteracao.", gps.Ordem);
            return new(ViagemObservadaStatus.InvalidState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na transacao duravel de viagem/outbox de {ordem}.", gps.Ordem);
            return new(ViagemObservadaStatus.InfrastructureFailure);
        }
    }

    private static ContextoOperacional Contexto(EstadoDuravel durable) => new(
        ["postgres", durable.Versao.ToString(CultureInfo.InvariantCulture)],
        durable.Estado.Observada, durable.Estado, durable.Versao, durable.AtualizadoEmUtc);

    private static long VersaoContexto(ContextoOperacional? contexto) =>
        contexto?.SnapshotCas.Count == 2 && contexto.SnapshotCas[0] == "postgres"
        && long.TryParse(contexto.SnapshotCas[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version : 0;

    private static async Task<EstadoDuravel?> LerDuravelAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string ordem, bool forUpdate, CancellationToken ct)
    {
        var sql = "SELECT \"Estado\"::text, \"Versao\", \"AtualizadoEmUtc\" FROM \"ViagensOperacionais\" WHERE \"OrdemVeiculo\"=@ordem"
            + (forUpdate ? " FOR UPDATE" : "");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ordem", ordem);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var values = JsonSerializer.Deserialize<string[]>(reader.GetString(0))
            ?? throw new FormatException("Estado duravel vazio.");
        if (values.Length != ViagemOperacionalCodec.Names.Length)
            throw new FormatException("Versao desconhecida do estado duravel.");
        var state = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(values)
            .ToDictionary(x => x.First, x => x.Second), ordem);
        return new(state, reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task GravarEstadoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string ordem, string[] state, long version, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "ViagensOperacionais" ("OrdemVeiculo","Estado","Versao","AtualizadoEmUtc")
            VALUES (@ordem,@estado::jsonb,@versao,now())
            ON CONFLICT ("OrdemVeiculo") DO UPDATE SET
                "Estado"=EXCLUDED."Estado", "Versao"=EXCLUDED."Versao", "AtualizadoEmUtc"=EXCLUDED."AtualizadoEmUtc"
            """, connection, transaction);
        command.Parameters.AddWithValue("ordem", ordem);
        command.Parameters.AddWithValue("estado", JsonSerializer.Serialize(state));
        command.Parameters.AddWithValue("versao", version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InserirOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<EventoViagem> eventos, CancellationToken ct)
    {
        if (eventos.Count == 0) return;
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var evento in eventos)
        {
            var payload = JsonSerializer.Serialize(evento);
            var command = new NpgsqlBatchCommand("""
                INSERT INTO "OutboxViagens" ("EventId","Tipo","Payload","CriadoEmUtc","Tentativas")
                VALUES (@id,@tipo,@payload::jsonb,now(),0)
                ON CONFLICT ("EventId") DO UPDATE SET "EventId"=EXCLUDED."EventId"
                RETURNING "Payload" = @payload::jsonb
                """);
            command.Parameters.AddWithValue("id", evento.EventId);
            command.Parameters.AddWithValue("tipo", evento.Tipo);
            command.Parameters.AddWithValue("payload", payload);
            batch.BatchCommands.Add(command);
        }
        await using var reader = await batch.ExecuteReaderAsync(ct);
        for (var i = 0; i < eventos.Count; i++)
        {
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0))
                throw new EventoViagemPayloadConflictException(eventos[i].EventId, ["payload"], false);
            if (i + 1 < eventos.Count && !await reader.NextResultAsync(ct))
                throw new InvalidOperationException("Resultado incompleto do batch de Outbox.");
        }
    }

    private async Task ProjetarRedisAsync(string ordem, ViagemOperacionalState state,
        IReadOnlyList<EventoViagem> events, long version, DateTimeOffset checkpointUtc, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var db = redis.GetDatabase();
            RedisKey key = ViagemObservadaRepository.ChaveVeiculoViagem(ordem);
            var encoded = ViagemOperacionalCodec.Encode(state);
            var args = encoded.Select(x => (RedisValue)x).Append(version).Append(
                ViagemOperacionalCodec.Tick(checkpointUtc)).Append((long)ProjectionTtl.TotalSeconds).ToArray();
            var projection = (long)await db.ScriptEvaluateAsync(
                ViagemOperacionalRedisScript.ProjectDurable, [key], args);
            if (projection is 3 or 4)
                GpsCommitPerformanceContext.Current?.RegistrarViagemRedisProjectionPreservedNewer();
            if (projection is not (2 or 3 or 4))
                throw new InvalidOperationException($"Projeção Redis rejeitada ({projection}).");
            // Espelho temporario e bounded para diagnostico/compatibilidade; nenhum consumidor
            // funcional depende dele e a fonte duravel e sempre o outbox PostgreSQL.
            var streamWrites = events.Select(e => db.StreamAddAsync(StreamKey,
                Fields(e).Select(x => new NameValueEntry(x.Key, x.Value)).ToArray(),
                maxLength: 10_000, useApproximateMaxLength: true)).ToArray();
            await Task.WhenAll(streamWrites).WaitAsync(ct);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(ex,
                "Projecao Redis cancelada depois do commit duravel da viagem de {ordem}.", ordem);
        }
        catch (Exception ex)
        {
            LogRedisUnavailable(ex,
                "Projecao Redis indisponivel; PostgreSQL permanece autoritativo.");
        }
    }

    private void LogRedisUnavailable(Exception exception, string message)
    {
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _nextRedisWarningAt);
        if (now < next || Interlocked.CompareExchange(ref _nextRedisWarningAt,
                now + RedisWarningWindowMs, next) != next)
        {
            Interlocked.Increment(ref _suppressedRedisWarnings);
            return;
        }
        var suppressed = Interlocked.Exchange(ref _suppressedRedisWarnings, 0);
        logger.LogWarning(exception,
            "{message} Ocorrencias suprimidas desde o ultimo aviso: {suppressed}.", message, suppressed);
    }

    private static ViagemObservadaResultado Divergencia(ViagemObservadaState observed) =>
        new(ViagemObservadaStatus.ItineraryChanged, observed);

    internal static Dictionary<string, string> Fields(EventoViagem evento)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evento));
        return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String
                ? p.Value.GetString()! : p.Value.GetRawText());
    }

    private async Task<EstruturaViagem?> EstruturaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        PosicaoVeiculoDto gps, CancellationToken ct)
    {
        const string sql = """
            WITH escolhida AS (
                SELECT i."Id", s."LinhaId", s."Id" AS sentido, l."Codigo"
                FROM "Itinerarios" i JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                JOIN "Linhas" l ON l."Id" = s."LinhaId"
                WHERE i."Id" = @id AND l."Codigo" = @codigo
            ), candidatos AS (
                SELECT DISTINCT s."Id" FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id" = i."SentidoId" JOIN escolhida e ON e."LinhaId" = s."LinhaId"
                CROSS JOIN LATERAL (SELECT ST_LineLocatePoint(i."Geometria", ST_SetSRID(ST_MakePoint(@lon,@lat),4326)) AS p) local
                WHERE ST_DWithin(i."Geometria"::geography, ST_SetSRID(ST_MakePoint(@lon,@lat),4326)::geography,@dist)
                AND abs(mod((degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(i."Geometria",greatest(0,local.p-0.025))::geography,
                    ST_LineInterpolatePoint(i."Geometria",least(1,local.p+0.025))::geography)) - @bearing + 540)::numeric,360)-180) < 80
            ) SELECT e."Id", e."LinhaId", e.sentido, e."Codigo",
                @tem_bearing AND (SELECT count(*) FROM candidatos) = 1
                AND EXISTS (SELECT 1 FROM candidatos WHERE "Id" = e.sentido)
            FROM escolhida e
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", gps.ItinerarioId!.Value);
        command.Parameters.AddWithValue("codigo", gps.CodigoLinha);
        command.Parameters.AddWithValue("lat", gps.Latitude);
        command.Parameters.AddWithValue("lon", gps.Longitude);
        command.Parameters.AddWithValue("dist", options.Value.DistanciaMaximaRotaMetros);
        command.Parameters.AddWithValue("bearing", gps.Bearing ?? 0);
        command.Parameters.AddWithValue("tem_bearing",
            gps.Bearing is { } b && double.IsFinite(b) && b is >= 0 and < 360);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetBoolean(4)) : null;
    }

    private async Task<bool> TentarProjetarQuenteAsync(string ordem, ViagemOperacionalState previous,
        ViagemOperacionalState next, long version, CancellationToken ct)
    {
        var result = (int)await redis.GetDatabase().ScriptEvaluateAsync(
            ViagemOperacionalRedisScript.CommitHot,
            [ViagemObservadaRepository.ChaveVeiculoViagem(ordem)],
            [JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(previous)),
                JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(next)),
                version.ToString(CultureInfo.InvariantCulture),
                ((long)ProjectionTtl.TotalSeconds).ToString(CultureInfo.InvariantCulture)]);
        return result == (int)ViagemObservadaStatus.Updated;
    }

    private sealed record EstadoDuravel(ViagemOperacionalState Estado, long Versao,
        DateTimeOffset AtualizadoEmUtc);
}
