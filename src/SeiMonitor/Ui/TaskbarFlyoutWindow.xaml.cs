using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

/// <summary>
/// Janela flyout suspensa estilo Windows Widgets ancorada à barra de tarefas.
/// Suporta fechamento automático em Deactivated, contadores e atalhos rápidos de processos.
/// </summary>
public partial class TaskbarFlyoutWindow : Window
{
    private readonly Config _config;
    private readonly ISeiPoller _poller;
    private readonly Database _db;
    private readonly IShellInterop _shell;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TaskbarFlyoutViewModel _viewModel;

    public DateTime LastDeactivatedTime { get; private set; } = DateTime.MinValue;
    public TaskbarFlyoutViewModel ViewModel => _viewModel;

    public TaskbarFlyoutWindow(
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

        _viewModel = new TaskbarFlyoutViewModel(_db, _config, _poller, _uiDispatcher);
        DataContext = _viewModel;

        Deactivated += OnDeactivated;
        _poller.ProcessChanged += OnProcessChanged;
        _poller.SyncCompleted += OnSyncCompleted;

        RefreshData();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        LastDeactivatedTime = DateTime.UtcNow;
        _viewModel.OnDeactivated();
        Hide();
    }

    private void OnProcessChanged(ProcessChangedEventArgs e)
    {
        _uiDispatcher.BeginInvoke(RefreshData);
    }

    private void OnSyncCompleted(SyncResult result)
    {
        _uiDispatcher.BeginInvoke(() =>
        {
            RefreshData();
            BtnSync.IsEnabled = true;
        });
    }

    /// <summary>
    /// Exibe e posiciona a janela flyout ancorada à barra de tarefas e adjacente ao widget.
    /// </summary>
    public void ShowAndPosition(TaskbarWidgetWindow widget)
    {
        ArgumentNullException.ThrowIfNull(widget);

        RefreshData();
        _viewModel.IsOpen = true;

        var info = _shell.GetTaskbarInfo();
        var workArea = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);

        double widgetLeft = widget.Left;
        double widgetTop = widget.Top;
        double widgetWidth = widget.Width;
        double widgetHeight = widget.Height;

        var widgetHwnd = new System.Windows.Interop.WindowInteropHelper(widget).Handle;
        if (widgetHwnd != IntPtr.Zero && _shell.GetWindowRect(widgetHwnd, out var wRect) && wRect.Width > 0)
        {
            widgetLeft = wRect.Left / dpi.DpiScaleX;
            widgetTop = wRect.Top / dpi.DpiScaleY;
            widgetWidth = wRect.Width / dpi.DpiScaleX;
            widgetHeight = wRect.Height / dpi.DpiScaleY;
        }

        var (left, top) = Win32Taskbar.CalculateFlyoutPosition(
            info,
            widgetLeft,
            widgetTop,
            widgetWidth,
            widgetHeight,
            Width,
            Height,
            workArea
        );

        Left = left;
        Top = top;

        Show();
        Activate();
    }

    public void RefreshData()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshData);
            return;
        }

        _viewModel.RefreshData();

        TxtGreeting.Text = _viewModel.Greeting;
        TxtSubheader.Text = _viewModel.Subheader;

        CntDestinados.Text = _viewModel.CountDestinados.ToString();
        CntTerceiros.Text = _viewModel.CountTerceiros.ToString();
        CntAcomp.Text = _viewModel.CountAcomp.ToString();
        CntCaixa.Text = _viewModel.CountCaixa.ToString();

        // 3. Renderiza Card Destinados
        PanelDestinados.Children.Clear();
        BadgeDestinadosHeader.Text = $"({_viewModel.Destinados.Count})";

        if (_viewModel.Destinados.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Nenhum processo destinado pendente.",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            PanelDestinados.Children.Add(empty);
        }
        else
        {
            foreach (var proc in _viewModel.Destinados)
            {
                PanelDestinados.Children.Add(CreateProcessRow(proc, isPriority: true));
            }
        }

        // 4. Renderiza Card Movimentações Recentes
        PanelMovimentacoes.Children.Clear();
        BadgeMovimentacoesHeader.Text = $"({_viewModel.UltimasMovimentacoes.Count})";

        if (_viewModel.UltimasMovimentacoes.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Nenhuma movimentação registrada.",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            PanelMovimentacoes.Children.Add(empty);
        }
        else
        {
            foreach (var proc in _viewModel.UltimasMovimentacoes)
            {
                PanelMovimentacoes.Children.Add(CreateProcessRow(proc, isPriority: false));
            }
        }

        TxtLastSync.Text = _viewModel.LastSyncText;
    }

    private UIElement CreateProcessRow(MonitoredProcess proc, bool isPriority)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg3");
        border.SetResourceReference(Border.BorderBrushProperty, isPriority ? "UrgentBrush" : "BorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();

        // Número do processo e badge de terceiro
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        var txtProtocolo = new TextBlock
        {
            Text = proc.ProtocoloFormatado,
            FontWeight = FontWeights.Bold,
            FontSize = 11
        };
        txtProtocolo.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        titlePanel.Children.Add(txtProtocolo);

        if (proc.AbertoPorTerceiro)
        {
            var badgeAberto = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x78, 0x35, 0x0F)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0)
            };
            badgeAberto.Child = new TextBlock
            {
                Text = $"👁️ {proc.UltimoUsuarioNome ?? "Colega"}",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A))
            };
            titlePanel.Children.Add(badgeAberto);
        }

        textStack.Children.Add(titlePanel);

        // Descrição do último andamento
        if (!string.IsNullOrWhiteSpace(proc.UltimaAtividadeDesc))
        {
            var activity = new TextBlock
            {
                Text = proc.UltimaAtividadeDesc,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            activity.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            textStack.Children.Add(activity);
        }

        grid.Children.Add(textStack);
        Grid.SetColumn(textStack, 0);

        // Botão de ação rápida: Abrir no SEI web
        var btnOpen = new Button
        {
            Content = "🌐",
            ToolTip = "Abrir processo no SEI",
            FontSize = 11,
            Width = 24,
            Height = 24,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        btnOpen.SetResourceReference(Button.ForegroundProperty, "FgMuted");
        btnOpen.Click += (_, _) => OpenProcessInBrowser(proc);

        grid.Children.Add(btnOpen);
        Grid.SetColumn(btnOpen, 1);

        border.Child = grid;
        return border;
    }

    private void OpenProcessInBrowser(MonitoredProcess proc)
    {
        try
        {
            string baseUrl = _config.SeiWsseiBaseUrl.Replace("/api/v2", "/sei");
            string url = $"{baseUrl}/controlador.php?acao=procedimento_trabalhar&id_procedimento={proc.Id}";

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _db.SetLocalReadStatus(proc.Id, isRead: true, docCount: proc.TotalDocumentos);
            RefreshData();
        }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao abrir processo no navegador: {ex.Message}");
        }
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OnDeactivated();
        Hide();
    }

    private void OnOpenMainPanelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OnDeactivated();
        Hide();
        if (Application.Current?.MainWindow != null)
        {
            Application.Current.MainWindow.Show();
            Application.Current.MainWindow.WindowState = WindowState.Normal;
            Application.Current.MainWindow.Activate();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _poller.ProcessChanged -= OnProcessChanged;
        _poller.SyncCompleted -= OnSyncCompleted;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
