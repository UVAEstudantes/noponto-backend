using System.Globalization;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GPS;

// Offline somente: não registrado no Program, não lê banco ou altera histórico.
public enum QualidadeExecucaoDataset { NaoVerificada, AuditadaSemProtecao, ProtegidaOuAmbigua }
public sealed record DestinoDatasetEta(Guid Id, Guid ParadaId, Guid VersaoId, Guid PadraoId,
    Guid LinhaId, Guid SentidoId, string CodigoLinha, string Topologia, double Posicao);
public sealed record CandidatoDatasetEta(TelemetriaVeiculoMl Gps, HistoricoPassagem Passagem,
    DestinoDatasetEta Destino, EventoViagem? Journal, double? DistanciaRotaConferidaMetros = null);
// Fonte deve entregar execução inteira, inclusive fronteiras, ordenada por UUID textual ordinal.
// AuditadaSemProtecao exige evidência externa; ausência de marker no estado atual NÃO é evidência.
public sealed record ExecucaoDatasetEta(Guid ViagemId, DateTimeOffset Inicio, DateTimeOffset Fim,
    QualidadeExecucaoDataset Qualidade, string? ReferenciaAuditoria, IReadOnlyList<CandidatoDatasetEta> Candidatos);
public sealed record PaginaDatasetEta(IReadOnlyList<ExecucaoDatasetEta> Execucoes, string? ProximoCursor);
public interface IFonteDatasetEta
{
    Task<PaginaDatasetEta> LerPaginaAsync(string? cursor, int limiteExecucoes, CancellationToken ct);
}
public sealed record OpcoesDatasetEta(DateTimeOffset Inicio, DateTimeOffset Fim,
    DateTimeOffset FimTreino, DateTimeOffset FimValidacao, int ExecucoesPorPagina = 1,
    int MaxCandidatosPorExecucao = 10000, int MaxPaginas = 1000, double MaxLabelSegundos = 3600);
public sealed record AmostraDatasetEta(string ObservacaoId, Guid ViagemId, int Volta, string Veiculo,
    string Modal, string Provedor, DateTimeOffset TimestampGps, Guid LinhaId, string CodigoLinha,
    Guid SentidoId, Guid PadraoId, Guid VersaoId, Guid OcorrenciaId, Guid ParadaId, string Topologia,
    double PosicaoGps, double PosicaoDestino, double DistanciaMetros, double? VelocidadeKmh,
    double? VelocidadeMediaCausalKmh, int HoraDia, int DiaSemana, string Split,
    DateTimeOffset TimestampPassagem, double LabelSegundos);
public sealed class EstatisticasDatasetEta
{
    public long Lidos { get; internal set; }
    public long Exportados { get; internal set; }
    public Dictionary<string,long> Descartes { get; } = new(StringComparer.Ordinal);
    internal void Descartar(string motivo) => Descartes[motivo] = Descartes.GetValueOrDefault(motivo)+1;
}

