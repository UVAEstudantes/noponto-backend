using System.Diagnostics;
using System.Collections.Concurrent;

namespace NoPonto.Application.GPS;

internal enum ResultadoRequisicaoEta
{
    Sucesso,
    Timeout,
    Falha,
}

/// <summary>
/// Agregador observacional de baixo custo, criado uma vez por ciclo GPS.
/// Tempos acumulados por operação podem superar o tempo de parede quando há concorrência.
/// </summary>
internal sealed class GpsCicloPerformance(DateTimeOffset inicio, long intervaloConfiguradoMs)
{
    private long _matchingGlobalTicks;
    private long _matchingDirecionadoTicks;
    private long _matchingMaxTicks;
    private int _matchingGlobais;
    private int _matchingDirecionados;
    private int _matchingGlobalSemHistorico;
    private int _matchingGlobalMesmoItinerario;
    private int _matchingGlobalItinerarioDiferente;
    private int _matchingDirecionadoTroca;
    private int _matchingDirecionadoContinuidadeFaixa;
    private int _matchingDirecionadoComFaixa;
    private int _matchingDirecionadoSemFaixa;
    private int _matchingDirecionadoEncontrado;
    private int _matchingDirecionadoInelegivel;
    private int _matchingDirecionadoFalha;
    private int _matchingComandosPostgres;
    private int _matchingGlobaisSimples;
    private int _matchingCombinados;
    private int _matchingContinuidadeSemSegundaQuery;
    private int _matchingTrocaQueryAntiga;
    private int _matchingCombinadoGlobalInelegivel;
    private int _matchingCombinadoAnteriorInelegivel;
    private int _matchingCombinadoFalha;
    private int _matchingBatchInputs;
    private int _matchingBatchOperations;
    private int _matchingBatchCommandsPostgres;
    private int _matchingFallbackCommandsPostgres;
    private int _matchingBatchSizeTotal;
    private int _matchingBatchSizeMax;
    private long _matchingBatchTicks;
    private long _matchingBatchMaxTicks;
    private int _matchingGlobalSimpleBatches;
    private int _matchingCombinedBatches;
    private int _matchingDirectedBatches;
    private int _matchingBatchCircuitOpened;
    private int _matchingBatchProbes;
    private int _matchingBatchProbesSucesso;
    private int _matchingBatchProbesFalha;
    private int _matchingBatchEntradasPuladas;
    private int _matchingBatchComandosEvitados;
    private int _matchingBatchOperacoesDegradadas;
    private int _matchingBatchInfrastructureFailures;
    private int _projecaoOperacionalSolicitada;
    private int _projecaoOperacionalEncontrada;
    private int _projecaoOperacionalInelegivel;
    private int _projecaoOperacionalFalha;
    private int _projecaoOperacionalComandos;
    private long _projecaoOperacionalTicks;
    private int _continuidadeComparacoes;
    private int _continuidadeDirecionadoFound;
    private int _continuidadeDirecionadoInelegivel;
    private int _continuidadeDirecionadoFalha;
    private int _continuidadeMesmaProximaParada;
    private int _continuidadeProximaParadaDiferente;
    private int _continuidadeGlobalNullDirecionadoValido;
    private int _continuidadeGlobalValidoDirecionadoNull;
    private int _continuidadeAmbosNull;
    private int _continuidadePosicaoDiffAte0001;
    private int _continuidadePosicaoDiffAte001;
    private int _continuidadePosicaoDiffMaior001;
    private int _continuidadeDistanciaDiffAte5m;
    private int _continuidadeDistanciaDiffAte20m;
    private int _continuidadeDistanciaDiffMaior20m;
    private int _continuidadeBearingComparavel;
    private int _continuidadeBearingDiffAte5;
    private int _continuidadeBearingDiffMaior5;
    private int _continuidadeDistanciaProximaComparavel;
    private int _continuidadeDistanciaProximaDiffAte5m;
    private int _continuidadeDistanciaProximaDiffAte20m;
    private int _continuidadeDistanciaProximaDiffMaior20m;

    private long _etaRequisicoesTicks;
    private long _etaRequisicaoMaxTicks;
    private int _etaRequisicoes;
    private int _etaSucessos;
    private int _etaTimeouts;
    private int _etaFalhas;

    private long _commitTicks;
    private long _commitMaxTicks;
    private int _commitsTentados;
    private int _commitsAceitos;
    private int _commitsRejeitados;
    private int _commitsInfra;

    private long _lockTicks;
    private long _lockMaxTicks;
    private int _lockOperacoes;
    private int _lockTentativas;
    private int _lockPrimeiraTentativa;
    private int _lockComRetry;
    private int _lockRetries;
    private int _lockMaxTentativas;

    private long _serializacaoTicks;
    private long _serializacaoMaxTicks;
    private long _serializacaoCaracteres;
    private int _serializacoes;
    private int _serializacaoMaxCaracteres;

    private long _commitLuaTicks;
    private long _commitLuaMaxTicks;
    private int _commitLuaExecucoes;

    private long _unlockTicks;
    private long _unlockMaxTicks;
    private int _unlocks;
    private int _unlocksNoLua;

