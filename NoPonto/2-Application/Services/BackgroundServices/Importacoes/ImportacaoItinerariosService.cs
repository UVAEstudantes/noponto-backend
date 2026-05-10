using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.Services;

public sealed class ImportacaoItinerariosService : BackgroundService
{
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

        var linhasCriadas        = 0;
        var linhasAtualizadas    = 0;
        var sentidosCriados      = 0;
        var itinerariosCriados   = 0;
        var totalRegistrosSalvos = 0;

        var modaisPendentes      = new List<Modal>();
        var linhasPendentes      = new List<Linha>();
        var sentidosPendentes    = new List<Sentido>();
        var itinerariosPendentes = new List<Itinerario>();

        // ── Modais ────────────────────────────────────────────────────────────
        var modaisExistentes = await contexto.Modais
            .AsNoTracking()
            .ToDictionaryAsync(m => m.Nome, m => m.Id, cancellationToken);

        Guid ObterOuCriarModal(string tipoRota)
        {
            var nomeModal = tipoRota.ToLowerInvariant() switch
            {
                "brt" => "BRT",
                _     => "Ônibus"
            };

            if (modaisExistentes.TryGetValue(nomeModal, out var id))
                return id;

            var novoId = Guid.NewGuid();
            modaisPendentes.Add(new Modal { Id = novoId, Nome = nomeModal });
            modaisExistentes[nomeModal] = novoId;
            return novoId;
        }

