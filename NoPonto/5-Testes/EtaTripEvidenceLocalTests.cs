using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using NoPonto.Migrations;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class EvidenceLocalFact : FactAttribute
{
    public EvidenceLocalFact() { if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ETA_EVIDENCE_TEST_CONNECTION")))
        Skip="Exclusive disposable fixture required; run homologate_evidence.ps1"; }
}

public sealed class EtaTripEvidenceLocalTests(ITestOutputHelper output)
{
    const string Marker="noponto-exclusive-evidence-3g3b1";
    static async Task<NpgsqlDataSource> Fixture()
    {
        var builder=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ETA_EVIDENCE_TEST_CONNECTION"));
        Assert.Equal("127.0.0.1",builder.Host);Assert.StartsWith("eta_evidence_3g3b1_",builder.Database);
        Assert.True(Guid.TryParseExact(builder.Database!["eta_evidence_3g3b1_".Length..],"N",out _));
        Assert.Equal("eta-evidence-3g3b1",builder.ApplicationName);
        var source=NpgsqlDataSource.Create(builder.ConnectionString);
        try {
            await using var guard=source.CreateCommand("SELECT current_setting('server_version_num')::int/10000,postgis_lib_version(),shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()");
            await using(var reader=await guard.ExecuteReaderAsync()){
                Assert.True(await reader.ReadAsync());Assert.Equal(16,reader.GetInt32(0));Assert.StartsWith("3.4.",reader.GetString(1));Assert.Equal(Marker,reader.GetString(2));
            }
            await using(var empty=source.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename NOT IN ('spatial_ref_sys')"))
                Assert.Equal(0L,(long)(await empty.ExecuteScalarAsync())!);
            // Minimal prior schema; no operational migrations, destructive cleanup or existing database.
            await using(var prior=source.CreateCommand("""
                CREATE TABLE "ViagensOperacionais"("OrdemVeiculo" text PRIMARY KEY,"Estado" jsonb NOT NULL,"Versao" bigint NOT NULL,"AtualizadoEmUtc" timestamptz NOT NULL,"IntegridadeCircular" jsonb NULL);
                CREATE TABLE "OutboxViagens"("EventId" text PRIMARY KEY,"Tipo" text NOT NULL,"Payload" jsonb NOT NULL,"CriadoEmUtc" timestamptz NOT NULL,"Tentativas" int NOT NULL,
                  "ProcessadoEmUtc" timestamptz NULL,"ProximaTentativaEmUtc" timestamptz NULL,"BloqueadoAteUtc" timestamptz NULL,"BloqueadoPor" text NULL,"UltimoErro" text NULL);
                INSERT INTO "ViagensOperacionais" VALUES ('fixture','["old"]',1,now(),'{"protected":true}');
                """))await prior.ExecuteNonQueryAsync();
            // Execute the actual additive migration Up operation, not a separately copied schema.
            var migration=new EtaTripEvidenceFoundation();
            foreach(var operation in migration.UpOperations){var sql=Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(operation);
                await using var apply=source.CreateCommand(sql.Sql);await apply.ExecuteNonQueryAsync();}
            return source;
        } catch {await source.DisposeAsync();throw;}
    }

    [EvidenceLocalFact] public async Task TransactionsDispatchFencingAndMeasuredCost()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(180));var ct=deadline.Token;
        await using var source=await Fixture();var template=EtaTripEvidenceTests.Events()[0];
        var epoch=new EtaProducerEpoch(Guid.NewGuid(),template.ProfileHash,new string('a',40),EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow),null,null,false);
        async Task Tx(Func<NpgsqlConnection,NpgsqlTransaction,Task> action){await using var c=await source.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);await action(c,t);await t.CommitAsync(ct);}
        async Task<long> Count(string table){await using var c=source.CreateCommand($"SELECT count(*) FROM \"{table}\"");return (long)(await c.ExecuteScalarAsync(ct))!;}
        await Tx((c,t)=>EtaTripEvidenceRepository.RegisterEpochAsync(c,t,epoch,ct));
        EtaEvidenceOwner owner=null!;await Tx(async(c,t)=>owner=await EtaTripEvidenceRepository.AcquireOwnerAsync(c,t,template.ScopeHash,epoch.EpochId,ct));
        EtaEvidenceEvent Begin(Guid trip,EtaEvidenceOwner o)=>EtaTripEvidenceTests.Sign(template with{
            TripId=trip,EventId=$"quality:{trip:D}:1",EpochId=o.EpochId,OwnerToken=o.Token,ScopeHash=o.ScopeHash,
            GpsUs=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow),RecordedUs=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow)+10,
            CoveredFromUs=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow),CoveredThroughUs=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow)});
        EtaEvidenceEvent start=Begin(Guid.NewGuid(),owner);
        start=EtaTripEvidenceTests.Sign(start with{CoveredFromUs=start.GpsUs!.Value,CoveredThroughUs=start.GpsUs.Value});
        // Throw after state/head/outbox writes: all three must roll back, circular information remains.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Tx(async(c,t)=>{
            await using var state=new NpgsqlCommand("UPDATE \"ViagensOperacionais\" SET \"Versao\"=2",c,t);await state.ExecuteNonQueryAsync(ct);
            await EtaTripEvidenceRepository.PersistAsync(c,t,owner,0,start,ct);throw new InvalidOperationException("injected rollback");}));
        Assert.Equal(0,await Count("EtaEvidenceHeads"));Assert.Equal(0,await Count("OutboxViagens"));
        await using(var state=source.CreateCommand("SELECT \"Versao\"=1 AND \"IntegridadeCircular\"='{"+"\"protected\":true}'::jsonb FROM \"ViagensOperacionais\""))
            Assert.Equal(true,await state.ExecuteScalarAsync(ct));
        var times=new List<double>();var before=GC.GetTotalMemory(true);var watch=Stopwatch.StartNew();
        await Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,owner,0,start,ct));times.Add(watch.Elapsed.TotalMilliseconds);
        // A commit whose client acknowledgement was lost is reconciled by exact payload retry.
        await Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,owner,0,start,ct));
        Assert.Equal(1,await Count("EtaEvidenceHeads"));Assert.Equal(1,await Count("OutboxViagens"));
        var head=EtaTripEvidence.Apply(null,start);
        for(int i=0;i<20;i++){
            var previous=head.Last;long gps=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow);
            var e=EtaTripEvidenceTests.Sign(previous with{Kind=i==19?"Close":"Checkpoint",Sequence=previous.Sequence+1,
                EventId=$"quality:{previous.TripId:D}:{previous.Sequence+1}",GpsUs=gps,RecordedUs=gps+1,
                CoveredFromUs=previous.CoveredThroughUs,CoveredThroughUs=gps,OperationalVersion=previous.OperationalVersion+1,
                Admitted=previous.Admitted+1,Settled=previous.Settled+1,PreviousDigest=previous.Digest,
                WitnessHash=EtaTripEvidence.Sha(previous.WitnessHash+"fixture-decision-"+i),
                OperationalEventId=i==19?"fixture-close":""});
            watch.Restart();await Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,owner,previous.Sequence,e,ct));times.Add(watch.Elapsed.TotalMilliseconds);
            head=EtaTripEvidence.Apply(head,e);
        }
        Assert.Equal(21,await Count("OutboxViagens"));Assert.Equal(0,await Count("EtaEvidenceJournal"));
        var worker=new ViagemOutboxWorker(source,new HistoricoEventoRepository(source),NullLogger<ViagemOutboxWorker>.Instance);
        var claimed=await worker.ClaimAsync(ct);
        // Lose batch lease: journal changes MUST roll back together with the failed ACK.
        await using(var steal=source.CreateCommand("UPDATE \"OutboxViagens\" SET \"BloqueadoPor\"='fixture-other'"))await steal.ExecuteNonQueryAsync(ct);
        await worker.ProcessarLoteAsync(claimed,ct);Assert.Equal(0,await Count("EtaEvidenceJournal"));
        await using(var restore=source.CreateCommand("UPDATE \"OutboxViagens\" SET \"BloqueadoPor\"=@owner")){restore.Parameters.AddWithValue("owner",worker.Consumer);await restore.ExecuteNonQueryAsync(ct);}
        await worker.ProcessarLoteAsync(claimed,ct);Assert.Equal(21,await Count("EtaEvidenceJournal"));
        await using(var done=source.CreateCommand("SELECT count(*) FROM \"OutboxViagens\" WHERE \"ProcessadoEmUtc\" IS NOT NULL"))Assert.Equal(21L,await done.ExecuteScalarAsync(ct));
        await Tx((c,t)=>EtaTripEvidenceRepository.MaterializeAsync(c,t,start.EventId,start,ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Tx((c,t)=>EtaTripEvidenceRepository.MaterializeAsync(c,t,start.EventId,EtaTripEvidenceTests.Sign(start with{NegativeFlags=1}),ct)));
        // Same expected head CAS from two compatible writers: one divergent proposal wins.
        var race=Begin(Guid.NewGuid(),owner);race=EtaTripEvidenceTests.Sign(race with{CoveredFromUs=race.GpsUs!.Value,CoveredThroughUs=race.GpsUs.Value});
        async Task<bool> TryWrite(EtaEvidenceEvent e){try{await Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,owner,0,e,ct));return true;}catch(InvalidOperationException){return false;}}
        var races=await Task.WhenAll(TryWrite(race),TryWrite(EtaTripEvidenceTests.Sign(race with{NegativeFlags=1})));
        Assert.Single(races,x=>x);
        // Even different advisory-lock scopes cannot race a shared ViagemId into an overwrite.
        EtaEvidenceOwner alternate=null!;await Tx(async(c,t)=>alternate=await EtaTripEvidenceRepository.AcquireOwnerAsync(c,t,EtaTripEvidence.Sha("fixture-alternate-scope"),epoch.EpochId,ct));
        var sharedTrip=Guid.NewGuid();
        EtaEvidenceEvent Proposal(EtaEvidenceOwner o){var e=Begin(sharedTrip,o);return EtaTripEvidenceTests.Sign(e with{CoveredFromUs=e.GpsUs!.Value,CoveredThroughUs=e.GpsUs.Value});}
        async Task<bool> OtherScope(EtaEvidenceOwner o){try{await Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,o,0,Proposal(o),ct));return true;}catch(InvalidOperationException){return false;}}
        Assert.Single(await Task.WhenAll(OtherScope(owner),OtherScope(alternate)),x=>x);
        // An unknown quality contract never dispatches to the operational journal or receives an ACK.
        await using(var bad=source.CreateCommand("""
            INSERT INTO "OutboxViagens"("EventId","Tipo","Payload","CriadoEmUtc","Tentativas")
            VALUES ('quality:unknown','EtaTripEvidence','{"contract":"unknown"}',now(),0)
            """))await bad.ExecuteNonQueryAsync(ct);
        var badItems=await worker.ClaimAsync(ct);await worker.ProcessarLoteAsync(badItems,ct);
        await using(var bad=source.CreateCommand("SELECT \"ProcessadoEmUtc\" IS NULL AND \"Tentativas\"=1 FROM \"OutboxViagens\" WHERE \"EventId\"='quality:unknown'"))Assert.Equal(true,await bad.ExecuteScalarAsync(ct));
        Assert.Equal(23,await Count("EtaEvidenceJournal"));
        await using(var processed=source.CreateCommand("SELECT count(*) FROM \"OutboxViagens\" WHERE \"ProcessadoEmUtc\" IS NOT NULL"))Assert.Equal(23L,await processed.ExecuteScalarAsync(ct));
        // Simultaneous acquisitions are serialized; old token and release cannot adopt a head.
        async Task<EtaEvidenceOwner> Acquire(Guid id){EtaEvidenceOwner o=null!;await Tx(async(c,t)=>o=await EtaTripEvidenceRepository.AcquireOwnerAsync(c,t,owner.ScopeHash,id,ct));return o;}
        var acquisitions=await Task.WhenAll(Acquire(epoch.EpochId),Acquire(epoch.EpochId));
        Assert.Equal(new long[]{2,3},acquisitions.Select(x=>x.Token).Order().ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,owner,0,start,ct)));
        var current=acquisitions.MaxBy(x=>x.Token)!;var resumed=EtaTripEvidenceTests.Sign(head.Last with{OwnerToken=current.Token,RecordedUs=EtaTripEvidence.Microseconds(DateTimeOffset.UtcNow)});
        await Assert.ThrowsAsync<FormatException>(()=>Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,current,head.Last.Sequence,resumed,ct)));
        var newEpoch=epoch with{EpochId=Guid.NewGuid(),Release=new string('b',40)};
        await Tx((c,t)=>EtaTripEvidenceRepository.RegisterEpochAsync(c,t,newEpoch,ct));
        var newOwner=await Acquire(newEpoch.EpochId);
        await Tx((c,t)=>EtaTripEvidenceRepository.RevokeEpochAsync(c,t,newEpoch.EpochId,ct));
        var revoked=Begin(Guid.NewGuid(),newOwner);revoked=EtaTripEvidenceTests.Sign(revoked with{CoveredFromUs=revoked.GpsUs!.Value,CoveredThroughUs=revoked.GpsUs.Value});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Tx((c,t)=>EtaTripEvidenceRepository.PersistAsync(c,t,newOwner,0,revoked,ct)));
        times.Sort();output.WriteLine($"FOUNDATION writes={times.Count} p50_ms={times[times.Count/2]:F3} p90_ms={times[(int)Math.Ceiling(times.Count*.9)-1]:F3} p95_ms={times[(int)Math.Ceiling(times.Count*.95)-1]:F3} retained_heap_delta_bytes={GC.GetTotalMemory(true)-before} process_working_set_bytes={Process.GetCurrentProcess().WorkingSet64}");
        output.WriteLine("Fixture transaction rollback, exact retry, journal/ACK rollback, idempotence, concurrent tokens, stale writer, release and revocation passed; production fencing NOT proved.");
        await CoverageV2(source, epoch, ct);
    }

    private async Task CoverageV2(NpgsqlDataSource source, EtaProducerEpoch epoch, CancellationToken ct)
    {
        await using var walStart=source.CreateCommand("SELECT pg_current_wal_insert_lsn()::text");
        var startLsn=(string)(await walStart.ExecuteScalarAsync(ct))!;
        async Task Tx(Func<NpgsqlConnection,NpgsqlTransaction,Task> action)
        { await using var c=await source.OpenConnectionAsync(ct);await using var t=await c.BeginTransactionAsync(ct);await action(c,t);await t.CommitAsync(ct); }
        var trip=Guid.NewGuid();var scope=EtaTripEvidence.Sha("fixture-coverage-v2");
        EtaEvidenceOwner owner=null!;
        await Tx(async(c,t)=>owner=await EtaTripEvidenceRepository.AcquireOwnerAsync(c,t,scope,epoch.EpochId,ct));
        var coverage=new EtaDecisionCoverageCoordinator(Microsoft.Extensions.Options.Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        coverage.BindLocal("fixture-v2",trip,epoch.EpochId,owner.Token);
        var writer=new EtaEvidenceBoundaryWriter(coverage);writer.BindLocal("fixture-v2",trip,owner,epoch.ProfileHash,EtaTripEvidence.Sha("fixture-v2-identity"));
        var gps=DateTimeOffset.UtcNow;
        var state=new ViagemOperacionalState(new(trip,"fixture-v2",Guid.NewGuid(),gps,gps,.1),"fixture",Guid.NewGuid(),Guid.NewGuid());
        var begin=new EventoViagem("fixture-v2-begin","ViagemIniciada",trip,"fixture-v2","fixture",state.SentidoId,state.Observada.PadraoVersaoId,gps);
        var decision=new DecisaoViagem(state,[begin]);
        using var first=coverage.Admit("fixture-v2","one",gps);
        // Head/outbox failure must roll back state too, in the caller's same transaction.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Tx(async(c,t)=>{
            await using var change=new NpgsqlCommand("UPDATE \"ViagensOperacionais\" SET \"Versao\"=99 WHERE \"OrdemVeiculo\"='fixture'",c,t);await change.ExecuteNonQueryAsync(ct);
            await writer.WriteInOperationalTransactionAsync(c,t,"fixture-v2",decision,1,ct);
            throw new InvalidOperationException("fixture failure after quality write");
        }));
        await using(var absent=source.CreateCommand("SELECT count(*) FROM \"EtaEvidenceHeads\" WHERE \"ViagemId\"=@trip"))
        {absent.Parameters.AddWithValue("trip",trip);Assert.Equal(0L,await absent.ExecuteScalarAsync(ct));}
        IReadOnlyList<Guid> pending=[];
        await Tx(async(c,t)=>pending=await writer.WriteInOperationalTransactionAsync(c,t,"fixture-v2",decision,1,ct));
        writer.OperationalCommitted("fixture-v2",pending);first!.Resolve("accepted",true);
        using var second=coverage.Admit("fixture-v2","two",gps.AddSeconds(1));
        var finish=begin with{EventId="fixture-v2-close",Tipo="ViagemFinalizada",TimestampEvento=gps.AddSeconds(1)};
        await Tx(async(c,t)=>pending=await writer.WriteInOperationalTransactionAsync(c,t,"fixture-v2",new(state,[finish]),2,ct));
        writer.OperationalCommitted("fixture-v2",pending);
        await writer.CompleteLocalAsync(source,"fixture-v2",ct);
        async Task<EtaEvidenceHead> Head(){await using var cmd=source.CreateCommand("SELECT \"Payload\"::text FROM \"EtaEvidenceHeads\" WHERE \"ViagemId\"=@trip");cmd.Parameters.AddWithValue("trip",trip);return EtaTripEvidence.Parse<EtaEvidenceHead>((string)(await cmd.ExecuteScalarAsync(ct))!);}
        Assert.Equal("Closing",(await Head()).Last.Kind);
        coverage.Mark("fixture-v2","post-commit-failed",unknown:EtaTripEvidence.CommitUncertain);
        second!.Resolve("accepted-with-uncertainty",false);
        await writer.CompleteLocalAsync(source,"fixture-v2",ct);
        var closed=await Head();Assert.Equal("Close",closed.Last.Kind);Assert.Equal(2,closed.Last.OperationalVersion);
        Assert.Equal(0,closed.Last.Pending);Assert.NotEqual(0,closed.Last.UnknownFlags);Assert.False(closed.Last.CoverageProven);
        Assert.False(await writer.RetireClosedLocalAsync(source,trip,ct)); // No durable dispatch receipts yet.
        var worker=new ViagemOutboxWorker(source,null!,NullLogger<ViagemOutboxWorker>.Instance);
        var claims=await worker.ClaimAsync(ct);await worker.ProcessarLoteAsync(claims,ct);
        await using(var journal=source.CreateCommand("SELECT count(*) FROM \"EtaEvidenceJournal\" WHERE \"ViagemId\"=@trip"))
        {journal.Parameters.AddWithValue("trip",trip);Assert.Equal(4L,await journal.ExecuteScalarAsync(ct));}
        output.WriteLine("COVERAGE_V2 same-transaction rollback, prospective Begin, pending Closing, post-COMMIT uncertainty, same-version quality Close and dispatch passed. Certification remains OFF.");
        var inspection=await EtaEvidenceRecoveryRepository.InspectLocalAsync(source,[trip,Guid.NewGuid()],ct);
        Assert.All(inspection,x=>Assert.Equal("NaoVerificada",x.Classification));
        Assert.Equal("missing",inspection[1].HeadKind);
        var restarted=new EtaDecisionCoverageCoordinator(Microsoft.Extensions.Options.Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        Assert.Throws<InvalidOperationException>(()=>restarted.FreezeAtDurableBoundary(trip,epoch.EpochId,owner.Token));
        Assert.True(await writer.RetireClosedLocalAsync(source,trip,ct));
        Assert.Null(coverage.Snapshot("fixture-v2"));
        Assert.Throws<InvalidOperationException>(()=>coverage.FreezeAtDurableBoundary(trip,epoch.EpochId,owner.Token));
        await using var walEnd=source.CreateCommand("SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),@before::pg_lsn)::bigint");
        walEnd.Parameters.AddWithValue("before",startLsn);
        output.WriteLine($"COVERAGE_V2 fixture_interval_WAL_bytes={await walEnd.ExecuteScalarAsync(ct)} quality_envelopes=4 quality_transactions=3 attempted_rollback_transactions=1 max_pool_size=2; interval includes owner acquisition, rollback and dispatch, not paired polling overhead.");
        if (Environment.GetEnvironmentVariable("ETA_COVERAGE_REDIS_CONNECTION") is { Length: >0 } connection)
            await RedisCoverage(source,epoch,connection,ct);
    }

    private async Task RedisCoverage(NpgsqlDataSource source,EtaProducerEpoch epoch,string connection,CancellationToken ct)
    {
        var marker=Environment.GetEnvironmentVariable("ETA_COVERAGE_REDIS_MARKER");
        Assert.True(Guid.TryParseExact(marker,"N",out _));
        var config=StackExchange.Redis.ConfigurationOptions.Parse(connection);
        Assert.Equal("eta-coverage-"+marker,config.ClientName);Assert.Single(config.EndPoints);
        Assert.Equal(System.Net.IPAddress.Loopback,Assert.IsType<System.Net.IPEndPoint>(config.EndPoints[0]).Address);
        using var redis=await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(config);
        var server=redis.GetServer(config.EndPoints[0]);Assert.Equal(7,server.Version.Major);Assert.Equal(0,await server.DatabaseSizeAsync());
        var store=new PendenciaOperacionalGpsRepository(redis,Microsoft.Extensions.Options.Options.Create(new RetryOperacionalGpsOptions()))
            {Prefixo="fixture-coverage:"+marker+":"};
        var gps=new PosicaoVeiculoDto{Ordem="fixture-redis",CodigoLinha="fixture",TimestampGps=DateTimeOffset.UtcNow,
            Latitude=-22.9,Longitude=-43.2,ModalFonte="BUS",ProvedorFonte="fixture"};
        var id=TelemetriaMlContrato.ObservacaoId(gps.ModalFonte,gps.ProvedorFonte,gps.Ordem,gps.TimestampGps);
        var pending=new PendenciaOperacionalGps(id,gps,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(180));
        var coverage=new EtaDecisionCoverageCoordinator(Microsoft.Extensions.Options.Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
        coverage.BindLocal(gps.Ordem,Guid.NewGuid(),Guid.NewGuid(),1);
        using(var a=coverage.Admit(gps.Ordem,id,gps.TimestampGps))a!.LeavePending("retry");
        Assert.Equal("CRIADA",await store.AdicionarAsync(pending,ct));Assert.Equal("EXISTENTE",await store.AdicionarAsync(pending,ct));
        var leases=await Task.WhenAll(store.ClaimAsync(gps.Ordem,DateTimeOffset.UtcNow,ct),store.ClaimAsync(gps.Ordem,DateTimeOffset.UtcNow,ct));
        var lease=Assert.Single(leases,x=>x is not null)!;
        Assert.False(await store.ConcluirAsync(lease with{Token="obsolete"},ct));
        Assert.True(await store.ConcluirAsync(lease,ct));coverage.ResolveRetry(gps.Ordem,id,"durable-ack",true);
        Assert.Equal(0,coverage.Snapshot(gps.Ordem)!.Pending);Assert.NotEqual(0,coverage.Snapshot(gps.Ordem)!.UnknownFlags);
        Assert.False(await store.TemPendenciaAsync(gps.Ordem,ct));
        output.WriteLine("COVERAGE_REDIS Redis7 exclusive empty fixture: deduplication, concurrent lease, stale ACK rejected, ACK resolves pending without clearing gap passed.");
        await ConnectedBenchmark(source,epoch,redis,ct);
    }

    private async Task ConnectedBenchmark(NpgsqlDataSource source,EtaProducerEpoch epoch,StackExchange.Redis.IConnectionMultiplexer redis,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        async Task<long> Wal(){await using var q=new NpgsqlCommand("SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'0/0'::pg_lsn)::bigint",c);return(long)(await q.ExecuteScalarAsync(ct))!;}
        async Task Project(ViagemOperacionalState state,int version)
        {
            var args=ViagemOperacionalCodec.Encode(state).Select(x=>(StackExchange.Redis.RedisValue)x).Concat(new StackExchange.Redis.RedisValue[]{version,ViagemOperacionalCodec.Tick(clockForProject(state)),3600}).ToArray();
            var result=(int)await redis.GetDatabase().ScriptEvaluateAsync(ViagemOperacionalRedisScript.ProjectDurable,["fixture-benchmark:"+state.Observada.OrdemVeiculo],args);
            Assert.Equal(2,result);
        }
        DateTimeOffset clockForProject(ViagemOperacionalState state)=>state.Observada.TimestampUltimaAtualizacao;
        foreach(var count in new[]{1,10,50,200})
        {
            var measurements=new List<double>[3];long[] wals=new long[3];long[] transactions=new long[3];
            for(int mode=0;mode<3;mode++)
            {
                var clock=DateTimeOffset.UtcNow;
                var coverage=new EtaDecisionCoverageCoordinator(Microsoft.Extensions.Options.Options.Create(new EtaDecisionCoverageOptions{Enabled=mode==2})){Clock=()=>clock};
                var writer=new EtaEvidenceBoundaryWriter(coverage);
                List<ViagemOperacionalState> states=[];
                for(int i=0;i<count;i++)
                {
                    var vehicle=$"fixture-bench-{count}-{mode}-{i}";
                    EtaEvidenceOwner owner=null!;
                    if(mode==2){await using var own=await c.BeginTransactionAsync(ct);owner=await EtaTripEvidenceRepository.AcquireOwnerAsync(c,own,EtaTripEvidence.Sha(vehicle),epoch.EpochId,ct);await own.CommitAsync(ct);writer.AuthorizeLocalScope(vehicle,owner,epoch.ProfileHash);}
                    clock=DateTimeOffset.UtcNow;
                    var s=new ViagemOperacionalState(new(Guid.NewGuid(),vehicle,Guid.NewGuid(),clock,clock,.1,
                        PadraoOperacionalId:Guid.NewGuid(),Volta:1),"fixture",Guid.NewGuid(),Guid.NewGuid());states.Add(s);
                    var begin=new EventoViagem($"inicio:{s.Observada.ViagemId:D}","ViagemIniciada",s.Observada.ViagemId,vehicle,"fixture",s.SentidoId,s.Observada.PadraoVersaoId,clock,
                        PadraoOperacionalId:s.Observada.PadraoOperacionalId,Volta:1,LinhaId:s.LinhaId);
                    using var admission=mode==2?coverage.Admit(vehicle,"begin",clock):null;
                    await using var tx=await c.BeginTransactionAsync(ct);
                    await using(var insert=new NpgsqlCommand("INSERT INTO \"ViagensOperacionais\" VALUES (@vehicle,@state::jsonb,1,now(),NULL)",c,tx))
                    {insert.Parameters.AddWithValue("vehicle",vehicle);insert.Parameters.AddWithValue("state",System.Text.Json.JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(s)));await insert.ExecuteNonQueryAsync(ct);}
                    await using(var insert=new NpgsqlCommand("INSERT INTO \"OutboxViagens\"(\"EventId\",\"Tipo\",\"Payload\",\"CriadoEmUtc\",\"Tentativas\") VALUES (@id,'ViagemIniciada',@payload::jsonb,now(),0)",c,tx))
                    {insert.Parameters.AddWithValue("id",begin.EventId);insert.Parameters.AddWithValue("payload",System.Text.Json.JsonSerializer.Serialize(begin));await insert.ExecuteNonQueryAsync(ct);}
                    var closing=await writer.WriteInOperationalTransactionAsync(c,tx,vehicle,new(s,[begin]),1,ct);
                    if(mode==2&&i==0)
                    {
                        Assert.False(coverage.IsLocalBeginConfirmed(s.Observada.ViagemId));
                        await Assert.ThrowsAsync<InvalidOperationException>(()=>writer.ConfirmProspectiveLocalAsync(c,vehicle,ct));
                    }
                    await tx.CommitAsync(ct);writer.OperationalCommitted(vehicle,closing);
                    if(mode==2)await writer.ConfirmProspectiveLocalAsync(c,vehicle,ct);
                    if(mode==2)Assert.True(coverage.IsLocalBeginConfirmed(s.Observada.ViagemId));
                    admission?.Resolve("accepted",true);
                    await Project(s,1);
                }
                measurements[mode]=[];long before=await Wal();long heap=GC.GetTotalMemory(true);int checkpoints=0;
                for(int round=0;round<12;round++)
                {
                    clock=clock.AddSeconds(10);var watch=Stopwatch.StartNew();
                    for(int i=0;i<states.Count;i++)
                    {
                        var s=states[i];var next=s with{Observada=s.Observada with{TimestampUltimaAtualizacao=clock,PosicaoNaRotaConfirmada=.1+(round+1)*.001}};
                        using var admission=mode==0?null:coverage.Admit(s.Observada.OrdemVeiculo,$"round-{round}",clock,"fixture-predecessor",1+round/6);
                        var cas=(int)await redis.GetDatabase().ScriptEvaluateAsync(ViagemOperacionalRedisScript.CommitHot,
                            ["fixture-benchmark:"+s.Observada.OrdemVeiculo],
                            [System.Text.Json.JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(s)),System.Text.Json.JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(next)),(1+round/6).ToString(),3600]);
                        Assert.Equal(2,cas);
                        if((round+1)%6==0)
                        {
                            await using var tx=await c.BeginTransactionAsync(ct);transactions[mode]++;
                            await using(var update=new NpgsqlCommand("UPDATE \"ViagensOperacionais\" SET \"Estado\"=@state::jsonb,\"Versao\"=\"Versao\"+1,\"AtualizadoEmUtc\"=now() WHERE \"OrdemVeiculo\"=@vehicle",c,tx))
                            {update.Parameters.AddWithValue("vehicle",s.Observada.OrdemVeiculo);update.Parameters.AddWithValue("state",System.Text.Json.JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(next)));Assert.Equal(1,await update.ExecuteNonQueryAsync(ct));}
                            if(mode==2&&coverage.CheckpointDue(s.Observada.OrdemVeiculo))
                            {var closed=await writer.WriteInOperationalTransactionAsync(c,tx,s.Observada.OrdemVeiculo,new(next,[]),1+(round+1)/6,ct);await tx.CommitAsync(ct);writer.OperationalCommitted(s.Observada.OrdemVeiculo,closed);checkpoints++;}
                            else await tx.CommitAsync(ct);
                            await Project(next,1+(round+1)/6);
                        }
                        states[i]=next;
                        admission?.Resolve("committed",true);
                    }
                    if(round>=4)measurements[mode].Add(watch.Elapsed.TotalMilliseconds);
                }
                wals[mode]=await Wal()-before;measurements[mode].Sort();
                double P(double p)=>measurements[mode][(int)Math.Ceiling(measurements[mode].Count*p)-1];
                await using var pool=new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()",c);
                output.WriteLine($"CONNECTED vehicles={count} mode={mode} rounds_measured=8 p50_ms={P(.5):F3} p90_ms={P(.9):F3} p95_ms={P(.95):F3} transactions={transactions[mode]} WAL_bytes={wals[mode]} retained_heap_delta_bytes={GC.GetTotalMemory(true)-heap} process_working_set_bytes={Process.GetCurrentProcess().WorkingSet64} database_connections={await pool.ExecuteScalarAsync(ct)} max_pool_size=2 checkpoints={checkpoints} simulated_seconds=120 pending={states.Sum(s=>coverage.Snapshot(s.Observada.OrdemVeiculo)?.Pending??0)}");
                if(mode==2)
                {
                    var first=states[0];
                    Assert.True(await redis.GetDatabase().KeyDeleteAsync("fixture-benchmark:"+first.Observada.OrdemVeiculo));
                    coverage.Mark(first.Observada.OrdemVeiculo,"controlled-redis-loss",unknown:EtaTripEvidence.RedisLost);
                    var recovery=await EtaEvidenceRecoveryRepository.InspectLocalAsync(source,[first.Observada.ViagemId],ct);
                    Assert.Equal("NaoVerificada",Assert.Single(recovery).Classification);Assert.True(recovery[0].OperationalBeginCommitted);
                    await Project(first,3);
                    Assert.NotEqual(0,coverage.Snapshot(first.Observada.OrdemVeiculo)!.UnknownFlags&EtaTripEvidence.RedisLost);
                    var restarted=new EtaDecisionCoverageCoordinator(Microsoft.Extensions.Options.Options.Create(new EtaDecisionCoverageOptions{Enabled=true}));
                    Assert.Null(restarted.Snapshot(first.Observada.OrdemVeiculo));
                    Assert.Throws<InvalidOperationException>(()=>restarted.FreezeAtDurableBoundary(first.Observada.ViagemId,epoch.EpochId,1));
                }
            }
            output.WriteLine($"CONNECTED_INCREMENTAL vehicles={count} ON_minus_baseline_p50_ms={measurements[2][3]-measurements[0][3]:F3} OFF_minus_baseline_p50_ms={measurements[1][3]-measurements[0][3]:F3} ON_minus_baseline_WAL_bytes={wals[2]-wals[0]} added_transactions={transactions[2]-transactions[0]}; fixture state-commit workload, not full polling/provider benchmark");
        }
    }
}
