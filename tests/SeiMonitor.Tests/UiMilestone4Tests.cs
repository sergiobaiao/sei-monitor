using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using SeiMonitor.Core;
using SeiMonitor.Ui;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suíte de testes unitários herméticos para o Milestone 4 (Desktop UI Widgets & Windows Integration).
/// Cobre integralmente os 10 grupos de requisitos arquiteturais especificados para M4.
/// Executa 100% headless sem necessidade de sessão interativa ou display conectado.
/// </summary>
[SupportedOSPlatform("windows")]
public class UiMilestone4Tests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly MockSeiPoller _mockPoller;
    private readonly MockShellInterop _mockShell;
    private readonly ImmediateDispatcher _dispatcher;

    public UiMilestone4Tests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db);
        _mockPoller = new MockSeiPoller();
        _mockShell = new MockShellInterop();
        _dispatcher = new ImmediateDispatcher();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // =========================================================================
    // GRUPO 1: Theme & Visual Tokens (Dark & Light Mode)
    // =========================================================================

    [Fact]
    public void G1_Theme_Apply_PopulatesDarkResources()
    {
        var dict = new ResourceDictionary();
        Theme.Apply(dict, "Dark");

        Assert.True(dict.Contains("Bg0"));
        Assert.True(dict.Contains("Bg1"));
        Assert.True(dict.Contains("Bg2"));
        Assert.True(dict.Contains("Bg3"));
        Assert.True(dict.Contains("Fg"));
        Assert.True(dict.Contains("FgMuted"));
        Assert.True(dict.Contains("FgDim"));
        Assert.True(dict.Contains("BorderBrush"));
        Assert.True(dict.Contains("AccentBrush"));
        Assert.True(dict.Contains("PillDestinedBrush"));
        Assert.True(dict.Contains("PillAttentionBrush"));
        Assert.True(dict.Contains("PillNewDocsBrush"));
        Assert.True(dict.Contains("PillMonitoredBrush"));
        Assert.True(dict.Contains("UrgentBrush"));

        var bg0 = Assert.IsType<SolidColorBrush>(dict["Bg0"]);
        Assert.Equal(Color.FromRgb(0x09, 0x09, 0x0B), bg0.Color);

        var fg = Assert.IsType<SolidColorBrush>(dict["Fg"]);
        Assert.Equal(Color.FromRgb(0xF4, 0xF4, 0xF5), fg.Color);
    }

    [Fact]
    public void G1_Theme_Apply_LightMode_UsesLightPalette()
    {
        var dict = new ResourceDictionary();
        Theme.Apply(dict, "Light");

        var bg0 = Assert.IsType<SolidColorBrush>(dict["Bg0"]);
        Assert.Equal(Color.FromRgb(0xF8, 0xFA, 0xFC), bg0.Color);

        var bg1 = Assert.IsType<SolidColorBrush>(dict["Bg1"]);
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), bg1.Color);

        var fg = Assert.IsType<SolidColorBrush>(dict["Fg"]);
        Assert.Equal(Color.FromRgb(0x0F, 0x17, 0x2A), fg.Color);
    }

    [Fact]
    public void G1_Theme_ReadAccent_FallbackOnHeadless()
    {
        var accent = Theme.ReadAccent();
        Assert.NotEqual(default, accent);
        Assert.StartsWith("#", Theme.AccentHex);
        Assert.Equal(7, Theme.AccentHex.Length);
    }

    // =========================================================================
    // GRUPO 2: Config.Theme & Reatividade
    // =========================================================================

    [Fact]
    public void G2_Config_Theme_DefaultsToDark()
    {
        Assert.Equal("Dark", _config.Theme);

        _config.Theme = "Light";
        Assert.Equal("Light", _config.Theme);
        Assert.Equal("Light", _db.GetSetting("app_theme"));

        _config.Theme = "Dark";
        Assert.Equal("Dark", _config.Theme);
    }

    [Fact]
    public void G2_Config_Theme_DispatchesSettingChanged()
    {
        string? notifiedSetting = null;
        _config.SettingChanged += name => notifiedSetting = name;

        _config.Theme = "Light";
        Assert.Equal("Theme", notifiedSetting);
    }

    // =========================================================================
    // GRUPO 3: UI Dispatcher Abstraction
    // =========================================================================

    [Fact]
    public void G3_ImmediateDispatcher_ExecutesInline()
    {
        var dispatcher = new ImmediateDispatcher();
        Assert.True(dispatcher.CheckAccess());

        bool invoked = false;
        dispatcher.Invoke(() => invoked = true);
        Assert.True(invoked);

        bool beginInvoked = false;
        dispatcher.BeginInvoke(() => beginInvoked = true);
        Assert.True(beginInvoked);
    }

    // =========================================================================
    // GRUPO 4: FenceViewModel & Cálculo de Contadores (Pills)
    // =========================================================================

    [Fact]
    public void G4_FenceViewModel_Pills_CalculatedCorrectly()
    {
        // 1. Processo normal pendente de leitura
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "101",
            ProtocoloFormatado = "00001.000001/2026-01",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = false,
            AbertoPorTerceiro = false,
            TotalDocumentos = 2
        });

        // 2. Processo destinado ao usuário pendente
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "102",
            ProtocoloFormatado = "00001.000002/2026-02",
            Origem = "ACOMPANHAMENTO",
            DestinadoAoUsuario = true,
            AbertoPorTerceiro = false,
            TotalDocumentos = 3
        });

        // 3. Processo aberto por terceiro pendente
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "103",
            ProtocoloFormatado = "00001.000003/2026-03",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = false,
            AbertoPorTerceiro = true,
            TotalDocumentos = 1
        });

        // 4. Processo lido
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "104",
            ProtocoloFormatado = "00001.000004/2026-04",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = false,
            AbertoPorTerceiro = false,
            TotalDocumentos = 1
        });
        _db.SetLocalReadStatus("104", isRead: true, docCount: 1);

        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);

        Assert.Equal(4, vm.CountMonitored);
        Assert.Equal(3, vm.CountNewActivities); // 101, 102, 103 não lidos
        Assert.Equal(1, vm.CountDestined);      // 102
        Assert.Equal(1, vm.CountAttention);     // 103
    }

    [Fact]
    public void G4_FenceViewModel_ProcessChanged_UpdatesPillCounts()
    {
        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        Assert.Equal(0, vm.CountMonitored);

        var proc = new MonitoredProcess
        {
            Id = "201",
            ProtocoloFormatado = "00001.000201/2026-01",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = true,
            TotalDocumentos = 1
        };
        _db.UpsertMonitoredProcess(proc);

        _mockPoller.RaiseProcessChanged(new ProcessChangedEventArgs(proc, UpdateType.NewProcess));

        Assert.Equal(1, vm.CountMonitored);
        Assert.Equal(1, vm.CountDestined);
        Assert.Equal(1, vm.CountNewActivities);
    }

    // =========================================================================
    // GRUPO 5: Dropdown de Grupos Especiais & Filtragem
    // =========================================================================

    [Fact]
    public void G5_FenceViewModel_FilterByGroup_UpdatesProcessesAndPills()
    {
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "GRP_URG", Nome = "Urgentes GINFO" });
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "GRP_ROT", Nome = "Rotina PGE" });

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "301",
            ProtocoloFormatado = "00001.000301/2026-01",
            Origem = "ACOMPANHAMENTO",
            IdGrupoEspecial = "GRP_URG",
            DestinadoAoUsuario = true
        });

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "302",
            ProtocoloFormatado = "00001.000302/2026-02",
            Origem = "ACOMPANHAMENTO",
            IdGrupoEspecial = "GRP_ROT",
            DestinadoAoUsuario = false
        });

        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        vm.RefreshGroups();

        Assert.Equal(3, vm.AvailableGroups.Count); // Todos + 2 grupos
        Assert.Equal(2, vm.CountMonitored);

        // Filtra por grupo Urgentes
        vm.SelectGroup("GRP_URG");
        Assert.Equal(1, vm.CountMonitored);
        Assert.Equal(1, vm.CountDestined);
        Assert.Single(vm.DisplayedProcesses);
        Assert.Equal("301", vm.DisplayedProcesses[0].Id);

        // Remove filtro
        vm.SelectGroup(null);
        Assert.Equal(2, vm.CountMonitored);
        Assert.Equal(2, vm.DisplayedProcesses.Count);
    }

    [Fact]
    public void G5_FenceViewModel_GroupSelection_PersistsInConfig()
    {
        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        vm.SelectGroup("GRP_PERSIST");

        Assert.Equal("GRP_PERSIST", _config.FenceActiveGroup);
        Assert.Equal("GRP_PERSIST", _config.FencePinnedGroup);
        Assert.Equal("GRP_PERSIST", _db.GetSetting("desktop_fence_active_group"));
    }

    // =========================================================================
    // GRUPO 6: Estado de Recolhimento & Aliases de Geometria
    // =========================================================================

    [Fact]
    public void G6_FenceViewModel_ToggleCollapse_UpdatesHeightAndSaves()
    {
        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        Assert.False(vm.IsCollapsed);

        vm.ToggleCollapse();
        Assert.True(vm.IsCollapsed);
        Assert.True(_config.FenceCollapsed);

        vm.ToggleCollapse();
        Assert.False(vm.IsCollapsed);
        Assert.False(_config.FenceCollapsed);
    }

    [Fact]
    public void G6_Config_Fence_GeometryAliases_SyncCorrectly()
    {
        _config.FenceLeft = 120;
        Assert.Equal(120, _config.FenceX);
        Assert.Equal(120, _config.FenceLeft);

        _config.FenceTop = 80;
        Assert.Equal(80, _config.FenceY);
        Assert.Equal(80, _config.FenceTop);

        _config.FenceWidth = 450;
        Assert.Equal(450, _config.FenceW);
        Assert.Equal(450, _config.FenceWidth);

        // Clamping de Width [280, 800]
        _config.FenceWidth = 1000;
        Assert.Equal(800, _config.FenceW);

        _config.FenceHeight = 750;
        Assert.Equal(750, _config.FenceH);
        Assert.Equal(750, _config.FenceHeight);

        // Clamping de Height [200, 1200]
        _config.FenceHeight = 150;
        Assert.Equal(200, _config.FenceH);

        _config.FenceOpacity = 0.85;
        Assert.Equal(0.85, _config.FenceAlpha, 2);
        Assert.Equal(0.85, _config.FenceOpacity, 2);

        _config.FencePinnedGroup = "GRUPO_TESTE";
        Assert.Equal("GRUPO_TESTE", _config.FenceActiveGroup);
        Assert.Equal("GRUPO_TESTE", _config.FencePinnedGroup);
    }

    // =========================================================================
    // GRUPO 7: Win32 P/Invoke Mock & Watchdog de Explorer
    // =========================================================================

    [Fact]
    public void G7_Fence_Pinning_SetsOwnerAndExStyles()
    {
        var progmanHwnd = new IntPtr(0x1111);
        var fenceHwnd = new IntPtr(0x2222);

        _mockShell.ProgmanHwnd = progmanHwnd;

        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        bool pinned = vm.PinWindow(fenceHwnd);

        Assert.True(pinned);
        Assert.Equal(1, _mockShell.AppliedStylesCount);
        Assert.Equal(progmanHwnd, _mockShell.LastOwnerSet);
        Assert.Equal(1, _mockShell.SendToBottomCount);
    }

    [Fact]
    public void G7_Fence_ExplorerWatchdog_ReanchorsOnHwndChange()
    {
        var fenceHwnd = new IntPtr(0x3333);
        var oldProgman = new IntPtr(0x4444);
        var newProgman = new IntPtr(0x5555);

        _mockShell.ProgmanHwnd = oldProgman;

        var vm = new FenceViewModel(_config, _db, _mockPoller, _dispatcher, _mockShell);
        vm.PinWindow(fenceHwnd);

        // Simula crash/restart do Explorer: Progman agora possui novo HWND
        _mockShell.ProgmanHwnd = newProgman;

        bool reanchored = vm.MaintainPin(fenceHwnd);
        Assert.True(reanchored);
        Assert.Equal(newProgman, _mockShell.LastOwnerSet);
        Assert.Equal(2, _mockShell.SendToBottomCount);
    }

    // =========================================================================
    // GRUPO 8: Marquee Ticker & Streaming de Notificações
    // =========================================================================

    [Fact]
    public void G8_TaskbarWidget_PushMessage_EnqueuesAndScrolls()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);

        _mockPoller.RaiseNotificationPushed("🎯 Processo destinado a você: 00001.000099/2026-99");

        Assert.Equal("🎯 Processo destinado a você: 00001.000099/2026-99", vm.CurrentMessage);
        Assert.Equal("🎯", vm.CurrentIcon);
        Assert.Equal(220, vm.Offset);

        vm.StepScroll(15);
        Assert.Equal(205, vm.Offset);
    }

    [Fact]
    public void G8_TaskbarWidget_Hover_PausesAndResumes()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        vm.Offset = 100;

        vm.Pause();
        Assert.True(vm.IsPaused);

        vm.StepScroll(10);
        Assert.Equal(100, vm.Offset); // Congelado em hover

        vm.Resume();
        Assert.False(vm.IsPaused);

        vm.StepScroll(10);
        Assert.Equal(90, vm.Offset);  // Retomou rolagem
    }

    // =========================================================================
    // GRUPO 9: Geometria de Barra de Tarefas (Todas as Bordas e DPI)
    // =========================================================================

    [Fact]
    public void G9_ShellInterop_CalculateTaskbarPositions_AllEdges()
    {
        var workArea = new Rect(0, 0, 1920, 1032);

        // 1. Taskbar na base com bandeja
        var bottomInfo = new TaskbarInfo(
            TaskbarEdge.Bottom,
            new Win32Taskbar.RECT(0, 1032, 1920, 1080),
            new Win32Taskbar.RECT(1620, 1032, 1920, 1080),
            HasTray: true,
            IsAutoHide: false
        );

        var (left1, top1) = Win32Taskbar.CalculateWidgetPosition(
            bottomInfo,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            dpiScaleX: 1.0,
            dpiScaleY: 1.0,
            workAreaDip: workArea
        );

        // 1620 - 320 - 12 = 1288
        Assert.Equal(1288, left1);
        // 1032 + (48 - 36)/2 = 1038
        Assert.Equal(1038, top1);

        // 2. Taskbar no topo
        var topInfo = new TaskbarInfo(
            TaskbarEdge.Top,
            new Win32Taskbar.RECT(0, 0, 1920, 48),
            new Win32Taskbar.RECT(1620, 0, 1920, 48),
            HasTray: true,
            IsAutoHide: false
        );

        var (left2, top2) = Win32Taskbar.CalculateWidgetPosition(
            topInfo,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            dpiScaleX: 1.0,
            dpiScaleY: 1.0,
            workAreaDip: new Rect(0, 48, 1920, 1032)
        );

        Assert.Equal(1288, left2);
        Assert.Equal(6, top2);

        // 3. High DPI (150% = 1.5)
        var (leftDpi, topDpi) = Win32Taskbar.CalculateWidgetPosition(
            bottomInfo,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            dpiScaleX: 1.5,
            dpiScaleY: 1.5,
            workAreaDip: workArea
        );

        Assert.True(leftDpi > 0);
        Assert.True(topDpi > 0);
    }

    [Fact]
    public void G9_ShellInterop_CalculateFlyoutPositions_AllEdges()
    {
        var workArea = new Rect(0, 0, 1920, 1032);

        var bottomInfo = new TaskbarInfo(
            TaskbarEdge.Bottom,
            new Win32Taskbar.RECT(0, 1032, 1920, 1080),
            new Win32Taskbar.RECT(1620, 1032, 1920, 1080),
            HasTray: true,
            IsAutoHide: false
        );

        var (fLeft, fTop) = Win32Taskbar.CalculateFlyoutPosition(
            bottomInfo,
            widgetLeftDip: 1288,
            widgetTopDip: 1038,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            flyoutWidthDip: 380,
            flyoutHeightDip: 520,
            workAreaDip: workArea
        );

        // Clamped na WorkArea: 1032 - 520 - 8 = 504 (para não sobrepor a margem inferior da tela)
        Assert.Equal(504, fTop);
        // 1288 + 320 - 380 = 1228
        Assert.Equal(1228, fLeft);
    }

    // =========================================================================
    // GRUPO 10: TaskbarFlyout Auto-Close em Deactivated & Mini-Dashboard
    // =========================================================================

    [Fact]
    public void G10_TaskbarFlyout_Deactivated_TriggersClose()
    {
        var vm = new TaskbarFlyoutViewModel(_db, _config, _mockPoller, _dispatcher);
        vm.IsOpen = true;

        vm.OnDeactivated();

        Assert.False(vm.IsOpen);
        Assert.True((DateTime.UtcNow - vm.LastDeactivatedTime).TotalSeconds < 2);
    }

    [Fact]
    public void G10_TaskbarFlyout_RefreshData_PopulatesGreetingAndCards()
    {
        _config.SeiUsuarioNome = "Carlos";
        _config.SeiUnidadeSigla = "GINFO";

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "401",
            ProtocoloFormatado = "00001.000401/2026-01",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = true,
            UltimaAtividadeDesc = "Despacho conclusivo"
        });

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "402",
            ProtocoloFormatado = "00001.000402/2026-02",
            Origem = "ACOMPANHAMENTO",
            DestinadoAoUsuario = false,
            AbertoPorTerceiro = true,
            UltimaAtividadeDesc = "Juntada de parecer"
        });

        var vm = new TaskbarFlyoutViewModel(_db, _config, _mockPoller, _dispatcher);

        Assert.Contains("Carlos", vm.Greeting);
        Assert.Equal("Unidade: GINFO", vm.Subheader);
        Assert.Equal(1, vm.CountDestinados);
        Assert.Equal(1, vm.CountTerceiros);
        Assert.Equal(1, vm.CountAcomp);
        Assert.Equal(1, vm.CountCaixa);

        Assert.Single(vm.Destinados);
        Assert.Equal("401", vm.Destinados[0].Id);
        Assert.Equal(2, vm.UltimasMovimentacoes.Count);
    }

    [Fact]
    public void G10_WpfWindows_CanInstantiateInStaThread()
    {
        RunInSta(() =>
        {
            var fence = new FenceWindow(_config, _db, _mockPoller, _mockShell, _dispatcher);
            Assert.NotNull(fence.ViewModel);
            Assert.Equal(_config.FenceW, fence.Width);
            Assert.Equal(_config.FenceH, fence.Height);

            var widget = new TaskbarWidgetWindow(_config, _mockPoller, _db, _mockShell, _dispatcher);
            Assert.NotNull(widget.ViewModel);
            widget.Reposition();
            Assert.True(widget.Left > 0);
            Assert.True(widget.Top > 0);

            var flyout = new TaskbarFlyoutWindow(_config, _mockPoller, _db, _mockShell, _dispatcher);
            Assert.NotNull(flyout.ViewModel);
            flyout.RefreshData();
            Assert.Equal(DateTime.MinValue, flyout.LastDeactivatedTime);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
        {
            throw new AggregateException("Falha na execução em thread STA", error);
        }
    }
}

// =============================================================================
// Mocks Utilitários para Testes Herméticos
// =============================================================================

internal sealed class MockShellInterop : IShellInterop
{
    public IntPtr ProgmanHwnd { get; set; } = new(0x1000);
    public IntPtr TrayHwnd { get; set; } = new(0x2000);
    public IntPtr LastOwnerSet { get; private set; } = IntPtr.Zero;
    public int AppliedStylesCount { get; private set; }
    public int SendToBottomCount { get; private set; }
    public int SendToTopmostCount { get; private set; }

    public IntPtr FindProgmanWindow() => ProgmanHwnd;

    public IntPtr FindTrayWindow() => TrayHwnd;

    public IntPtr GetWindowOwner(IntPtr hwnd) => LastOwnerSet;

    public bool ApplyToolWindowStyles(IntPtr hwnd)
    {
        AppliedStylesCount++;
        return true;
    }

    public bool SetWindowOwner(IntPtr hwnd, IntPtr ownerHwnd)
    {
        LastOwnerSet = ownerHwnd;
        return true;
    }

    public bool SendToBottom(IntPtr hwnd)
    {
        SendToBottomCount++;
        return true;
    }

    public bool SendToTopmost(IntPtr hwnd)
    {
        SendToTopmostCount++;
        return true;
    }

    public bool GetWindowRect(IntPtr hwnd, out Win32Taskbar.RECT rect)
    {
        rect = new Win32Taskbar.RECT(0, 0, 400, 600);
        return true;
    }

    public TaskbarInfo GetTaskbarInfo() => new(
        TaskbarEdge.Bottom,
        new Win32Taskbar.RECT(0, 1032, 1920, 1080),
        new Win32Taskbar.RECT(1620, 1032, 1920, 1080),
        HasTray: true,
        IsAutoHide: false
    );
}

internal sealed class MockSeiPoller : ISeiPoller
{
    public bool IsRunning { get; set; }
    public DateTime? LastSyncTime { get; set; } = DateTime.UtcNow;
    public SyncResult? LastSyncResult { get; set; }

    public event Action<ProcessChangedEventArgs>? ProcessChanged;
#pragma warning disable CS0067
    public event Action<bool, string?>? SyncStatusChanged;
#pragma warning restore CS0067
    public event Action<SyncResult>? SyncCompleted;
    public event Action<string>? NotificationPushed;
    public event Action<string, string>? NotificationDetailPushed;

    public void RaiseProcessChanged(ProcessChangedEventArgs e) => ProcessChanged?.Invoke(e);
    public void RaiseSyncCompleted(SyncResult res) => SyncCompleted?.Invoke(res);
    public void RaiseNotificationPushed(string msg) => NotificationPushed?.Invoke(msg);
    public void RaiseNotificationDetailPushed(string title, string msg) => NotificationDetailPushed?.Invoke(title, msg);

    public Task StartAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task<SyncResult> PollOnceAsync(CancellationToken ct = default) =>
        Task.FromResult(new SyncResult(totalChecked: 0, changedCount: 0, skippedCount: 0, timestamp: DateTime.UtcNow, success: true));

    public Task<SyncResult> SyncNowAsync(CancellationToken ct = default) =>
        Task.FromResult(new SyncResult(totalChecked: 0, changedCount: 0, skippedCount: 0, timestamp: DateTime.UtcNow, success: true));

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
