using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoPonto.Application.GPS;
using NoPonto.Domain.Entities;
using Npgsql;

// Offline: não inicializa a API, serviços hosted ou migrations.
if(args.Length!=4) throw new ArgumentException("Uso: <audit.json> <opcoes.json> <SQL 3A> <diretorio novo>. Conexão local via ETA_ML_LOCAL_CONNECTION.");
var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true};
json.Converters.Add(new JsonStringEnumConverter());
var audit=JsonSerializer.Deserialize<Audit>(File.ReadAllText(args[0]),json)!;
var options=JsonSerializer.Deserialize<OpcoesDatasetEta>(File.ReadAllText(args[1]),json)!;
var cutoff=DateTimeOffset.Parse("2026-10-07T14:53:59.225082Z");
if(audit.DatasetVersion!=EtaDataset.Versao || audit.CollectionStartedAtUtc!=cutoff || options.Inicio<cutoff
    || audit.DataKind is not ("real" or "synthetic")
    || string.IsNullOrWhiteSpace(audit.SnapshotReference) || audit.SnapshotReference.StartsWith("PREENCHER")
    || audit.Trips.Length==0 || audit.Trips.Any(t=>t.ViagemId==Guid.Empty || t.Fim<=t.Inicio
        || t.Qualidade!=QualidadeExecucaoDataset.AuditadaSemProtecao || string.IsNullOrWhiteSpace(t.ReferenciaAuditoria))
    || audit.Trips.Select(t=>t.ViagemId).Distinct().Count()!=audit.Trips.Length)
    throw new ArgumentException("Contrato/cutoff/snapshot/auditoria inválido.");
if(audit.BackendCommit!="3e40d92327c517a4f2e5342cfe6f84a5be9568ee"
    || audit.Image!="sha256:2bbb601108bfe9c7d0f7723da3bc3b2f1c696868e7dfe88f087b345d733eb0eb"
    || audit.Migration!="20261006180000_IntegridadeCircularDuravel"
    || !audit.Sampling.GetProperty("enabled").GetBoolean()
    || audit.Sampling.GetProperty("line_percentage").GetInt32()!=10
    || audit.Sampling.GetProperty("block_minutes").GetInt32()!=60
    || audit.Sampling.GetProperty("seed").GetString()!="NOPONTO_ML_V1")
    throw new ArgumentException("Manifesto diverge da coleta oficial 3B.");
if(Directory.Exists(args[3])) throw new ArgumentException("Diretório de saída já existe.");
var cs=Environment.GetEnvironmentVariable("ETA_ML_LOCAL_CONNECTION") ?? throw new ArgumentException("Conexão local ausente.");
var builder=new NpgsqlConnectionStringBuilder(cs);
if(builder.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new ArgumentException("Somente snapshot PostgreSQL local; produção/remoto bloqueado.");
await using var connection=new NpgsqlConnection(cs); await connection.OpenAsync();
await using var tx=await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,tx)) await ro.ExecuteNonQueryAsync();
var source=new Source(connection,tx,File.ReadAllText(args[2]),audit,options,json);
Directory.CreateDirectory(args[3]);
var csv=Path.Combine(args[3],"dataset.csv");
try {
    EstatisticasDatasetEta stats;
    await using(var writer=new StreamWriter(csv+".partial")) stats=await EtaDataset.ExportarCsvAsync(source,writer,options);
    File.Move(csv+".partial",csv);
    var manifest=new {exporter_version="eta-export-3b-v1",dataset_version=EtaDataset.Versao,data_kind=audit.DataKind,cutoff_utc=cutoff,
        collection=audit, bounds=options, trips=audit.Trips.Select(t=>new{viagem_id=t.ViagemId,inicio=t.Inicio,fim=t.Fim}),
        features=EtaDataset.Features, counts=stats, discovery=source.Discovery,
        dataset_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csv))).ToLowerInvariant(),
        audit_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant(),
        sql_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))).ToLowerInvariant(),
        options_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))).ToLowerInvariant()};
    await File.WriteAllTextAsync(Path.Combine(args[3],"dataset.manifest.json"),JsonSerializer.Serialize(manifest,json));
    await tx.RollbackAsync();
} catch { if(File.Exists(csv+".partial")) File.Delete(csv+".partial"); throw; }

record Trip(Guid ViagemId,DateTimeOffset Inicio,DateTimeOffset Fim,string CodigoLinha,
    QualidadeExecucaoDataset Qualidade,string ReferenciaAuditoria);
record Audit(string DatasetVersion,DateTimeOffset CollectionStartedAtUtc,string SnapshotReference,
    string BackendCommit,string Image,string Migration,JsonElement Sampling,Trip[] Trips,string DataKind="real");
