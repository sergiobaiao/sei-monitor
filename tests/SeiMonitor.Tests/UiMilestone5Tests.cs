using System;
using System.Collections.Generic;
using System.Linq;
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
/// Suíte de testes unitários herméticos para o Milestone 5 (Painel Principal, Bandeja do Sistema e Instância Única).
/// Cobre 10 grupos funcionais e arquiteturais:
/// 1. SingleInstanceController & IPC
/// 2. TrayIconManager & Callbacks
/// 3. MainWindowViewModel — Tabs, KPIs e Reatividade
/// 4. MainWindowViewModel — Filtros e Busca da Caixa
/// 5. MainWindowViewModel — Comandos (Marcar Lido, Sincronizar)
/// 6. MainWindowViewModel — Acompanhamento Especial
/// 7. MainWindowViewModel — Configurações e Credenciais
/// 8. MainWindowViewModel — Logs Operacionais
/// 9. DetailWindowViewModel — Documentos, Andamentos e Terceiros
/// 10. Telas WPF em STA Thread (MainWindow, DetailWindow, Theme)
/// </summary>
[SupportedOSPlatform("windows")]
public class UiMilestone5Tests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly MockSeiPoller _mockPoller;
    private readonly MockSeiClientForM5 _mockClient;
    private readonly ImmediateDispatcher _dispatcher;

    public UiMilestone5Tests()
    {
        _db = new Database(":memory:");
        _db.InitializeDatabase();
        _config = new Config(_db);
        _mockPoller = new MockSeiPoller();
        _mockClient = new MockSeiClientForM5();
        _dispatcher = new ImmediateDispatcher();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // =========================================================================
    // GRUPO 1: SingleInstanceController & IPC
    // =========================================================================

    [Fact]
    public void G1_SingleInstance_FirstInstance_AcquiresSuccessfully()
    {
        var uniqueMutex = $@"Local\Test_Mutex_{Guid.NewGuid():N}";
        using var controller = new SingleInstanceController(uniqueMutex);

        bool acquired = controller.TryAcquire();

        Assert.True(acquired);
        Assert.True(controller.IsFirstInstance);
    }

    [Fact]
    public void G1_SingleInstance_SecondInstance_FailsToAcquire()
    {
        var uniqueMutex = $@"Local\Test_Mutex_{Guid.NewGuid():N}";
        using var first = new SingleInstanceController(uniqueMutex);
        Assert.True(first.TryAcquire());

        using var second = new SingleInstanceController(uniqueMutex);
        bool secondAcquired = second.TryAcquire();

        Assert.False(secondAcquired);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void G1_SingleInstance_SignalExistingInstance_TriggersCallback()
    {
        var uniqueMutex = $@"Local\Test_Mutex_{Guid.NewGuid():N}";
        var uniqueEvent = $@"Local\Test_Event_{Guid.NewGuid():N}";

        using var first = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.True(first.TryAcquire());

        var signalReceived = new ManualResetEventSlim(false);
        first.StartListeningForShowSignal(() => signalReceived.Set());

        // Segunda instância sinaliza
        bool signaled = SingleInstanceController.SignalExistingInstance(uniqueEvent);
        Assert.True(signaled);

        bool received = signalReceived.Wait(TimeSpan.FromSeconds(3));
        Assert.True(received);
    }

    [Fact]
    public void G1_SingleInstance_Dispose_ReleasesMutex()
    {
        var uniqueMutex = $@"Local\Test_Mutex_{Guid.NewGuid():N}";
        var first = new SingleInstanceController(uniqueMutex);
        Assert.True(first.TryAcquire());

        // Disposição da 1ª instância libera o Mutex
        first.Dispose();

        using var second = new SingleInstanceController(uniqueMutex);
        bool acquired = second.TryAcquire();
        Assert.True(acquired);
        Assert.True(second.IsFirstInstance);
    }

    [Fact]
    public void G1_SingleInstance_DefaultNames_AreLocalScope()
    {
        Assert.StartsWith(@"Local\", SingleInstanceController.DefaultMutexName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(@"Local\", SingleInstanceController.DefaultShowEventName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void G1_SingleInstance_Aliases_InitializeAndWaitForShowSignal()
    {
        var uniqueMutex = $@"Local\Test_Mutex_{Guid.NewGuid():N}";
        var uniqueEvent = $@"Local\Test_Event_{Guid.NewGuid():N}";

        using var controller = new SingleInstanceController(uniqueMutex, uniqueEvent);
        Assert.True(controller.Initialize());

        var received = new ManualResetEventSlim(false);
        controller.WaitForShowSignal(() => received.Set());

        SingleInstanceController.SignalExistingInstance(uniqueEvent);
        Assert.True(received.Wait(TimeSpan.FromSeconds(3)));
    }

    // =========================================================================
    // GRUPO 2: TrayIconManager & Callbacks
    // =========================================================================

    [Fact]
    public void G2_TrayIconCallbacks_CanInvokeAllSixActions()
    {
        bool openMain = false, sync = false, fence = false, taskbar = false, settings = false, exit = false;

        var callbacks = new TrayIconCallbacks
        {
            OpenMainWindow = () => openMain = true,
            TriggerSync = () => sync = true,
            ToggleDesktopFence = () => fence = true,
            ToggleTaskbarWidget = () => taskbar = true,
            OpenSettings = () => settings = true,
            ExitApplication = () => exit = true
        };

        callbacks.OpenMainWindow();
        callbacks.TriggerSync();
        callbacks.ToggleDesktopFence();
        callbacks.ToggleTaskbarWidget();
        callbacks.OpenSettings();
        callbacks.ExitApplication();

        Assert.True(openMain);
        Assert.True(sync);
        Assert.True(fence);
        Assert.True(taskbar);
        Assert.True(settings);
        Assert.True(exit);
    }

    [Fact]
    public void G2_TrayIconManager_CanInitializeAndDisposeWithoutGhostIcon()
    {
        var callbacks = new TrayIconCallbacks();
        using var manager = new TrayIconManager(callbacks, _dispatcher);

        manager.Initialize();
        Assert.True(manager.IsVisible);

        manager.UpdateTooltip("SEI Monitor Ativo");
        manager.Dispose();

        Assert.False(manager.IsVisible);
    }

    [Fact]
    public void G2_TrayIconManager_UpdateTooltip_LimitsLength()
    {
        var callbacks = new TrayIconCallbacks();
        using var manager = new TrayIconManager(callbacks, _dispatcher);
        manager.Initialize();

        string veryLong = new string('A', 150);
        manager.UpdateTooltip(veryLong);
        // Não deve estourar ArgumentException no Win32
        Assert.True(manager.IsVisible);
    }

    // =========================================================================
    // GRUPO 3: MainWindowViewModel — Tabs, KPIs e Reatividade
    // =========================================================================

    [Fact]
    public void G3_MainWindowViewModel_InitialTabsAndCounters()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        Assert.Equal(MainTab.Overview, vm.SelectedTab);
        Assert.Equal(0, vm.SelectedTabIndex);

        vm.SelectedTab = MainTab.Inbox;
        Assert.Equal(1, vm.SelectedTabIndex);

        vm.SelectedTabIndex = 3;
        Assert.Equal(MainTab.Settings, vm.SelectedTab);
    }

    [Fact]
    public void G3_MainWindowViewModel_CalculatesKpisAccurately()
    {
        // 1 Processo de Entrada não lido, destinado a você e aberto por terceiro
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "101",
            ProtocoloFormatado = "00001.000101/2026-01",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = true,
            AbertoPorTerceiro = true,
            IsReadLocally = false
        });

        // 1 Processo de Acompanhamento lido
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "102",
            ProtocoloFormatado = "00001.000102/2026-02",
            Origem = "ACOMPANHAMENTO",
            DestinadoAoUsuario = false,
            AbertoPorTerceiro = false,
            IsReadLocally = true
        });
        _db.SetLocalReadStatus("102", true);

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        Assert.Equal(2, vm.CountTotal);
        Assert.Equal(1, vm.CountUnread);
        Assert.Equal(1, vm.CountDestined);
        Assert.Equal(1, vm.CountAttention);
        Assert.Equal(1, vm.CountCaixa);
        Assert.Equal(1, vm.CountAcompanhamento);
    }

    [Fact]
    public void G3_MainWindowViewModel_PollerEvents_TriggerReactiveUpdate()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        Assert.Equal(0, vm.CountTotal);

        var proc103 = new MonitoredProcess
        {
            Id = "103",
            ProtocoloFormatado = "00001.000103/2026-03",
            Origem = "CAIXA_ENTRADA",
            IsReadLocally = false
        };
        _db.UpsertMonitoredProcess(proc103);

        _mockPoller.RaiseProcessChanged(new ProcessChangedEventArgs(proc103, UpdateType.NewProcess));

        Assert.Equal(1, vm.CountTotal);
        Assert.Equal(1, vm.CountUnread);

        _mockPoller.RaiseSyncCompleted(new SyncResult(1, 1, 0, DateTime.UtcNow, true));
        Assert.Contains("Atualizado:", vm.LastSyncText);
    }

    // =========================================================================
    // GRUPO 4: MainWindowViewModel — Filtros e Busca da Caixa de Entrada
    // =========================================================================

    [Fact]
    public void G4_MainWindowViewModel_FilterAll_ReturnsAllInboxProcesses()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "201", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA" });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "202", ProtocoloFormatado = "002", Origem = "ACOMPANHAMENTO" });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        vm.SelectedFilter = ProcessFilterMode.All;

        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("201", vm.DisplayedInboxProcesses[0].Id);
    }

    [Fact]
    public void G4_MainWindowViewModel_FilterDestined_ReturnsOnlyDestined()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "201", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA", DestinadoAoUsuario = true });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "202", ProtocoloFormatado = "002", Origem = "CAIXA_ENTRADA", DestinadoAoUsuario = false });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        vm.SelectedFilter = ProcessFilterMode.DestinedOnly;

        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("201", vm.DisplayedInboxProcesses[0].Id);
    }

    [Fact]
    public void G4_MainWindowViewModel_FilterThirdParty_ReturnsOnlyThirdParty()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "201", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA", AbertoPorTerceiro = true });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "202", ProtocoloFormatado = "002", Origem = "CAIXA_ENTRADA", AbertoPorTerceiro = false });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        vm.SelectedFilter = ProcessFilterMode.ThirdPartyOnly;

        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("201", vm.DisplayedInboxProcesses[0].Id);
    }

    [Fact]
    public void G4_MainWindowViewModel_FilterUnread_ReturnsOnlyUnread()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "201", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA", IsReadLocally = false });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "202", ProtocoloFormatado = "002", Origem = "CAIXA_ENTRADA", IsReadLocally = true });
        _db.SetLocalReadStatus("202", true);

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        vm.SelectedFilter = ProcessFilterMode.UnreadOnly;

        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("201", vm.DisplayedInboxProcesses[0].Id);
    }

    [Fact]
    public void G4_MainWindowViewModel_SearchText_FiltersByProtocoloOrDescOrUser()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "201",
            ProtocoloFormatado = "00001.000201/2026-01",
            Descricao = "Aquisição de computadores",
            UltimoUsuarioNome = "joao.silva",
            Origem = "CAIXA_ENTRADA"
        });
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "202",
            ProtocoloFormatado = "00001.000202/2026-02",
            Descricao = "Contratação de internet",
            UltimoUsuarioNome = "maria.santos",
            Origem = "CAIXA_ENTRADA"
        });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.SearchText = "computadores";
        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("201", vm.DisplayedInboxProcesses[0].Id);

        vm.SearchText = "maria";
        Assert.Single(vm.DisplayedInboxProcesses);
        Assert.Equal("202", vm.DisplayedInboxProcesses[0].Id);

        vm.SearchText = "99999";
        Assert.Empty(vm.DisplayedInboxProcesses);
    }

    [Fact]
    public void G4_ProcessItemViewModel_WrapperProperties_ReflectModel()
    {
        var proc = new MonitoredProcess
        {
            Id = "205",
            ProtocoloFormatado = "00001.000205/2026-05",
            DestinadoAoUsuario = true,
            AbertoPorTerceiro = true,
            IsReadLocally = false
        };

        var itemVm = new ProcessItemViewModel(proc);

        Assert.True(itemVm.HasDestinedBadge);
        Assert.True(itemVm.HasAttentionBadge);
        Assert.True(itemVm.IsUnread);
        Assert.Equal("Não Lido", itemVm.StatusBadgeText);

        itemVm.IsReadLocally = true;
        Assert.False(itemVm.IsUnread);
        Assert.Equal("Lido", itemVm.StatusBadgeText);
    }

    // =========================================================================
    // GRUPO 5: MainWindowViewModel — Comandos (Marcar Lido, Sincronizar)
    // =========================================================================

    [Fact]
    public void G5_MainWindowViewModel_MarkAsRead_UpdatesDatabaseAndObservableItem()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "301", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA", IsReadLocally = false });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        Assert.Equal(1, vm.CountUnread);
        Assert.True(vm.DisplayedInboxProcesses[0].IsUnread);

        vm.MarkAsRead("301");

        Assert.Equal(0, vm.CountUnread);
        Assert.False(vm.DisplayedInboxProcesses[0].IsUnread);
        Assert.True(_db.GetLocalReadStatus("301"));
    }

    [Fact]
    public void G5_MainWindowViewModel_MarkAllAsRead_MarksAllInboxProcesses()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "301", ProtocoloFormatado = "001", Origem = "CAIXA_ENTRADA", IsReadLocally = false });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "302", ProtocoloFormatado = "002", Origem = "CAIXA_ENTRADA", IsReadLocally = false });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        Assert.Equal(2, vm.CountUnread);

        vm.MarkAllAsRead();

        Assert.Equal(0, vm.CountUnread);
        Assert.True(_db.GetLocalReadStatus("301"));
        Assert.True(_db.GetLocalReadStatus("302"));
    }

    [Fact]
    public async Task G5_MainWindowViewModel_SyncNowCommand_ExecutesPollerSync()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        await vm.SyncNowAsync();

        Assert.Contains("Atualizado:", vm.LastSyncText);
        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public void G5_RelayCommand_CanExecuteAndExecute()
    {
        bool executed = false;
        bool canExec = true;

        var cmd = new RelayCommand(_ => executed = true, _ => canExec);

        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.True(executed);

        canExec = false;
        Assert.False(cmd.CanExecute(null));
    }

    [Fact]
    public async Task G5_AsyncRelayCommand_PreventsConcurrentExecution()
    {
        var tcs = new TaskCompletionSource();
        int executions = 0;

        var cmd = new AsyncRelayCommand(async () =>
        {
            executions++;
            await tcs.Task;
        });

        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);

        // Enquanto estiver executando, CanExecute deve ser falso
        Assert.False(cmd.CanExecute(null));

        tcs.SetResult();
        await Task.Yield();

        // Após concluir, volta a ser executável
        Assert.Equal(1, executions);
        Assert.True(cmd.CanExecute(null));
    }

    // =========================================================================
    // GRUPO 6: MainWindowViewModel — Acompanhamento Especial
    // =========================================================================

    [Fact]
    public void G6_MainWindowViewModel_AddSpecialGroup_PersistsToDatabase()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        int initialCount = vm.AvailableGroups.Count;
        vm.AddSpecialGroup("Contratos 2026");

        Assert.Equal(initialCount + 1, vm.AvailableGroups.Count);
        Assert.Contains(vm.AvailableGroups, g => g.Nome == "Contratos 2026");

        var dbGroups = _db.ListSpecialGroups();
        Assert.Contains(dbGroups, g => g.Nome == "Contratos 2026");
    }

    [Fact]
    public void G6_MainWindowViewModel_DeleteSpecialGroup_RemovesFromDatabase()
    {
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "grp-del", Nome = "Grupo Temporário" });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        Assert.Contains(vm.AvailableGroups, g => g.Id == "grp-del");

        vm.DeleteSpecialGroup("grp-del");

        Assert.DoesNotContain(vm.AvailableGroups, g => g.Id == "grp-del");
    }

    [Fact]
    public void G6_MainWindowViewModel_FilterBySpecialGroup_UpdatesMonitoringList()
    {
        _db.UpsertSpecialGroup(new SpecialGroup { Id = "grp-1", Nome = "TI" });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "401", ProtocoloFormatado = "001", Origem = "ACOMPANHAMENTO", IdGrupoEspecial = "grp-1" });
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "402", ProtocoloFormatado = "002", Origem = "ACOMPANHAMENTO", IdGrupoEspecial = null });

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.SelectedMonitoringGroupId = "grp-1";
        Assert.Single(vm.DisplayedMonitoringProcesses);
        Assert.Equal("401", vm.DisplayedMonitoringProcesses[0].Id);

        vm.SelectedMonitoringGroupId = null;
        Assert.Equal(2, vm.DisplayedMonitoringProcesses.Count);
    }

    // =========================================================================
    // GRUPO 7: MainWindowViewModel — Configurações e Credenciais
    // =========================================================================

    [Fact]
    public void G7_MainWindowViewModel_SaveConfig_ValidatesWsseiUrl()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.SeiWsseiBaseUrl = "invalid-not-url";
        vm.SaveConfig();

        Assert.True(vm.HasConfigError);
        Assert.Contains("URL do WSSEI v2 inválida", vm.ConfigStatusMessage);
    }

    [Fact]
    public void G7_MainWindowViewModel_SaveConfig_ValidatesCheckInterval()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.SeiWsseiBaseUrl = "https://sei.pi.gov.br/wssei";
        vm.CheckIntervalMinutes = 0; // Inválido (< 1)
        vm.SaveConfig();

        Assert.True(vm.HasConfigError);
        Assert.Contains("no mínimo 1 minuto", vm.ConfigStatusMessage);
    }

    [Fact]
    public void G7_MainWindowViewModel_SaveConfig_PersistsValidSettings()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.SeiWsseiBaseUrl = "https://sei.teste.gov.br";
        vm.SeiUnidadeSigla = "GINFO-TEST";
        vm.CheckIntervalMinutes = 10;
        vm.DesktopFenceEnabled = false;

        vm.SaveConfig();

        Assert.False(vm.HasConfigError);
        Assert.Equal("https://sei.teste.gov.br", _config.SeiWsseiBaseUrl);
        Assert.Equal("GINFO-TEST", _config.SeiUnidadeSigla);
        Assert.Equal(10, _config.CheckIntervalMinutes);
        Assert.False(_config.DesktopFenceEnabled);
    }

    [Fact]
    public void G7_MainWindowViewModel_LoadConfigProperties_ReflectsConfig()
    {
        _config.SeiUnidadeSigla = "ASJUR";
        _config.CheckIntervalMinutes = 15;

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        Assert.Equal("ASJUR", vm.SeiUnidadeSigla);
        Assert.Equal(15, vm.CheckIntervalMinutes);
    }

    // =========================================================================
    // GRUPO 8: MainWindowViewModel — Logs Operacionais
    // =========================================================================

    [Fact]
    public void G8_MainWindowViewModel_LoadsLogsFromDatabase()
    {
        _db.Log("INFO", "Aplicação inicializada com sucesso");
        _db.Log("WARN", "Latência detectada no endpoint");
        _db.Log("ERROR", "Falha de conexão com SEI");

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        Assert.Equal(3, vm.Logs.Count);
    }

    [Fact]
    public void G8_MainWindowViewModel_FilterLogsByLevel()
    {
        _db.Log("INFO", "Mensagem informativa");
        _db.Log("ERROR", "Erro crítico 1");
        _db.Log("ERROR", "Erro crítico 2");

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.LogFilterLevel = "ERROR";
        Assert.Equal(2, vm.Logs.Count);
        Assert.All(vm.Logs, l => Assert.Equal("ERROR", l.Level));
    }

    [Fact]
    public void G8_MainWindowViewModel_SearchLogsByText()
    {
        _db.Log("INFO", "Sincronização concluída para unidade GINFO");
        _db.Log("INFO", "Sincronização concluída para unidade GAD");

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);

        vm.LogSearchText = "GINFO";
        Assert.Single(vm.Logs);
        Assert.Contains("GINFO", vm.Logs[0].Message);
    }

    [Fact]
    public void G8_MainWindowViewModel_ClearLogs_ClearsDatabaseAndCollection()
    {
        _db.Log("INFO", "Log para deletar");

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher, _mockClient);
        Assert.Single(vm.Logs);

        vm.ClearLogs();

        Assert.Empty(vm.Logs);
        Assert.Empty(_db.GetLogs());
    }

    // =========================================================================
    // GRUPO 9: DetailWindowViewModel — Documentos, Andamentos e Terceiros
    // =========================================================================

    [Fact]
    public async Task G9_DetailWindowViewModel_LoadsCachedMetadataFromDatabase()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "501",
            ProtocoloFormatado = "00001.000501/2026-01",
            TipoProcesso = "Licitação e Contratos",
            Descricao = "Aquisição de servidores de rede",
            DestinadoAoUsuario = true,
            AbertoPorTerceiro = false,
            IsReadLocally = true
        });
        _db.SetLocalReadStatus("501", true);

        var vm = new DetailWindowViewModel(_db, _config, client: null, _dispatcher);
        await vm.LoadDetailsAsync("501");

        Assert.Equal("501", vm.ProcessId);
        Assert.Equal("00001.000501/2026-01", vm.ProtocoloFormatado);
        Assert.Equal("Licitação e Contratos", vm.TipoProcesso);
        Assert.True(vm.DestinadoAoUsuario);
        Assert.True(vm.IsReadLocally);
        Assert.Equal("Lido", vm.ReadStatusText);
    }

    [Fact]
    public void G9_DetailWindowViewModel_ToggleReadStatus_FlipsReadStatus()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "502", ProtocoloFormatado = "002", IsReadLocally = false });

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        _ = vm.LoadDetailsAsync("502");

        Assert.False(vm.IsReadLocally);

        vm.ToggleReadStatus();
        Assert.True(vm.IsReadLocally);
        Assert.True(_db.GetLocalReadStatus("502"));

        vm.ToggleReadStatus();
        Assert.False(vm.IsReadLocally);
        Assert.False(_db.GetLocalReadStatus("502"));
    }

    [Fact]
    public void G9_DetailWindowViewModel_MarkAsReadAndUnread()
    {
        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = "503", ProtocoloFormatado = "003", IsReadLocally = false });

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        _ = vm.LoadDetailsAsync("503");

        vm.MarkAsRead();
        Assert.True(vm.IsReadLocally);
        Assert.True(_db.GetLocalReadStatus("503"));

        vm.MarkAsUnread();
        Assert.False(vm.IsReadLocally);
        Assert.False(_db.GetLocalReadStatus("503"));
    }

    [Fact]
    public async Task G9_DetailWindowViewModel_LoadsDocumentsAndActivitiesFromClient()
    {
        _config.SeiUsuarioNome = "carlos.silva";
        _mockClient.DetailToReturn = new ProcessDetail(
            IdProcedimento: "504",
            ProtocoloFormatado: "00001.000504/2026-04",
            Descricao: "Processo teste com documentos",
            TipoProcesso: "Administrativo",
            UnidadeSigla: "GINFO",
            NivelAcessoGlobal: 0,
            UnidadesAbertas: new List<string> { "GINFO" },
            Documentos: new List<SeiDocumentItem>
            {
                new("d1", "001", "Despacho 001", "Despacho", "application/pdf", "Despacho", "1", true, false, false, "2026-10-30"),
                new("d2", "002", "Nota Técnica 002", "Nota", "application/pdf", "Nota", "1", true, false, true, "2026-10-30")
            },
            Atividades: new List<SeiActivityItem>
            {
                new("a1", "2026-10-30", "10:00", "Processo aberto na unidade", "GINFO", "carlos.silva"),
                new("a2", "2026-10-30", "11:00", "Visualização do processo", "GINFO", "maria.colega")
            }
        );

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        await vm.LoadDetailsAsync("504");

        Assert.Equal(2, vm.Documents.Count);
        Assert.Equal(2, vm.Activities.Count);
        Assert.Equal(2, vm.TimelineItems.Count);

        // Documento sigiloso
        Assert.True(vm.Documents[1].Sigiloso);
    }

    [Fact]
    public async Task G9_DetailWindowViewModel_DetectsThirdPartyWithoutDespacho()
    {
        _config.SeiUsuarioNome = "carlos.silva";
        _mockClient.DetailToReturn = new ProcessDetail(
            IdProcedimento: "505",
            ProtocoloFormatado: "00001.000505/2026-05",
            Descricao: "Auditoria",
            TipoProcesso: "Auditoria",
            UnidadeSigla: "GINFO",
            NivelAcessoGlobal: 0,
            UnidadesAbertas: new List<string> { "GINFO" },
            Documentos: new List<SeiDocumentItem>(),
            Atividades: new List<SeiActivityItem>
            {
                new("a1", "2026-10-30", "09:30", "Processo aberto na unidade", "GINFO", "outro.servidor")
            }
        );

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        await vm.LoadDetailsAsync("505");

        Assert.Single(vm.Activities);
        Assert.True(vm.Activities[0].IsThirdPartyWithoutDespacho);
        Assert.False(vm.Activities[0].IsFromCurrentUser);

        Assert.Single(vm.TimelineItems);
        Assert.True(vm.TimelineItems[0].IsOpenedByThirdParty);
        Assert.Equal("⚠️", vm.TimelineItems[0].IconMarker);
    }

    [Fact]
    public async Task G9_DetailWindowViewModel_DetectsCurrentUserActivity()
    {
        _config.SeiUsuarioNome = "carlos.silva";
        _mockClient.DetailToReturn = new ProcessDetail(
            IdProcedimento: "506",
            ProtocoloFormatado: "00001.000506/2026-06",
            Descricao: "Teste Usuário",
            TipoProcesso: "Geral",
            UnidadeSigla: "GINFO",
            NivelAcessoGlobal: 0,
            UnidadesAbertas: new List<string> { "GINFO" },
            Documentos: new List<SeiDocumentItem>(),
            Atividades: new List<SeiActivityItem>
            {
                new("a1", "2026-10-30", "14:00", "Juntada de despacho", "GINFO", "carlos.silva")
            }
        );

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        await vm.LoadDetailsAsync("506");

        Assert.True(vm.Activities[0].IsFromCurrentUser);
        Assert.False(vm.Activities[0].IsThirdPartyWithoutDespacho);
        Assert.False(vm.TimelineItems[0].IsOpenedByThirdParty);
        Assert.Equal("●", vm.TimelineItems[0].IconMarker);
    }

    [Fact]
    public async Task G9_DetailWindowViewModel_HandlesSeiExceptionGracefully()
    {
        _mockClient.ShouldThrowSeiException = true;

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        await vm.LoadDetailsAsync("507");

        Assert.True(vm.HasError);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task G9_DetailWindowViewModel_SortsActivitiesChronologicallyDescending()
    {
        _config.SeiUsuarioNome = "carlos.silva";
        _mockClient.DetailToReturn = new ProcessDetail(
            IdProcedimento: "508",
            ProtocoloFormatado: "00001.000508/2026-08",
            Descricao: "Ordenação",
            TipoProcesso: "Geral",
            UnidadeSigla: "GINFO",
            NivelAcessoGlobal: 0,
            UnidadesAbertas: new List<string> { "GINFO" },
            Documentos: new List<SeiDocumentItem>(),
            Atividades: new List<SeiActivityItem>
            {
                new("a1", "2026-10-28", "08:00", "Atividade Antiga", "GINFO", "usuario"),
                new("a2", "2026-10-30", "17:00", "Atividade Mais Recente", "GINFO", "usuario"),
                new("a3", "2026-10-30", "09:00", "Atividade Intermediária", "GINFO", "usuario")
            }
        );

        var vm = new DetailWindowViewModel(_db, _config, _mockClient, _dispatcher);
        await vm.LoadDetailsAsync("508");

        Assert.Equal("a2", vm.Activities[0].Id);
        Assert.Equal("a3", vm.Activities[1].Id);
        Assert.Equal("a1", vm.Activities[2].Id);
    }

    [Fact]
    public void G9_ActivityTimelineItem_StyleProperties_AmberWhenThirdParty()
    {
        var itemThirdParty = new ActivityTimelineItem
        {
            IsOpenedByThirdParty = true
        };

        Assert.Equal("⚠️", itemThirdParty.IconMarker);
        Assert.Equal(new Thickness(2), itemThirdParty.BorderThickness);

        var itemNormal = new ActivityTimelineItem
        {
            IsOpenedByThirdParty = false
        };

        Assert.Equal("●", itemNormal.IconMarker);
        Assert.Equal(new Thickness(1), itemNormal.BorderThickness);
    }

    // =========================================================================
    // GRUPO 10: Telas WPF em STA Thread (MainWindow, DetailWindow, Theme)
    // =========================================================================

    [Fact]
    public void G10_MainWindow_CanInstantiateInStaThread()
    {
        RunInSta(() =>
        {
            var mainWin = new MainWindow(_config, _db, _mockPoller, _mockClient, _dispatcher);
            Assert.NotNull(mainWin.ViewModel);

            mainWin.RefreshAll();
            mainWin.SelectSettingsTab();
            Assert.Equal(MainTab.Settings, mainWin.ViewModel.SelectedTab);
        });
    }

    [Fact]
    public void G10_DetailWindow_CanInstantiateInStaThread()
    {
        RunInSta(() =>
        {
            var proc = new MonitoredProcess
            {
                Id = "601",
                ProtocoloFormatado = "00001.000601/2026-01",
                Descricao = "Teste STA Detail",
                TipoProcesso = "Administrativo"
            };

            var detailWin = new DetailWindow(proc, _config, _db, _mockClient, _dispatcher);
            Assert.NotNull(detailWin.ViewModel);
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
// Mock ISeiClient para Testes Herméticos do Marco 5
// =============================================================================

internal sealed class MockSeiClientForM5 : ISeiClient
{
    public ProcessDetail? DetailToReturn { get; set; }
    public bool ShouldThrowSeiException { get; set; }

    public Task<SeiAuthResult> AuthenticateAsync(bool forceRenew = false, CancellationToken ct = default)
    {
        return Task.FromResult(new SeiAuthResult("mock-token-123", "WSSEI", DateTime.UtcNow));
    }

    public Task<ProcessListResult> SearchProcessesAsync(string unidade, int start = 0, int limit = 50, CancellationToken ct = default)
    {
        return Task.FromResult(new ProcessListResult(0, new List<SeiProcessItem>()));
    }

    public Task<LightProcessSummary> GetLightSummaryAsync(string idProcedimento, string unidade, CancellationToken ct = default)
    {
        return Task.FromResult(new LightProcessSummary(idProcedimento, 1, DateTime.UtcNow.ToString("o"), "Atividade", "usuario", "001"));
    }

    public Task<ProcessDetail> GetProcessDetailsAsync(string idProcedimento, string unidade, CancellationToken ct = default)
    {
        if (ShouldThrowSeiException)
        {
            throw new SeiException("SEI_INDISPONIVEL");
        }

        if (DetailToReturn != null)
        {
            return Task.FromResult(DetailToReturn);
        }

        return Task.FromResult(new ProcessDetail(
            IdProcedimento: idProcedimento,
            ProtocoloFormatado: $"00001.{idProcedimento}/2026-00",
            Descricao: "Processo Padrão Mock",
            TipoProcesso: "Geral",
            UnidadeSigla: "GINFO",
            NivelAcessoGlobal: 0,
            UnidadesAbertas: new List<string> { "GINFO" },
            Documentos: new List<SeiDocumentItem>(),
            Atividades: new List<SeiActivityItem>()
        ));
    }

    public void Dispose() { }
}
