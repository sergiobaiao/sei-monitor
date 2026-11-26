using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suíte de testes empíricos de concorrência e resiliência conduzida pelo agente m3_challenger_2.
/// Foco exclusivo em:
/// - Exclusão mútua do SemaphoreSlim(1,1) entre polling de background e sincronização manual;
/// - Cancelamento gracioso sem vazamento de travas ou recursos;
/// - Sobrevivência do timer periódico a falhas repetidas (HTTP 5xx, timeouts, exceções de rede);
/// - Ciclo de vida Start/Stop/Restart e reentrância de inicialização;
/// - Comprovação de vulnerabilidades de resiliência e falsos positivos de sucesso.
/// </summary>
public class M3ConcurrencyResilienceTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly TestSeiClient _client;
    private readonly SeiPoller _poller;

    public M3ConcurrencyResilienceTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db)
        {
            SeiUnidade = "110000000",
            SeiUnidadeSigla = "GINFO",
            SeiUsuarioNome = "victor.lima",
            UserKeywordsRaw = "Victor; GINFO/Sistemas",
            CheckIntervalMinutes = 1
        };
        _client = new TestSeiClient();
        _poller = new SeiPoller(_config, _db, _client);
    }

    public void Dispose()
    {
        _poller.Dispose();
        _client.Dispose();
        _db.Dispose();
    }

    // =========================================================================
    // 1. CONCORRÊNCIA: EXCLUSÃO MÚTUA E SERIALIZAÇÃO DE 40 INVOCAÇÕES SIMULTÂNEAS
    // =========================================================================

    [Fact]
    public async Task Challenger2_Concurrency_MixedSyncNowAndPollOnce_NeverOverlapsCriticalSection()
    {
        // Arrange
        const int totalCallers = 40;
        int activeInCriticalSection = 0;
        int maxSimultaneousObserved = 0;
        var exceptions = new ConcurrentBag<Exception>();

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new("P_CONC", "00001.000000/2026-00", "Desc", "Tipo", "GINFO", null, null)
        });

        _client.OnSearchAsync = async _ =>
        {
            int current = Interlocked.Increment(ref activeInCriticalSection);
            int prevMax;
            do
            {
                prevMax = maxSimultaneousObserved;
                if (current <= prevMax) break;
            } while (Interlocked.CompareExchange(ref maxSimultaneousObserved, current, prevMax) != prevMax);

            await Task.Delay(15); // Simula latência de rede
            Interlocked.Decrement(ref activeInCriticalSection);
        };

        // Act: mistura de chamadas a PollOnceAsync e SyncNowAsync
        var tasks = Enumerable.Range(0, totalCallers).Select(async i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    return await _poller.PollOnceAsync();
                }
                else
                {
                    return await _poller.SyncNowAsync();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                throw;
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Empty(exceptions);
        Assert.Equal(totalCallers, results.Length);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(1, maxSimultaneousObserved); // Comprovado: SemaphoreSlim(1,1) impediu sobreposição em 100% dos casos
    }

    // =========================================================================
    // 2. CONCORRÊNCIA: CANCELAMENTO NA FILA DO SEMÁFORO NÃO CORROMPE TRAVA
    // =========================================================================

    [Fact]
    public async Task Challenger2_Concurrency_CancellationInSemaphoreQueue_DoesNotCorruptLockOrLeakState()
    {
        // Arrange: Tarefa A entra no semáforo e segura por 200ms
        var taskAEntered = new TaskCompletionSource<bool>();
        var taskACanFinish = new TaskCompletionSource<bool>();

        _client.OnSearchAsync = async _ =>
        {
            taskAEntered.TrySetResult(true);
            await taskACanFinish.Task;
        };

        var taskA = Task.Run(() => _poller.PollOnceAsync());
        await taskAEntered.Task; // Garante que Tarefa A está dentro do semáforo

        // Act: Tarefa B tenta entrar com token cancelável e é cancelada antes de obter a trava
        using var ctsB = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var taskBEx = await Record.ExceptionAsync(() => _poller.PollOnceAsync(ctsB.Token));

        // Assert: Tarefa B sofreu OperationCanceledException sem travar Tarefa A
        Assert.NotNull(taskBEx);
        Assert.IsAssignableFrom<OperationCanceledException>(taskBEx);

        // Libera Tarefa A
        taskACanFinish.TrySetResult(true);
        var resultA = await taskA;
        Assert.NotNull(resultA);

        // Tarefa C deve conseguir adquirir o semáforo normalmente sem deadlocks
        _client.OnSearchAsync = null;
        var resultC = await _poller.PollOnceAsync();
        Assert.NotNull(resultC);
        Assert.True(resultC.Success);
    }

    // =========================================================================
    // 3. RESILIÊNCIA: CANCELAMENTO DURANTE A VARREDURA LIBERA RECURSOS LIMPOS
    // =========================================================================

    [Fact]
    public async Task Challenger2_Resilience_CancellationMidExecution_ReleasesSemaphorePromptly()
    {
        // Arrange: 10 processos. No 3º processo, cancelamos a execução
        using var cts = new CancellationTokenSource();
        int processedCount = 0;

        var items = Enumerable.Range(1, 10).Select(i =>
            new SeiProcessItem($"MID_{i}", $"00001.{i:D6}/2026-00", "Desc", "Tipo", "GINFO", null, null)
        ).ToList();

        _client.SearchResult = new ProcessListResult(10, items);

        _client.OnLightSummaryAsync = (id, ct) =>
        {
            processedCount++;
            if (processedCount == 3)
            {
                cts.Cancel(); // Dispara cancelamento durante o processamento
            }
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        // Act
        var ex = await Record.ExceptionAsync(() => _poller.PollOnceAsync(cts.Token));

        // Assert: Cancelamento detectado e semáforo liberado
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);

        // O poller deve aceitar nova chamada imediatamente com novo token limpo
        _client.OnLightSummaryAsync = null;
        var newResult = await _poller.PollOnceAsync();
        Assert.NotNull(newResult);
        Assert.True(newResult.Success);
    }

    // =========================================================================
    // 4. RESILIÊNCIA: RECUPERAÇÃO AUTOMÁTICA APÓS SEQUÊNCIA DE FALHAS CRÍTICAS
    // =========================================================================

    [Fact]
    public async Task Challenger2_Resilience_SurvivesRepeatedNetworkFailuresAndRecoversAutomatically()
    {
        // Arrange: Simula 4 ciclos sucessivos de polling:
        // Ciclo 1: HTTP 500 (Erro de servidor SEI)
        // Ciclo 2: HTTP 503 (Indisponibilidade do SEI)
        // Ciclo 3: HttpRequestException (Timeout de conexão)
        // Ciclo 4: Sucesso completo!
        int cycle = 0;

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new("P_RECOVER", "00001.000777/2026-77", "Recuperacao", "Tipo", "GINFO", null, null)
        });
        _client.LightSummaries["P_RECOVER"] = new LightProcessSummary(
            "P_RECOVER", 1, "2026-10-29 10:00", "Recuperacao", "user", "00001.000777/2026-77"
        );
        _client.ProcessDetails["P_RECOVER"] = new ProcessDetail(
            "P_RECOVER", "00001.000777/2026-77", "Recuperacao", "Tipo", "GINFO", 0,
            new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>()
        );

        _client.OnSearchAsync = _ =>
        {
            cycle++;
            return cycle switch
            {
                1 => throw new SeiException(SeiErrorCode.Indisponivel, "HTTP 500 Internal Error"),
                2 => throw new SeiException(SeiErrorCode.Indisponivel, "HTTP 503 Service Unavailable"),
                3 => throw new HttpRequestException("Timeout de conexão TCP"),
                _ => Task.CompletedTask
            };
        };

        // Act & Assert:
        // 1º ciclo falha com erro 500
        var r1 = await _poller.PollOnceAsync();
        Assert.Equal(1, r1.ErrorsCount);

        // 2º ciclo falha com erro 503
        var r2 = await _poller.PollOnceAsync();
        Assert.Equal(1, r2.ErrorsCount);

        // 3º ciclo falha com exceção de rede pura
        var r3 = await _poller.PollOnceAsync();
        Assert.Equal(1, r3.ErrorsCount);

        // 4º ciclo recupera com 100% de sucesso
        var r4 = await _poller.PollOnceAsync();
        Assert.Equal(0, r4.ErrorsCount);
        Assert.Equal(1, r4.TotalChecked);
        Assert.True(r4.Success);

        // Verifica que o processo foi persistido normalmente após a recuperação
        var proc = _db.GetMonitoredProcess("P_RECOVER");
        Assert.NotNull(proc);
        Assert.Equal("00001.000777/2026-77", proc!.ProtocoloFormatado);
    }

    // =========================================================================
    // 5. CICLO DE VIDA: START -> STOP -> RESTART LIMPO SEM ZOMBIES
    // =========================================================================

    [Fact]
    public async Task Challenger2_Lifecycle_StartStopRestart_ExecutesCleanlyWithoutZombieTasks()
    {
        // Act 1: Inicia poller
        await _poller.StartAsync();
        Assert.True(_poller.IsRunning);

        // Act 2: Para poller
        await _poller.StopAsync();
        Assert.False(_poller.IsRunning);

        // Act 3: Reinicia poller
        await _poller.StartAsync();
        Assert.True(_poller.IsRunning);

        // Act 4: Para novamente
        await _poller.StopAsync();
        Assert.False(_poller.IsRunning);
    }

    [Fact]
    public async Task Challenger2_Lifecycle_ConcurrentStartCalls_AreIdempotent()
    {
        // Arrange & Act: Múltiplas chamadas simultâneas a StartAsync
        var tasks = Enumerable.Range(0, 10).Select(_ => _poller.StartAsync()).ToList();
        await Task.WhenAll(tasks);

        // Assert
        Assert.True(_poller.IsRunning);

        await _poller.StopAsync();
        Assert.False(_poller.IsRunning);
    }

    // =========================================================================
    // 6. ANÁLISE ADVERSARIAL: VULNERABILIDADE DE FALSO POSITIVO QUANDO TUDO FALHA
    // =========================================================================

    [Fact]
    public async Task Challenger2_Vulnerability_WhenAllCandidatesFail_DemonstratesFalseSuccessEvaluation()
    {
        // Cenário de teste adversarial:
        // Existem 3 processos já monitorados no banco local.
        // A rede cai completamente: Search falha e GetLightSummary falha para os 3 processos.
        // Observamos como o SyncResult.Success e a mensagem de status são calculados.

        const int cachedCount = 3;
        for (int i = 1; i <= cachedCount; i++)
        {
            _db.UpsertMonitoredProcess(new MonitoredProcess
            {
                Id = $"FAIL_{i}",
                ProtocoloFormatado = $"00001.{i:D6}/2026-00",
                TotalDocumentos = 5,
                UltimaAtividadeData = "2026-10-29 10:00",
                Origem = "CAIXA_ENTRADA"
            });
        }

        // Falha na busca
        _client.OnSearchAsync = _ => throw new SeiException(SeiErrorCode.Indisponivel);

        // Falha em todos os resumos leves
        _client.OnLightSummaryAsync = (_, _) => throw new SeiException(SeiErrorCode.Indisponivel);

        string? emittedStatusMessage = null;
        _poller.SyncStatusChanged += (syncing, msg) =>
        {
            if (!syncing && msg != null) emittedStatusMessage = msg;
        };

        // Act
        var result = await _poller.PollOnceAsync();

        // Análise:
        // 1 erro no search + 3 erros no loop = 4 erros.
        // 0 alterados, 0 inalterados.
        Assert.Equal(cachedCount, result.TotalChecked);
        Assert.Equal(4, result.ErrorsCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(0, result.UnchangedCount);

        // AVALIAÇÃO DO CÓDIGO ATUAL:
        // A expressão atual é `success: errorsCount == 0 || totalChecked > 0`.
        // Como totalChecked é 3 (embora todos os 3 tenham falhado!), result.Success é avaliado como TRUE!
        // E a mensagem de status é "Atualizado em HH:mm:ss" ao invés de "Falha na sincronização".
        // Comprovamos empiricamente esta fragilidade semântica do motor!
        bool actualSuccessInCode = result.Success;
        Assert.True(actualSuccessInCode, "Comprovado: o código atual retorna Success = true mesmo quando 100% das consultas falham devido a totalChecked > 0.");
        Assert.Contains("Atualizado em", emittedStatusMessage);
    }

    // =========================================================================
    // 7. DESCARTE: DISPOSEASSYNC ENCERRA LIMPO SEM EXCEÇÕES NÃO TRATADAS
    // =========================================================================

    [Fact]
    public async Task Challenger2_Disposal_DisposeAsync_TerminatesCleanly()
    {
        await _poller.StartAsync();
        Assert.True(_poller.IsRunning);

        await _poller.DisposeAsync();
        Assert.False(_poller.IsRunning);
    }
}
