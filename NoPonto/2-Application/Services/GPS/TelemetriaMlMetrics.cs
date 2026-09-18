namespace NoPonto.Application.GPS;

using NoPonto.Application.Services.BackgroundServices;

public sealed class TelemetriaMlMetrics
{
    private long _produzidos, _falhasPublicacao, _consumidos, _persistidos, _duplicados;
    private long _invalidos, _retries, _deadLetter, _batches, _itensBatch, _persistenciaTicks;
    private long _publisherRecebidos, _publisherMicrobatches, _publisherItens, _publisherMaiorLote;
    private long _publisherSerializacaoTicks, _publisherRedisTicks, _publisherPublicados, _publisherFalhasRedis;
    private long _publisherFalhasPreparacao, _publisherFalhasInesperadas;
    private long _falhasChannel;
    private long _channelOcupacao, _channelOcupacaoMaxima;
    private long _workerLeituraTicks, _workerDesserializacaoTicks, _workerPostgresTicks;
    private long _workerAckTicks, _workerCleanupTicks;

    public long Produzidos => Interlocked.Read(ref _produzidos);
    public long FalhasPublicacao => Interlocked.Read(ref _falhasPublicacao);
    public long Consumidos => Interlocked.Read(ref _consumidos);
    public long Persistidos => Interlocked.Read(ref _persistidos);
    public long Duplicados => Interlocked.Read(ref _duplicados);
    public long Invalidos => Interlocked.Read(ref _invalidos);
    public long Retries => Interlocked.Read(ref _retries);
    public long DeadLetter => Interlocked.Read(ref _deadLetter);
    public long Batches => Interlocked.Read(ref _batches);
    public long ItensBatch => Interlocked.Read(ref _itensBatch);
    public double PersistenciaTotalMs => TimeSpan.FromTicks(Interlocked.Read(ref _persistenciaTicks)).TotalMilliseconds;
    public double TamanhoMedioBatch => Batches == 0 ? 0 : (double)ItensBatch / Batches;
    public long PublisherRecebidos => Interlocked.Read(ref _publisherRecebidos);
    public long PublisherMicrobatches => Interlocked.Read(ref _publisherMicrobatches);
    public long PublisherItens => Interlocked.Read(ref _publisherItens);
    public long PublisherMaiorLote => Interlocked.Read(ref _publisherMaiorLote);
    public double PublisherTamanhoMedioLote => PublisherMicrobatches == 0 ? 0 : (double)PublisherItens / PublisherMicrobatches;
    public double PublisherSerializacaoMs => TicksEmMs(_publisherSerializacaoTicks);
    public double PublisherRedisMs => TicksEmMs(_publisherRedisTicks);
    public long PublisherPublicados => Interlocked.Read(ref _publisherPublicados);
    public long PublisherFalhasRedis => Interlocked.Read(ref _publisherFalhasRedis);
    public long PublisherFalhasPreparacao => Interlocked.Read(ref _publisherFalhasPreparacao);
    public long PublisherFalhasInesperadas => Interlocked.Read(ref _publisherFalhasInesperadas);
    public long FalhasChannel => Interlocked.Read(ref _falhasChannel);
    public long ChannelOcupacao => Interlocked.Read(ref _channelOcupacao);
    public long ChannelOcupacaoMaxima => Interlocked.Read(ref _channelOcupacaoMaxima);
    public double WorkerLeituraMs => TicksEmMs(_workerLeituraTicks);
    public double WorkerDesserializacaoMs => TicksEmMs(_workerDesserializacaoTicks);
    public double WorkerPostgresMs => TicksEmMs(_workerPostgresTicks);
    public double WorkerAckMs => TicksEmMs(_workerAckTicks);
    public double WorkerCleanupMs => TicksEmMs(_workerCleanupTicks);