        // ── Linhas existentes ─────────────────────────────────────────────────
        //
        // REGRA CRÍTICA: NÃO usar .Select() nem .AsNoTracking() aqui.
        //
        // O EF só gera UPDATE quando compara o valor atual da propriedade com o
        // "snapshot" original que ele gravou no momento do carregamento.
        // Se usarmos .Select(new { Id, Codigo }), o snapshot só tem Id e Codigo;
        // quando mudamos TipoRota, o EF não sabe o valor original e não detecta
        // mudança — o SaveChanges não emite nenhum UPDATE.
        //
        // Com .ToListAsync() sem projeção, o EF carrega a entidade completa e
        // rastreia todos os campos corretamente.
        //
        var codigosLinhas = metadados
            .Select(m => m.Servico.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var linhasExistentesTracked = codigosLinhas.Count == 0
            ? []
            : await contexto.Linhas
                .Where(l => codigosLinhas.Contains(l.Codigo))
                .ToListAsync(cancellationToken); // sem .Select() e sem .AsNoTracking()

        var linhasPorCodigo = linhasExistentesTracked.ToDictionary(
            l => l.Codigo.Trim(),
            l => l,
            StringComparer.OrdinalIgnoreCase);

        // ── Sentidos existentes ───────────────────────────────────────────────
        var linhasIdsExistentes = linhasExistentesTracked.Select(l => l.Id).ToList();

        var sentidosExistentes = linhasIdsExistentes.Count == 0
            ? []
            : await contexto.Sentidos
                .AsNoTracking()
                .Where(s => linhasIdsExistentes.Contains(s.LinhaId))
                .Select(s => new { s.Id, s.LinhaId, s.Nome })
                .ToListAsync(cancellationToken);

        var sentidosPorChave = sentidosExistentes.ToDictionary(
            s => CriarChaveSentido(s.LinhaId, s.Nome),
            s => s.Id,
            StringComparer.Ordinal);

        // ── Itinerários existentes ────────────────────────────────────────────
        var sentidosIdsExistentes = sentidosExistentes.Select(s => s.Id).ToList();
        var chavesItinerariosExistentes = new HashSet<string>(StringComparer.Ordinal);

        if (sentidosIdsExistentes.Count > 0)
        {
            var itinerariosExistentes = await contexto.Itinerarios
                .AsNoTracking()
                .Where(i => sentidosIdsExistentes.Contains(i.SentidoId))
                .Select(i => new { i.SentidoId, i.DistanciaMetros })
                .ToListAsync(cancellationToken);

            foreach (var ie in itinerariosExistentes)
                chavesItinerariosExistentes.Add(CriarChaveItinerario(ie.SentidoId, ie.DistanciaMetros));
        }

        // ── Loop principal ────────────────────────────────────────────────────
        var alteracoesPendentes = modaisPendentes.Count;

        foreach (var metadado in metadados)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var codigoLinha = metadado.Servico.Trim();
            var destino     = metadado.Destino.Trim();
            var direcao     = metadado.Direcao.Trim();
            var shapeId     = metadado.ShapeId.Trim();
            var tipoRota    = metadado.TipoRota?.Trim().ToLowerInvariant() ?? "regular";
            var consorcio   = metadado.Consorcio?.Trim();

            if (string.IsNullOrWhiteSpace(codigoLinha)
                || string.IsNullOrWhiteSpace(destino)
                || string.IsNullOrWhiteSpace(direcao)
                || string.IsNullOrWhiteSpace(shapeId))
            {
                continue;
            }

            Guid linhaId;

            if (!linhasPorCodigo.TryGetValue(codigoLinha, out var linhaExistente))
            {
                // ── NOVA linha ────────────────────────────────────────────────
                var modalId = ObterOuCriarModal(tipoRota);

                var novaLinha = new Linha
                {
                    Id        = Guid.NewGuid(),
                    Codigo    = codigoLinha,
                    Nome      = $"{codigoLinha} - {destino}",
                    ModalId   = modalId,
                    TipoRota  = tipoRota,
                    Consorcio = consorcio
                };

                linhasPendentes.Add(novaLinha);
                linhasPorCodigo[codigoLinha] = novaLinha;

                linhaId = novaLinha.Id;
                linhasCriadas++;
                alteracoesPendentes++;
            }
            else
            {
                // ── EXISTENTE — detecta e aplica mudanças ─────────────────────
                linhaId = linhaExistente.Id;

                var modalIdNovo = ObterOuCriarModal(tipoRota);
                var nomeNovo    = $"{codigoLinha} - {destino}";
                var mudou       = false;

                if (!string.Equals(linhaExistente.TipoRota, tipoRota, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Linha {Codigo}: TipoRota '{Antigo}' → '{Novo}'.",
                        codigoLinha, linhaExistente.TipoRota, tipoRota);
                    linhaExistente.TipoRota = tipoRota;
                    mudou = true;
                }

                if (linhaExistente.ModalId != modalIdNovo)
                {
                    linhaExistente.ModalId = modalIdNovo;
                    mudou = true;
                }

                if (!string.Equals(linhaExistente.Nome, nomeNovo, StringComparison.OrdinalIgnoreCase))
                {
                    linhaExistente.Nome = nomeNovo;
                    mudou = true;
                }

                if (!string.Equals(linhaExistente.Consorcio, consorcio, StringComparison.OrdinalIgnoreCase))
                {
                    linhaExistente.Consorcio = consorcio;
                    mudou = true;
                }

                if (mudou)
                {
                    linhasAtualizadas++;
                    alteracoesPendentes++;
                }
            }

            // ── Sentido ───────────────────────────────────────────────────────
            var nomeSentido  = $"{destino} ({direcao})";
            var chaveSentido = CriarChaveSentido(linhaId, nomeSentido);

            if (!sentidosPorChave.TryGetValue(chaveSentido, out var sentidoId))
            {
                sentidoId = Guid.NewGuid();

                sentidosPendentes.Add(new Sentido
                {
                    Id      = sentidoId,
                    LinhaId = linhaId,
                    Nome    = nomeSentido
                });

                sentidosPorChave[chaveSentido] = sentidoId;
                sentidosCriados++;
                alteracoesPendentes++;
            }

            // ── Itinerário ────────────────────────────────────────────────────
            var chaveItinerario = CriarChaveItinerario(sentidoId, metadado.DistanciaMetros);

            if (chavesItinerariosExistentes.Contains(chaveItinerario))
                continue;

            try
            {
                var geometria = await arcGisClient.BuscarGeometriaAsync(shapeId, cancellationToken);

                if (geometria is null)
                {
                    _logger.LogError("Geometria nula para shape_id: {ShapeId}", shapeId);
                    continue;
                }

                itinerariosPendentes.Add(new Itinerario
                {
                    Id              = Guid.NewGuid(),
                    SentidoId       = sentidoId,
                    DistanciaMetros = metadado.DistanciaMetros,
                    Geometria       = geometria
                });

                chavesItinerariosExistentes.Add(chaveItinerario);
                itinerariosCriados++;
                alteracoesPendentes++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao importar shape_id: {ShapeId}", shapeId);
            }

            if (alteracoesPendentes >= tamanhoLote)
            {
                // Persiste UPDATEs das linhas rastreadas ANTES do ChangeTracker.Clear()
                // que acontece dentro de SalvarLotesPendentesAsync.
                if (linhasAtualizadas > 0)
                    await contexto.SaveChangesAsync(cancellationToken);

                totalRegistrosSalvos += await SalvarLotesPendentesAsync(
                    contexto,
                    modaisPendentes,
                    linhasPendentes,
                    sentidosPendentes,
                    itinerariosPendentes,
                    tamanhoLote,
                    cancellationToken);

                alteracoesPendentes =
                    modaisPendentes.Count      +
                    linhasPendentes.Count      +
                    sentidosPendentes.Count    +
                    itinerariosPendentes.Count;
            }
        }

