using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

// SOMENTE fixture descartável identificada. Compilar, NÃO executar na validação local.
public sealed class EtaDatasetPostgresTests(ViagemOperacionalFixture db):IClassFixture<ViagemOperacionalFixture>
{
    [Fact]
    public async Task ConsultaCandidatos_JournalGeographyEPaginacaoTimestampIgual()
    {
        var ts=DateTimeOffset.UtcNow.AddMinutes(-5); var viagem=Guid.NewGuid();
        var ordem="DATASET-"+Guid.NewGuid().ToString("N");
        var evento=new EventoTelemetriaMl{ObservacaoId=TelemetriaMlContrato.ObservacaoId("ONIBUS","TEST",ordem,ts),
            Modal="ONIBUS",Provedor="TEST",OrdemVeiculo=ordem,CodigoLinha="VIAGEM3",LatitudeRecebida=-22.9,
            LongitudeRecebida=-43.208,VelocidadeInstantanea=20,TimestampGps=ts,RecebidoEmUtc=ts,
            EventoCriadoEmUtc=ts,ViagemId=viagem,Volta=0,LinhaId=db.Linha,SentidoId=db.S1,PadraoVersaoId=db.R1,
            OcorrenciaParadaPadraoId=db.Occurrences[0],ProximaOcorrenciaParadaPadraoId=db.Occurrences[0],
            PosicaoNaRota=.1,ComprimentoRotaMetros=2000,DistanciaProximaParadaMetros=200};
        var outra=evento with{OrdemVeiculo=ordem+"B",ObservacaoId=TelemetriaMlContrato.ObservacaoId("ONIBUS","TEST",ordem+"B",ts),ViagemId=null};
        var passagem=CriarPassagem(viagem,ordem,db.S1,db.R1,db.Occurrences[0],db.Stop,db.P1,db.Linha,ts);
        try
        {
            await new TelemetriaMlRepository(db.Source).PersistirLoteAsync([evento,outra],default);
            await new HistoricoEventoRepository(db.Source).PersistirAsync(passagem,default);
            var sql=LocalizarConsulta();
            await using var con=await db.Source.OpenConnectionAsync();
            await using var trans=await con.BeginTransactionAsync();
            await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",con,trans)) await readOnly.ExecuteNonQueryAsync();
            var encontrados=new List<TelemetriaVeiculoMl>();var cursor=Guid.Empty;
            for(var pagina=0;pagina<3;pagina++)
            {
                await using var cmd=new NpgsqlCommand(sql,con,trans);
                cmd.Parameters.AddWithValue("inicio",ts);cmd.Parameters.AddWithValue("fim",ts.AddSeconds(1));
                cmd.Parameters.AddWithValue("cursor_ts",ts);cmd.Parameters.AddWithValue("cursor_id",cursor);
                cmd.Parameters.AddWithValue("codigo","VIAGEM3");cmd.Parameters.AddWithValue("tamanho",1);
                await using var r=await cmd.ExecuteReaderAsync();
                if(!await r.ReadAsync()) break;
                var gps=JsonSerializer.Deserialize<TelemetriaVeiculoMl>(r.GetString(r.GetOrdinal("gps")))!;
                encontrados.Add(gps);cursor=gps.Id;
                if(gps.ViagemId==viagem)
                {
                    var h=JsonSerializer.Deserialize<HistoricoPassagem>(r.GetString(r.GetOrdinal("passagem")))!;
                    var j=JsonSerializer.Deserialize<EventoViagem>(r.GetString(r.GetOrdinal("journal")))!;
                    Assert.Equal(passagem.EventId,j.EventId);Assert.Equal(60,(h.TimestampPassagem!.Value-gps.TimestampGps).TotalSeconds);
                    Assert.True(r.GetDouble(r.GetOrdinal("distancia_rota_conferida_metros"))>0);
                }
                else Assert.True(r.IsDBNull(r.GetOrdinal("passagem"))); // Incompleta preservada para descarte.
                Assert.False(await r.ReadAsync());
            }
            Assert.Equal(2,encontrados.Count);Assert.Equal(2,encontrados.Select(g=>g.Id).Distinct().Count());
            await trans.CommitAsync();
        }
        finally
        {
            await using var limpeza=db.Source.CreateCommand("""
                DELETE FROM "TelemetriasVeiculoMl" WHERE "ObservacaoId"=ANY(@ids);
                DELETE FROM "HistoricoPassagens" WHERE "ViagemId"=@viagem;
                DELETE FROM "EventosViagem" WHERE "EventId"=@evento;
                """);
            limpeza.Parameters.AddWithValue("ids",new[]{evento.ObservacaoId,outra.ObservacaoId});
            limpeza.Parameters.AddWithValue("viagem",viagem);limpeza.Parameters.AddWithValue("evento",passagem.EventId);
            await limpeza.ExecuteNonQueryAsync();
        }
    }
    internal static EventoViagem CriarPassagem(Guid viagem,string ordem,Guid sentido,Guid versao,
        Guid ocorrencia,Guid parada,Guid padrao,Guid linha,DateTimeOffset gps)
    {
        var passagem=gps.AddSeconds(60).ToUniversalTime();
        return new(EventId:$"passagem:{viagem:D}:{ocorrencia:D}:0",Tipo:"PassagemParada",ViagemId:viagem,
            OrdemVeiculo:ordem,CodigoLinha:"VIAGEM3",SentidoId:sentido,PadraoVersaoId:versao,
            TimestampEvento:passagem,OcorrenciaParadaPadraoId:ocorrencia,ParadaId:parada,Ordem:1,
            PosicaoLinha:.2,TimestampPassagem:passagem,TimestampGps:gps.AddSeconds(70).ToUniversalTime(),
            VelocidadeInstantanea:20,VelocidadeMedia:18,SchemaVersion:2,
            PadraoOperacionalId:padrao,Volta:0,LinhaId:linha);
    }

    private static string LocalizarConsulta()
    {
        for(var pasta=new DirectoryInfo(AppContext.BaseDirectory);pasta is not null;pasta=pasta.Parent)
        {
            var caminho=Path.Combine(pasta.FullName,"ETA_ML_CANDIDATOS_3A.sql");
            if(File.Exists(caminho)) return File.ReadAllText(caminho);
        }
        throw new FileNotFoundException("Executar a partir do checkout com a consulta de candidatos.");
    }
}
