using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

/// <summary>
/// Cliente HTTP tipado para a API pública de GPS da Mobilidade Rio (fornecedora Zirix).
///
/// ATENÇÃO (07/09/2026): endpoint migrado de
/// https://dados.mobilidade.rio/gps/sppo (descontinuado, será desligado até 30/11/2026)
/// para
/// https://dados.mobilidade.rio/sppo/zirix/gps
/// Ver PosicaoApiDto.cs para o mapeamento de campos do novo schema.
///
/// O filtro dataInicial/dataFinal agora referencia datetime_servidor (quando a
/// posição foi disponibilizada no endpoint), e a doc recomenda ISO 8601 (UTC).
/// Para evitar perder posições que chegaram com atraso, usamos uma janela com
/// overlap retroativo:
///   dataInicial = agora - JanelaSegundos (default: 60s)
///   dataFinal   = agora
///
/// O deduplicador no PollingService (GroupBy + OrderByDescending) garante
/// que só fica a posição mais recente por veículo.
/// </summary>
public sealed class GpsSppoClient
{
    // ISO 8601 UTC, ex: 2026-09-07T19:22:41Z
    private const string FormatoData = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly HttpClient _http;
    private readonly ILogger<GpsSppoClient> _logger;

    public GpsSppoClient(HttpClient http, ILogger<GpsSppoClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Busca posições com janela de overlap.
    /// dataInicial = ate - janelaSegundos
    /// dataFinal   = ate
    /// </summary>
    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarPosicoesPorIntervaloAsync(
        DateTimeOffset de,
        DateTimeOffset ate,
        CancellationToken cancellationToken = default)
        => (await BuscarResultadoPorIntervaloAsync(de, ate, cancellationToken)).Posicoes;

    /// <summary>
    /// Busca usando janela retroativa a partir de "referencia".
    /// janelaSegundos deve ser ao menos 2× o intervalo de polling.
    /// </summary>
    public async Task<IReadOnlyList<PosicaoVeiculoDto>> BuscarComJanelaAsync(
        DateTimeOffset referencia,
        CancellationToken cancellationToken = default,
        int janelaSegundos = 60)
        => (await BuscarResultadoComJanelaAsync(referencia, cancellationToken, janelaSegundos)).Posicoes;

    internal async Task<ResultadoFonteGps> BuscarResultadoComJanelaAsync(
        DateTimeOffset referencia,
        CancellationToken cancellationToken = default,
        int janelaSegundos = 60)
    {
        var inicio = referencia.AddSeconds(-janelaSegundos);
        var fim    = referencia;

        return await BuscarResultadoPorIntervaloAsync(inicio, fim, cancellationToken);
    }

    internal async Task<ResultadoFonteGps> BuscarResultadoPorIntervaloAsync(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        CancellationToken cancellationToken = default)
    {
        if (fim <= inicio)
            throw new ArgumentOutOfRangeException(nameof(fim), "O fim da janela SPPO deve ser posterior ao inicio.");

        var janelaSegundos = (fim - inicio).TotalSeconds;

        // ATENÇÃO: novo endpoint espera UTC em ISO 8601, não mais hora local.
        var dataInicial = inicio.ToUniversalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var dataFinal   = fim.ToUniversalTime().ToString(FormatoData, CultureInfo.InvariantCulture);
        var url = $"?dataInicial={dataInicial}&dataFinal={dataFinal}";

        _logger.LogDebug("Buscando GPS SPPO janela {janela}s: {url}", janelaSegundos, url);

        var cronometro = Stopwatch.StartNew();
        long? contentLength = null;
        int? statusCode = null;
        string? contentEncoding = null;
        using var timeoutDaOperacao = _http.Timeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_http.Timeout);
        using var cancelamento = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutDaOperacao?.Token ?? CancellationToken.None);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var resposta = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancelamento.Token);
            statusCode = (int)resposta.StatusCode;
            contentLength = resposta.Content.Headers.ContentLength;
            contentEncoding = resposta.Content.Headers.ContentEncoding.Count == 0
                ? "identity/ausente"
                : string.Join(',', resposta.Content.Headers.ContentEncoding);

            _logger.LogDebug(
                "SPPO headers recebidos: janela [{inicio}, {fim}] ({janela}s), status {status}, " +
                "headers em {headersMs}ms, Content-Length {contentLength}, Content-Encoding {contentEncoding}",
                inicio, fim, janelaSegundos, statusCode, cronometro.Elapsed.TotalMilliseconds, contentLength,
                contentEncoding);

            if (!resposta.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SPPO respondeu HTTP {status} para janela [{inicio}, {fim}] após {totalMs}ms; " +
                    "Content-Length {contentLength}, Content-Encoding {contentEncoding}",
                    statusCode, inicio, fim, cronometro.Elapsed.TotalMilliseconds, contentLength, contentEncoding);
                return ResultadoFonteGps.Falha($"http_{statusCode}", cronometro.Elapsed);
            }

            var raw = await resposta.Content.ReadFromJsonAsync<List<PosicaoApiDto>>(cancelamento.Token);

            if (raw is null || raw.Count == 0)
            {
                _logger.LogWarning(
                    "SPPO respondeu 200 sem posições para janela [{inicio}, {fim}] após {totalMs}ms; " +
                    "Content-Length {contentLength}, Content-Encoding {contentEncoding}",
                    inicio, fim, cronometro.Elapsed.TotalMilliseconds, contentLength, contentEncoding);
                return ResultadoFonteGps.Vazio(cronometro.Elapsed);
            }

            var normalizadas = new List<PosicaoVeiculoDto>(raw.Count);
            var foraDeOperacao = 0;
            var invalidas = 0;
            DateTimeOffset? watermarkFonte = null;
            var agoraWatermark = DateTimeOffset.UtcNow;

            foreach (var dto in raw)
            {
                if (GpsLeituraValidator.TimestampValido(
                        dto.DataHoraServidor, agoraWatermark, out var timestampServidorFonte)
                    && timestampServidorFonte <= fim
                    && (!watermarkFonte.HasValue || timestampServidorFonte > watermarkFonte.Value))
                {
                    watermarkFonte = timestampServidorFonte;
                }

                var normalizado = Normalizar(dto, out var motivo);

                if (normalizado is not null)
                {
                    normalizadas.Add(normalizado);
                }
                else if (motivo == MotivoDescarte.ForaDeOperacao)
                {
                    foraDeOperacao++;
                }
                else
                {
                    invalidas++;
                }
            }

            _logger.LogInformation(
                "SPPO retornou {total} registros (janela {janela}s): {ativas} normalizadas, " +
                "{fora} fora de operação, {inv} inválidas; status {status}; total {totalMs}ms; " +
                "Content-Length {contentLength}, Content-Encoding {contentEncoding}",
                raw.Count, janelaSegundos, normalizadas.Count, foraDeOperacao, invalidas,
                statusCode, cronometro.Elapsed.TotalMilliseconds, contentLength, contentEncoding);

            return ResultadoFonteGps.Sucesso(normalizadas, cronometro.Elapsed, watermarkFonte);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Consulta SPPO cancelada pelo chamador após {totalMs}ms.",
                cronometro.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex,
                "Timeout/cancelamento interno ao consultar SPPO após {totalMs}ms; status {status}; " +
                "Content-Length {contentLength}; Content-Encoding {contentEncoding}; janela [{inicio}, {fim}]",
                cronometro.Elapsed.TotalMilliseconds, statusCode, contentLength, contentEncoding, inicio, fim);
            return ResultadoFonteGps.Falha("timeout_http", cronometro.Elapsed);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "JSON inválido na resposta SPPO; status {status}; Content-Length {contentLength}; Content-Encoding {contentEncoding}; " +
                "janela [{inicio}, {fim}] após {totalMs}ms",
                statusCode, contentLength, contentEncoding, inicio, fim, cronometro.Elapsed.TotalMilliseconds);
            return ResultadoFonteGps.Falha("json_invalido", cronometro.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "Falha HTTP/transporte SPPO; status {status}; Content-Length {contentLength}; Content-Encoding {contentEncoding}; " +
                "janela [{inicio}, {fim}] após {totalMs}ms",
                ex.StatusCode is { } statusHttp ? (int)statusHttp : statusCode,
                contentLength, contentEncoding, inicio, fim, cronometro.Elapsed.TotalMilliseconds);
            return ResultadoFonteGps.Falha("http_transporte", cronometro.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha inesperada ao ler resposta SPPO; status {status}; Content-Length {contentLength}; Content-Encoding {contentEncoding}; " +
                "janela [{inicio}, {fim}] após {totalMs}ms",
                statusCode, contentLength, contentEncoding, inicio, fim, cronometro.Elapsed.TotalMilliseconds);
            return ResultadoFonteGps.Falha("inesperada", cronometro.Elapsed);
        }
    }

    private enum MotivoDescarte
    {
        Nenhum,
        ForaDeOperacao,
        Invalida,
        SemTimestampConfiavel,
    }

    private PosicaoVeiculoDto? Normalizar(PosicaoApiDto dto, out MotivoDescarte motivo)
    {
        if (string.IsNullOrWhiteSpace(dto.Ordem))
        {
            motivo = MotivoDescarte.Invalida;
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Linha))
        {
            motivo = MotivoDescarte.ForaDeOperacao;
            return null;
        }

        if (!TryParseDecimalBr(dto.Latitude, out var lat) ||
            !TryParseDecimalBr(dto.Longitude, out var lon) ||
            !GpsLeituraValidator.CoordenadaValida(lat, lon))
        {
            motivo = MotivoDescarte.Invalida;
            return null;
        }

        var agora = DateTimeOffset.UtcNow;
        if (!GpsLeituraValidator.TimestampValido(dto.DataHora, agora, out var timestampGps))
        {
            motivo = MotivoDescarte.SemTimestampConfiavel;
            return null;
        }

        if (!TryParseDouble(dto.Velocidade, out var velocidade))
            velocidade = 0;

        var timestampServidor = dto.DataHoraServidor ?? dto.DataHoraEnvio ?? agora;

        motivo = MotivoDescarte.Nenhum;

        return new PosicaoVeiculoDto
        {
            Ordem             = dto.Ordem.Trim().ToUpperInvariant(),
            CodigoLinha       = dto.Linha.Trim().ToUpperInvariant(),
            Latitude          = lat,
            Longitude         = lon,
            Velocidade        = velocidade,
            TimestampGps      = timestampGps,
            TimestampServidor = timestampServidor,
            TimestampEnvioFonte = dto.DataHoraEnvio,
            TimestampServidorFonte = dto.DataHoraServidor,
        };
    }

    private static bool TryParseDecimalBr(string? valor, out double resultado)
    {
        resultado = 0;
        return !string.IsNullOrWhiteSpace(valor) &&
            double.TryParse(
                valor.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out resultado);
    }

    private static bool TryParseDouble(string? valor, out double resultado)
    {
        resultado = 0;
        return !string.IsNullOrWhiteSpace(valor) &&
            double.TryParse(
                valor.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out resultado);
    }
}
