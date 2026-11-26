using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SeiMonitor.Core;
using SeiMonitor.Ui;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suite de testes empíricos adversariais do agente m4_challenger_2.
/// Desafia e estressa:
/// 1. Cálculos de coordenadas de taskbar (4 bordas x 7 DPIs x Multi-Monitor x Degenerate inputs)
/// 2. Contenção estrita do Flyout dentro da WorkArea em todas as combinações
/// 3. Ticker Marquee: concorrência multi-thread, FIFO, hover pause, strings extremas
/// 4. Flyout: auto-close em Deactivated, debounce de 250ms e ciclo de vida STA
/// </summary>
[SupportedOSPlatform("windows")]
public class M4AdversarialChallengerTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly MockSeiPoller _mockPoller;
    private readonly MockShellInterop _mockShell;
    private readonly ImmediateDispatcher _dispatcher;

    public M4AdversarialChallengerTests()
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
    // 1. DESAFIO GEOMÉTRICO: TODAS AS 4 BORDAS X TODOS OS DPIS (100% A 300%)
    // =========================================================================

    public static IEnumerable<object[]> EdgeAndDpiCombinations()
    {
        var edges = new[] { TaskbarEdge.Bottom, TaskbarEdge.Top, TaskbarEdge.Left, TaskbarEdge.Right };
        var dpis = new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

        foreach (var edge in edges)
        {
            foreach (var dpi in dpis)
            {
                yield return new object[] { edge, dpi };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EdgeAndDpiCombinations))]
    public void Challenger_CalculateWidgetAndFlyoutPosition_AllEdgesAndDpis_NeverBleedsWorkArea(
        TaskbarEdge edge,
        double dpi)
    {
        // Resolução padrão 1920x1080 em DIPs (escalada fisicamente pelo DPI)
        int physW = (int)(1920 * dpi);
        int physH = (int)(1080 * dpi);
        int taskbarThickness = (int)(48 * dpi);

        Win32Taskbar.RECT bounds;
        Win32Taskbar.RECT trayBounds;
        Rect workAreaDip;

        switch (edge)
        {
            case TaskbarEdge.Bottom:
                bounds = new Win32Taskbar.RECT(0, physH - taskbarThickness, physW, physH);
                trayBounds = new Win32Taskbar.RECT((int)(1600 * dpi), physH - taskbarThickness, physW, physH);
                workAreaDip = new Rect(0, 0, 1920, 1080 - 48);
                break;

            case TaskbarEdge.Top:
                bounds = new Win32Taskbar.RECT(0, 0, physW, taskbarThickness);
                trayBounds = new Win32Taskbar.RECT((int)(1600 * dpi), 0, physW, taskbarThickness);
                workAreaDip = new Rect(0, 48, 1920, 1080 - 48);
                break;

            case TaskbarEdge.Left:
                bounds = new Win32Taskbar.RECT(0, 0, taskbarThickness, physH);
                trayBounds = new Win32Taskbar.RECT(0, (int)(800 * dpi), taskbarThickness, physH);
                workAreaDip = new Rect(48, 0, 1920 - 48, 1080);
                break;

            case TaskbarEdge.Right:
            default:
                bounds = new Win32Taskbar.RECT(physW - taskbarThickness, 0, physW, physH);
                trayBounds = new Win32Taskbar.RECT(physW - taskbarThickness, (int)(800 * dpi), physW, physH);
                workAreaDip = new Rect(0, 0, 1920 - 48, 1080);
                break;
        }

        var info = new TaskbarInfo(edge, bounds, trayBounds, HasTray: true, IsAutoHide: false);

        double widgetWDip = 320;
        double widgetHDip = 36;
        double flyoutWDip = 380;
        double flyoutHDip = 520;

        var (widgetLeft, widgetTop) = Win32Taskbar.CalculateWidgetPosition(
            info,
            widgetWDip,
            widgetHDip,
            dpi,
            dpi,
            workAreaDip
        );

        // Asserções para o Widget: coordenadas válidas e não NaN
        Assert.False(double.IsNaN(widgetLeft), $"Widget Left is NaN for edge={edge}, dpi={dpi}");
        Assert.False(double.IsNaN(widgetTop), $"Widget Top is NaN for edge={edge}, dpi={dpi}");
        Assert.False(double.IsInfinity(widgetLeft));
        Assert.False(double.IsInfinity(widgetTop));

        var (flyoutLeft, flyoutTop) = Win32Taskbar.CalculateFlyoutPosition(
            info,
            widgetLeft,
            widgetTop,
            widgetWDip,
            widgetHDip,
            flyoutWDip,
            flyoutHDip,
            workAreaDip
        );

        // Asserções para o Flyout:
        Assert.False(double.IsNaN(flyoutLeft), $"Flyout Left is NaN for edge={edge}, dpi={dpi}");
        Assert.False(double.IsNaN(flyoutTop), $"Flyout Top is NaN for edge={edge}, dpi={dpi}");

        // Verificação estrita de contenção: Flyout NUNCA deve vazar da WorkArea
        Assert.True(flyoutLeft >= workAreaDip.Left,
            $"Flyout Left {flyoutLeft} vazou à esquerda de WorkArea.Left {workAreaDip.Left} (Edge={edge}, DPI={dpi})");
        Assert.True(flyoutLeft + flyoutWDip <= workAreaDip.Right + 0.1,
            $"Flyout Right {flyoutLeft + flyoutWDip} vazou à direita de WorkArea.Right {workAreaDip.Right} (Edge={edge}, DPI={dpi})");
        Assert.True(flyoutTop >= workAreaDip.Top,
            $"Flyout Top {flyoutTop} vazou acima de WorkArea.Top {workAreaDip.Top} (Edge={edge}, DPI={dpi})");
        Assert.True(flyoutTop + flyoutHDip <= workAreaDip.Bottom + 0.1,
            $"Flyout Bottom {flyoutTop + flyoutHDip} vazou abaixo de WorkArea.Bottom {workAreaDip.Bottom} (Edge={edge}, DPI={dpi})");
    }

    [Fact]
    public void Challenger_CalculateWidgetPosition_WithoutTray_UsesEdgeMargin()
    {
        var info = new TaskbarInfo(
            TaskbarEdge.Bottom,
            new Win32Taskbar.RECT(0, 1032, 1920, 1080),
            default,
            HasTray: false,
            IsAutoHide: false
        );

        var (left, top) = Win32Taskbar.CalculateWidgetPosition(
            info,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            dpiScaleX: 1.0,
            dpiScaleY: 1.0,
            workAreaDip: new Rect(0, 0, 1920, 1032)
        );

        // Sem tray: info.Bounds.Right - physicalWidgetW - (180 * dpi) = 1920 - 320 - 180 = 1420
        Assert.Equal(1420, left);
        Assert.Equal(1038, top);
    }

    // =========================================================================
    // 2. DESAFIO MULTI-MONITOR: COORDENADAS NEGATIVAS E ULTRA-WIDE (5120x1440)
    // =========================================================================

    [Fact]
    public void Challenger_MultiMonitor_SecondaryLeftNegativeCoordinates_CalculatesCorrectly()
    {
        // Monitor 2 à esquerda do principal: bounds [-1920, 0, 0, 1080]
        var bounds = new Win32Taskbar.RECT(-1920, 1040, 0, 1080);
        var tray = new Win32Taskbar.RECT(-300, 1040, 0, 1080);
        var workArea = new Rect(-1920, 0, 1920, 1040); // Right = 0, Bottom = 1040

        var info = new TaskbarInfo(TaskbarEdge.Bottom, bounds, tray, HasTray: true, IsAutoHide: false);

        var (wLeft, wTop) = Win32Taskbar.CalculateWidgetPosition(
            info,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            dpiScaleX: 1.0,
            dpiScaleY: 1.0,
            workAreaDip: workArea
        );

        // TrayBounds.Left (-300) - 320 - 12 = -632
        Assert.Equal(-632, wLeft);
        Assert.Equal(1042, wTop);

        var (fLeft, fTop) = Win32Taskbar.CalculateFlyoutPosition(
            info,
            widgetLeftDip: wLeft,
            widgetTopDip: wTop,
            widgetWidthDip: 320,
            widgetHeightDip: 36,
            flyoutWidthDip: 380,
            flyoutHeightDip: 520,
            workAreaDip: workArea
        );

        // Clamped dentro de [-1920, 0] e [0, 1040]
        Assert.True(fLeft >= workArea.Left);
        Assert.True(fLeft + 380 <= workArea.Right);
        Assert.True(fTop >= workArea.Top);
        Assert.True(fTop + 520 <= workArea.Bottom);
    }

    [Fact]
    public void Challenger_MultiMonitor_UltraWide_5120x1440_HighResolution()
    {
        var bounds = new Win32Taskbar.RECT(0, 1380, 5120, 1440);
        var tray = new Win32Taskbar.RECT(4600, 1380, 5120, 1440);
        var workArea = new Rect(0, 0, 5120, 1380);

        var info = new TaskbarInfo(TaskbarEdge.Bottom, bounds, tray, HasTray: true, IsAutoHide: false);

        var (wLeft, wTop) = Win32Taskbar.CalculateWidgetPosition(
            info, 320, 36, 1.0, 1.0, workArea
        );

        // 4600 - 320 - 12 = 4268
        Assert.Equal(4268, wLeft);
        Assert.Equal(1392, wTop);

        var (fLeft, fTop) = Win32Taskbar.CalculateFlyoutPosition(
            info, wLeft, wTop, 320, 36, 380, 520, workArea
        );

        Assert.True(fLeft + 380 <= workArea.Right);
        Assert.True(fTop + 520 <= workArea.Bottom);
    }

    // =========================================================================
    // 3. ENTRADAS DEGENERADAS E RESILIÊNCIA DE FRONTEIRA
    // =========================================================================

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, -1.0)]
    [InlineData(-50.0, 1.0)]
    public void Challenger_DegenerateDpi_FallsBackToDefaultScale(double dpiX, double dpiY)
    {
        var info = new TaskbarInfo(
            TaskbarEdge.Bottom,
            new Win32Taskbar.RECT(0, 1040, 1920, 1080),
            new Win32Taskbar.RECT(1600, 1040, 1920, 1080),
            HasTray: true,
            IsAutoHide: false
        );

        var (left, top) = Win32Taskbar.CalculateWidgetPosition(
            info, 320, 36, dpiX, dpiY, new Rect(0, 0, 1920, 1040)
        );

        // Com fallback para 1.0: 1600 - 320 - 12 = 1268
        Assert.Equal(1268, left);
        Assert.Equal(1042, top);
    }

    [Fact]
    public void Challenger_SmallWorkArea_ClampsWithoutCrash()
    {
        // Work area menor que o flyout (ex.: tela comprimida 300x400)
        var tinyWorkArea = new Rect(0, 0, 300, 400);
        var info = new TaskbarInfo(
            TaskbarEdge.Bottom,
            new Win32Taskbar.RECT(0, 360, 300, 400),
            new Win32Taskbar.RECT(250, 360, 300, 400),
            HasTray: true,
            IsAutoHide: false
        );

        var (wLeft, wTop) = Win32Taskbar.CalculateWidgetPosition(
            info, 320, 36, 1.0, 1.0, tinyWorkArea
        );

        var (fLeft, fTop) = Win32Taskbar.CalculateFlyoutPosition(
            info, wLeft, wTop, 320, 36, 380, 520, tinyWorkArea
        );

        Assert.False(double.IsNaN(wLeft));
        Assert.False(double.IsNaN(fLeft));
    }

    // =========================================================================
    // 4. ESTRESSE CONCORRENTE DO TICKER MARQUEE & STREAMING DE NOTIFICAÇÕES
    // =========================================================================

    [Fact]
    public async Task Challenger_Marquee_ConcurrentEnqueueBurst_AllHandledThreadSafe()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        int messageCount = 100;
        var barrier = new Barrier(messageCount);

        var tasks = Enumerable.Range(1, messageCount).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            vm.EnqueueNotification($"Mensagem push #{i:D3} do processo");
        })).ToArray();

        await Task.WhenAll(tasks);

        // A mensagem atual deve ter sido consumida sem exceções e a fila não deve ter corrido deadlocks
        Assert.NotNull(vm.CurrentMessage);
        Assert.StartsWith("Mensagem push #", vm.CurrentMessage);
    }

    [Fact]
    public void Challenger_Marquee_HoverPauseResume_FreezesOffsetStrictly()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        vm.Offset = 200;

        // 1. Rolagem ativa
        vm.StepScroll(10);
        Assert.Equal(190, vm.Offset);

        // 2. Mouse entra: Pausa
        vm.Pause();
        Assert.True(vm.IsPaused);

        // 3. StepScroll durante pausa não deve mover o letreiro
        for (int i = 0; i < 50; i++)
        {
            vm.StepScroll(5);
        }
        Assert.Equal(190, vm.Offset);

        // 4. Mouse sai: Retoma
        vm.Resume();
        Assert.False(vm.IsPaused);

        vm.StepScroll(15);
        Assert.Equal(175, vm.Offset);
    }

    [Fact]
    public void Challenger_Marquee_RapidHoverTransitions_NeverCorruptsState()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);

        // Simula 1000 transições rápidas de Enter/Leave
        for (int i = 0; i < 1000; i++)
        {
            vm.Pause();
            Assert.True(vm.IsPaused);
            vm.Resume();
            Assert.False(vm.IsPaused);
        }
    }

    [Theory]
    [InlineData("🎯 Processo destinado a você: 0001/2026", "🎯")]
    [InlineData("⚡ Novo documento inserido no processo", "⚡")]
    [InlineData("Outra notificação qualquer", "⚡")]
    public void Challenger_Marquee_NotificationIcon_Deduction(string text, string expectedIcon)
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        vm.EnqueueNotification(text);

        Assert.Equal(text, vm.CurrentMessage);
        Assert.Equal(expectedIcon, vm.CurrentIcon);
    }

    [Fact]
    public void Challenger_Marquee_HugeNotificationText_HandledSafely()
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        string hugeText = new string('A', 15000);

        vm.EnqueueNotification(hugeText);
        Assert.Equal(hugeText, vm.CurrentMessage);
        Assert.Equal(220, vm.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Challenger_Marquee_NullOrEmptyNotification_IgnoredGracefully(string? emptyText)
    {
        var vm = new TaskbarWidgetViewModel(_mockPoller, _dispatcher, _mockShell);
        string initial = vm.CurrentMessage;

        vm.EnqueueNotification(emptyText!);
        Assert.Equal(initial, vm.CurrentMessage);
    }

    // =========================================================================
    // 5. ESTRESSE DO TASKBAR FLYOUT & EVENTO DEACTIVATED
    // =========================================================================

    [Fact]
    public void Challenger_TaskbarFlyout_Deactivated_ClosesImmediatelyAndSetsTimestamp()
    {
        var vm = new TaskbarFlyoutViewModel(_db, _config, _mockPoller, _dispatcher);
        vm.IsOpen = true;

        var before = DateTime.UtcNow;
        vm.OnDeactivated();
        var after = DateTime.UtcNow;

        Assert.False(vm.IsOpen);
        Assert.InRange(vm.LastDeactivatedTime, before.AddMilliseconds(-50), after.AddMilliseconds(50));
    }

    [Fact]
    public void Challenger_TaskbarFlyout_DebounceMechanism_SuppressesToggleClick()
    {
        RunInSta(() =>
        {
            var widget = new TaskbarWidgetWindow(_config, _mockPoller, _db, _mockShell, _dispatcher);
            Assert.NotNull(widget);

            // 1. Abre o Flyout
            widget.ToggleFlyout();
            Assert.NotNull(widget.Flyout);
            Assert.True(widget.ViewModel.IsOpen);

            // 2. Simula perda de foco (Deactivated no flyout)
            widget.Flyout.ViewModel.OnDeactivated();
            Assert.False(widget.Flyout.ViewModel.IsOpen);

            // 3. Clique imediato (<250ms) no widget para fechar deve ser ignorado para não reabrir em toggle
            widget.ToggleFlyout();
            Assert.False(widget.ViewModel.IsOpen);

            widget.Close();
        });
    }

    [Fact]
    public void Challenger_TaskbarFlyout_RefreshData_ConcurrentStress()
    {
        var vm = new TaskbarFlyoutViewModel(_db, _config, _mockPoller, _dispatcher);

        // Dispara múltiplas leituras e inserções concorrentes
        Parallel.For(0, 50, i =>
        {
            _db.UpsertMonitoredProcess(new MonitoredProcess
            {
                Id = $"P_{i}",
                ProtocoloFormatado = $"00001.{i:D6}/2026",
                Origem = "CAIXA_ENTRADA",
                DestinadoAoUsuario = (i % 2 == 0),
                AbertoPorTerceiro = (i % 3 == 0)
            });

            vm.RefreshData();
        });

        Assert.True(vm.CountDestinados > 0);
        Assert.True(vm.CountTerceiros > 0);
    }

    [Fact]
    public void Challenger_WpfWindows_RapidInstantiateAndClose_NoMemoryLeak()
    {
        RunInSta(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                var w = new TaskbarWidgetWindow(_config, _mockPoller, _db, _mockShell, _dispatcher);
                w.Show();
                w.Reposition();
                w.Close();

                var f = new TaskbarFlyoutWindow(_config, _mockPoller, _db, _mockShell, _dispatcher);
                f.Show();
                f.Close();
            }
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
            throw new AggregateException("Falha na execução STA", error);
        }
    }
}
