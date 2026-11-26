using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using SeiMonitor.Core;
using SeiMonitor.Ui;

namespace SeiMonitor;

/// <summary>
/// Ponto de entrada da aplicação WPF SEI Monitor.
/// Orquestra a instância única, a bandeja do sistema (NotifyIcon), serviços de background e ciclo de vida de janelas.
/// </summary>
public partial class App : Application
{
    private static SingleInstanceController? _singleInstance;
    private Database? _database;
    private Config? _config;
    private SeiClient? _seiClient;
    private SeiPoller? _poller;
    private TrayIconManager? _trayManager;

    private FenceWindow? _fenceWindow;
    private TaskbarWidgetWindow? _taskbarWidgetWindow;
    private MainWindow? _mainWindow;

    private bool _isExplicitExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Instância Única via Mutex nomeado
        _singleInstance = new SingleInstanceController();
        if (!_singleInstance.TryAcquire())
        {
            // Sinaliza a instância existente para restaurar a janela e encerra esta cópia
            SingleInstanceController.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        // 2. Inicia escuta IPC de sinal de reexibição
        _singleInstance.StartListeningForShowSignal(() =>
        {
            Dispatcher.BeginInvoke(RestoreAndFocusMainWindow);
        });

        // 3. Inicialização da Infraestrutura Local
        _database = new Database();
        _database.InitializeDatabase();

        _config = new Config(_database);
        Theme.Apply(Resources, _config.Theme);

        _seiClient = new SeiClient(_config, _database);
        _poller = new SeiPoller(_config, _database, _seiClient);

        // 4. Inicialização da Bandeja do Sistema
        var trayCallbacks = new TrayIconCallbacks
        {
            OpenMainWindow = RestoreAndFocusMainWindow,
            TriggerSync = () => Task.Run(async () => await _poller.SyncNowAsync()),
            ToggleDesktopFence = ToggleDesktopFence,
            ToggleTaskbarWidget = ToggleTaskbarWidget,
            OpenSettings = OpenSettingsTab,
            ExitApplication = ExitApplication
        };

        _trayManager = new TrayIconManager(trayCallbacks, Dispatcher);
        _trayManager.Initialize();

        // 5. Inicialização dos Widgets de Desktop e Taskbar
        if (_config.DesktopFenceEnabled)
        {
            _fenceWindow = new FenceWindow(_config, _database, _poller);
            _fenceWindow.Show();
        }

        if (_config.TaskbarWidgetEnabled)
        {
            _taskbarWidgetWindow = new TaskbarWidgetWindow(_config, _poller, _database);
            _taskbarWidgetWindow.Show();
        }

        // 6. Inicialização da MainWindow
        _mainWindow = new MainWindow(_config, _database, _poller, _seiClient);
        _mainWindow.Closing += (s, args) =>
        {
            if (!_isExplicitExit && _config.CloseToTray)
            {
                args.Cancel = true;
                _mainWindow.Hide();
            }
        };
        _mainWindow.StateChanged += (s, args) =>
        {
            if (_mainWindow.WindowState == WindowState.Minimized && _config.MinimizeToTray)
            {
                _mainWindow.Hide();
            }
        };

        // 7. Avalia inicialização minimizada
        if (_config.StartMinimized)
        {
            _trayManager.ShowBalloon("SEI Monitor", "Monitor iniciado em segundo plano na bandeja do sistema.");
        }
        else
        {
            _mainWindow.Show();
        }

        // 8. Inicia loop periódico de polling em background
        _ = _poller.StartAsync();
    }

    public void RestoreAndFocusMainWindow()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        // Garante foco de primeiro plano contornando o foreground lock timeout do Windows
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    private void ToggleDesktopFence()
    {
        if (_fenceWindow == null)
        {
            if (_config != null && _database != null && _poller != null)
            {
                _fenceWindow = new FenceWindow(_config, _database, _poller);
                _fenceWindow.Show();
                _config.DesktopFenceEnabled = true;
            }
            return;
        }

        if (_fenceWindow.IsVisible)
        {
            _fenceWindow.Hide();
            if (_config != null) _config.DesktopFenceEnabled = false;
        }
        else
        {
            _fenceWindow.Show();
            if (_config != null) _config.DesktopFenceEnabled = true;
        }
    }

    private void ToggleTaskbarWidget()
    {
        if (_taskbarWidgetWindow == null)
        {
            if (_config != null && _poller != null && _database != null)
            {
                _taskbarWidgetWindow = new TaskbarWidgetWindow(_config, _poller, _database);
                _taskbarWidgetWindow.Show();
                _config.TaskbarWidgetEnabled = true;
            }
            return;
        }

        if (_taskbarWidgetWindow.IsVisible)
        {
            _taskbarWidgetWindow.Hide();
            if (_config != null) _config.TaskbarWidgetEnabled = false;
        }
        else
        {
            _taskbarWidgetWindow.Show();
            if (_config != null) _config.TaskbarWidgetEnabled = true;
        }
    }

    private void OpenSettingsTab()
    {
        RestoreAndFocusMainWindow();
        _mainWindow?.SelectSettingsTab();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;

        _trayManager?.Dispose();
        _trayManager = null;

        _ = _poller?.StopAsync();
        _poller?.Dispose();

        _fenceWindow?.Close();
        _taskbarWidgetWindow?.Close();
        _mainWindow?.Close();

        _database?.Dispose();
        _singleInstance?.Dispose();

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayManager?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