        // Persiste UPDATEs finais das linhas rastreadas
        if (linhasAtualizadas > 0)
        {
            await contexto.SaveChangesAsync(cancellationToken);
            totalRegistrosSalvos += linhasAtualizadas;
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
        _logger.LogInformation("Linhas atualizadas: {LinhasAtualizadas}", linhasAtualizadas);
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
        var total = 0;
        total += await SalvarEntidadesEmLotesAsync(contexto, modaisPendentes,       tamanhoLote, "Modais",       cancellationToken);
        total += await SalvarEntidadesEmLotesAsync(contexto, linhasPendentes,       tamanhoLote, "Linhas",       cancellationToken);
        total += await SalvarEntidadesEmLotesAsync(contexto, sentidosPendentes,     tamanhoLote, "Sentidos",     cancellationToken);
        total += await SalvarEntidadesEmLotesAsync(contexto, itinerariosPendentes,  tamanhoLote, "Itinerários",  cancellationToken);
        return total;
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
        var totalSalvos = 0;

        for (var indice = 0; indice < entidadesPendentes.Count; indice += tamanhoLote)
        {
            var loteAtual = (indice / tamanhoLote) + 1;
            var lote = entidadesPendentes.Skip(indice).Take(tamanhoLote).ToList();

            _logger.LogInformation(
                "Salvando lote {LoteAtual}/{TotalLotes} ({NomeEntidade})...",
                loteAtual, totalLotes, nomeEntidade);

            contexto.AddRange(lote);
            await contexto.SaveChangesAsync(cancellationToken);
            contexto.ChangeTracker.Clear(); // limpa tracking após cada lote de INSERTs

            totalSalvos += lote.Count;

            _logger.LogInformation(
                "Lote salvo ({NomeEntidade}): {Quantidade} registros.",
                nomeEntidade, lote.Count);
        }

        entidadesPendentes.Clear();
        return totalSalvos;
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
            var pagina = await arcGisClient.BuscarMetadadosAsync(resultOffset, tamanhoPagina, cancellationToken);

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
        var horaStr   = _configuration["IMPORTACAO_HORA"];
        var minutoStr = _configuration["IMPORTACAO_MINUTO"];

        if (int.TryParse(horaStr,   NumberStyles.Integer, CultureInfo.InvariantCulture, out var hora)
         && int.TryParse(minutoStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minuto))
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

        if (TimeSpan.TryParseExact(horarioConfigurado, @"hh\:mm", CultureInfo.InvariantCulture, out var ts))
            return ts;

        if (TimeSpan.TryParse(horarioConfigurado, CultureInfo.InvariantCulture, out ts))
            return new TimeSpan(ts.Hours, ts.Minutes, 0);

        throw new InvalidOperationException(
            "Valor inválido para ARCGIS__HORARIO_IMPORTACAO. Formato esperado: HH:mm.");
    }

    private int LerInteiroConfiguracao(string chave, int valorPadrao)
    {
        var v = _configuration[chave];
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor) && valor > 0
            ? valor
            : valorPadrao;
    }

    private int LerInteiroConfiguracaoComFallback(string chavePrincipal, string chaveSecundaria, int valorPadrao)
    {
        var v = _configuration[chavePrincipal];
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor) && valor > 0
            ? valor
            : LerInteiroConfiguracao(chaveSecundaria, valorPadrao);
    }

    private static string CriarChaveSentido(Guid linhaId, string nomeSentido)
        => $"{linhaId:N}|{nomeSentido.Trim().ToUpperInvariant()}";

    private static string CriarChaveItinerario(Guid sentidoId, double distanciaMetros)
        => $"{sentidoId:N}|{distanciaMetros.ToString("G17", CultureInfo.InvariantCulture)}";
}