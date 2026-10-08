using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

public sealed record EtaRecoveryInspection(Guid TripId,string HeadKind,long Sequence,long Pending,
    bool OperationalBeginCommitted,long JournalRows,long PendingReceipts,string Classification,string Reason);

/// <summary>Explicit local PK reads; never reconstructs a clean witness or adopts an old epoch.</summary>
public static class EtaEvidenceRecoveryRepository
{
    public static async Task<IReadOnlyList<EtaRecoveryInspection>> InspectLocalAsync(NpgsqlDataSource source,
        IReadOnlyList<Guid> trips,CancellationToken ct)
    {
        if(trips.Count>200||trips.Any(x=>x==Guid.Empty)||trips.Distinct().Count()!=trips.Count)
            throw new ArgumentException("At most 200 distinct explicit trips required");
        await using var c=await source.OpenConnectionAsync(ct);EtaEvidenceBoundaryWriter.GuardLocal(c);
        await using var t=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,t))await readOnly.ExecuteNonQueryAsync(ct);
        List<EtaRecoveryInspection> result=[];
        foreach(var trip in trips)
        {
            await using var headRead=new NpgsqlCommand("SELECT \"Payload\"::text FROM \"EtaEvidenceHeads\" WHERE \"ViagemId\"=@trip",c,t);
            headRead.Parameters.AddWithValue("trip",trip);
            if(await headRead.ExecuteScalarAsync(ct) is not string json)
            { result.Add(new(trip,"missing",0,0,false,0,0,"NaoVerificada","no-visible-head-commit-not-proven"));continue; }
            var head=EtaTripEvidence.Parse<EtaEvidenceHead>(json);
            if(head.Last.Sequence>10000)throw new InvalidOperationException("Recovery inventory limit exceeded; explicit operator review required");
            await using var journal=new NpgsqlCommand("SELECT count(*) FROM \"EtaEvidenceJournal\" WHERE \"ViagemId\"=@trip",c,t);
            journal.Parameters.AddWithValue("trip",trip);var rows=(long)(await journal.ExecuteScalarAsync(ct))!;
            // Bounded EventId primary-key lookups. Missing receipts are unknown, not ACKed.
            await using var receipts=new NpgsqlCommand("""
                SELECT count(*) FILTER(WHERE o."ProcessadoEmUtc" IS NULL),
                  bool_or(n=1 AND b."Tipo"='ViagemIniciada' AND b."Payload"->>'viagem_id'=@trip::text)
                FROM generate_series(1,@seq) n
                LEFT JOIN "OutboxViagens" o ON o."EventId"='quality:'||@trip::text||':'||n::text
                LEFT JOIN "OutboxViagens" b ON n=1 AND b."EventId"=o."Payload"->>'operational_event_id'
                """,c,t);
            receipts.Parameters.AddWithValue("trip",trip);receipts.Parameters.AddWithValue("seq",head.Last.Sequence);
            await using var reader=await receipts.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
            var pending=reader.GetInt64(0);var committed=!reader.IsDBNull(1)&&reader.GetBoolean(1);
            result.Add(new(trip,head.Last.Kind,head.Last.Sequence,head.Last.Pending,committed,rows,pending,
                "NaoVerificada",head.Last.Kind=="Close"?"restart-inspection-is-not-a-certificate":"restart-open-execution-or-closing-no-witness-adoption"));
        }
        await t.CommitAsync(ct);return result;
    }
}
