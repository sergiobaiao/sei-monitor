using System;
using System.Threading;

namespace SeiMonitor.Ui;

/// <summary>
/// Gerencia o controle de instância única da aplicação via Mutex e IPC com EventWaitHandle.
/// Suporta recuperação automática de mutexes abandonados e escuta via ThreadPool para reexibir a janela ativa.
/// </summary>
public sealed class SingleInstanceController : IDisposable
{
    public const string DefaultMutexName = @"Local\SEI_Monitor_Desktop";
    public const string DefaultShowEventName = @"Local\SEI_Monitor_Show";

    private readonly string _mutexName;
    private readonly string _eventName;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _waitHandleRegistration;
    private bool _isFirstInstance;
    private bool _disposed;

    public bool IsFirstInstance => _isFirstInstance;

    public SingleInstanceController(
        string? mutexName = null,
        string? eventName = null)
    {
        _mutexName = !string.IsNullOrWhiteSpace(mutexName) ? mutexName : DefaultMutexName;
        _eventName = !string.IsNullOrWhiteSpace(eventName) ? eventName : DefaultShowEventName;
    }

    /// <summary>
    /// Tenta adquirir a posse do Mutex de instância única.
    /// Trata com segurança AbandonedMutexException caso o processo anterior tenha encerrado anormalmente.
    /// </summary>
    public bool TryAcquire()
    {
        if (_mutex != null)
        {
            return _isFirstInstance;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out _isFirstInstance);
        }
        catch (AbandonedMutexException)
        {
            // O processo anterior encerrou anormalmente retendo o mutex.
            // A thread atual agora possui legalmente o mutex e é a instância primária legítima.
            _isFirstInstance = true;
        }

        return _isFirstInstance;
    }

    /// <summary>
    /// Alias para TryAcquire() para compatibilidade de API.
    /// </summary>
    public bool Initialize() => TryAcquire();

    /// <summary>
    /// Inicia a escuta contínua do evento de reexibição em threadpool.
    /// Invocado pela primeira instância ativa.
    /// </summary>
    public void StartListeningForShowSignal(Action onShowRequested)
    {
        ArgumentNullException.ThrowIfNull(onShowRequested);
        if (!_isFirstInstance) return;

        try
        {
            _showEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: _eventName,
                out _);

            _waitHandleRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (state, timedOut) =>
                {
                    if (!timedOut)
                    {
                        try
                        {
                            onShowRequested();
                        }
                        catch
                        {
                            // Protege contra exceções não tratadas no callback
                        }
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // Resiliente contra falhas de segurança do ambiente
        }
    }

    /// <summary>
    /// Alias para StartListeningForShowSignal para compatibilidade de API.
    /// </summary>
    public void WaitForShowSignal(Action onShow) => StartListeningForShowSignal(onShow);

    /// <summary>
    /// Sinaliza a primeira instância em execução para restaurar sua janela principal.
    /// Invocado pela segunda instância antes de seu encerramento imediato.
    /// </summary>
    public static bool SignalExistingInstance(string? eventName = null)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(eventName ?? DefaultShowEventName);
            return handle.Set();
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_waitHandleRegistration != null)
        {
            _waitHandleRegistration.Unregister(null);
            _waitHandleRegistration = null;
        }

        if (_showEvent != null)
        {
            _showEvent.Dispose();
            _showEvent = null;
        }

        if (_mutex != null)
        {
            if (_isFirstInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Thread pode não ser mais dona em cenários de finalização abrupta
                }
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
