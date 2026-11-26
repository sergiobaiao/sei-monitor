using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

/// <summary>
/// Mini-widget ancorado à barra de tarefas do Windows.
/// Possui Marquee Ticker (letreiro deslizante contínuo suave) com hardware composition,
/// streaming de notificações do ISeiPoller, pausa suave em hover e acionamento de Flyout.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private readonly Config _config;
    private readonly ISeiPoller _poller;
    private readonly Database _db;
    private readonly IShellInterop _shell;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TaskbarWidgetViewModel _viewModel;

    private readonly DispatcherTimer _repositionTimer;
    private readonly ConcurrentQueue<string> _notificationsQueue = new();
    private readonly List<string> _rotationSummaries = new();

    private AnimationClock? _currentClock;
    private TaskbarFlyoutWindow? _flyout;
    private bool _isMouseOver;
    private IntPtr _hwnd = IntPtr.Zero;

    public TaskbarWidgetViewModel ViewModel => _viewModel;
    public TaskbarFlyoutWindow? Flyout => _flyout;

    public TaskbarWidgetWindow(
        Config config,
        ISeiPoller poller,
        Database db,
        IShellInterop? shell = null,
        IUiDispatcher? dispatcher = null)
    {
        InitializeComponent();

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _shell = shell ?? Win32ShellInterop.Instance;
        _uiDispatcher = dispatcher ?? new WpfDispatcher(Dispatcher);

        _viewModel = new TaskbarWidgetViewModel(_poller, _uiDispatcher, _shell);
        DataContext = _viewModel;

        // Timer de 500ms para manter o widget embutido na taskbar e impedir que o Explorer expanda os botões por baixo
        _repositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _repositionTimer.Tick += (_, _) => Reposition();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;

        _poller.NotificationPushed += OnNotificationPushed;
        _poller.ProcessChanged += OnProcessChanged;
        _poller.SyncCompleted += OnSyncCompleted;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != IntPtr.Zero)
        {
            _shell.ApplyToolWindowStyles(_hwnd);
        }
        Reposition();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reposition();
        _repositionTimer.Start();
        UpdateSummariesFromDb();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _repositionTimer.Stop();
        _poller.NotificationPushed -= OnNotificationPushed;
        _poller.ProcessChanged -= OnProcessChanged;
        _poller.SyncCompleted -= OnSyncCompleted;

        _currentClock?.Controller?.Stop();
        _flyout?.Close();
        _shell.RestoreTaskbarLayout();
        _viewModel.Dispose();
    }

    public void Reposition()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Reposition);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        int physicalWidth = (int)Math.Round(Width * dpi.DpiScaleX);

        if (_hwnd != IntPtr.Zero && _shell.EmbedInTaskbar(_hwnd, physicalWidth, out int heightPhys))
        {
            if (heightPhys > 0)
            {
                Height = heightPhys / dpi.DpiScaleY;
            }
            return;
        }

        var info = _shell.GetTaskbarInfo();
        var workArea = SystemParameters.WorkArea;

        var (left, top) = Win32Taskbar.CalculateWidgetPosition(
            info,
            Width,
            Height,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            workArea
        );

        Left = left;
        Top = top;

        if (_hwnd != IntPtr.Zero)
        {
            _shell.SendToTopmost(_hwnd);
        }
    }

    // ==========================================
    // Mecanismo de Ticker Marquee Suave
    // ==========================================

    private void OnNotificationPushed(string notifText)
    {
        _notificationsQueue.Enqueue(notifText);
        _viewModel.EnqueueNotification(notifText);

        _uiDispatcher.BeginInvoke(() =>
        {
            if (_notificationsQueue.Count == 1 && !_isMouseOver)
            {
                StartNextMarquee();
            }
        });
    }

    private void OnProcessChanged(ProcessChangedEventArgs e)
    {
        _uiDispatcher.BeginInvoke(UpdateSummariesFromDb);
    }

    private void OnSyncCompleted(SyncResult result)
    {
        _uiDispatcher.BeginInvoke(UpdateSummariesFromDb);
    }

    public void UpdateSummariesFromDb()
    {
        try
        {
            var processes = _db.ListMonitoredProcesses();
            int monitorados = processes.Count;
            int destinados = processes.Count(p => p.DestinadoAoUsuario);
            int abertos = processes.Count(p => p.AbertoPorTerceiro);
            int novos = processes.Count(p => !p.IsReadLocally);

            TxtIndMonitorados.Text = monitorados.ToString();
            TxtIndNovos.Text = novos.ToString();
            TxtIndParaMim.Text = destinados.ToString();
            TxtIndTerceiros.Text = abertos.ToString();

            // Atualiza Badge
            int totalBadge = destinados + abertos + novos;
            _viewModel.BadgeCount = totalBadge;
            if (totalBadge > 0)
            {
                CountBadge.Visibility = Visibility.Visible;
                TxtCount.Text = totalBadge.ToString();
                CountBadge.Background = destinados > 0
                    ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)) // Vermelho
                    : new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)); // Cinza
            }
            else
            {
                CountBadge.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Resiliente a acessos concorrentes ao SQLite
        }
    }

    public void StartNextMarquee()
    {
        if (!_notificationsQueue.TryDequeue(out var pushMessage))
        {
            // Sem notificações pendentes: volta para os indicadores fixos
            TickerPanel.Visibility = Visibility.Collapsed;
            IndicatorsPanel.Visibility = Visibility.Visible;
            _currentClock = null;
            return;
        }

        IndicatorsPanel.Visibility = Visibility.Collapsed;
        TickerPanel.Visibility = Visibility.Visible;

        string message = pushMessage;
        if (message.StartsWith("🎯"))
        {
            IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            TxtIcon.Text = "🎯";
        }
        else
        {
            IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)); // Laranja Push
            TxtIcon.Text = "⚡";
        }

        _viewModel.CurrentMessage = message;
        _viewModel.CurrentIcon = TxtIcon.Text;

        TickerText.Text = message;
        TickerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TickerText.DesiredSize.Width;
        double canvasWidth = TickerCanvas.ActualWidth > 0 ? TickerCanvas.ActualWidth : 190;

        double distance = canvasWidth + textWidth;
        double speedPixelsPerSec = 50.0; // Velocidade suave e legível
        var duration = TimeSpan.FromSeconds(distance / speedPixelsPerSec);

        var anim = new DoubleAnimation
        {
            From = canvasWidth,
            To = -textWidth,
            Duration = duration,
            FillBehavior = FillBehavior.Stop
        };

        _currentClock = anim.CreateClock();
        _currentClock.Completed += (s, e) =>
        {
            _currentClock = null;
            StartNextMarquee();
        };

        TickerTransform.ApplyAnimationClock(TranslateTransform.XProperty, _currentClock);

        if (_isMouseOver)
        {
            _currentClock.Controller?.Pause();
        }
    }

    // ==========================================
    // Interações de Mouse e Abertura de Flyout
    // ==========================================

    private void OnWidgetMouseEnter(object sender, MouseEventArgs e)
    {
        _isMouseOver = true;
        _viewModel.Pause();
        _currentClock?.Controller?.Pause();
        PillBorder.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    }

    private void OnWidgetMouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOver = false;
        _viewModel.Resume();
        _currentClock?.Controller?.Resume();
        PillBorder.Background = Brushes.Transparent;
    }

    private void OnWidgetMouseClick(object sender, MouseButtonEventArgs e)
    {
        ToggleFlyout();
    }

    public void ToggleFlyout()
    {
        if (_flyout != null && (DateTime.UtcNow - _flyout.LastDeactivatedTime).TotalMilliseconds < 250)
        {
            // Ignora o clique que causou a perda de foco (Deactivated) do flyout
            return;
        }

        if (_flyout == null)
        {
            _flyout = new TaskbarFlyoutWindow(_config, _poller, _db, _shell, _uiDispatcher);
        }

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            _viewModel.IsOpen = false;
        }
        else
        {
            _flyout.ShowAndPosition(this);
            _viewModel.IsOpen = true;
        }
    }
}
