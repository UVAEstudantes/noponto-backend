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
    [JsonPropertyName("ocorrencia_parada_padrao_id")] public Guid? OcorrenciaParadaPadraoId { get; init; }
    [JsonPropertyName("volta")] public int? Volta { get; init; }
    [JsonPropertyName("linha_id")] public Guid? LinhaId { get; init; }
    [JsonPropertyName("sentido_id")] public Guid? SentidoId { get; init; }
    [JsonPropertyName("viagem_id")] public Guid? ViagemId { get; init; }
    [JsonPropertyName("posicao_na_rota")] public double? PosicaoNaRota { get; init; }
    [JsonPropertyName("comprimento_rota_metros")] public double? ComprimentoRotaMetros { get; init; }
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
            OcorrenciaParadaPadraoId = viagem?.ProximaOcorrenciaOperacional?.Id
                ?? posicao.ProximaOcorrenciaParadaPadraoId,
            Volta = viagem?.Estado?.Volta,
            LinhaId = posicao.LinhaId,
            SentidoId = posicao.SentidoId,
            ViagemId = viagem?.Estado?.ViagemId,
            PosicaoNaRota = posicao.PosicaoNaRota,
            ComprimentoRotaMetros = posicao.ComprimentoRotaMetros,
            ProximaOcorrenciaParadaPadraoId = viagem?.ProximaOcorrenciaOperacional?.Id,
            DistanciaProximaParadaMetros = posicao.DistanciaProximaParadaMetros,
            VelocidadeMediaCausal = posicao.VelocidadeMedia,
        };
    }
}
