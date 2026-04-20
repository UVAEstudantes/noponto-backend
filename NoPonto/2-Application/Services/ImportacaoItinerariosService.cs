using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class ImportacaoItinerariosService : BackgroundService
{
    private const string NomeModalOnibus = "Ônibus";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImportacaoItinerariosService> _logger;

    public ImportacaoItinerariosService(
        IServiceScopeFactory serviceScopeFactory,
        IConfiguration configuration,
        ILogger<ImportacaoItinerariosService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var horarioImportacao = LerHorarioImportacao();
                var agora = DateTime.Now;
                var proximoHorario = agora.Date.Add(horarioImportacao);

                if (proximoHorario <= agora)
                    proximoHorario = proximoHorario.AddDays(1);

                var tempoEspera = proximoHorario - agora;

                _logger.LogInformation(
                    "Próxima importação de itinerários agendada para {ProximoHorario}.",
                    proximoHorario);

                await Task.Delay(tempoEspera, stoppingToken);
                await ExecutarImportacaoAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na execução agendada da importação de itinerários.");

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task ExecutarImportacaoAsync(CancellationToken cancellationToken)
    {
        var cronometro = Stopwatch.StartNew();

        _logger.LogInformation("Iniciando importação de itinerários...");

        using var escopo = _serviceScopeFactory.CreateScope();
        var contexto = escopo.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var arcGisClient = escopo.ServiceProvider.GetRequiredService<ArcGisClientService>();

        var tamanhoPagina = LerInteiroConfiguracao("ARCGIS:PAGE_SIZE", 2000);
        var tamanhoLote = LerInteiroConfiguracao("IMPORT:BATCH_SIZE", 100);

        var metadados = await BuscarTodosMetadadosAsync(arcGisClient, tamanhoPagina, cancellationToken);

        _logger.LogInformation("Total de registros recebidos: {TotalRegistros}", metadados.Count);

        var alteracoesPendentes = 0;
        var linhasCriadas = 0;
        var sentidosCriados = 0;
        var itinerariosCriados = 0;

        var modalOnibus = await contexto.Modais
            .FirstOrDefaultAsync(modal => modal.Nome == NomeModalOnibus, cancellationToken);

        if (modalOnibus is null)
        {
            modalOnibus = new Modal
            {
                Id = Guid.NewGuid(),
                Nome = NomeModalOnibus
            };

            await contexto.Modais.AddAsync(modalOnibus, cancellationToken);
            alteracoesPendentes++;
        }

        var codigosLinhas = metadados
            .Select(item => item.Servico.Trim())
            .Where(codigo => !string.IsNullOrWhiteSpace(codigo))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var linhasExistentes = codigosLinhas.Count == 0
            ? []
            : await contexto.Linhas
                .Where(linha => codigosLinhas.Contains(linha.Codigo))
                .ToListAsync(cancellationToken);

        var linhasPorCodigo = linhasExistentes.ToDictionary(
            linha => linha.Codigo.Trim(),
            linha => linha,
            StringComparer.OrdinalIgnoreCase);

        var linhasIdsExistentes = linhasExistentes.Select(item => item.Id).ToList();
        var sentidosExistentes = linhasIdsExistentes.Count == 0
            ? []
            : await contexto.Sentidos
                .Where(sentido => linhasIdsExistentes.Contains(sentido.LinhaId))
                .ToListAsync(cancellationToken);

        var sentidosPorChave = sentidosExistentes.ToDictionary(
            sentido => CriarChaveSentido(sentido.LinhaId, sentido.Nome),
            sentido => sentido,
            StringComparer.Ordinal);

        var sentidosIdsExistentes = sentidosExistentes.Select(item => item.Id).ToList();
        var chavesItinerariosExistentes = new HashSet<string>(StringComparer.Ordinal);

        if (sentidosIdsExistentes.Count > 0)
        {
            var itinerariosExistentes = await contexto.Itinerarios
                .Where(itinerario => sentidosIdsExistentes.Contains(itinerario.SentidoId))
                .Select(itinerario => new { itinerario.SentidoId, itinerario.DistanciaMetros })
                .ToListAsync(cancellationToken);

            foreach (var itinerarioExistente in itinerariosExistentes)
            {
                chavesItinerariosExistentes.Add(
                    CriarChaveItinerario(itinerarioExistente.SentidoId, itinerarioExistente.DistanciaMetros));
            }
        }

        foreach (var metadado in metadados)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var codigoLinha = metadado.Servico.Trim();
            var destino = metadado.Destino.Trim();
            var direcao = metadado.Direcao.Trim();
            var shapeId = metadado.ShapeId.Trim();

            if (string.IsNullOrWhiteSpace(codigoLinha)
                || string.IsNullOrWhiteSpace(destino)
                || string.IsNullOrWhiteSpace(direcao)
                || string.IsNullOrWhiteSpace(shapeId))
            {
                continue;
            }

            if (!linhasPorCodigo.TryGetValue(codigoLinha, out var linha))
            {
                linha = new Linha
                {
                    Id = Guid.NewGuid(),
                    Codigo = codigoLinha,
                    Nome = $"{codigoLinha} - {destino}",
                    ModalId = modalOnibus.Id,
                    Modal = modalOnibus
                };

                await contexto.Linhas.AddAsync(linha, cancellationToken);

                linhasPorCodigo[codigoLinha] = linha;
                linhasCriadas++;
                alteracoesPendentes++;
            }

            var nomeSentido = $"{destino} ({direcao})";
            var chaveSentido = CriarChaveSentido(linha.Id, nomeSentido);

            if (!sentidosPorChave.TryGetValue(chaveSentido, out var sentido))
            {
                sentido = new Sentido
                {
                    Id = Guid.NewGuid(),
                    LinhaId = linha.Id,
                    Nome = nomeSentido
                };

                await contexto.Sentidos.AddAsync(sentido, cancellationToken);

                sentidosPorChave[chaveSentido] = sentido;
                sentidosCriados++;
                alteracoesPendentes++;
            }

            var chaveItinerario = CriarChaveItinerario(sentido.Id, metadado.DistanciaMetros);

            if (chavesItinerariosExistentes.Contains(chaveItinerario))
                continue;

            try
            {
                var geometria = await arcGisClient.BuscarGeometriaAsync(shapeId, cancellationToken);

                if (geometria is null)
                {
                    _logger.LogError("Erro ao importar shape_id: {shapeId}", shapeId);
                    continue;
                }

                var itinerario = new Itinerario
                {
                    Id = Guid.NewGuid(),
                    SentidoId = sentido.Id,
                    DistanciaMetros = metadado.DistanciaMetros,
                    Geometria = geometria
                };

                await contexto.Itinerarios.AddAsync(itinerario, cancellationToken);

                chavesItinerariosExistentes.Add(chaveItinerario);
                itinerariosCriados++;
                alteracoesPendentes++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao importar shape_id: {shapeId}", shapeId);
            }

            if (alteracoesPendentes >= tamanhoLote)
            {
                await contexto.SaveChangesAsync(cancellationToken);
                alteracoesPendentes = 0;
            }
        }

        if (alteracoesPendentes > 0)
        {
            await contexto.SaveChangesAsync(cancellationToken);
        }

        cronometro.Stop();

        _logger.LogInformation("Linhas criadas: {LinhasCriadas}", linhasCriadas);
        _logger.LogInformation("Sentidos criados: {SentidosCriados}", sentidosCriados);
        _logger.LogInformation("Itinerários criados: {ItinerariosCriados}", itinerariosCriados);
        _logger.LogInformation(
            "Importação concluída em {Segundos} segundos.",
            cronometro.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    private async Task<List<MetadadoItinerarioArcGis>> BuscarTodosMetadadosAsync(
        ArcGisClientService arcGisClient,
        int tamanhoPagina,
        CancellationToken cancellationToken)
    {
        var resultOffset = 0;
        var metadados = new List<MetadadoItinerarioArcGis>();

        while (true)
        {
            var pagina = await arcGisClient.BuscarMetadadosAsync(
                resultOffset,
                tamanhoPagina,
                cancellationToken);

            if (pagina.Count == 0)
                break;

            metadados.AddRange(pagina);

            if (pagina.Count < tamanhoPagina)
                break;

            resultOffset += tamanhoPagina;
        }

        return metadados;
    }

    private TimeSpan LerHorarioImportacao()
    {
        var horarioConfigurado = _configuration["ARCGIS:HORARIO_IMPORTACAO"];

        if (string.IsNullOrWhiteSpace(horarioConfigurado))
            throw new InvalidOperationException("Variável de ambiente ARCGIS__HORARIO_IMPORTACAO não configurada.");

        if (TimeSpan.TryParseExact(
                horarioConfigurado,
                @"hh\:mm",
                CultureInfo.InvariantCulture,
                out var horarioImportacao))
        {
            return horarioImportacao;
        }

        if (TimeSpan.TryParse(horarioConfigurado, CultureInfo.InvariantCulture, out horarioImportacao))
            return new TimeSpan(horarioImportacao.Hours, horarioImportacao.Minutes, 0);

        throw new InvalidOperationException(
            "Valor inválido para ARCGIS__HORARIO_IMPORTACAO. Formato esperado: HH:mm.");
    }

    private int LerInteiroConfiguracao(string chave, int valorPadrao)
    {
        var valorConfigurado = _configuration[chave];

        if (int.TryParse(valorConfigurado, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor)
            && valor > 0)
        {
            return valor;
        }

        return valorPadrao;
    }

    private static string CriarChaveSentido(Guid linhaId, string nomeSentido)
    {
        return $"{linhaId:N}|{nomeSentido.Trim().ToUpperInvariant()}";
    }

    private static string CriarChaveItinerario(Guid sentidoId, double distanciaMetros)
    {
        return $"{sentidoId:N}|{distanciaMetros.ToString("G17", CultureInfo.InvariantCulture)}";
    }
}