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
                await ExecutarImportacaoParadasAsync(stoppingToken);
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

        var tamanhoPagina = LerInteiroConfiguracaoComFallback(
            "ARCGIS:ITINERARIOS:PAGE_SIZE",
            "ARCGIS:PAGE_SIZE",
            2000);
        var tamanhoLote = LerInteiroConfiguracao("IMPORT:BATCH_SIZE", 500);

        var metadados = await BuscarTodosMetadadosAsync(arcGisClient, tamanhoPagina, cancellationToken);

        _logger.LogInformation("Total de registros recebidos: {TotalRegistros}", metadados.Count);

        var linhasCriadas = 0;
        var sentidosCriados = 0;
        var itinerariosCriados = 0;
        var totalRegistrosSalvos = 0;

        var modaisPendentes = new List<Modal>();
        var linhasPendentes = new List<Linha>();
        var sentidosPendentes = new List<Sentido>();
        var itinerariosPendentes = new List<Itinerario>();

        var modalOnibusId = await contexto.Modais
            .AsNoTracking()
            .Where(modal => modal.Nome == NomeModalOnibus)
            .Select(modal => modal.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (modalOnibusId == Guid.Empty)
        {
            modalOnibusId = Guid.NewGuid();

            var modalOnibus = new Modal
            {
                Id = modalOnibusId,
                Nome = NomeModalOnibus
            };

            modaisPendentes.Add(modalOnibus);
        }

        var codigosLinhas = metadados
            .Select(item => item.Servico.Trim())
            .Where(codigo => !string.IsNullOrWhiteSpace(codigo))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var linhasExistentes = codigosLinhas.Count == 0
            ? []
            : await contexto.Linhas
                .AsNoTracking()
                .Where(linha => codigosLinhas.Contains(linha.Codigo))
                .Select(linha => new { linha.Id, linha.Codigo })
                .ToListAsync(cancellationToken);

        var linhasPorCodigo = linhasExistentes.ToDictionary(
            linha => linha.Codigo.Trim(),
            linha => linha.Id,
            StringComparer.OrdinalIgnoreCase);

        var linhasIdsExistentes = linhasExistentes.Select(item => item.Id).ToList();
        var sentidosExistentes = linhasIdsExistentes.Count == 0
            ? []
            : await contexto.Sentidos
                .AsNoTracking()
                .Where(sentido => linhasIdsExistentes.Contains(sentido.LinhaId))
                .Select(sentido => new { sentido.Id, sentido.LinhaId, sentido.Nome })
                .ToListAsync(cancellationToken);

        var sentidosPorChave = sentidosExistentes.ToDictionary(
            sentido => CriarChaveSentido(sentido.LinhaId, sentido.Nome),
            sentido => sentido.Id,
            StringComparer.Ordinal);

        var sentidosIdsExistentes = sentidosExistentes.Select(item => item.Id).ToList();
        var chavesItinerariosExistentes = new HashSet<string>(StringComparer.Ordinal);

        if (sentidosIdsExistentes.Count > 0)
        {
            var itinerariosExistentes = await contexto.Itinerarios
                .AsNoTracking()
                .Where(itinerario => sentidosIdsExistentes.Contains(itinerario.SentidoId))
                .Select(itinerario => new { itinerario.SentidoId, itinerario.DistanciaMetros })
                .ToListAsync(cancellationToken);

            foreach (var itinerarioExistente in itinerariosExistentes)
            {
                chavesItinerariosExistentes.Add(
                    CriarChaveItinerario(itinerarioExistente.SentidoId, itinerarioExistente.DistanciaMetros));
            }
        }

        var alteracoesPendentes = modaisPendentes.Count;

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

            if (!linhasPorCodigo.TryGetValue(codigoLinha, out var linhaId))
            {
                linhaId = Guid.NewGuid();

                var linha = new Linha
                {
                    Id = linhaId,
                    Codigo = codigoLinha,
                    Nome = $"{codigoLinha} - {destino}",
                    ModalId = modalOnibusId
                };

                linhasPendentes.Add(linha);

                linhasPorCodigo[codigoLinha] = linhaId;
                linhasCriadas++;
                alteracoesPendentes++;
            }

            var nomeSentido = $"{destino} ({direcao})";
            var chaveSentido = CriarChaveSentido(linhaId, nomeSentido);

            if (!sentidosPorChave.TryGetValue(chaveSentido, out var sentidoId))
            {
                sentidoId = Guid.NewGuid();

                var sentido = new Sentido
                {
                    Id = sentidoId,
                    LinhaId = linhaId,
                    Nome = nomeSentido
                };

                sentidosPendentes.Add(sentido);

                sentidosPorChave[chaveSentido] = sentidoId;
                sentidosCriados++;
                alteracoesPendentes++;
            }

            var chaveItinerario = CriarChaveItinerario(sentidoId, metadado.DistanciaMetros);

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
                    SentidoId = sentidoId,
                    DistanciaMetros = metadado.DistanciaMetros,
                    Geometria = geometria
                };

                itinerariosPendentes.Add(itinerario);

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
                totalRegistrosSalvos += await SalvarLotesPendentesAsync(
                    contexto,
                    modaisPendentes,
                    linhasPendentes,
                    sentidosPendentes,
                    itinerariosPendentes,
                    tamanhoLote,
                    cancellationToken);

                alteracoesPendentes =
                    modaisPendentes.Count +
                    linhasPendentes.Count +
                    sentidosPendentes.Count +
                    itinerariosPendentes.Count;
            }
        }

        totalRegistrosSalvos += await SalvarLotesPendentesAsync(
            contexto,
            modaisPendentes,
            linhasPendentes,
            sentidosPendentes,
            itinerariosPendentes,
            tamanhoLote,
            cancellationToken);

        cronometro.Stop();

        _logger.LogInformation("Linhas criadas: {LinhasCriadas}", linhasCriadas);
        _logger.LogInformation("Sentidos criados: {SentidosCriados}", sentidosCriados);
        _logger.LogInformation("Itinerários criados: {ItinerariosCriados}", itinerariosCriados);
        _logger.LogInformation("Total de registros salvos: {TotalRegistrosSalvos}", totalRegistrosSalvos);
        _logger.LogInformation(
            "Importação concluída em {Segundos} segundos.",
            cronometro.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
    }

    private async Task ExecutarImportacaoParadasAsync(CancellationToken cancellationToken)
    {
        using var escopo = _serviceScopeFactory.CreateScope();
        var importacaoParadasService = escopo.ServiceProvider.GetRequiredService<ImportacaoParadasService>();

        await importacaoParadasService.ExecutarImportacaoAsync(cancellationToken);
    }

    private async Task<int> SalvarLotesPendentesAsync(
        TransporteDbContext contexto,
        List<Modal> modaisPendentes,
        List<Linha> linhasPendentes,
        List<Sentido> sentidosPendentes,
        List<Itinerario> itinerariosPendentes,
        int tamanhoLote,
        CancellationToken cancellationToken)
    {
        var totalRegistrosSalvos = 0;

        totalRegistrosSalvos += await SalvarEntidadesEmLotesAsync(
            contexto,
            modaisPendentes,
            tamanhoLote,
            "Modais",
            cancellationToken);

        totalRegistrosSalvos += await SalvarEntidadesEmLotesAsync(
            contexto,
            linhasPendentes,
            tamanhoLote,
            "Linhas",
            cancellationToken);

        totalRegistrosSalvos += await SalvarEntidadesEmLotesAsync(
            contexto,
            sentidosPendentes,
            tamanhoLote,
            "Sentidos",
            cancellationToken);

        totalRegistrosSalvos += await SalvarEntidadesEmLotesAsync(
            contexto,
            itinerariosPendentes,
            tamanhoLote,
            "Itinerários",
            cancellationToken);

        return totalRegistrosSalvos;
    }

    private async Task<int> SalvarEntidadesEmLotesAsync<TEntity>(
        TransporteDbContext contexto,
        List<TEntity> entidadesPendentes,
        int tamanhoLote,
        string nomeEntidade,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (entidadesPendentes.Count == 0)
            return 0;

        var totalLotes = (int)Math.Ceiling((double)entidadesPendentes.Count / tamanhoLote);
        var totalRegistrosSalvos = 0;

        for (var indice = 0; indice < entidadesPendentes.Count; indice += tamanhoLote)
        {
            var loteAtual = (indice / tamanhoLote) + 1;
            var lote = entidadesPendentes
                .Skip(indice)
                .Take(tamanhoLote)
                .ToList();

            _logger.LogInformation(
                "Salvando lote {LoteAtual} de {TotalLotes} ({NomeEntidade})...",
                loteAtual,
                totalLotes,
                nomeEntidade);

            contexto.AddRange(lote);
            await contexto.SaveChangesAsync(cancellationToken);
            contexto.ChangeTracker.Clear();

            totalRegistrosSalvos += lote.Count;

            _logger.LogInformation(
                "Lote salvo com sucesso ({NomeEntidade}): {Quantidade} registros.",
                nomeEntidade,
                lote.Count);
        }

        entidadesPendentes.Clear();
        return totalRegistrosSalvos;
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
        var horaImportacao = _configuration["IMPORTACAO_HORA"];
        var minutoImportacao = _configuration["IMPORTACAO_MINUTO"];

        if (int.TryParse(horaImportacao, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hora)
            && int.TryParse(minutoImportacao, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minuto))
        {
            if (hora is < 0 or > 23)
                throw new InvalidOperationException("Valor inválido para IMPORTACAO_HORA. Intervalo esperado: 0-23.");

            if (minuto is < 0 or > 59)
                throw new InvalidOperationException("Valor inválido para IMPORTACAO_MINUTO. Intervalo esperado: 0-59.");

            return new TimeSpan(hora, minuto, 0);
        }

        var horarioConfigurado =
            _configuration["ARCGIS:ITINERARIOS:HORARIO_IMPORTACAO"]
            ?? _configuration["ARCGIS:HORARIO_IMPORTACAO"];

        if (string.IsNullOrWhiteSpace(horarioConfigurado))
            throw new InvalidOperationException(
                "Defina IMPORTACAO_HORA/IMPORTACAO_MINUTO ou ARCGIS__HORARIO_IMPORTACAO.");

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

    private int LerInteiroConfiguracaoComFallback(string chavePrincipal, string chaveSecundaria, int valorPadrao)
    {
        var valorPrincipal = _configuration[chavePrincipal];

        if (int.TryParse(valorPrincipal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor)
            && valor > 0)
        {
            return valor;
        }

        return LerInteiroConfiguracao(chaveSecundaria, valorPadrao);
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