    public void RegistrarProduzido() => Interlocked.Increment(ref _produzidos);
    public void RegistrarFalhaPublicacao() => Interlocked.Increment(ref _falhasPublicacao);
    public void RegistrarFalhaChannel()
    {
        Interlocked.Increment(ref _falhasChannel);
        RegistrarFalhaPublicacao();
    }
    public void RegistrarConsumidos(int quantidade) => Interlocked.Add(ref _consumidos, quantidade);
    public void RegistrarPersistencia(int persistidos, int duplicados, int tamanho, TimeSpan duracao)
    {
        Interlocked.Add(ref _persistidos, persistidos);
        Interlocked.Add(ref _duplicados, duplicados);
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _itensBatch, tamanho);
        Interlocked.Add(ref _persistenciaTicks, duracao.Ticks);
    }
    public void RegistrarInvalido() => Interlocked.Increment(ref _invalidos);
    public void RegistrarRetry(int quantidade = 1) => Interlocked.Add(ref _retries, quantidade);
    public void RegistrarDeadLetter() => Interlocked.Increment(ref _deadLetter);
    public void RegistrarFalhaPreparacaoPublisher() => Interlocked.Increment(ref _publisherFalhasPreparacao);
    public void RegistrarFalhaInesperadaPublisher() => Interlocked.Increment(ref _publisherFalhasInesperadas);

    public void RegistrarEntradaChannel()
    {
        var atual = Interlocked.Increment(ref _channelOcupacao);
        long maximo;
        while (atual > (maximo = Interlocked.Read(ref _channelOcupacaoMaxima))
               && Interlocked.CompareExchange(ref _channelOcupacaoMaxima, atual, maximo) != maximo) { }
    }
    public void DesfazerEntradaChannel() => Interlocked.Decrement(ref _channelOcupacao);
    public void RegistrarSaidaChannel(int quantidade)
    {
        Interlocked.Add(ref _channelOcupacao, -quantidade);
        Interlocked.Add(ref _publisherRecebidos, quantidade);
    }
    public void RegistrarMicrobatch(int tamanho, TimeSpan serializacao, TimeSpan redis, int publicados, int falhas)
    {
        Interlocked.Increment(ref _publisherMicrobatches);
        Interlocked.Add(ref _publisherItens, tamanho);
        long maximo;
        while (tamanho > (maximo = Interlocked.Read(ref _publisherMaiorLote))
               && Interlocked.CompareExchange(ref _publisherMaiorLote, tamanho, maximo) != maximo) { }
        Interlocked.Add(ref _publisherSerializacaoTicks, serializacao.Ticks);
        Interlocked.Add(ref _publisherRedisTicks, redis.Ticks);
        Interlocked.Add(ref _publisherPublicados, publicados);
        Interlocked.Add(ref _publisherFalhasRedis, falhas);
    }
    public void RegistrarWorkerLeitura(TimeSpan duracao) => Interlocked.Add(ref _workerLeituraTicks, duracao.Ticks);
    public void RegistrarWorkerDesserializacao(TimeSpan duracao) => Interlocked.Add(ref _workerDesserializacaoTicks, duracao.Ticks);
    public void RegistrarWorkerPostgres(TimeSpan duracao) => Interlocked.Add(ref _workerPostgresTicks, duracao.Ticks);
    public void RegistrarWorkerAck(TimeSpan duracao) => Interlocked.Add(ref _workerAckTicks, duracao.Ticks);
    public void RegistrarWorkerCleanup(TimeSpan duracao) => Interlocked.Add(ref _workerCleanupTicks, duracao.Ticks);
    private static double TicksEmMs(long ticks) => TimeSpan.FromTicks(Interlocked.Read(ref ticks)).TotalMilliseconds;
}

public sealed class TelemetriaMlMetricsReporter(
    TelemetriaMlMetrics metrics,
    ILogger<TelemetriaMlMetricsReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogInformation(
                    "Telemetria ML: produzidos={produzidos}, falhas_publicacao={falhas}, " +
                    "consumidos={consumidos}, persistidos={persistidos}, duplicados={duplicados}, " +
                    "invalidos={invalidos}, retries={retries}, dlq={dlq}, batches={batches}, " +
                    "batch_medio={batchMedio:F1}, persistencia_total_ms={persistenciaMs:F1}, " +
                    "publisher_recebidos={publisherRecebidos}, publisher_microbatches={publisherBatches}, " +
                    "publisher_batch_medio={publisherMedio:F1}, publisher_batch_max={publisherMax}, " +
                    "publisher_serializacao_ms={publisherSerializacao:F1}, publisher_redis_ms={publisherRedis:F1}, " +
                    "publisher_publicados={publisherPublicados}, publisher_falhas_redis={publisherFalhasRedis}, " +
                    "publisher_falhas_preparacao={publisherFalhasPreparacao}, " +
                    "publisher_falhas_inesperadas={publisherFalhasInesperadas}, " +
                    "falhas_channel={falhasChannel}, " +
                    "channel_ocupacao={channelAtual}, channel_ocupacao_max={channelMax}, channel_capacidade={channelCapacidade}, " +
                    "worker_leitura_ms={workerLeitura:F1}, worker_desserializacao_ms={workerDesserializacao:F1}, " +
                    "worker_postgres_ms={workerPostgres:F1}, worker_ack_ms={workerAck:F1}, worker_cleanup_ms={workerCleanup:F1}",
                    metrics.Produzidos, metrics.FalhasPublicacao, metrics.Consumidos,
                    metrics.Persistidos, metrics.Duplicados, metrics.Invalidos,
                    metrics.Retries, metrics.DeadLetter, metrics.Batches,
                    metrics.TamanhoMedioBatch, metrics.PersistenciaTotalMs,
                    metrics.PublisherRecebidos, metrics.PublisherMicrobatches,
                    metrics.PublisherTamanhoMedioLote, metrics.PublisherMaiorLote,
                    metrics.PublisherSerializacaoMs, metrics.PublisherRedisMs,
                    metrics.PublisherPublicados, metrics.PublisherFalhasRedis,
                    metrics.PublisherFalhasPreparacao, metrics.PublisherFalhasInesperadas,
                    metrics.FalhasChannel,
                    metrics.ChannelOcupacao, metrics.ChannelOcupacaoMaxima,
                    TelemetriaMlStreamPublisher.Capacidade,
                    metrics.WorkerLeituraMs, metrics.WorkerDesserializacaoMs,
                    metrics.WorkerPostgresMs, metrics.WorkerAckMs, metrics.WorkerCleanupMs);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            // Métricas são secundárias e nunca podem encerrar o host.
        }
    }
}