public static class EtaDataset
{
    public const string Versao = "noponto-eta-gps-v1";
    // Allowlist de features: IDs de viagem/observação e campos de label NÃO entram no modelo.
    public static IReadOnlyList<string> Features { get; } = Array.AsReadOnly(new[]{"modal","linha_id","codigo_linha",
        "sentido_id","padrao_id","versao_id","ocorrencia_id","parada_id","topologia","posicao_gps",
        "posicao_destino","distancia_metros","velocidade_kmh","velocidade_media_causal_kmh","hora_dia","dia_semana"});
    public static IReadOnlyList<string> Labels { get; } = Array.AsReadOnly(new[]{"label_segundos"});
    public const string Cabecalho = "observacao_id,viagem_id,volta,veiculo,modal,provedor,timestamp_gps,linha_id,codigo_linha,sentido_id,padrao_id,versao_id,ocorrencia_id,parada_id,topologia,posicao_gps,posicao_destino,distancia_metros,velocidade_kmh,velocidade_media_causal_kmh,hora_dia,dia_semana,split,timestamp_passagem,label_segundos";
    private static bool Id(Guid? id) => id is { } g && g != Guid.Empty;
    // timestamptz armazena microssegundos, JSON do journal pode preservar ticks de100ns.
    private static bool MesmoInstante(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y && Math.Abs(x.UtcTicks-y.UtcTicks)<=9;
    private static bool Fracao(double? n) => n is { } x && double.IsFinite(x) && x is >= 0 and <= 1;
    private static double? Velocidade(double? n) => n is { } x && double.IsFinite(x) && x is >= 0 and <= 160 ? x : null;

    public static (AmostraDatasetEta? Amostra,string? Motivo) Avaliar(CandidatoDatasetEta c,
        ExecucaoDatasetEta e, OpcoesDatasetEta o)
    {
        var g=c.Gps; var h=c.Passagem; var d=c.Destino;
        if(e.Qualidade!=QualidadeExecucaoDataset.AuditadaSemProtecao || string.IsNullOrWhiteSpace(e.ReferenciaAuditoria))
            return (null,"ProcedenciaNaoAuditadaOuProtegida");
        if(c.Journal is not {Tipo:"PassagemParada"} j || j.SchemaVersion!=2
            ||j.EventId!=$"passagem:{h.ViagemId:D}:{h.OcorrenciaParadaPadraoId:D}:{h.Volta}"
            ||j.ViagemId!=h.ViagemId||j.OrdemVeiculo!=h.Ordem||j.CodigoLinha!=h.CodigoLinha
            ||j.SentidoId!=h.SentidoId||j.PadraoVersaoId!=h.PadraoVersaoId
            ||j.OcorrenciaParadaPadraoId!=h.OcorrenciaParadaPadraoId||j.ParadaId!=h.ParadaId
            ||j.Volta!=h.Volta||!MesmoInstante(j.TimestampPassagem,h.TimestampPassagem)||!MesmoInstante(j.TimestampGps,h.TimestampGps)
            ||j.PosicaoLinha!=h.PosicaoNaRota||j.LinhaId!=d.LinhaId||j.PadraoOperacionalId!=d.PadraoId)
            return (null,"PassagemSemJournalConferido");
        if(!Id(g.ViagemId)||!Id(g.SentidoId)||!Id(g.LinhaId)||!Id(g.PadraoVersaoId)
            ||!Id(g.ProximaOcorrenciaParadaPadraoId)||!Id(h.ViagemId)||!Id(h.SentidoId)
            ||!Id(h.PadraoVersaoId)||!Id(h.OcorrenciaParadaPadraoId)||g.Volta is null||h.Volta is null
            ||g.TimestampGps<=DateTimeOffset.UnixEpoch||h.TimestampPassagem is null
            ||string.IsNullOrWhiteSpace(g.ObservacaoId)||string.IsNullOrWhiteSpace(g.OrdemVeiculo)
            ||string.IsNullOrWhiteSpace(g.CodigoLinha)||string.IsNullOrWhiteSpace(g.Provedor)
            ||!Id(d.Id)||!Id(d.ParadaId)||!Id(d.PadraoId)) return (null,"DadosIncompletos");
        if(g.OrigemPosicao!="REAL" || g.Modal is not ("ONIBUS" or "BRT")) return(null,"OrigemOuModalInelegivel");
        if(g.ViagemId!=e.ViagemId||h.ViagemId!=e.ViagemId||g.OrdemVeiculo!=h.Ordem) return(null,"ExecucaoDiferente");
        if(g.Volta<0||g.Volta!=h.Volta) return(null,"VoltaDiferente");
        if(g.SentidoId!=h.SentidoId||g.SentidoId!=d.SentidoId||g.LinhaId!=d.LinhaId
            ||g.CodigoLinha!=h.CodigoLinha||g.CodigoLinha!=d.CodigoLinha||g.PadraoVersaoId!=h.PadraoVersaoId
            ||g.PadraoVersaoId!=d.VersaoId||g.ProximaOcorrenciaParadaPadraoId!=d.Id
            ||g.OcorrenciaParadaPadraoId!=d.Id||h.OcorrenciaParadaPadraoId!=d.Id||h.ParadaId!=d.ParadaId)
            return(null,"IdentidadeEstruturalIncompativel");
        if(d.Topologia is not ("LINEAR" or "CIRCULAR")) return(null,"TopologiaInvalida");
        if(e.Inicio>g.TimestampGps||e.Fim<g.TimestampGps||e.Fim<h.TimestampPassagem||e.Fim<=e.Inicio)
            return(null,"FronteirasExecucaoInvalidas");
        if(g.TimestampGps<o.Inicio||g.TimestampGps>=o.Fim) return(null,"ForaIntervalo");
        // Purga execução que atravesse qualquer corte, sem dividir seus GPS entre splits.
        if((e.Inicio<o.FimTreino&&e.Fim>=o.FimTreino)||(e.Inicio<o.FimValidacao&&e.Fim>=o.FimValidacao))
            return(null,"ExecucaoCruzaSplit");
        var label=(h.TimestampPassagem.Value-g.TimestampGps).TotalSeconds;
        if(!double.IsFinite(label)||label<=0||label>o.MaxLabelSegundos||h.TimestampPassagem>h.TimestampGps)
            return(null,"TempoInvalido");
        if(!Fracao(g.PosicaoNaRota)||!Fracao(d.Posicao)||!Fracao(h.PosicaoNaRota)
            ||Math.Abs(h.PosicaoNaRota-d.Posicao)>1e-8||d.Posicao<=g.PosicaoNaRota
            ||g.ComprimentoRotaMetros is not { } comprimento||!double.IsFinite(comprimento)||comprimento<=0)
            return(null,"DestinoNaoAdianteOuDistanciaInvalida");
        // Fração de geometry não é fração de comprimento geography: não multiplicar por comprimento.
        if(c.DistanciaRotaConferidaMetros is not { } distancia||!double.IsFinite(distancia)||distancia<=0
            ||distancia>comprimento+10) return(null,"DistanciaNaoConferida");
        if(g.DistanciaProximaParadaMetros is not { } informada||!double.IsFinite(informada)||informada<=0
            ||Math.Abs(informada-distancia)>Math.Max(10,distancia*0.1)) return(null,"DistanciasDivergentes");
        var local=g.TimestampGps.ToOffset(TimeSpan.FromHours(-3));
        var split=e.Fim<o.FimTreino?"TRAIN":e.Fim<o.FimValidacao?"VALIDATION":"TEST";
        return(new(g.ObservacaoId,e.ViagemId,g.Volta.Value,g.OrdemVeiculo,g.Modal,g.Provedor,g.TimestampGps,
            d.LinhaId,g.CodigoLinha,d.SentidoId,d.PadraoId,d.VersaoId,d.Id,d.ParadaId,d.Topologia,
            g.PosicaoNaRota!.Value,d.Posicao,distancia,Velocidade(g.VelocidadeInstantanea),
            Velocidade(g.VelocidadeMediaCausal),local.Hour,(int)local.DayOfWeek,split,h.TimestampPassagem.Value,label),null);
    }

    public static async Task<EstatisticasDatasetEta> ExportarCsvAsync(IFonteDatasetEta fonte,
        TextWriter destino, OpcoesDatasetEta o, CancellationToken ct=default)
    {
        if(o.Inicio>=o.Fim||o.FimTreino<=o.Inicio||o.FimValidacao<=o.FimTreino||o.FimValidacao>=o.Fim
            ||o.ExecucoesPorPagina is <1 or >500||o.MaxCandidatosPorExecucao is <1 or >100000
            ||o.MaxPaginas is <1 or >10000||!double.IsFinite(o.MaxLabelSegundos)||o.MaxLabelSegundos<=0)
            throw new ArgumentException("Limites/intervalos inválidos.");
        var stats=new EstatisticasDatasetEta(); string? cursor=null, ultimaViagem=null;
        await destino.WriteLineAsync(Cabecalho.AsMemory(),ct);
        for(var pagina=0;pagina<o.MaxPaginas;pagina++)
        {
            ct.ThrowIfCancellationRequested();
            var p=await fonte.LerPaginaAsync(cursor,o.ExecucoesPorPagina,ct);
            if(p.Execucoes.Count>o.ExecucoesPorPagina) throw new InvalidOperationException("Fonte excedeu tamanho de página.");
            foreach(var e in p.Execucoes)
            {
                var id=e.ViagemId.ToString("D");
                if(e.ViagemId==Guid.Empty||ultimaViagem is not null&&string.CompareOrdinal(id,ultimaViagem)<=0)
                    throw new InvalidOperationException("Execução repetida, fora de ordem ou fragmentada.");
                ultimaViagem=id;
                if(e.Candidatos.Count>o.MaxCandidatosPorExecucao) throw new InvalidOperationException("Execução excedeu limite de candidatos.");
                var validas=new List<AmostraDatasetEta>();
                foreach(var c in e.Candidatos)
                {
                    stats.Lidos++;
                    var r=Avaliar(c,e,o);
                    if(r.Amostra is not { } a) {stats.Descartar(r.Motivo!);continue;}
                    validas.Add(a);
                }
                foreach(var grupo in validas.GroupBy(a=>(a.ObservacaoId,a.OcorrenciaId)))
                {
                    if(grupo.Distinct().Skip(1).Any())
                    {foreach(var _ in grupo) stats.Descartar("DuplicadaConflitante");continue;}
                    foreach(var _ in grupo.Skip(1)) stats.Descartar("Duplicada");
                    await destino.WriteLineAsync(LinhaCsv(grupo.First()).AsMemory(),ct); stats.Exportados++;
                }
            }
            if(p.ProximoCursor is null) return stats;
            if(p.ProximoCursor==cursor||p.Execucoes.Count==0) throw new InvalidOperationException("Paginação sem progresso.");
            cursor=p.ProximoCursor;
        }
        throw new InvalidOperationException("Limite de páginas atingido; exportação parcial não é dataset concluído.");
    }
    private static string Campo(object? x)
    {
        var s=x switch { null=>"",DateTimeOffset t=>t.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),
            IFormattable f=>f.ToString(null,CultureInfo.InvariantCulture),_=>x.ToString()! };
        return "\""+s.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
    }
    internal static string LinhaCsv(AmostraDatasetEta a) => string.Join(',',new object?[]{a.ObservacaoId,a.ViagemId,a.Volta,
        a.Veiculo,a.Modal,a.Provedor,a.TimestampGps,a.LinhaId,a.CodigoLinha,a.SentidoId,a.PadraoId,a.VersaoId,
        a.OcorrenciaId,a.ParadaId,a.Topologia,a.PosicaoGps,a.PosicaoDestino,a.DistanciaMetros,a.VelocidadeKmh,
        a.VelocidadeMediaCausalKmh,a.HoraDia,a.DiaSemana,a.Split,a.TimestampPassagem,a.LabelSegundos}.Select(Campo));
}
