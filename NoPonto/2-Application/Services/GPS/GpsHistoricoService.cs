using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GPS;

/// <summary>
/// Serviço responsável por detectar passagens de veículos por paradas
/// e persistir o histórico para uso em modelos de ML.
///
/// Registrado como SINGLETON — mantém estado em memória entre ciclos
/// para calcular TempoDesdeParadaAnterior e detectar duplicatas.
///
/// Lógica de detecção:
///   Uma passagem é registrada quando DistanciaProximaParadaMetros cai
///   abaixo de DistanciaRegistroMetros. Para evitar duplicatas, mantemos
///   a última parada registrada por veículo+itinerário e só registramos
///   quando a parada muda.
/// </summary>
public sealed class GpsHistoricoService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GpsHistoricoService> _logger;
    private readonly GpsHistoricoOptions _opcoes;

    // Estado em memória: última passagem registrada por veículo
    // Chave: "{Ordem}|{ItinerarioId}"
    private readonly System.Collections.Concurrent.ConcurrentDictionary
        <string, UltimaPassagem> _ultimaPassagem = new();

    public GpsHistoricoService(
        IServiceScopeFactory scopeFactory,
        ILogger<GpsHistoricoService> logger,
        GpsHistoricoOptions opcoes)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _opcoes       = opcoes;
    }

    /// <summary>
    /// Processa um lote de posições enriquecidas e registra passagens detectadas.
    /// Chamado pelo GpsPollingService após cada ciclo de enriquecimento.
    /// </summary>
    public async Task ProcessarLoteAsync(
        IReadOnlyList<PosicaoVeiculoDto> posicoes,
        CancellationToken ct)
    {
        var passagensParaInserir = new List<HistoricoPassagem>();
        var agora = DateTimeOffset.UtcNow;

        foreach (var pos in posicoes)
        {
            // Só processa veículos com dados de rota completos
            if (pos.ItinerarioId is null
                || pos.PosicaoNaRota is null
                || pos.DistanciaProximaParadaMetros is null
                || string.IsNullOrWhiteSpace(pos.ProximaParadaNome))
                continue;

            // Só registra quando está suficientemente perto da parada
            if (pos.DistanciaProximaParadaMetros > _opcoes.DistanciaRegistroMetros)
                continue;

            var chave = $"{pos.Ordem}|{pos.ItinerarioId}";

            // Verifica se já registramos essa parada para esse veículo
            if (_ultimaPassagem.TryGetValue(chave, out var ultima)
                && ultima.NomeParada == pos.ProximaParadaNome)
                continue;

            // Calcula tempo e distância desde a parada anterior
            double? tempoAnterior   = null;
            double? distanciaTracho = null;

            if (ultima is not null)
            {
                tempoAnterior = (pos.TimestampGps - ultima.TimestampGps).TotalSeconds;

                // Descarta intervalos implausíveis (viagem nova ou dado ruim)
                if (tempoAnterior < 0 || tempoAnterior > _opcoes.MaxTempoEntreParadasSegundos)
                    tempoAnterior = null;

                if (pos.PosicaoNaRota > ultima.PosicaoNaRota && pos.ComprimentoRotaMetros.HasValue)
                {
                    distanciaTracho = (pos.PosicaoNaRota.Value - ultima.PosicaoNaRota)
                                    * pos.ComprimentoRotaMetros.Value;
                }
            }

            // Precisamos do ParadaId — buscamos da tabela ParadasItinerario
            // em background sem bloquear o ciclo GPS
            var paradaIdTask = BuscarParadaIdAsync(
                pos.ItinerarioId.Value,
                pos.PosicaoNaRota.Value,
                pos.ProximaParadaNome,
                ct);

            var paradaId = await paradaIdTask;
            if (paradaId == Guid.Empty)
            {
                _logger.LogDebug(
                    "Parada '{nome}' não encontrada no itinerário {itin} — passagem não registrada",
                    pos.ProximaParadaNome, pos.ItinerarioId);
                continue;
            }

            var passagem = new HistoricoPassagem
            {
                Id                               = Guid.NewGuid(),
                Ordem                            = pos.Ordem,
                CodigoLinha                      = pos.CodigoLinha,
                ItinerarioId                     = pos.ItinerarioId.Value,
                ParadaId                         = paradaId,
                PosicaoNaRota                    = pos.PosicaoNaRota.Value,
                DistanciaParadaMetros            = pos.DistanciaProximaParadaMetros.Value,
                TimestampGps                     = pos.TimestampGps,
                TimestampRegistro                = agora,
                VelocidadeInstantanea            = pos.Velocidade,
                VelocidadeMedia                  = pos.VelocidadeMedia,
                HoraDia                          = pos.TimestampGps.ToLocalTime().Hour,
                DiaSemana                        = (int)pos.TimestampGps.ToLocalTime().DayOfWeek,
                TempoDesdeParadaAnteriorSegundos = tempoAnterior,
                DistanciaTrechoMetros            = distanciaTracho,
            };

            passagensParaInserir.Add(passagem);

            // Atualiza estado em memória
            _ultimaPassagem[chave] = new UltimaPassagem(
                pos.ProximaParadaNome,
                pos.PosicaoNaRota.Value,
                pos.TimestampGps);
        }

        if (passagensParaInserir.Count == 0) return;

        // Persiste em lote via scope separado (não bloqueia o ciclo GPS)
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();

                db.HistoricoPassagens.AddRange(passagensParaInserir);
                await db.SaveChangesAsync(ct);

                _logger.LogDebug(
                    "Histórico: {qtd} passagens registradas", passagensParaInserir.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao persistir histórico de passagens");
            }
        }, ct);
    }

    /// <summary>
    /// Busca o ParadaId pela posição na rota e nome da próxima parada.
    /// Usa a parada mais próxima à posição atual no itinerário.
    /// </summary>
    private async Task<Guid> BuscarParadaIdAsync(
        Guid itinerarioId,
        double posicaoAtual,
        string nomeParada,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();

            // Busca a próxima parada à frente do veículo com aquele nome
            var parada = await db.ParadasItinerario
                .AsNoTracking()
                .Where(pi =>
                    pi.ItinerarioId == itinerarioId
                    && pi.PosicaoLinha >= posicaoAtual
                    && pi.Parada.Nome == nomeParada)
                .OrderBy(pi => pi.PosicaoLinha)
                .Select(pi => pi.ParadaId)
                .FirstOrDefaultAsync(ct);

            return parada;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao buscar ParadaId para '{nome}'", nomeParada);
            return Guid.Empty;
        }
    }

    // ── Tipos internos ────────────────────────────────────────────────────────

    private sealed record UltimaPassagem(
        string NomeParada,
        double PosicaoNaRota,
        DateTimeOffset TimestampGps);
}