    private long _viagemTicks;
    private long _viagemMaxTicks;
    private long _viagemProcessadaTicks;
    private long _viagemProcessadaMaxTicks;
    private int _viagemChamadas;
    private int _viagemProcessadas;
    private int _viagemConcluidas;
    private int _viagemConflitos;
    private int _viagemInfra;
    private int _itineraryChangedOcorrencias;
    private int _viagemPgReads;
    private int _viagemPgFallbackReads;
    private int _viagemRedisContextHits;
    private int _viagemDurableWrites;
    private int _viagemCheckpointWrites;
    private int _viagemSemanticWrites;
    private int _viagemSkippedDurableWrites;
    private int _viagemOutboxEvents;
    private int _viagemTransacoes;
    private int _viagemAdvisoryLocks;
    private int _viagemRedisProjectionPreservedNewer;
    private int _catchupPassagensTotal;
    private int _catchupPassagensMaxPorObservacao;
    private long _catchupGapMsMax;
    private int _catchupGapGt180s;

    public DateTimeOffset Inicio { get; } = inicio;
    public long IntervaloConfiguradoMs { get; } = intervaloConfiguradoMs;

    public long SnapshotSppoMs { get; set; }
    public long FontesBrtMs { get; set; }
    public long NormalizacaoMs { get; set; }
    public long RedisLeituraInicialMs { get; set; }
    public long MatchingEtapaMs { get; set; }
    public long EtaEtapaMs { get; set; }
    public long CommitViagemEtapaMs { get; set; }
    public long IndicesLinhaMs { get; set; }
    public long SignalrEtapaMs { get; set; }
    public long SignalrPreparacaoMs { get; set; }
    public long SignalrBroadcastMs { get; set; }
    public long DelayPlanejadoMs { get; set; }
    public long? StartToStartMs { get; set; }
    public bool CicloConcluidoNormalmente { get; set; }
    public int BrtConsultasHttp { get; set; }
    public int BrtCacheReutilizacoes { get; set; }
    public long? BrtCacheIdadeMs { get; set; }
    public StatusFonteGps? BrtResultadoConsulta { get; set; }
    public bool SnapshotSppoConsumido { get; set; }
    public bool SnapshotSppoPermaneceuPendente { get; set; }

    public int Entrada { get; set; }
    public int Validas { get; set; }
    public int Novas { get; set; }
    public int EnriquecimentoSolicitado { get; set; }
    public int Enriquecidas { get; set; }
    public int RedisLeiturasIniciais { get; set; }
    public int RedisLeiturasIndicesRecente { get; set; }
    public int RedisIndicesCacheHits { get; set; }
    public int LinhasPublicadas { get; set; }
    public int VeiculosEnviados { get; set; }
    public int EtaVeiculosElegiveis { get; set; }
    public int EtaChunksPlanejados { get; set; }
    public int EtaCooldownIgnorado { get; set; }
    public int EtaChunksIgnoradosCooldown { get; set; }

