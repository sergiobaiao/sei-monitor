using System;
using System.Windows;
using System.Windows.Threading;

namespace SeiMonitor.Ui;

/// <summary>
/// Abstração para despacho de chamadas na thread de interface do usuário (UI Thread).
/// Permite isolamento completo e testes unitários herméticos sem necessidade de sessão STA.
/// </summary>
public interface IUiDispatcher
{
    void Invoke(Action action);
    void BeginInvoke(Action action);
    bool CheckAccess();
}

/// <summary>
/// Implementação padrão que despacha para o Dispatcher do WPF em tempo de execução.
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.BeginInvoke(action);
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();
}

/// <summary>
/// Implementação imediata e síncrona para execução em testes unitários headless (MTA/CI).
/// </summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public bool CheckAccess() => true;
}
