using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class WrapDuranteCandidatoIntegracaoTests(ViagemOperacionalFixture db):IClassFixture<ViagemOperacionalFixture>
{
    [Fact]
    public async Task CancelamentoCircular_ProtegeOutboxEHistorico_RecuperaNovaExecucao()
    {
        var ordem="WRAP-REAL-"+Guid.NewGuid().ToString("N");
        var stream="teste:wrap:"+Guid.NewGuid().ToString("N");
        var t=DateTimeOffset.UtcNow.AddMinutes(-5);
        var logger=new ConflictLogger();
        var options=new GpsPollingOptions{MudancaOperacionalHabilitada=true,CheckpointViagemSegundos=0};
        var repository=new ViagemOperacionalRepository(db.Redis,db.Source,
            Options.Create(options),
            logger){StreamKey=stream};
        PosicaoVeiculoDto G(int seconds,double p,bool b=false)=>new()
        {
            Ordem=ordem,CodigoLinha="VIAGEM3",LinhaId=db.Linha,SentidoId=b?db.S2:db.S1,
            PadraoOperacionalId=b?db.P2:db.P1,PadraoVersaoId=b?db.R2:db.R1,
            TopologiaPadrao=b?"LINEAR":"CIRCULAR",Latitude=-22.9,Longitude=b?-43.19-.02*p:-43.21+.02*p,
            Bearing=b?270:90,Velocidade=20,ComprimentoRotaMetros=2220,PosicaoNaRota=p,
            TimestampGps=t.AddSeconds(seconds),MatchingOperacionalPlausivel=true
        };
        async Task<long> Count(string table)
        {
            await using var c=db.Source.CreateCommand(table=="HistoricoPassagens"
                ? "SELECT count(*) FROM \"HistoricoPassagens\" WHERE \"Ordem\"=@ordem"
                : $"SELECT count(*) FROM \"{table}\" WHERE \"Payload\"->>'ordem_veiculo'=@ordem");
            c.Parameters.AddWithValue("ordem",ordem); return (long)(await c.ExecuteScalarAsync())!;
        }
        async Task<string> PayloadPersistido(string id)
        {
            await using var command=db.Source.CreateCommand("SELECT \"Payload\"::text FROM \"OutboxViagens\" WHERE \"EventId\"=@id");
            command.Parameters.AddWithValue("id",id);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }
        async Task<string> ConfigurarTopologia(string nova)
        {
            // Dados exclusivos da fixture: a topologia pertence à versão. O trigger
            // protege versões referenciadas por VersaoAtualId; retirar e repor essa
            // referência na mesma transação evita expor uma publicação intermediária.
            await using var connection=await db.Source.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await using var leitura=new Npgsql.NpgsqlCommand("""
                SELECT v."Topologia", p."VersaoAtualId"
                FROM "PadroesOperacionais" p
                JOIN "PadroesVersoes" v ON v."PadraoOperacionalId"=p."Id" AND v."Id"=@versao
                WHERE p."Id"=@padrao FOR UPDATE OF p,v
                """,connection,transaction);
            leitura.Parameters.AddWithValue("padrao",db.P1);
            leitura.Parameters.AddWithValue("versao",db.R1);
            string anterior;
            await using(var reader=await leitura.ExecuteReaderAsync())
            {
                if(!await reader.ReadAsync() || reader.IsDBNull(1) || reader.GetGuid(1)!=db.R1)
                    throw new InvalidOperationException("A fixture não possui R1 como versão atual de P1.");
                anterior=reader.GetString(0);
            }
            await using var escrita=new Npgsql.NpgsqlCommand("""
                UPDATE "PadroesOperacionais" SET "VersaoAtualId"=NULL WHERE "Id"=@padrao;
                UPDATE "PadroesVersoes" SET "Topologia"=@topologia WHERE "Id"=@versao;
                UPDATE "PadroesOperacionais" SET "VersaoAtualId"=@versao WHERE "Id"=@padrao;
                """,connection,transaction);
            escrita.Parameters.AddWithValue("padrao",db.P1);
            escrita.Parameters.AddWithValue("versao",db.R1);
            escrita.Parameters.AddWithValue("topologia",nova);
            await escrita.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return anterior;
        }
        var topologiaOriginal=await ConfigurarTopologia("CIRCULAR");
        try
        {
            Assert.Equal(ViagemObservadaStatus.Created,(await repository.TentarAtualizarAsync(G(0,.1),default)).Status);
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(G(10,.25),default)).Status);
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(G(20,.95),default)).Status);
            var worker=new ViagemOutboxWorker(db.Source,new HistoricoEventoRepository(db.Source),NullLogger<ViagemOutboxWorker>.Instance);
            await worker.ProcessarLoteAsync(await worker.ClaimAsync(default),default);
            Assert.Equal(3,await Count("HistoricoPassagens"));
            var old=(await repository.LerContextoAsync(ordem,default))!.Estado!;
            Assert.Equal(0,old.Observada.Volta);
            await repository.TentarAtualizarAsync(G(30,.3,true),default);
            Assert.NotNull((await repository.LerContextoAsync(ordem,default))!.Estado!.Candidato);
            var cancel=await repository.TentarAtualizarAsync(G(40,.05),default);
            Assert.Equal(ViagemObservadaStatus.Updated,cancel.Status);
            Assert.Empty(cancel.OcorrenciasUltrapassadas);
            Assert.Equal(db.Occurrences[0],cancel.ProximaOcorrenciaOperacional!.Id);
            var before=(await repository.LerContextoAsync(ordem,default))!.Estado!;
            Assert.Null(before.Candidato); Assert.Equal(0,before.Observada.Volta);
            Assert.Equal(old.Observada.OcorrenciaCursorId,before.Observada.OcorrenciaCursorId);
            Assert.Equal(old.Observada.ViagemId,before.Observada.ViagemId);
            Assert.Equal(ContinuidadeCircular.Ambigua,before.Integridade!.Continuidade);
            var count=await Count("OutboxViagens");
            var eventId=$"passagem:{old.Observada.ViagemId:D}:{db.Occurrences[0]:D}:0";
            var originalPayload=await PayloadPersistido(eventId);
            options.MudancaOperacionalHabilitada=false;
            // Mesmo com a flag desligada e Redis removido, não chegar ao conflito antigo.
            await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(ordem));
            Assert.Equal(before,(await repository.LerContextoAsync(ordem,default))!.Estado);
            logger.Exceptions.Clear();
            // Exatamente o cruzamento .2 que colidia anteriormente: agora é suprimido.
            var result=await repository.TentarAtualizarAsync(G(50,.25),default);
            Assert.Equal(ViagemObservadaStatus.Updated,result.Status);
            Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(result.EstadoOperacional!));
            Assert.DoesNotContain(logger.Exceptions,e=>e is EventoViagemPayloadConflictException);
            Assert.Equal(originalPayload,await PayloadPersistido(eventId));
            Assert.Equal(count,await Count("OutboxViagens"));
            await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(ordem));
            Assert.Equal(result.EstadoOperacional,(await repository.LerContextoAsync(ordem,default))!.Estado);
            Assert.Empty(await worker.ClaimAsync(default));
            Assert.Equal(3,await Count("HistoricoPassagens"));
            var recuperacao=await repository.TentarAtualizarAsync(G(60,.30),default);
            Assert.Equal(ViagemObservadaStatus.Updated,recuperacao.Status);
            Assert.NotEqual(old.Observada.ViagemId,recuperacao.Estado!.ViagemId);
            Assert.Equal(0,recuperacao.Estado.Volta);
            Assert.Empty(recuperacao.OcorrenciasUltrapassadas);
            var eventos=await worker.ClaimAsync(default);
            Assert.Equal(2,eventos.Count);
            var fim=Assert.Single(eventos,x=>JsonSerializer.Deserialize<EventoViagem>(x.Payload)!.Tipo=="ViagemFinalizada");
            Assert.Equal("PerdaContinuidadeCircular",JsonSerializer.Deserialize<EventoViagem>(fim.Payload)!.MotivoFim);
            await worker.ProcessarLoteAsync(eventos,default);
            Assert.Equal(3,await Count("HistoricoPassagens"));
            var primeiraPassagem=await repository.TentarAtualizarAsync(G(70,.45),default);
            Assert.Equal(ViagemObservadaStatus.Updated,primeiraPassagem.Status);
            Assert.Equal(db.Occurrences[1],Assert.Single(primeiraPassagem.OcorrenciasUltrapassadas).Id);
            await worker.ProcessarLoteAsync(await worker.ClaimAsync(default),default);
            Assert.Equal(4,await Count("HistoricoPassagens"));
            Assert.Equal(originalPayload,await PayloadPersistido(eventId));
            Assert.DoesNotContain(logger.Exceptions,e=>e is EventoViagemPayloadConflictException);
            await using var payload=db.Source.CreateCommand("SELECT \"Payload\"->>'volta',\"Payload\"->>'timestamp_gps' FROM \"OutboxViagens\" WHERE \"EventId\"=@id");
            payload.Parameters.AddWithValue("id",$"passagem:{old.Observada.ViagemId:D}:{db.Occurrences[0]:D}:0");
            await using var reader=await payload.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal("0",reader.GetString(0));
            Assert.Equal(G(10,.25).TimestampGps,DateTimeOffset.Parse(reader.GetString(1)));
        }
        finally
        {
            try { await ConfigurarTopologia(topologiaOriginal); }
            finally { await db.Redis.GetDatabase().KeyDeleteAsync([ViagemObservadaRepository.ChaveVeiculoViagem(ordem),stream]); }
        }
    }

    // Instância exclusiva do teste, sem subscriptions globais ou recursos a liberar.
    private sealed class ConflictLogger : ILogger<ViagemOperacionalRepository>
    {
        internal List<Exception> Exceptions { get; }=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel logLevel)=>true;
        public void Log<TState>(LogLevel logLevel,EventId eventId,TState state,Exception? exception,
            Func<TState,Exception?,string> formatter)
        {
            if(exception is not null) Exceptions.Add(exception);
        }
    }
}
