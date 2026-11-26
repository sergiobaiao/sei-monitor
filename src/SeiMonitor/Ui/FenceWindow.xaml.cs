using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

/// <summary>
/// Janela WPF translúcida estilo Stardock Fences ancorada na camada de Área de Trabalho (Progman).
/// Suporta arrasto, redimensionamento, recolhimento, sobrevivência a Win+D e watchdog de 1s para o Explorer.
/// </summary>
public partial class FenceWindow : Window
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiPoller _poller;
    private readonly IShellInterop _shell;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly FenceViewModel _viewModel;

    private readonly DispatcherTimer _watchdogTimer;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _collapsed;

    public FenceViewModel ViewModel => _viewModel;

    public FenceWindow(
        Config config,
        Database db,
        ISeiPoller poller,
        IShellInterop? shell = null,
        IUiDispatcher? dispatcher = null)
    {
        InitializeComponent();

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _shell = shell ?? Win32ShellInterop.Instance;
        _uiDispatcher = dispatcher ?? new WpfDispatcher(Dispatcher);

        _viewModel = new FenceViewModel(_config, _db, _poller, _uiDispatcher, _shell);
        DataContext = _viewModel;

        // Restauração das dimensões salvas
        Width = _config.FenceW;
        Height = _config.FenceH;
        Opacity = _config.FenceAlpha;

        if (_config.FenceX.HasValue && _config.FenceY.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _config.FenceX.Value;
            Top = _config.FenceY.Value;
        }

        // Conecta controles da view ao ViewModel
        CmbGroups.ItemsSource = _viewModel.AvailableGroups;
        if (!string.IsNullOrEmpty(_viewModel.SelectedGroupId))
        {
            CmbGroups.SelectedValue = _viewModel.SelectedGroupId;
        }
        else
        {
            CmbGroups.SelectedIndex = 0;
        }

        ListProcesses.ItemsSource = _viewModel.DisplayedProcesses;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePills();

        if (_config.FenceCollapsed)
        {
            ApplyCollapseState(collapsed: true);
        }

        // Timer watchdog de 1s para detectar crash/restart do Explorer e manter ancoragem
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdogTimer.Tick += (_, _) => MaintainPin();

        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != IntPtr.Zero)
        {
            _shell.ApplyToolWindowStyles(_hwnd);
            PinToProgman();
        }
        _watchdogTimer.Start();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Ao perder foco, garante que permanece na base da pilha de janelas
        SendToBottom();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _watchdogTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }

    private void PinToProgman()
    {
        if (_hwnd == IntPtr.Zero) return;
        var progman = _shell.FindProgmanWindow();
        if (progman != IntPtr.Zero)
        {
            _shell.SetWindowOwner(_hwnd, progman);
        }
        SendToBottom();
    }

    public void MaintainPin()
    {
        if (!IsVisible || _hwnd == IntPtr.Zero) return;
        var progman = _shell.FindProgmanWindow();
        if (progman == IntPtr.Zero) return;

        var currentOwner = _shell.GetWindowOwner(_hwnd);
        if (currentOwner != progman)
        {
            _shell.SetWindowOwner(_hwnd, progman);
        }
        SendToBottom();
    }

    public void SendToBottom()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _shell.SendToBottom(_hwnd);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FenceViewModel.CountMonitored) ||
            e.PropertyName == nameof(FenceViewModel.CountNewActivities) ||
            e.PropertyName == nameof(FenceViewModel.CountDestined) ||
            e.PropertyName == nameof(FenceViewModel.CountAttention))
        {
            UpdatePills();
        }
        else if (e.PropertyName == nameof(FenceViewModel.LastSyncText))
        {
            TxtLastSync.Text = _viewModel.LastSyncText;
        }
    }

    private void UpdatePills()
    {
        TxtCntMonitored.Text = _viewModel.CountMonitored.ToString();
        TxtCntNewDocs.Text = _viewModel.CountNewActivities.ToString();
        TxtCntDestined.Text = _viewModel.CountDestined.ToString();
        TxtCntAttention.Text = _viewModel.CountAttention.ToString();
    }

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        // Ignora cliques que vieram de controles filhos interativos (ex: botões, combo)
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;
        if (e.OriginalSource is DependencyObject d2 && FindParent<ComboBox>(d2) != null) return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Proteção contra chamadas durante transições de janela
            }
            SavePosition();
            SendToBottom();
        }
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Clamp(Width + e.HorizontalChange, 280, 800);
        if (!_collapsed)
        {
            Height = Math.Clamp(Height + e.VerticalChange, 200, 1200);
        }
    }

    private void OnResizeDone(object sender, DragCompletedEventArgs e)
    {
        _config.FenceW = (int)Width;
        if (!_collapsed)
        {
            _config.FenceH = (int)Height;
        }
        SavePosition();
        SendToBottom();
    }

    private void SavePosition()
    {
        _config.FenceX = (int)Left;
        _config.FenceY = (int)Top;
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbGroups.SelectedValue is string grpId)
        {
            _viewModel.SelectGroup(string.IsNullOrWhiteSpace(grpId) ? null : grpId);
        }
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapseState(_collapsed);
    }

    private void ApplyCollapseState(bool collapsed)
    {
        _collapsed = collapsed;
        _viewModel.IsCollapsed = collapsed;

        if (collapsed)
        {
            RowBody.Height = new GridLength(0);
            RowFooter.Height = new GridLength(0);
            BodyGrid.Visibility = Visibility.Collapsed;
            FooterGrid.Visibility = Visibility.Collapsed;
            Height = 50;
            BtnCollapse.Content = "▸";
        }
        else
        {
            RowBody.Height = new GridLength(1, GridUnitType.Star);
            RowFooter.Height = GridLength.Auto;
            BodyGrid.Visibility = Visibility.Visible;
            FooterGrid.Visibility = Visibility.Visible;
            Height = _config.FenceH;
            BtnCollapse.Content = "▾";
        }
        SendToBottom();
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        BtnSync.IsEnabled = false;
        try
        {
            await _poller.SyncNowAsync();
        }
        finally
        {
            BtnSync.IsEnabled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typed) return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