sealed class Source(NpgsqlConnection conn,NpgsqlTransaction tx,string sql,Audit audit,OpcoesDatasetEta options,JsonSerializerOptions json):IFonteDatasetEta
{
    readonly Trip[] trips=audit.Trips.OrderBy(t=>t.ViagemId.ToString("D"),StringComparer.Ordinal).ToArray();
    public Dictionary<string,long> Discovery {get;}=new();
    void Count(string key)=>Discovery[key]=Discovery.GetValueOrDefault(key)+1;
    public async Task<PaginaDatasetEta> LerPaginaAsync(string? cursor,int limit,CancellationToken ct)
    {
        var offset=cursor is null?0:int.Parse(cursor); var result=new List<ExecucaoDatasetEta>();
        foreach(var trip in trips.Skip(offset).Take(limit)) {
            var candidates=new List<CandidatoDatasetEta>();
            var outside=trip.Inicio<options.Inicio || trip.Fim>=options.Fim;
            var crosses=(trip.Inicio<options.FimTreino && trip.Fim>=options.FimTreino)
                || (trip.Inicio<options.FimValidacao && trip.Fim>=options.FimValidacao);
            if(outside) Count("trips_outside_window");
            else if(crosses) Count("trips_crossing_split");
            if(!outside && !crosses) {
                // Fronteiras têm de existir no journal do mesmo snapshot; não aceitar min/max GPS.
                await using(var check=new NpgsqlCommand("SELECT \"Tipo\",\"Payload\" FROM \"EventosViagem\" WHERE \"EventId\" IN (@inicio,@fim)",conn,tx)) {
                    check.Parameters.AddWithValue("inicio",$"inicio:{trip.ViagemId:D}");check.Parameters.AddWithValue("fim",$"fim:{trip.ViagemId:D}");
                    await using var reader=await check.ExecuteReaderAsync(ct); var boundaries=new Dictionary<string,DateTimeOffset>();
                    while(await reader.ReadAsync(ct)) {var e=JsonSerializer.Deserialize<EventoViagem>(reader.GetString(1),json)!;
                        if(e.ViagemId!=trip.ViagemId||e.CodigoLinha!=trip.CodigoLinha||e.SchemaVersion!=2
                            || e.Tipo!=reader.GetString(0) || e.EventId!=$"{(e.Tipo=="ViagemIniciada"?"inicio":"fim")}:{trip.ViagemId:D}")
                            throw new InvalidOperationException("Journal de fronteira incompatível.");
                        boundaries.Add(reader.GetString(0),e.TimestampEvento);}
                    if(!boundaries.TryGetValue("ViagemIniciada",out var start)||start!=trip.Inicio
                        ||!boundaries.TryGetValue("ViagemFinalizada",out var end)||end!=trip.Fim) throw new InvalidOperationException("Execução sem fronteiras fechadas conferidas.");
                }
                var timestamp=trip.Inicio; var id=Guid.Empty; var finished=false;
                for(var page=0;page<options.MaxPaginas;page++) {
                    await using var cmd=new NpgsqlCommand(sql,conn,tx);
                    cmd.Parameters.AddWithValue("codigo",trip.CodigoLinha);cmd.Parameters.AddWithValue("inicio",trip.Inicio);
                    cmd.Parameters.AddWithValue("fim",trip.Fim);cmd.Parameters.AddWithValue("cursor_ts",timestamp);
                    cmd.Parameters.AddWithValue("cursor_id",id);cmd.Parameters.AddWithValue("tamanho",1000);
                    await using var r=await cmd.ExecuteReaderAsync(ct);var read=0;
                    while(await r.ReadAsync(ct)) {read++;
                        var g=JsonSerializer.Deserialize<TelemetriaVeiculoMl>(r.GetString(0),json)!;
                        timestamp=g.TimestampGps;id=g.Id;
                        if(g.ViagemId!=trip.ViagemId) continue;
                        Count("gps_trip_candidates");
                        if(r.IsDBNull(1)||r.IsDBNull(2)||r.IsDBNull(3)||r.IsDBNull(6)) {Count("missing_label_journal_structure");continue;}
                        var h=JsonSerializer.Deserialize<HistoricoPassagem>(r.GetString(1),json)!;
                        var j=JsonSerializer.Deserialize<EventoViagem>(r.GetString(2),json)!;
                        var d=new DestinoDatasetEta(r.GetGuid(3),r.GetGuid(4),r.GetGuid(6),r.GetGuid(7),r.GetGuid(10),r.GetGuid(9),r.GetString(11),r.GetString(8),r.GetDouble(5));
                        candidates.Add(new(g,h,d,j,r.IsDBNull(12)?null:r.GetDouble(12)));
                        if(candidates.Count>options.MaxCandidatosPorExecucao) throw new InvalidOperationException("Execução excedeu limite.");
                    }
                    if(read==0){finished=true;break;}
                }
                if(!finished) throw new InvalidOperationException("Paginação SQL incompleta.");
            }
            result.Add(new(trip.ViagemId,trip.Inicio,trip.Fim,trip.Qualidade,trip.ReferenciaAuditoria,candidates));
        }
        var next=offset+result.Count; return new(result,next>=trips.Length?null:next.ToString());
    }
}
