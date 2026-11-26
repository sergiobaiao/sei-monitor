using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Ui;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Bateria de testes de estresse adversarial empírico contra o SingleInstanceController.cs.
/// Cobre concorrência massiva de threads, simulação de Mutex abandonado,
/// rajada IPC de alta frequência, re-aquisição pós-Dispose e segurança de threads.
/// </summary>
public class M5SingleInstanceChallengerTests
{
    // =========================================================================
    // 1. CONCORRÊNCIA MASSIVA (RACE CONDITION CHECK)
    // =========================================================================

    [Fact]
    public void Stress_MassiveConcurrency_OnlyOneWinsFirstInstance()
    {
        var mutexName = $@"Local\Test_Stress_Mutex_{Guid.NewGuid():N}";
        const int threadCount = 40;
        var startGate = new ManualResetEventSlim(false);
        var controllers = new SingleInstanceController[threadCount];
        var results = new bool[threadCount];
        var exceptions = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    controllers[index] = new SingleInstanceController(mutexName);
                    startGate.Wait();
                    results[index] = controllers[index].TryAcquire();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[i].Start();
        }

        // Dispara todas as 40 threads simultaneamente
        startGate.Set();

        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join(TimeSpan.FromSeconds(5));
        }

        // Limpeza dos controllers
        for (int i = 0; i < threadCount; i++)
        {
            controllers[i]?.Dispose();
        }

        Assert.Empty(exceptions);

        int firstInstanceCount = 0;
        for (int i = 0; i < threadCount; i++)
        {
            if (results[i]) firstInstanceCount++;
        }

        // Exatamente UMA thread deve ter se tornado a instância primária
        Assert.Equal(1, firstInstanceCount);
    }

    // =========================================================================
    // 2. INVESTIGAÇÃO EMPÍRICA: ABANDONED MUTEX EXCEPTION & RECUPERAÇÃO
    // =========================================================================

    [Fact]
    public void Empirical_HowDotNetHandlesAbandonedMutex_WaitOneThrowsAbandonedMutexException()
    {
        var mutexName = $@"Local\Test_Abandoned_Mechanics_{Guid.NewGuid():N}";

        Mutex? initialOwnerMutex = null;
        var initialThread = new Thread(() =>
        {
            initialOwnerMutex = new Mutex(true, mutexName, out bool created);
            Assert.True(created);
            // Thread encerra retendo o lock
        });
        initialThread.Start();
        initialThread.Join();

        // Mutex continua vivo no processo através de initialOwnerMutex, mas em estado abandonado
        using var testMutex = new Mutex(false, mutexName, out bool createdNew);
        Assert.False(createdNew);

        // O construtor não lança AbandonedMutexException.
        // Apenas chamadas de espera como WaitOne lançam AbandonedMutexException!
        bool caughtAbandoned = false;
        try
        {
            testMutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            caughtAbandoned = true;
            testMutex.ReleaseMutex();
        }

        initialOwnerMutex?.Dispose();
        Assert.True(caughtAbandoned, "WaitOne deve lançar AbandonedMutexException quando o mutex foi abandonado.");
    }

    [Fact]
    public void Challenger_SingleInstanceController_AbandonedMutex_FailsToRecoverWithoutWaitOne()
    {
        var mutexName = $@"Local\Test_Abandoned_Bug_{Guid.NewGuid():N}";

        Mutex? initialOwnerMutex = null;
        var initialThread = new Thread(() =>
        {
            initialOwnerMutex = new Mutex(true, mutexName, out bool created);
            Assert.True(created);
            // Thread encerra retendo o lock
        });
        initialThread.Start();
        initialThread.Join();

        // Agora testamos o SingleInstanceController real
        using var controller = new SingleInstanceController(mutexName);
        bool acquired = controller.TryAcquire();

        initialOwnerMutex?.Dispose();

        // O SingleInstanceController atual tenta:
        // _mutex = new Mutex(initiallyOwned: true, _mutexName, out _isFirstInstance);
        // Porém, como o Mutex já existe no kernel em estado abandonado, createdNew é false,
        // o construtor do Mutex NÃO lança AbandonedMutexException (ele não realiza wait),
        // e _isFirstInstance permanece false!
        // Portanto, o SingleInstanceController falha em adquirir o mutex abandonado!
        //
        // Este teste documenta formalmente o comportamento atual do SingleInstanceController.
        // Se acquired == false, comprova o defeito de recuperação de mutex abandonado.
        Assert.False(acquired, "Documentação do defeito: TryAcquire() retorna false em mutex abandonado porque new Mutex(...) não lança AbandonedMutexException sem WaitOne.");
    }

    // =========================================================================
    // 3. RAJADA IPC EM ALTA FREQUÊNCIA (EVENTWAITHANDLE BURST)
    // =========================================================================

    [Fact]
    public void Stress_HighFrequencySignalBurst_DoesNotDeadlockOrLeak()
    {
        var uniqueMutex = $@"Local\Test_Burst_Mutex_{Guid.NewGuid():N}";
        var uniqueEvent = $@"Local\Test_Burst_Event_{Guid.NewGuid():N}";

        using var listener = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.True(listener.TryAcquire());

        int callbackCount = 0;
        var firstSignalReceived = new ManualResetEventSlim(false);

        listener.StartListeningForShowSignal(() =>
        {
            Interlocked.Increment(ref callbackCount);
            firstSignalReceived.Set();
        });

        const int burstCount = 100;
        var parallelExceptions = new ConcurrentBag<Exception>();

        // Dispara rajada paralela de 100 sinais IPC em alta frequência
        Parallel.For(0, burstCount, i =>
        {
            try
            {
                bool signaled = SingleInstanceController.SignalExistingInstance(uniqueEvent);
                if (!signaled)
                {
                    parallelExceptions.Add(new InvalidOperationException($"SignalExistingInstance retornou false no sinal {i}"));
                }
            }
            catch (Exception ex)
            {
                parallelExceptions.Add(ex);
            }
        });

        // Aguarda pelo menos o primeiro sinal ser processado
        bool received = firstSignalReceived.Wait(TimeSpan.FromSeconds(5));

        Assert.Empty(parallelExceptions);
        Assert.True(received, "Pelo menos um sinal de reexibição deveria ter sido processado pela escuta.");
        Assert.True(callbackCount >= 1, $"Total de callbacks disparados na rajada: {callbackCount}");
    }

    // =========================================================================
    // 4. DISPOSE E RE-AQUISIÇÃO DO MESMO MUTEX
    // =========================================================================

    [Fact]
    public void Lifecycle_DisposeAndReacquire_AllowsNextInstanceToTakeOwnership()
    {
        var uniqueMutex = $@"Local\Test_Reacquire_Mutex_{Guid.NewGuid():N}";
        var uniqueEvent = $@"Local\Test_Reacquire_Event_{Guid.NewGuid():N}";

        // Ciclo 1: Instância 1 adquire e descarta
        var instance1 = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.True(instance1.TryAcquire());
        Assert.True(instance1.IsFirstInstance);

        var instance1Received = new ManualResetEventSlim(false);
        instance1.StartListeningForShowSignal(() => instance1Received.Set());

        // Sinaliza instância 1
        Assert.True(SingleInstanceController.SignalExistingInstance(uniqueEvent));
        Assert.True(instance1Received.Wait(TimeSpan.FromSeconds(3)));

        // Instância 1 fecha completamente
        instance1.Dispose();

        // Ciclo 2: Instância 2 tenta adquirir o mesmo nome
        using var instance2 = new SingleInstanceController(uniqueMutex, uniqueEvent);
        bool instance2Acquired = instance2.TryAcquire();
        Assert.True(instance2Acquired, "Instância 2 deveria adquirir com sucesso o mutex após Dispose da Instância 1.");
        Assert.True(instance2.IsFirstInstance);

        var instance2Received = new ManualResetEventSlim(false);
        instance2.StartListeningForShowSignal(() => instance2Received.Set());

        // Sinaliza instância 2
        Assert.True(SingleInstanceController.SignalExistingInstance(uniqueEvent));
        Assert.True(instance2Received.Wait(TimeSpan.FromSeconds(3)));
    }

    // =========================================================================
    // 5. IDEMPOTÊNCIA DE DISPOSE E CHAMADAS MÚLTIPLAS
    // =========================================================================

    [Fact]
    public void Lifecycle_DoubleDispose_IsIdempotentAndSafe()
    {
        var uniqueMutex = $@"Local\Test_DoubleDispose_{Guid.NewGuid():N}";
        var controller = new SingleInstanceController(uniqueMutex);

        Assert.True(controller.TryAcquire());
        
        // Chamadas sucessivas a Dispose não devem lançar exceções
        controller.Dispose();
        controller.Dispose();
        controller.Dispose();
    }

    [Fact]
    public void Robustness_MultipleTryAcquireCalls_ReturnsCachedState()
    {
        var uniqueMutex = $@"Local\Test_MultiAcquire_{Guid.NewGuid():N}";
        using var controller = new SingleInstanceController(uniqueMutex);

        bool firstCall = controller.TryAcquire();
        bool secondCall = controller.TryAcquire();
        bool thirdCall = controller.Initialize();

        Assert.True(firstCall);
        Assert.True(secondCall);
        Assert.True(thirdCall);
        Assert.True(controller.IsFirstInstance);
    }

    // =========================================================================
    // 6. INSTÂNCIA SECUNDÁRIA NÃO PODE ESCUTAR
    // =========================================================================

    [Fact]
    public void Robustness_SecondInstance_CannotListenForShowSignal()
    {
        var uniqueMutex = $@"Local\Test_SecondListen_{Guid.NewGuid():N}";
        var uniqueEvent = $@"Local\Test_SecondEvent_{Guid.NewGuid():N}";

        using var first = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.True(first.TryAcquire());

        using var second = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.False(second.TryAcquire());

        bool secondCallbackInvoked = false;
        second.StartListeningForShowSignal(() => secondCallbackInvoked = true);

        // Sinaliza evento
        SingleInstanceController.SignalExistingInstance(uniqueEvent);
        Thread.Sleep(200);

        // O callback da segunda instância NÃO pode ser invocado
        Assert.False(secondCallbackInvoked);
    }

    // =========================================================================
    // 7. SINALIZAÇÃO EM EVENTO INEXISTENTE
    // =========================================================================

    [Fact]
    public void Robustness_SignalNonExistentEvent_ReturnsFalseWithoutThrowing()
    {
        var nonExistentEvent = $@"Local\Test_NonExistent_{Guid.NewGuid():N}";
        bool signaled = SingleInstanceController.SignalExistingInstance(nonExistentEvent);
        Assert.False(signaled);
    }

    // =========================================================================
    // 8. DISPOSE DE THREAD DIFERENTE DA QUE ADQUIRIU
    // =========================================================================

    [Fact]
    public void Lifecycle_DisposeFromDifferentThread_CatchesApplicationExceptionGracefully()
    {
        var uniqueMutex = $@"Local\Test_CrossThreadDispose_{Guid.NewGuid():N}";

        SingleInstanceController? controller = null;
        var creationThread = new Thread(() =>
        {
            controller = new SingleInstanceController(uniqueMutex);
            Assert.True(controller.TryAcquire());
        });
        creationThread.Start();
        creationThread.Join();

        Assert.NotNull(controller);

        // Descarte executado em outra thread (simula finalizador ou threadpool)
        Exception? disposeException = null;
        var disposeThread = new Thread(() =>
        {
            try
            {
                controller.Dispose();
            }
            catch (Exception ex)
            {
                disposeException = ex;
            }
        });
        disposeThread.Start();
        disposeThread.Join();

        // ReleaseMutex() lança ApplicationException quando chamado por outra thread,
        // mas o SingleInstanceController trata ApplicationException em seu bloco catch!
        Assert.Null(disposeException);
    }
}
