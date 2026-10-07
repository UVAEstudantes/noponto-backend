using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

public static class TelemetriaMlContrato
{
    public const string OrigemReal = "REAL";
    public const string Stream = "noponto:ml:telemetria";
    public const string Group = "telemetria-ml-postgres";
    public const string DeadLetter = "noponto:ml:telemetria:dead-letter";

    public static string ObservacaoId(string modal, string provedor, string ordem, DateTimeOffset timestampGps)
    {
        var canonical = $"{modal.Trim().ToUpperInvariant()}|{provedor.Trim().ToUpperInvariant()}|" +
                        $"{ordem.Trim().ToUpperInvariant()}|{timestampGps.ToUniversalTime():O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record EventoTelemetriaMl
{
    [JsonPropertyName("observacao_id")] public required string ObservacaoId { get; init; }
    [JsonPropertyName("modal")] public required string Modal { get; init; }
    [JsonPropertyName("provedor")] public required string Provedor { get; init; }
    [JsonPropertyName("ordem_veiculo")] public required string OrdemVeiculo { get; init; }
    [JsonPropertyName("codigo_linha")] public required string CodigoLinha { get; init; }
    [JsonPropertyName("origem_posicao")] public string OrigemPosicao { get; init; } = TelemetriaMlContrato.OrigemReal;

    [JsonPropertyName("latitude_recebida")] public double LatitudeRecebida { get; init; }
    [JsonPropertyName("longitude_recebida")] public double LongitudeRecebida { get; init; }
    [JsonPropertyName("latitude_projetada")] public double? LatitudeProjetada { get; init; }
    [JsonPropertyName("longitude_projetada")] public double? LongitudeProjetada { get; init; }
    [JsonPropertyName("velocidade_instantanea")] public double VelocidadeInstantanea { get; init; }
    [JsonPropertyName("bearing")] public double? Bearing { get; init; }

    [JsonPropertyName("timestamp_gps")] public DateTimeOffset TimestampGps { get; init; }
    [JsonPropertyName("timestamp_envio_fonte")] public DateTimeOffset? TimestampEnvioFonte { get; init; }
    [JsonPropertyName("timestamp_servidor_fonte")] public DateTimeOffset? TimestampServidorFonte { get; init; }
    [JsonPropertyName("recebido_em_utc")] public DateTimeOffset RecebidoEmUtc { get; init; }
    [JsonPropertyName("evento_criado_em_utc")] public DateTimeOffset EventoCriadoEmUtc { get; init; }

    [JsonPropertyName("padrao_versao_id")] public Guid? PadraoVersaoId { get; init; }
    // Próxima ocorrência compatível; sem associação operacional, preserva o alvo observacional.
    [JsonPropertyName("ocorrencia_parada_padrao_id")] public Guid? OcorrenciaParadaPadraoId { get; init; }
    [JsonPropertyName("volta")] public int? Volta { get; init; }
    [JsonPropertyName("linha_id")] public Guid? LinhaId { get; init; }
    [JsonPropertyName("sentido_id")] public Guid? SentidoId { get; init; }
    // Somente uma execução confirmada e compatível com o matching desta observação.
    [JsonPropertyName("viagem_id")] public Guid? ViagemId { get; init; }
    [JsonPropertyName("posicao_na_rota")] public double? PosicaoNaRota { get; init; }
    [JsonPropertyName("comprimento_rota_metros")] public double? ComprimentoRotaMetros { get; init; }
    // Alvo operacional: não inclui o fallback observacional. Nome JSON legado preservado.
    [JsonPropertyName("proxima_parada_padrao_versao_id")] public Guid? ProximaOcorrenciaParadaPadraoId { get; init; }
    [JsonPropertyName("distancia_proxima_parada_metros")] public double? DistanciaProximaParadaMetros { get; init; }
    [JsonPropertyName("velocidade_media_causal")] public double? VelocidadeMediaCausal { get; init; }
}

public interface ITelemetriaMlIngress
{
    bool TentarPublicar(EventoTelemetriaMl evento);
}

public static class EventoTelemetriaMlFactory
{
    public static EventoTelemetriaMl Criar(
        PosicaoVeiculoDto posicao,
        ViagemObservadaResultado? viagem,
        DateTimeOffset criadoEmUtc)
    {
        var modal = posicao.ModalFonte;
        var provedor = posicao.ProvedorFonte;
        var operacional = IdentidadeCompativel(posicao, viagem) ? viagem : null;
        return new EventoTelemetriaMl
        {
            ObservacaoId = TelemetriaMlContrato.ObservacaoId(modal, provedor, posicao.Ordem, posicao.TimestampGps),
            Modal = modal,
            Provedor = provedor,
            OrdemVeiculo = posicao.Ordem,
            CodigoLinha = posicao.CodigoLinha,
            LatitudeRecebida = posicao.Latitude,
            LongitudeRecebida = posicao.Longitude,
            // O DTO atual não expõe a coordenada projetada; não duplicar a coordenada recebida.
            LatitudeProjetada = null,
            LongitudeProjetada = null,
            VelocidadeInstantanea = posicao.Velocidade,
            Bearing = posicao.Bearing,
            TimestampGps = posicao.TimestampGps.ToUniversalTime(),
            TimestampEnvioFonte = posicao.TimestampEnvioFonte?.ToUniversalTime(),
            TimestampServidorFonte = posicao.TimestampServidorFonte?.ToUniversalTime(),
            RecebidoEmUtc = posicao.RecebidoEmUtc.ToUniversalTime(),
            EventoCriadoEmUtc = criadoEmUtc.ToUniversalTime(),
            PadraoVersaoId = posicao.PadraoVersaoId,
            OcorrenciaParadaPadraoId = operacional?.ProximaOcorrenciaOperacional?.Id
                ?? posicao.ProximaOcorrenciaParadaPadraoId,
            Volta = operacional?.Estado?.Volta,
            LinhaId = posicao.LinhaId,
            SentidoId = posicao.SentidoId,
            ViagemId = operacional?.Estado?.ViagemId,
            PosicaoNaRota = posicao.PosicaoNaRota,
            ComprimentoRotaMetros = posicao.ComprimentoRotaMetros,
            ProximaOcorrenciaParadaPadraoId = operacional?.ProximaOcorrenciaOperacional?.Id,
            DistanciaProximaParadaMetros = posicao.DistanciaProximaParadaMetros,
            VelocidadeMediaCausal = posicao.VelocidadeMedia,
        };
    }

    private static bool IdentidadeCompativel(PosicaoVeiculoDto posicao, ViagemObservadaResultado? resultado)
    {
        if (resultado is not { Status: ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated,
                EstadoOperacional: { } operacional, Estado: { } observada }
            || operacional.Estado is not (EstadoViagem.Ativa or EstadoViagem.PossivelFim)
            || !ViagemOperacionalRegra.IdentidadeConfiavel(operacional)
            || operacional.Observada != observada
            || observada.ViagemId == Guid.Empty
            || observada.OrdemVeiculo != posicao.Ordem
            || observada.TimestampUltimaAtualizacao != posicao.TimestampGps
            || string.IsNullOrWhiteSpace(posicao.CodigoLinha)
            || operacional.CodigoLinha != posicao.CodigoLinha
            || operacional.LinhaId == Guid.Empty || operacional.LinhaId != posicao.LinhaId
            || operacional.SentidoId == Guid.Empty || operacional.SentidoId != posicao.SentidoId
            || observada.PadraoVersaoId == Guid.Empty || observada.PadraoVersaoId != posicao.PadraoVersaoId
            || observada.PadraoOperacionalId == Guid.Empty || observada.PadraoOperacionalId != posicao.PadraoOperacionalId
            || observada.Topologia != posicao.TopologiaPadrao)
            return false;

        // A ocorrência operacional também precisa pertencer à versão observacional.
        return resultado.Value.ProximaOcorrenciaOperacional is not { } proxima
            || (proxima.Id != Guid.Empty && proxima.PadraoVersaoId == observada.PadraoVersaoId);
    }
}