    public int MatchingGlobais => Volatile.Read(ref _matchingGlobais);
    public int MatchingDirecionados => Volatile.Read(ref _matchingDirecionados);
    public int MatchingGlobalSemHistorico => Volatile.Read(ref _matchingGlobalSemHistorico);
    public int MatchingGlobalMesmoItinerario => Volatile.Read(ref _matchingGlobalMesmoItinerario);
    public int MatchingGlobalItinerarioDiferente => Volatile.Read(ref _matchingGlobalItinerarioDiferente);
    public int MatchingDirecionadoTroca => Volatile.Read(ref _matchingDirecionadoTroca);
    public int MatchingDirecionadoContinuidadeFaixa =>
        Volatile.Read(ref _matchingDirecionadoContinuidadeFaixa);
    public int MatchingDirecionadoComFaixa => Volatile.Read(ref _matchingDirecionadoComFaixa);
    public int MatchingDirecionadoSemFaixa => Volatile.Read(ref _matchingDirecionadoSemFaixa);
    public int MatchingDirecionadoEncontrado => Volatile.Read(ref _matchingDirecionadoEncontrado);
    public int MatchingDirecionadoInelegivel => Volatile.Read(ref _matchingDirecionadoInelegivel);
    public int MatchingDirecionadoFalha => Volatile.Read(ref _matchingDirecionadoFalha);
    public int MatchingComandosPostgres => Volatile.Read(ref _matchingComandosPostgres);
    public int MatchingGlobaisSimples => Volatile.Read(ref _matchingGlobaisSimples);
    public int MatchingCombinados => Volatile.Read(ref _matchingCombinados);
    public int MatchingContinuidadeSemSegundaQuery =>
        Volatile.Read(ref _matchingContinuidadeSemSegundaQuery);
    public int MatchingTrocaQueryAntiga => Volatile.Read(ref _matchingTrocaQueryAntiga);
    public int MatchingCombinadoGlobalInelegivel =>
        Volatile.Read(ref _matchingCombinadoGlobalInelegivel);
    public int MatchingCombinadoAnteriorInelegivel =>
        Volatile.Read(ref _matchingCombinadoAnteriorInelegivel);
    public int MatchingCombinadoFalha => Volatile.Read(ref _matchingCombinadoFalha);
    public int MatchingBatchInputs => Volatile.Read(ref _matchingBatchInputs);
    public int MatchingBatchOperations => Volatile.Read(ref _matchingBatchOperations);
    public int MatchingBatchCommandsPostgres => Volatile.Read(ref _matchingBatchCommandsPostgres);
    public int MatchingFallbackCommandsPostgres =>
        Volatile.Read(ref _matchingFallbackCommandsPostgres);
    public double MatchingBatchSize => MatchingBatchCommandsPostgres == 0
        ? 0 : (double)Volatile.Read(ref _matchingBatchSizeTotal) / MatchingBatchCommandsPostgres;
    public int MatchingBatchSizeMax => Volatile.Read(ref _matchingBatchSizeMax);
    public double MatchingBatchDurationMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _matchingBatchTicks)).TotalMilliseconds;
    public double MatchingBatchDurationMediaMs => MatchingBatchCommandsPostgres == 0
        ? 0 : MatchingBatchDurationMs / MatchingBatchCommandsPostgres;
    public double MatchingBatchDurationMaxMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _matchingBatchMaxTicks)).TotalMilliseconds;
    public int MatchingGlobalSimpleBatches => Volatile.Read(ref _matchingGlobalSimpleBatches);
    public int MatchingCombinedBatches => Volatile.Read(ref _matchingCombinedBatches);
    public int MatchingDirectedBatches => Volatile.Read(ref _matchingDirectedBatches);
    public int MatchingBatchCircuitOpened => Volatile.Read(ref _matchingBatchCircuitOpened);
    public int MatchingBatchProbes => Volatile.Read(ref _matchingBatchProbes);
    public int MatchingBatchProbesSucesso => Volatile.Read(ref _matchingBatchProbesSucesso);
    public int MatchingBatchProbesFalha => Volatile.Read(ref _matchingBatchProbesFalha);
    public int MatchingBatchEntradasPuladas => Volatile.Read(ref _matchingBatchEntradasPuladas);
    public int MatchingBatchComandosEvitados => Volatile.Read(ref _matchingBatchComandosEvitados);
    public int MatchingBatchOperacoesDegradadas => Volatile.Read(ref _matchingBatchOperacoesDegradadas);
    public int MatchingBatchInfrastructureFailures => Volatile.Read(ref _matchingBatchInfrastructureFailures);
    public string MatchingBatchCircuitReason { get; private set; } = "none";
    public int ContinuidadeComparacoes => Volatile.Read(ref _continuidadeComparacoes);
    public int ContinuidadeDirecionadoFound => Volatile.Read(ref _continuidadeDirecionadoFound);
    public int ContinuidadeDirecionadoInelegivel => Volatile.Read(ref _continuidadeDirecionadoInelegivel);
    public int ContinuidadeDirecionadoFalha => Volatile.Read(ref _continuidadeDirecionadoFalha);
    public int ContinuidadeMesmaProximaParada => Volatile.Read(ref _continuidadeMesmaProximaParada);
    public int ContinuidadeProximaParadaDiferente => Volatile.Read(ref _continuidadeProximaParadaDiferente);
    public int ContinuidadeGlobalNullDirecionadoValido =>
        Volatile.Read(ref _continuidadeGlobalNullDirecionadoValido);
    public int ContinuidadeGlobalValidoDirecionadoNull =>
        Volatile.Read(ref _continuidadeGlobalValidoDirecionadoNull);
    public int ContinuidadeAmbosNull => Volatile.Read(ref _continuidadeAmbosNull);
    public int ContinuidadePosicaoDiffAte0001 => Volatile.Read(ref _continuidadePosicaoDiffAte0001);
    public int ContinuidadePosicaoDiffAte001 => Volatile.Read(ref _continuidadePosicaoDiffAte001);
    public int ContinuidadePosicaoDiffMaior001 => Volatile.Read(ref _continuidadePosicaoDiffMaior001);
    public int ContinuidadeDistanciaDiffAte5m => Volatile.Read(ref _continuidadeDistanciaDiffAte5m);
    public int ContinuidadeDistanciaDiffAte20m => Volatile.Read(ref _continuidadeDistanciaDiffAte20m);
    public int ContinuidadeDistanciaDiffMaior20m => Volatile.Read(ref _continuidadeDistanciaDiffMaior20m);
    public int ContinuidadeBearingComparavel => Volatile.Read(ref _continuidadeBearingComparavel);
    public int ContinuidadeBearingDiffAte5 => Volatile.Read(ref _continuidadeBearingDiffAte5);
    public int ContinuidadeBearingDiffMaior5 => Volatile.Read(ref _continuidadeBearingDiffMaior5);
    public int ContinuidadeDistanciaProximaComparavel =>
        Volatile.Read(ref _continuidadeDistanciaProximaComparavel);
    public int ContinuidadeDistanciaProximaDiffAte5m =>
        Volatile.Read(ref _continuidadeDistanciaProximaDiffAte5m);
    public int ContinuidadeDistanciaProximaDiffAte20m =>
        Volatile.Read(ref _continuidadeDistanciaProximaDiffAte20m);
    public int ContinuidadeDistanciaProximaDiffMaior20m =>
        Volatile.Read(ref _continuidadeDistanciaProximaDiffMaior20m);
    public double MatchingSomaMs => TimeSpan.FromTicks(
        Volatile.Read(ref _matchingGlobalTicks) + Volatile.Read(ref _matchingDirecionadoTicks)).TotalMilliseconds;
    public double MatchingMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _matchingMaxTicks)).TotalMilliseconds;
    public double MatchingMediaMs => MatchingComandosPostgres == 0
        ? 0 : MatchingSomaMs / MatchingComandosPostgres;
    public int ProjecaoOperacionalSolicitada => Volatile.Read(ref _projecaoOperacionalSolicitada);
    public int ProjecaoOperacionalEncontrada => Volatile.Read(ref _projecaoOperacionalEncontrada);
    public int ProjecaoOperacionalInelegivel => Volatile.Read(ref _projecaoOperacionalInelegivel);
    public int ProjecaoOperacionalFalha => Volatile.Read(ref _projecaoOperacionalFalha);
    public int ProjecaoOperacionalComandos => Volatile.Read(ref _projecaoOperacionalComandos);
    public double ProjecaoOperacionalSomaMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _projecaoOperacionalTicks)).TotalMilliseconds;
    public double ProjecaoOperacionalMediaMs => ProjecaoOperacionalComandos == 0
        ? 0 : ProjecaoOperacionalSomaMs / ProjecaoOperacionalComandos;

    public int EtaRequisicoes => Volatile.Read(ref _etaRequisicoes);
    public int EtaSucessos => Volatile.Read(ref _etaSucessos);
    public int EtaTimeouts => Volatile.Read(ref _etaTimeouts);
    public int EtaFalhas => Volatile.Read(ref _etaFalhas);
    public double EtaRequisicoesSomaMs => TimeSpan.FromTicks(Volatile.Read(ref _etaRequisicoesTicks)).TotalMilliseconds;
    public double EtaRequisicaoMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _etaRequisicaoMaxTicks)).TotalMilliseconds;

    public int CommitsTentados => Volatile.Read(ref _commitsTentados);
    public int CommitsAceitos => Volatile.Read(ref _commitsAceitos);
    public int CommitsRejeitados => Volatile.Read(ref _commitsRejeitados);
    public int CommitsInfra => Volatile.Read(ref _commitsInfra);
    public double CommitSomaMs => TimeSpan.FromTicks(Volatile.Read(ref _commitTicks)).TotalMilliseconds;
    public double CommitMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _commitMaxTicks)).TotalMilliseconds;
    public double CommitMediaMs => CommitsTentados == 0 ? 0 : CommitSomaMs / CommitsTentados;

    public int LockOperacoes => Volatile.Read(ref _lockOperacoes);
    public int LockTentativas => Volatile.Read(ref _lockTentativas);
    public int LockPrimeiraTentativa => Volatile.Read(ref _lockPrimeiraTentativa);
    public int LockComRetry => Volatile.Read(ref _lockComRetry);
    public int LockRetries => Volatile.Read(ref _lockRetries);
    public int LockMaxTentativas => Volatile.Read(ref _lockMaxTentativas);
    public double LockSomaMs => TimeSpan.FromTicks(Volatile.Read(ref _lockTicks)).TotalMilliseconds;
    public double LockMediaMs => LockOperacoes == 0 ? 0 : LockSomaMs / LockOperacoes;
    public double LockMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _lockMaxTicks)).TotalMilliseconds;

    public int Serializacoes => Volatile.Read(ref _serializacoes);
    public long SerializacaoCaracteres => Volatile.Read(ref _serializacaoCaracteres);
    public int SerializacaoMaxCaracteres => Volatile.Read(ref _serializacaoMaxCaracteres);
    public double SerializacaoMediaCaracteres => Serializacoes == 0
        ? 0 : (double)SerializacaoCaracteres / Serializacoes;
    public double SerializacaoSomaMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _serializacaoTicks)).TotalMilliseconds;
    public double SerializacaoMediaMs => Serializacoes == 0 ? 0 : SerializacaoSomaMs / Serializacoes;
    public double SerializacaoMaxMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _serializacaoMaxTicks)).TotalMilliseconds;

    public int CommitLuaExecucoes => Volatile.Read(ref _commitLuaExecucoes);
    public double CommitLuaSomaMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _commitLuaTicks)).TotalMilliseconds;
    public double CommitLuaMediaMs => CommitLuaExecucoes == 0 ? 0 : CommitLuaSomaMs / CommitLuaExecucoes;
    public double CommitLuaMaxMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _commitLuaMaxTicks)).TotalMilliseconds;

    public int Unlocks => Volatile.Read(ref _unlocks);
    public int UnlocksNoLua => Volatile.Read(ref _unlocksNoLua);
    public double UnlockSomaMs => TimeSpan.FromTicks(Volatile.Read(ref _unlockTicks)).TotalMilliseconds;
    public double UnlockMediaMs => Unlocks == 0 ? 0 : UnlockSomaMs / Unlocks;
    public double UnlockMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _unlockMaxTicks)).TotalMilliseconds;

    // Soma de tempos individuais, nÃ£o wall-clock. Inclui apenas o trabalho nÃ£o coberto
    // pelos quatro cronÃ´metros internos e Ã© limitado a zero contra ruÃ­do de mediÃ§Ã£o.
    public double CommitResidualSomaMs => Math.Max(0,
        CommitSomaMs - LockSomaMs - SerializacaoSomaMs - CommitLuaSomaMs - UnlockSomaMs);

    public int ViagemChamadas => Volatile.Read(ref _viagemChamadas);
    public int ViagemProcessadas => Volatile.Read(ref _viagemProcessadas);
    public int ViagemConcluidas => Volatile.Read(ref _viagemConcluidas);
    public int ViagemConflitos => Volatile.Read(ref _viagemConflitos);
    public int ViagemInfra => Volatile.Read(ref _viagemInfra);
    public double ViagemSomaMs => TimeSpan.FromTicks(Volatile.Read(ref _viagemTicks)).TotalMilliseconds;
    public double ViagemMaxMs => TimeSpan.FromTicks(Volatile.Read(ref _viagemMaxTicks)).TotalMilliseconds;
    public double ViagemMediaMs => ViagemChamadas == 0 ? 0 : ViagemSomaMs / ViagemChamadas;
    public double ViagemProcessadaSomaMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _viagemProcessadaTicks)).TotalMilliseconds;
    public double ViagemProcessadaMaxMs =>
        TimeSpan.FromTicks(Volatile.Read(ref _viagemProcessadaMaxTicks)).TotalMilliseconds;
    public double ViagemProcessadaMediaMs => ViagemProcessadas == 0
        ? 0 : ViagemProcessadaSomaMs / ViagemProcessadas;
    public int ItineraryChangedOcorrencias => Volatile.Read(ref _itineraryChangedOcorrencias);
    public int ViagemPgReads => Volatile.Read(ref _viagemPgReads);
    public int ViagemPgFallbackReads => Volatile.Read(ref _viagemPgFallbackReads);
    public int ViagemRedisContextHits => Volatile.Read(ref _viagemRedisContextHits);
    public int ViagemDurableWrites => Volatile.Read(ref _viagemDurableWrites);
    public int ViagemCheckpointWrites => Volatile.Read(ref _viagemCheckpointWrites);
    public int ViagemSemanticWrites => Volatile.Read(ref _viagemSemanticWrites);
    public int ViagemSkippedDurableWrites => Volatile.Read(ref _viagemSkippedDurableWrites);
    public int ViagemOutboxEvents => Volatile.Read(ref _viagemOutboxEvents);
    public int ViagemTransacoes => Volatile.Read(ref _viagemTransacoes);
    public int ViagemAdvisoryLocks => Volatile.Read(ref _viagemAdvisoryLocks);
    public int ViagemRedisProjectionPreservedNewer =>
        Volatile.Read(ref _viagemRedisProjectionPreservedNewer);
    public int CatchupPassagensTotal => Volatile.Read(ref _catchupPassagensTotal);
    public int CatchupPassagensMaxPorObservacao => Volatile.Read(ref _catchupPassagensMaxPorObservacao);
    public long CatchupGapMsMax => Volatile.Read(ref _catchupGapMsMax);
    public int CatchupGapGt180s => Volatile.Read(ref _catchupGapGt180s);

    public void RegistrarMatchingGlobal(TimeSpan duracao)
    {
        Interlocked.Increment(ref _matchingGlobais);
        Interlocked.Increment(ref _matchingGlobaisSimples);
        Interlocked.Increment(ref _matchingComandosPostgres);
        RegistrarDuracao(ref _matchingGlobalTicks, ref _matchingMaxTicks, duracao);
    }

    public void RegistrarMatchingGlobalLogico()
    {
        Interlocked.Increment(ref _matchingGlobais);
        Interlocked.Increment(ref _matchingGlobaisSimples);
    }

    public void RegistrarMatchingCombinado(
        TimeSpan duracao, ResultadoMatchingCombinado? resultado)
    {
        Interlocked.Increment(ref _matchingGlobais);
        Interlocked.Increment(ref _matchingCombinados);
        Interlocked.Increment(ref _matchingComandosPostgres);
        RegistrarDuracao(ref _matchingGlobalTicks, ref _matchingMaxTicks, duracao);
        RegistrarResultadoMatchingCombinado(resultado);
    }

    public void RegistrarMatchingCombinadoLogico(ResultadoMatchingCombinado resultado)
    {
        Interlocked.Increment(ref _matchingGlobais);
        Interlocked.Increment(ref _matchingCombinados);
        RegistrarResultadoMatchingCombinado(resultado);
    }

    private void RegistrarResultadoMatchingCombinado(ResultadoMatchingCombinado? resultado)
    {
        if (resultado is null
            || resultado.Global.Status == StatusBuscaItinerario.InfrastructureFailure
            || resultado.Anterior.Status == StatusBuscaItinerario.InfrastructureFailure)
            Interlocked.Increment(ref _matchingCombinadoFalha);
        if (resultado?.Global.Status == StatusBuscaItinerario.NotEligible)
            Interlocked.Increment(ref _matchingCombinadoGlobalInelegivel);
        if (resultado?.Anterior.Status == StatusBuscaItinerario.NotEligible)
            Interlocked.Increment(ref _matchingCombinadoAnteriorInelegivel);
    }

    public void RegistrarMatchingDirecionado(TimeSpan duracao)
    {
        Interlocked.Increment(ref _matchingDirecionados);
        Interlocked.Increment(ref _matchingComandosPostgres);
        RegistrarDuracao(ref _matchingDirecionadoTicks, ref _matchingMaxTicks, duracao);
    }

    public void RegistrarMatchingDirecionadoLogico() =>
        Interlocked.Increment(ref _matchingDirecionados);

    // Deve ser chamado uma unica vez pelo futuro estagio batch, antes de separar
    // as posicoes em operacoes simples/combinadas/dirigidas.
    public void RegistrarMatchingBatchInputs(int quantidade) =>
        Interlocked.Add(ref _matchingBatchInputs, quantidade);

    public void RegistrarMatchingLote(MetricasMatchingLote metricas)
    {
        Interlocked.Add(ref _matchingBatchOperations, metricas.MatchingBatchOperations);
        Interlocked.Add(ref _matchingComandosPostgres, metricas.MatchingCommandsPostgres);
        foreach (var comando in metricas.Comandos)
        {
            if (comando.Tipo == TipoBatchMatching.Direcionado)
                RegistrarDuracao(ref _matchingDirecionadoTicks, ref _matchingMaxTicks, comando.Duracao);
            else
                RegistrarDuracao(ref _matchingGlobalTicks, ref _matchingMaxTicks, comando.Duracao);

            if (comando.Origem == OrigemComandoMatchingLote.FallbackIndividual)
            {
                Interlocked.Increment(ref _matchingFallbackCommandsPostgres);
                continue;
            }

            Interlocked.Increment(ref _matchingBatchCommandsPostgres);
            Interlocked.Add(ref _matchingBatchSizeTotal, comando.TamanhoBatch);
            RegistrarMaximo(ref _matchingBatchSizeMax, comando.TamanhoBatch);
            RegistrarDuracao(ref _matchingBatchTicks, ref _matchingBatchMaxTicks, comando.Duracao);
            switch (comando.Tipo)
            {
                case TipoBatchMatching.GlobalSimples:
                    Interlocked.Increment(ref _matchingGlobalSimpleBatches);
                    break;
                case TipoBatchMatching.Combinado:
                    Interlocked.Increment(ref _matchingCombinedBatches);
                    break;
                case TipoBatchMatching.Direcionado:
                    Interlocked.Increment(ref _matchingDirectedBatches);
                    break;
            }
        }
    }

    public void RegistrarProtecaoBatch(MatchingBatchStageProtection protecao)
    {
        if (protecao.CircuitoAberto)
        {
            Interlocked.Increment(ref _matchingBatchCircuitOpened);
            Interlocked.Increment(ref _matchingBatchInfrastructureFailures);
            MatchingBatchCircuitReason = protecao.MotivoCircuito?.ToString() ?? "unknown";
        }
        Interlocked.Add(ref _matchingBatchProbes, protecao.ProbesExecutadas);
        Interlocked.Add(ref _matchingBatchProbesSucesso, protecao.ProbesSucesso);
        Interlocked.Add(ref _matchingBatchProbesFalha, protecao.ProbesFalha);
        Interlocked.Add(ref _matchingBatchEntradasPuladas, protecao.EntradasPuladas);
        Interlocked.Add(ref _matchingBatchComandosEvitados, protecao.ComandosEvitados);
        Interlocked.Add(ref _matchingBatchOperacoesDegradadas, protecao.OperacoesDegradadas);
    }

    public void RegistrarProjecaoOperacional(StatusProjecaoOperacional status, TimeSpan duracaoComando)
    {
        if (status == StatusProjecaoOperacional.NaoSolicitada) return;
        Interlocked.Increment(ref _projecaoOperacionalSolicitada);
        switch (status)
        {
            case StatusProjecaoOperacional.Encontrada:
                Interlocked.Increment(ref _projecaoOperacionalEncontrada);
                break;
            case StatusProjecaoOperacional.Inelegivel:
                Interlocked.Increment(ref _projecaoOperacionalInelegivel);
                break;
            case StatusProjecaoOperacional.FalhaInfraestrutura:
                Interlocked.Increment(ref _projecaoOperacionalFalha);
                break;
        }
        if (duracaoComando > TimeSpan.Zero)
        {
            Interlocked.Increment(ref _projecaoOperacionalComandos);
            Interlocked.Add(ref _projecaoOperacionalTicks, duracaoComando.Ticks);
        }
    }

    public void RegistrarContinuidadeSemSegundaQuery() =>
        Interlocked.Increment(ref _matchingContinuidadeSemSegundaQuery);

    public void RegistrarTrocaQueryAntiga() =>
        Interlocked.Increment(ref _matchingTrocaQueryAntiga);

    public void RegistrarMatchingGlobalSemHistorico() =>
        Interlocked.Increment(ref _matchingGlobalSemHistorico);

    public void RegistrarMatchingGlobalComHistorico(bool mesmoItinerario)
    {
        if (mesmoItinerario)
            Interlocked.Increment(ref _matchingGlobalMesmoItinerario);
        else
            Interlocked.Increment(ref _matchingGlobalItinerarioDiferente);
    }

    public void RegistrarMotivoMatchingDirecionado(bool mesmoItinerario, bool temFaixa)
    {
        if (!mesmoItinerario)
            Interlocked.Increment(ref _matchingDirecionadoTroca);
        else
            Interlocked.Increment(ref _matchingDirecionadoContinuidadeFaixa);

        if (temFaixa)
            Interlocked.Increment(ref _matchingDirecionadoComFaixa);
        else
            Interlocked.Increment(ref _matchingDirecionadoSemFaixa);
    }

    public void RegistrarResultadoMatchingDirecionado(StatusBuscaItinerario status)
    {
        switch (status)
        {
            case StatusBuscaItinerario.Found:
                Interlocked.Increment(ref _matchingDirecionadoEncontrado);
                break;
            case StatusBuscaItinerario.NotEligible:
                Interlocked.Increment(ref _matchingDirecionadoInelegivel);
                break;
            case StatusBuscaItinerario.InfrastructureFailure:
                Interlocked.Increment(ref _matchingDirecionadoFalha);
                break;
        }
    }

    public void RegistrarComparacaoContinuidade(
        EnriquecimentoRotaDto global, ResultadoBuscaItinerario direcionado)
        {
            Interlocked.Increment(ref _continuidadeComparacoes);

            switch (direcionado.Status)
            {
                case StatusBuscaItinerario.Found:
                    Interlocked.Increment(ref _continuidadeDirecionadoFound);
                    break;
                case StatusBuscaItinerario.NotEligible:
                    Interlocked.Increment(ref _continuidadeDirecionadoInelegivel);
                    return;
                case StatusBuscaItinerario.InfrastructureFailure:
                    Interlocked.Increment(ref _continuidadeDirecionadoFalha);
                    return;
            }

            var rota = direcionado.Rota;
            if (rota is null) return;

            var diferencaPosicao = Math.Abs(global.PosicaoNaRota - rota.PosicaoNaRota);
            if (diferencaPosicao <= 0.0001)
                Interlocked.Increment(ref _continuidadePosicaoDiffAte0001);
            else if (diferencaPosicao <= 0.001)
                Interlocked.Increment(ref _continuidadePosicaoDiffAte001);
            else
                Interlocked.Increment(ref _continuidadePosicaoDiffMaior001);

            var diferencaDistancia = Math.Abs(global.DistanciaARotaMetros - rota.DistanciaARotaMetros);
            if (diferencaDistancia <= 5)
                Interlocked.Increment(ref _continuidadeDistanciaDiffAte5m);
            else if (diferencaDistancia <= 20)
                Interlocked.Increment(ref _continuidadeDistanciaDiffAte20m);
            else
                Interlocked.Increment(ref _continuidadeDistanciaDiffMaior20m);

            if (global.BearingLocal is { } bearingGlobal && rota.BearingLocal is { } bearingDirecionado)
            {
                Interlocked.Increment(ref _continuidadeBearingComparavel);
                var diferencaBearing = Math.Abs(GpsEnriquecimentoService.DiferencaAngular(
                    bearingGlobal, bearingDirecionado));
                if (diferencaBearing <= 5)
                    Interlocked.Increment(ref _continuidadeBearingDiffAte5);
                else
                    Interlocked.Increment(ref _continuidadeBearingDiffMaior5);
            }

            if (global.ProximaParadaNome is null && rota.ProximaParadaNome is null)
                Interlocked.Increment(ref _continuidadeAmbosNull);
            else if (global.ProximaParadaNome is null)
                Interlocked.Increment(ref _continuidadeGlobalNullDirecionadoValido);
            else if (rota.ProximaParadaNome is null)
                Interlocked.Increment(ref _continuidadeGlobalValidoDirecionadoNull);
            else if (string.Equals(global.ProximaParadaNome, rota.ProximaParadaNome,
                         StringComparison.Ordinal))
                Interlocked.Increment(ref _continuidadeMesmaProximaParada);
            else
                Interlocked.Increment(ref _continuidadeProximaParadaDiferente);

            if (global.DistanciaProximaParadaMetros is { } distanciaGlobal
                && rota.DistanciaProximaParadaMetros is { } distanciaDirecionada)
            {
                Interlocked.Increment(ref _continuidadeDistanciaProximaComparavel);
                var diferenca = Math.Abs(distanciaGlobal - distanciaDirecionada);
                if (diferenca <= 5)
                    Interlocked.Increment(ref _continuidadeDistanciaProximaDiffAte5m);
                else if (diferenca <= 20)
                    Interlocked.Increment(ref _continuidadeDistanciaProximaDiffAte20m);
                else
                    Interlocked.Increment(ref _continuidadeDistanciaProximaDiffMaior20m);
            }
        }

    public void RegistrarEtaRequisicao(ResultadoRequisicaoEta resultado, TimeSpan duracao)
    {
        Interlocked.Increment(ref _etaRequisicoes);
        RegistrarDuracao(ref _etaRequisicoesTicks, ref _etaRequisicaoMaxTicks, duracao);
        switch (resultado)
        {
            case ResultadoRequisicaoEta.Sucesso: Interlocked.Increment(ref _etaSucessos); break;
            case ResultadoRequisicaoEta.Timeout: Interlocked.Increment(ref _etaTimeouts); break;
            case ResultadoRequisicaoEta.Falha: Interlocked.Increment(ref _etaFalhas); break;
        }
    }

    public void RegistrarCommit(PosicaoVeiculoCacheStatus status, TimeSpan duracao)
    {
        Interlocked.Increment(ref _commitsTentados);
        RegistrarDuracao(ref _commitTicks, ref _commitMaxTicks, duracao);
        switch (status)
        {
            case PosicaoVeiculoCacheStatus.Accepted: Interlocked.Increment(ref _commitsAceitos); break;
            case PosicaoVeiculoCacheStatus.RejectedOlderOrEqual: Interlocked.Increment(ref _commitsRejeitados); break;
            case PosicaoVeiculoCacheStatus.InfrastructureFailure: Interlocked.Increment(ref _commitsInfra); break;
        }
    }

    public void RegistrarLock(int tentativas, bool adquirido, TimeSpan duracao)
    {
        Interlocked.Increment(ref _lockOperacoes);
        Interlocked.Add(ref _lockTentativas, tentativas);
        Interlocked.Add(ref _lockRetries, Math.Max(0, tentativas - 1));
        RegistrarMaximo(ref _lockMaxTentativas, tentativas);
        RegistrarDuracao(ref _lockTicks, ref _lockMaxTicks, duracao);
        if (adquirido && tentativas == 1)
            Interlocked.Increment(ref _lockPrimeiraTentativa);
        else if (adquirido && tentativas > 1)
            Interlocked.Increment(ref _lockComRetry);
    }

    public void RegistrarSerializacao(TimeSpan duracao, int caracteres)
    {
        Interlocked.Increment(ref _serializacoes);
        Interlocked.Add(ref _serializacaoCaracteres, caracteres);
        RegistrarMaximo(ref _serializacaoMaxCaracteres, caracteres);
        RegistrarDuracao(ref _serializacaoTicks, ref _serializacaoMaxTicks, duracao);
    }

    public void RegistrarCommitLua(TimeSpan duracao)
    {
        Interlocked.Increment(ref _commitLuaExecucoes);
        RegistrarDuracao(ref _commitLuaTicks, ref _commitLuaMaxTicks, duracao);
    }

    public void RegistrarUnlock(TimeSpan duracao)
    {
        Interlocked.Increment(ref _unlocks);
        RegistrarDuracao(ref _unlockTicks, ref _unlockMaxTicks, duracao);
    }

    public void RegistrarUnlockNoLua() => Interlocked.Increment(ref _unlocksNoLua);

    public void RegistrarViagemChamada() => Interlocked.Increment(ref _viagemChamadas);

    public void RegistrarViagem(ViagemObservadaResultado? resultado, TimeSpan duracao)
    {
        RegistrarDuracao(ref _viagemTicks, ref _viagemMaxTicks, duracao);
        if (resultado is null) return;
        Interlocked.Increment(ref _viagemProcessadas);
        RegistrarDuracao(ref _viagemProcessadaTicks, ref _viagemProcessadaMaxTicks, duracao);
        Interlocked.Increment(ref _viagemConcluidas);
        if (resultado.Value.Status == ViagemObservadaStatus.Conflict)
            Interlocked.Increment(ref _viagemConflitos);
        if (resultado.Value.Status == ViagemObservadaStatus.InfrastructureFailure)
            Interlocked.Increment(ref _viagemInfra);
    }

    public void RegistrarDivergenciaItinerario() =>
        Interlocked.Increment(ref _itineraryChangedOcorrencias);

    public void RegistrarViagemPgRead() => Interlocked.Increment(ref _viagemPgReads);
    public void RegistrarViagemPgFallbackRead() => Interlocked.Increment(ref _viagemPgFallbackReads);
    public void RegistrarViagemRedisContextHit() => Interlocked.Increment(ref _viagemRedisContextHits);
    public void RegistrarViagemDurableWriteSkipped() => Interlocked.Increment(ref _viagemSkippedDurableWrites);
    public void RegistrarViagemTransacao() => Interlocked.Increment(ref _viagemTransacoes);
    public void RegistrarViagemAdvisoryLock() => Interlocked.Increment(ref _viagemAdvisoryLocks);
    public void RegistrarViagemRedisProjectionPreservedNewer() =>
        Interlocked.Increment(ref _viagemRedisProjectionPreservedNewer);
    public void RegistrarCatchupPassagens(int quantidade, TimeSpan gap)
    {
        if (quantidade <= 0) return;
        Interlocked.Add(ref _catchupPassagensTotal, quantidade);
        RegistrarMaximo(ref _catchupPassagensMaxPorObservacao, quantidade);
        RegistrarMaximo(ref _catchupGapMsMax, Math.Max(0, (long)gap.TotalMilliseconds));
        if (gap > TimeSpan.FromSeconds(180)) Interlocked.Increment(ref _catchupGapGt180s);
    }
    public void RegistrarViagemDurableWrite(bool checkpoint, int eventos)
    {
        Interlocked.Increment(ref _viagemDurableWrites);
        Interlocked.Increment(ref checkpoint ? ref _viagemCheckpointWrites : ref _viagemSemanticWrites);
        Interlocked.Add(ref _viagemOutboxEvents, eventos);
    }

    private static void RegistrarDuracao(ref long acumulado, ref long maximo, TimeSpan duracao)
    {
        var ticks = duracao.Ticks;
        Interlocked.Add(ref acumulado, ticks);
        var atual = Volatile.Read(ref maximo);
        while (ticks > atual)
        {
            var observado = Interlocked.CompareExchange(ref maximo, ticks, atual);
            if (observado == atual) break;
            atual = observado;
        }
    }

    private static void RegistrarMaximo(ref int maximo, int valor)
    {
        var atual = Volatile.Read(ref maximo);
        while (valor > atual)
        {
            var observado = Interlocked.CompareExchange(ref maximo, valor, atual);
            if (observado == atual) break;
            atual = observado;
        }
    }

    private static void RegistrarMaximo(ref long maximo, long valor)
    {
        var atual = Volatile.Read(ref maximo);
        while (valor > atual)
        {
            var observado = Interlocked.CompareExchange(ref maximo, valor, atual);
            if (observado == atual) break;
            atual = observado;
        }
    }
}

/// <summary>
/// Propaga somente o agregador observacional do ciclo pela cadeia assÃ­ncrona do commit,
/// sem alterar o contrato pÃºblico do repository.
/// </summary>
internal static class GpsCommitPerformanceContext
{
    private static readonly AsyncLocal<GpsCicloPerformance?> Atual = new();

    public static GpsCicloPerformance? Current => Atual.Value;

    public static IDisposable Push(GpsCicloPerformance? performance)
    {
        var anterior = Atual.Value;
        Atual.Value = performance;
        return new Scope(anterior);
    }

    private sealed class Scope(GpsCicloPerformance? anterior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            Atual.Value = anterior;
            _disposed = true;
        }
    }
}
