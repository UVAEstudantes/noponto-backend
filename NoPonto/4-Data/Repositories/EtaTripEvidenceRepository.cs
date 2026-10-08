using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

/// <summary>Caller owns the SAME transaction. Local coverage writer reuses head CAS/outbox atomically.</summary>
public static class EtaTripEvidenceRepository
{
    public static async Task RevokeEpochAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid epochId, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("SELECT \"Payload\"::text FROM \"EtaProducerEpochs\" WHERE \"EpochId\"=@id FOR UPDATE",connection,transaction);
        read.Parameters.AddWithValue("id",epochId);
        if (await read.ExecuteScalarAsync(ct) is not string json) throw new InvalidOperationException("Epoch missing");
        var epoch=EtaTripEvidence.Parse<EtaProducerEpoch>(json);
        if(epoch.RevokedUs is not null)return;
        await using var timestamp=new NpgsqlCommand("SELECT (EXTRACT(EPOCH FROM clock_timestamp())*1000000)::bigint",connection,transaction);
        var now=(long)(await timestamp.ExecuteScalarAsync(ct))!;
        await using var write=new NpgsqlCommand("UPDATE \"EtaProducerEpochs\" SET \"Payload\"=@payload::jsonb WHERE \"EpochId\"=@id",connection,transaction);
        write.Parameters.AddWithValue("id",epochId);write.Parameters.AddWithValue("payload",EtaTripEvidence.Serialize(epoch with{RevokedUs=now}));
        await write.ExecuteNonQueryAsync(ct);
    }
    public static async Task<EtaProducerEpoch> RegisterEpochAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        EtaProducerEpoch epoch, CancellationToken ct)
    {
        if (epoch.EpochId == Guid.Empty || !EtaTripEvidence.Hash(epoch.ProfileHash)
            || epoch.Release is not { Length: 40 } || !EtaTripEvidence.Hash(epoch.Release + new string('0',24))
            || epoch.RegisteredUs <= 0 || epoch.ActivatedUs is not null
            || epoch.RevokedUs is not null || epoch.WriterBarrierProven)
            throw new FormatException("Registration is inactive/untrusted; activation is not authorized here");
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "EtaProducerEpochs" VALUES (@id,@profile,@payload::jsonb ||
                jsonb_build_object('registered_us',(EXTRACT(EPOCH FROM clock_timestamp())*1000000)::bigint))
            ON CONFLICT ("EpochId") DO UPDATE SET "EpochId"=EXCLUDED."EpochId"
            RETURNING "Payload"::text
            """, connection, transaction);
        cmd.Parameters.AddWithValue("id", epoch.EpochId); cmd.Parameters.AddWithValue("profile", epoch.ProfileHash);
        cmd.Parameters.AddWithValue("payload", EtaTripEvidence.Serialize(epoch));
        var registered=EtaTripEvidence.Parse<EtaProducerEpoch>((string)(await cmd.ExecuteScalarAsync(ct))!);
        if ((registered with{RegisteredUs=epoch.RegisteredUs}) != epoch) throw new InvalidOperationException("Epoch payload conflict");
        return registered;
    }

    public static async Task<EtaEvidenceOwner> AcquireOwnerAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string scopeHash, Guid epochId, CancellationToken ct)
    {
        if (!EtaTripEvidence.Hash(scopeHash)) throw new FormatException("Invalid scope hash");
        await Lock(connection, transaction, scopeHash, ct);
        await CheckEpoch(connection, transaction, epochId, null, ct);
        // Every acquisition is a new owner interval, including restart under the same release.
        await using (var release = new NpgsqlCommand("""
            UPDATE "EtaEvidenceOwners" SET "ReleasedUs"=(EXTRACT(EPOCH FROM clock_timestamp())*1000000)::bigint
                WHERE "ScopeHash"=@scope AND "ReleasedUs" IS NULL
            """, connection, transaction))
        {
            release.Parameters.AddWithValue("scope",scopeHash); await release.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "EtaEvidenceOwners"("ScopeHash","Token","EpochId","AcquiredUs")
            SELECT @scope, COALESCE(MAX("Token"),0)+1,@epoch,(EXTRACT(EPOCH FROM clock_timestamp())*1000000)::bigint
            FROM "EtaEvidenceOwners" WHERE "ScopeHash"=@scope
            RETURNING "Token","AcquiredUs"
            """, connection, transaction);
        cmd.Parameters.AddWithValue("scope", scopeHash); cmd.Parameters.AddWithValue("epoch", epochId);
        await using var reader = await cmd.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        return new(scopeHash, epochId, reader.GetInt64(0), reader.GetInt64(1), null);
    }

    public static async Task PersistAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        EtaEvidenceOwner owner, long expectedSequence, EtaEvidenceEvent envelope, CancellationToken ct)
    {
        ValidateEnvelope(envelope);
        if (owner.ScopeHash != envelope.ScopeHash || owner.EpochId != envelope.EpochId || owner.Token != envelope.OwnerToken)
            throw new InvalidOperationException("Owner identity mismatch");
        await Lock(connection, transaction, owner.ScopeHash, ct);
        await CheckEpoch(connection, transaction, owner.EpochId, envelope.ProfileHash, ct);
        await using (var fence = new NpgsqlCommand("""
            SELECT 1 FROM "EtaEvidenceOwners" WHERE "ScopeHash"=@scope AND "Token"=@token
              AND "EpochId"=@epoch AND "ReleasedUs" IS NULL AND "AcquiredUs"<=@recorded FOR UPDATE
            """, connection, transaction))
        {
            fence.Parameters.AddWithValue("scope", owner.ScopeHash); fence.Parameters.AddWithValue("token",owner.Token);
            fence.Parameters.AddWithValue("epoch",owner.EpochId); fence.Parameters.AddWithValue("recorded",envelope.RecordedUs);
            if (await fence.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Stale writer fenced");
        }
        EtaEvidenceHead? previous = null;
        await using (var read = new NpgsqlCommand("SELECT \"Payload\"::text FROM \"EtaEvidenceHeads\" WHERE \"ViagemId\"=@trip FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("trip",envelope.TripId);
            if (await read.ExecuteScalarAsync(ct) is string json) previous = EtaTripEvidence.Parse<EtaEvidenceHead>(json);
        }
        if (previous?.Last == envelope) return; // Uncertain commit reconciliation: exact retry only.
        if ((previous?.Last.Sequence ?? 0) != expectedSequence) throw new InvalidOperationException("Head CAS conflict");
        var next = envelope.Contract == EtaTripEvidenceV2.Contract
            ? EtaTripEvidenceV2.Apply(previous, envelope) : EtaTripEvidence.Apply(previous, envelope);
        await using var write = new NpgsqlCommand("""
            WITH head_write AS (
                INSERT INTO "EtaEvidenceHeads" VALUES (@trip,@scope,@token,@epoch,@seq,@digest,@head::jsonb)
                ON CONFLICT ("ViagemId") DO UPDATE SET "Sequence"=EXCLUDED."Sequence", "Digest"=EXCLUDED."Digest", "Payload"=EXCLUDED."Payload"
                WHERE "EtaEvidenceHeads"."Sequence"=@expected AND "EtaEvidenceHeads"."ScopeHash"=@scope
                  AND "EtaEvidenceHeads"."OwnerToken"=@token AND "EtaEvidenceHeads"."EpochId"=@epoch
                RETURNING 1
            )
            INSERT INTO "OutboxViagens"("EventId","Tipo","Payload","CriadoEmUtc","Tentativas")
            SELECT @id,'EtaTripEvidence',@event::jsonb,now(),0 FROM head_write
            ON CONFLICT ("EventId") DO UPDATE SET "EventId"=EXCLUDED."EventId"
            RETURNING "Payload"=@event::jsonb;
            """, connection, transaction);
        write.Parameters.AddWithValue("trip",envelope.TripId); write.Parameters.AddWithValue("scope",owner.ScopeHash);
        write.Parameters.AddWithValue("token",owner.Token); write.Parameters.AddWithValue("epoch",owner.EpochId);
        write.Parameters.AddWithValue("seq",envelope.Sequence); write.Parameters.AddWithValue("digest",envelope.Digest);
        write.Parameters.AddWithValue("expected",expectedSequence);
        write.Parameters.AddWithValue("head",EtaTripEvidence.Serialize(next)); write.Parameters.AddWithValue("event",EtaTripEvidence.Serialize(envelope));
        write.Parameters.AddWithValue("id",envelope.EventId);
        if (await write.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Atomic head CAS/outbox evidence conflict");
    }

    public static async Task MaterializeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string outboxEventId, EtaEvidenceEvent envelope, CancellationToken ct)
    {
        ValidateEnvelope(envelope);
        if (outboxEventId != envelope.EventId) throw new FormatException("Outbox/envelope identity mismatch");
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "EtaEvidenceJournal" VALUES (@id,@trip,@seq,@payload::jsonb)
            ON CONFLICT ("EventId") DO UPDATE SET "EventId"=EXCLUDED."EventId"
            RETURNING "Payload"=@payload::jsonb
            """,connection,transaction);
        cmd.Parameters.AddWithValue("id",envelope.EventId); cmd.Parameters.AddWithValue("trip",envelope.TripId);
        cmd.Parameters.AddWithValue("seq",envelope.Sequence); cmd.Parameters.AddWithValue("payload",EtaTripEvidence.Serialize(envelope));
        if (await cmd.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Journal payload conflict");
    }

    private static void ValidateEnvelope(EtaEvidenceEvent e)
    {
        if (e.Contract == EtaTripEvidenceV2.Contract) EtaTripEvidenceV2.Validate(e);
        else EtaTripEvidence.Validate(e);
    }

    private static async Task Lock(NpgsqlConnection c, NpgsqlTransaction t, string scope, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@scope, 318731))",c,t);
        cmd.Parameters.AddWithValue("scope",scope); await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task CheckEpoch(NpgsqlConnection c, NpgsqlTransaction t, Guid id, string? profile, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT \"Payload\"::text FROM \"EtaProducerEpochs\" WHERE \"EpochId\"=@id FOR SHARE",c,t);
        cmd.Parameters.AddWithValue("id",id);
        if (await cmd.ExecuteScalarAsync(ct) is not string payload) throw new InvalidOperationException("Epoch missing");
        var e = EtaTripEvidence.Parse<EtaProducerEpoch>(payload);
        if (e.EpochId != id || e.RevokedUs is not null || (profile is not null && e.ProfileHash != profile))
            throw new InvalidOperationException("Revoked/mixed epoch/profile");
    }
}
