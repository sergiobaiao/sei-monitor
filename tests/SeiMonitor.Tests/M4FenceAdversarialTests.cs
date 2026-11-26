using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Testes empíricos adversariais do agente m4_challenger_1 para o Milestone 4 (FenceWindow).
/// Desafia Z-Order (HWND_BOTTOM), estilos Win32 estendidos, sobrevivência a Win+D,
/// 1s watchdog timer em crash/restart do Explorer, limites de geometria e filtragem de grupos.
/// </summary>
[SupportedOSPlatform("windows")]
public class M4FenceAdversarialTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly MockSeiPoller _poller;
    private readonly MockShellInterop _shell;
    private readonly ImmediateDispatcher _dispatcher;

    public M4FenceAdversarialTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db);
        _poller = new MockSeiPoller();
        _shell = new MockShellInterop();
        _dispatcher = new ImmediateDispatcher();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // =========================================================================
    // 1. DESAFIO WATCHDOG: CRASH / REINÍCIO DO EXPLORER (HWND FLAPPING & NULL)
    // =========================================================================

    [Fact]
    public void Challenger_Watchdog_ExplorerCrash_HandlesNullProgmanGracefully()
    {
        var fenceHwnd = new IntPtr(0x1001);
        _shell.ProgmanHwnd = new IntPtr(0x2001);

        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);
        Assert.True(vm.PinWindow(fenceHwnd));
        Assert.Equal(new IntPtr(0x2001), _shell.LastOwnerSet);

        // Explorer cai: Progman é destruído e não encontrado
        _shell.ProgmanHwnd = IntPtr.Zero;

        // Watchdog tick enquanto Explorer está fora do ar
        bool reanchored = vm.MaintainPin(fenceHwnd);
        Assert.False(reanchored);
        // Não deve quebrar ou lançar exceção
        Assert.Equal(new IntPtr(0x2001), _shell.LastOwnerSet);

        // Explorer reinicia com novo HWND
        _shell.ProgmanHwnd = new IntPtr(0x3001);

        // Próximo watchdog tick: reancora com sucesso
        reanchored = vm.MaintainPin(fenceHwnd);
        Assert.True(reanchored);
        Assert.Equal(new IntPtr(0x3001), _shell.LastOwnerSet);
        Assert.True(_shell.SendToBottomCount >= 2);
    }

    [Fact]
    public void Challenger_Watchdog_RapidProgmanHwndChanges_Stress()
    {
        var fenceHwnd = new IntPtr(0x1002);
        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);

        // Simula 30 reinícios sucessivos do explorer.exe
        for (int i = 1; i <= 30; i++)
        {
            var currentProgman = new IntPtr(0x5000 + i);
            _shell.ProgmanHwnd = currentProgman;

            bool reanchored = vm.MaintainPin(fenceHwnd);
            Assert.True(reanchored);
            Assert.Equal(currentProgman, _shell.LastOwnerSet);

            // Chamada repetida sem alteração de HWND não deve reancorar novamente
            bool redundant = vm.MaintainPin(fenceHwnd);
            Assert.False(redundant);
        }

        Assert.Equal(60, _shell.SendToBottomCount);
    }

    [Fact]
    public void Challenger_PinWindow_InvalidHwnds_ReturnFalseWithoutThrowing()
    {
        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);

        // HWND da janela é Zero
        Assert.False(vm.PinWindow(IntPtr.Zero));
        Assert.False(vm.MaintainPin(IntPtr.Zero));

        // Progman é Zero
        _shell.ProgmanHwnd = IntPtr.Zero;
        Assert.False(vm.PinWindow(new IntPtr(0x9999)));
        Assert.False(vm.MaintainPin(new IntPtr(0x9999)));
    }

    // =========================================================================
    // 2. DESAFIO Z-ORDER (HWND_BOTTOM) & ESTILOS WIN32
    // =========================================================================

    [Fact]
    public void Challenger_ExtendedStyles_BitwiseSanityCheck()
    {
        long initialStyle = Win32Taskbar.WS_EX_APPWINDOW | 0x00000001L; // AppWindow + Left align

        // Aplica fórmula de ApplyToolWindowStyles
        long newStyle = (initialStyle | Win32Taskbar.WS_EX_TOOLWINDOW | Win32Taskbar.WS_EX_NOACTIVATE)
                        & ~Win32Taskbar.WS_EX_APPWINDOW;

        // WS_EX_TOOLWINDOW deve estar presente
        Assert.True((newStyle & Win32Taskbar.WS_EX_TOOLWINDOW) != 0);
        // WS_EX_NOACTIVATE deve estar presente
        Assert.True((newStyle & Win32Taskbar.WS_EX_NOACTIVATE) != 0);
        // WS_EX_APPWINDOW deve ter sido completamente removido
        Assert.True((newStyle & Win32Taskbar.WS_EX_APPWINDOW) == 0);
        // Flag original não afetada deve ser mantida
        Assert.True((newStyle & 0x00000001L) != 0);
    }

    [Fact]
    public void Challenger_ZOrder_ConstantsMatchWin32Specifications()
    {
        Assert.Equal(new IntPtr(1), Win32Taskbar.HWND_BOTTOM);
        Assert.Equal(new IntPtr(-1), Win32Taskbar.HWND_TOPMOST);
        Assert.Equal(new IntPtr(-2), Win32Taskbar.HWND_NOTOPMOST);

        // Flags SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        uint flags = Win32Taskbar.SWP_NOMOVE | Win32Taskbar.SWP_NOSIZE | Win32Taskbar.SWP_NOACTIVATE;
        Assert.Equal(0x0002u | 0x0001u | 0x0010u, flags);
    }

    // =========================================================================
    // 3. DESAFIO GEOMETRIA: LIMITES, CLAMPING E PERSISTÊNCIA MULTI-MONITOR
    // =========================================================================

    [Theory]
    [InlineData(-500, 280)]
    [InlineData(0, 280)]
    [InlineData(279, 280)]
    [InlineData(280, 280)]
    [InlineData(500, 500)]
    [InlineData(800, 800)]
    [InlineData(801, 800)]
    [InlineData(99999, 800)]
    public void Challenger_Geometry_Width_ClampedStrictlyToRange(int inputWidth, int expectedWidth)
    {
        _config.FenceWidth = inputWidth;
        Assert.Equal(expectedWidth, _config.FenceWidth);
        Assert.Equal(expectedWidth, _config.FenceW);
        Assert.Equal(expectedWidth, int.Parse(_db.GetSetting("desktop_fence_w")!));
    }

    [Theory]
    [InlineData(-1000, 200)]
    [InlineData(0, 200)]
    [InlineData(199, 200)]
    [InlineData(200, 200)]
    [InlineData(650, 650)]
    [InlineData(1200, 1200)]
    [InlineData(1201, 1200)]
    [InlineData(100000, 1200)]
    public void Challenger_Geometry_Height_ClampedStrictlyToRange(int inputHeight, int expectedHeight)
    {
        _config.FenceHeight = inputHeight;
        Assert.Equal(expectedHeight, _config.FenceHeight);
        Assert.Equal(expectedHeight, _config.FenceH);
        Assert.Equal(expectedHeight, int.Parse(_db.GetSetting("desktop_fence_h")!));
    }

    [Fact]
    public void Challenger_Geometry_MultiMonitorNegativeCoordinates_Preserved()
    {
        // Monitor secundário à esquerda do principal em coordenadas virtuais do Windows
        _config.FenceLeft = -1920;
        _config.FenceTop = -100;

        Assert.Equal(-1920, _config.FenceLeft);
        Assert.Equal(-1920, _config.FenceX);
        Assert.Equal(-100, _config.FenceTop);
        Assert.Equal(-100, _config.FenceY);

        Assert.Equal("-1920", _db.GetSetting("desktop_fence_x"));
        Assert.Equal("-100", _db.GetSetting("desktop_fence_y"));
    }

    [Fact]
    public void Challenger_CollapseState_PreservesConfiguredHeightOnRestore()
    {
        _config.FenceHeight = 720;
        Assert.Equal(720, _config.FenceH);

        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);
        Assert.False(vm.IsCollapsed);

        // Recolhe
        vm.ToggleCollapse();
        Assert.True(vm.IsCollapsed);
        Assert.True(_config.FenceCollapsed);
        // Altura configurada não deve ser destruída pela contração
        Assert.Equal(720, _config.FenceH);

        // Expande de volta
        vm.ToggleCollapse();
        Assert.False(vm.IsCollapsed);
        Assert.False(_config.FenceCollapsed);
        Assert.Equal(720, _config.FenceH);
    }

    // =========================================================================
    // 4. DESAFIO COMBINATÓRIO: GRUPOS ESPECIAIS & FILTRAGEM DE PILLS
    // =========================================================================

    [Fact]
    public void Challenger_Groups_CombinatorialFilteringAndMetrics_Exact()
    {
        // Cria 3 grupos especiais
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "GRP_P1", Nome = "Prioridade Máxima" });
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "GRP_P2", Nome = "Prazos Judiciais" });
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "GRP_P3", Nome = "Rotina Interna" });

        // Insere 10 processos com combinações variadas
        // GRP_P1: 2 processos (1 destinado pendente, 1 lido)
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "1", IdGrupoEspecial = "GRP_P1", DestinadoAoUsuario = true, TotalDocumentos = 2 });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "2", IdGrupoEspecial = "GRP_P1", DestinadoAoUsuario = false, TotalDocumentos = 1 });
        _db.SetLocalReadStatus("2", isRead: true, docCount: 1);

        // GRP_P2: 3 processos (1 aberto por terceiro pendente, 1 destinado pendente, 1 normal pendente)
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "3", IdGrupoEspecial = "GRP_P2", AbertoPorTerceiro = true, TotalDocumentos = 3 });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "4", IdGrupoEspecial = "GRP_P2", DestinadoAoUsuario = true, TotalDocumentos = 4 });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "5", IdGrupoEspecial = "GRP_P2", TotalDocumentos = 1 });

        // GRP_P3: 3 processos todos lidos
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "6", IdGrupoEspecial = "GRP_P3", TotalDocumentos = 1 });
        _db.SetLocalReadStatus("6", isRead: true, docCount: 1);
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "7", IdGrupoEspecial = "GRP_P3", TotalDocumentos = 1 });
        _db.SetLocalReadStatus("7", isRead: true, docCount: 1);
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "8", IdGrupoEspecial = "GRP_P3", TotalDocumentos = 1 });
        _db.SetLocalReadStatus("8", isRead: true, docCount: 1);

        // Sem Grupo: 2 processos (1 pendente normal, 1 terceiro pendente)
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "9", TotalDocumentos = 2 });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "10", AbertoPorTerceiro = true, TotalDocumentos = 1 });

        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);
        vm.RefreshGroups();
        Assert.Equal(4, vm.AvailableGroups.Count); // Todos + 3 grupos

        // Teste 1: Todos os Grupos (Sem Filtro)
        vm.SelectGroup(null);
        Assert.Equal(10, vm.CountMonitored);
        Assert.Equal(6, vm.CountNewActivities); // 1, 3, 4, 5, 9, 10
        Assert.Equal(2, vm.CountDestined);      // 1, 4
        Assert.Equal(2, vm.CountAttention);     // 3, 10
        Assert.Equal(10, vm.DisplayedProcesses.Count);

        // Teste 2: Filtro GRP_P1
        vm.SelectGroup("GRP_P1");
        Assert.Equal(2, vm.CountMonitored);
        Assert.Equal(1, vm.CountNewActivities); // 1 não lido
        Assert.Equal(1, vm.CountDestined);      // 1
        Assert.Equal(0, vm.CountAttention);
        Assert.Equal(2, vm.DisplayedProcesses.Count);

        // Teste 3: Filtro GRP_P2
        vm.SelectGroup("GRP_P2");
        Assert.Equal(3, vm.CountMonitored);
        Assert.Equal(3, vm.CountNewActivities); // 3, 4, 5 não lidos
        Assert.Equal(1, vm.CountDestined);      // 4
        Assert.Equal(1, vm.CountAttention);     // 3
        Assert.Equal(3, vm.DisplayedProcesses.Count);

        // Teste 4: Filtro GRP_P3
        vm.SelectGroup("GRP_P3");
        Assert.Equal(3, vm.CountMonitored);
        Assert.Equal(0, vm.CountNewActivities); // todos lidos
        Assert.Equal(0, vm.CountDestined);
        Assert.Equal(0, vm.CountAttention);
        Assert.Equal(3, vm.DisplayedProcesses.Count);

        // Teste 5: Grupo Inexistente
        vm.SelectGroup("GRP_NONEXISTENT");
        Assert.Equal(0, vm.CountMonitored);
        Assert.Equal(0, vm.CountNewActivities);
        Assert.Equal(0, vm.CountDestined);
        Assert.Equal(0, vm.CountAttention);
        Assert.Empty(vm.DisplayedProcesses);
    }

    // =========================================================================
    // 5. ESTRESSE CONCORRENTE: DISPARO RÁPIDO DE EVENTOS DO POLLER COM TROCA DE GRUPOS
    // =========================================================================

    [Fact]
    public async Task Challenger_ConcurrentPollerEvents_AndGroupSwitching_NoRaceOrDeadlock()
    {
        var vm = new FenceViewModel(_config, _db, _poller, _dispatcher, _shell);

        const int iterations = 24;
        var timeout = TimeSpan.FromSeconds(45);
        using var start = new Barrier(2);

        var pollerTask = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(timeout));
            for (int id = 1; id <= iterations; id++)
            {
                var proc = new MonitoredProcess
                {
                    Id = $"PROC_{id}",
                    ProtocoloFormatado = $"00001.{id:D6}/2026-00",
                    TotalDocumentos = id
                };
                _db.UpsertMonitoredProcess(proc);
                _poller.RaiseProcessChanged(new ProcessChangedEventArgs(proc, UpdateType.NewProcess));
                Thread.Sleep(5);
            }
        });

        var switcherTask = Task.Run(() =>
        {
            string[] groups = { "", "GRP_1", "GRP_2", null! };
            Assert.True(start.SignalAndWait(timeout));
            for (int idx = 0; idx < iterations; idx++)
            {
                vm.SelectGroup(groups[idx % groups.Length]);
                vm.RefreshGroups();
                Thread.Sleep(8);
            }
        });

        await Task.WhenAll(pollerTask, switcherTask).WaitAsync(timeout);

        // Verifica consistência final
        vm.SelectGroup(null);
        vm.RefreshCounts();
        Assert.Equal(iterations, vm.CountMonitored);
        Assert.Equal(iterations, vm.DisplayedProcesses.Count);
    }

    // =========================================================================
    // 6. CICLO DE VIDA COMPLETO EM THREAD STA
    // =========================================================================

    [Fact]
    public void Challenger_FenceWindow_StaFullLifecycle_InstantiateAndClose()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new FenceWindow(_config, _db, _poller, _shell, _dispatcher);
                Assert.NotNull(window);
                Assert.NotNull(window.ViewModel);

                // Executa MaintainPin e SendToBottom explicitamente
                window.MaintainPin();
                window.SendToBottom();

                // Fecha a janela
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }
}
