using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

/// <summary>
/// Painel central de controle do SEI Monitor.
/// Coordena as 5 abas operacionais (Visão Geral, Caixa de Entrada GINFO, Acompanhamento Especial, Configurações, Logs),
/// integrações com SQLite e acionamento da janela de detalhes (DetailWindow).
/// </summary>
public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiPoller _poller;
    private readonly ISeiClient _client;
    private readonly IUiDispatcher _dispatcher;
    private readonly MainWindowViewModel _viewModel;

    private readonly Dictionary<string, DetailWindow> _openDetailWindows = new(StringComparer.OrdinalIgnoreCase);

    private List<MonitoredProcess> _allProcesses = new();
    private List<LogEntry> _allLogs = new();

    public MainWindowViewModel ViewModel => _viewModel;

    public MainWindow(
        Config config,
        Database db,
        ISeiPoller poller,
        ISeiClient? client = null,
        IUiDispatcher? dispatcher = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _client = client ?? new SeiClient(_config, _db);
        _dispatcher = dispatcher ?? new WpfDispatcher(Dispatcher);

        _viewModel = new MainWindowViewModel(_config, _db, _poller, _dispatcher, _client);

        InitializeComponent();

        // Aplica o tema configurado
        if (Application.Current != null)
            Theme.Apply(Application.Current.Resources, _config.Theme);
        else
            Theme.Apply(Resources, _config.Theme);

        // Assinatura aos eventos do poller de background
        _poller.ProcessChanged += OnPollerProcessChanged;
        _poller.SyncCompleted += OnPollerSyncCompleted;
        _poller.SyncStatusChanged += OnPollerSyncStatusChanged;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadHeaderInfo();
        LoadConfigFields();
        RefreshAll();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_config.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _poller.ProcessChanged -= OnPollerProcessChanged;
        _poller.SyncCompleted -= OnPollerSyncCompleted;
        _poller.SyncStatusChanged -= OnPollerSyncStatusChanged;
        _viewModel.Dispose();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.MinimizeToTray)
        {
            Hide();
        }
        base.OnStateChanged(e);
    }

    private void LoadHeaderInfo()
    {
        TxtHeaderUnidade.Text = !string.IsNullOrWhiteSpace(_config.SeiUnidadeSigla) ? _config.SeiUnidadeSigla : "GINFO";
        TxtHeaderUsuario.Text = !string.IsNullOrWhiteSpace(_config.SeiUsuarioNome) ? _config.SeiUsuarioNome : "Servidor";
        TxtLastSyncStatus.Text = _poller.LastSyncTime.HasValue
            ? $"Atualizado: {_poller.LastSyncTime.Value.ToLocalTime():HH:mm:ss}"
            : "Aguardando sincronização...";
    }

    public void RefreshAll()
    {
        try
        {
            _allProcesses = _db.ListMonitoredProcesses();

            RefreshOverview();
            RefreshInbox();
            RefreshSpecialGroups();
            RefreshLogs();

            TxtProcessCountStatus.Text = $"{_allProcesses.Count} processos carregados";
        }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao atualizar dados no MainWindow: {ex.Message}");
        }
    }

    public void SelectSettingsTab()
    {
        MainTabs.SelectedItem = TabSettings;
        _viewModel.SelectedTab = MainTab.Settings;
    }

    // ==========================================
    // 1. Aba Visão Geral
    // ==========================================
    private void RefreshOverview()
    {
        if (TxtCardMonitored == null || _allProcesses == null) return;

        int countMonitored = _allProcesses.Count;
        int countNew = _allProcesses.Count(p => !p.IsReadLocally);
        int countDestined = _allProcesses.Count(p => p.DestinadoAoUsuario && !p.IsReadLocally);
        int countAttention = _allProcesses.Count(p => p.AbertoPorTerceiro && !p.IsReadLocally);

        TxtCardMonitored.Text = countMonitored.ToString();
        TxtCardNewActivities.Text = countNew.ToString();
        TxtCardDestined.Text = countDestined.ToString();
        TxtCardAttention.Text = countAttention.ToString();

        var recent = _allProcesses
            .OrderByDescending(p => p.UltimaAtividadeData)
            .Take(12)
            .ToList();

        ListRecentProcesses.ItemsSource = recent;
    }

    private void OnRecentItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredProcess proc)
        {
            OpenDetailWindow(proc);
        }
    }

    private void OnGoToInboxClick(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabInbox;
    }

    // ==========================================
    // 2. Aba Caixa de Entrada GINFO
    // ==========================================
    private void RefreshInbox()
    {
        if (DgInboxProcesses == null || _allProcesses == null) return;

        var filter = TxtSearchInbox?.Text?.Trim() ?? "";
        IEnumerable<MonitoredProcess> query = _allProcesses;

        if (RbFilterDestined?.IsChecked == true)
            query = query.Where(p => p.DestinadoAoUsuario);
        else if (RbFilterThirdParty?.IsChecked == true)
            query = query.Where(p => p.AbertoPorTerceiro);
        else if (RbFilterUnread?.IsChecked == true)
            query = query.Where(p => !p.IsReadLocally);

        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(p =>
                (p.ProtocoloFormatado != null && p.ProtocoloFormatado.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (p.TipoProcesso != null && p.TipoProcesso.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (p.Descricao != null && p.Descricao.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (p.UltimaAtividadeDesc != null && p.UltimaAtividadeDesc.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (p.UltimoUsuarioNome != null && p.UltimoUsuarioNome.Contains(filter, StringComparison.OrdinalIgnoreCase))
            );
        }

        DgInboxProcesses.ItemsSource = query.ToList();
    }

    private void OnSearchInboxTextChanged(object sender, TextChangedEventArgs e) => RefreshInbox();
    private void OnFilterRadioChanged(object sender, RoutedEventArgs e) => RefreshInbox();

    private void OnProcessRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && row.Item is MonitoredProcess proc)
        {
            OpenDetailWindow(proc);
        }
    }

    private void OnToggleReadRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredProcess proc)
        {
            bool newStatus = !proc.IsReadLocally;
            _db.SetLocalReadStatus(proc.Id, newStatus, proc.TotalDocumentos);
            proc.IsReadLocally = newStatus;
            RefreshAll();
        }
    }

    private void OnOpenInSeiRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredProcess proc)
        {
            OpenProcessInBrowser(proc.Id);
        }
    }

    private void OnOpenDetailRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitoredProcess proc)
        {
            OpenDetailWindow(proc);
        }
    }

    // ==========================================
    // 3. Aba Acompanhamento Especial
    // ==========================================
    private void RefreshSpecialGroups()
    {
        if (LstSpecialGroups == null) return;

        var groups = _db.ListSpecialGroups();
        LstSpecialGroups.ItemsSource = groups;

        var assignOptions = new List<SpecialGroup> { new() { Id = "", Nome = "(Nenhum grupo)" } };
        assignOptions.AddRange(groups);
        CmbAssignGroup.ItemsSource = assignOptions;

        if (LstSpecialGroups.SelectedItem == null && groups.Count > 0)
        {
            LstSpecialGroups.SelectedIndex = 0;
        }
    }

    private void OnSpecialGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstSpecialGroups.SelectedItem is SpecialGroup group)
        {
            TxtSelectedGroupTitle.Text = $"Processos vinculados ao grupo '{group.Nome}'";
            var procs = _allProcesses.Where(p => string.Equals(p.IdGrupoEspecial, group.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            DgGroupProcesses.ItemsSource = procs;
        }
        else
        {
            TxtSelectedGroupTitle.Text = "Nenhum grupo selecionado";
            DgGroupProcesses.ItemsSource = null;
        }
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        var input = Prompt("Novo Grupo", "Digite o nome do novo grupo de Acompanhamento:", "Prioritários");
        if (!string.IsNullOrWhiteSpace(input))
        {
            var grp = new SpecialGroup { Id = Guid.NewGuid().ToString("N")[..8], Nome = input.Trim(), TotalProcessos = 0 };
            _db.UpsertSpecialGroup(grp);
            RefreshSpecialGroups();
        }
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (LstSpecialGroups.SelectedItem is SpecialGroup group)
        {
            var input = Prompt("Renomear Grupo", "Digite o novo nome do grupo:", group.Nome);
            if (!string.IsNullOrWhiteSpace(input) && !input.Trim().Equals(group.Nome, StringComparison.OrdinalIgnoreCase))
            {
                group.Nome = input.Trim();
                _db.UpsertSpecialGroup(group);
                RefreshSpecialGroups();
            }
        }
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (LstSpecialGroups.SelectedItem is SpecialGroup group)
        {
            var res = MessageBox.Show($"Deseja remover o grupo '{group.Nome}'? Os processos vinculados permanecerão na base.", "Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _viewModel.DeleteSpecialGroup(group.Id);
                RefreshSpecialGroups();
            }
        }
    }

    private void OnAssignGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DgGroupProcesses.SelectedItem is MonitoredProcess proc && CmbAssignGroup.SelectedValue is string targetGroupId)
        {
            proc.IdGrupoEspecial = string.IsNullOrEmpty(targetGroupId) ? null : targetGroupId;
            _db.UpsertMonitoredProcess(proc);
            RefreshAll();
        }
    }

    // ==========================================
    // 4. Aba Configurações
    // ==========================================
    private void LoadConfigFields()
    {
        TxtCfgWsseiUrl.Text = _config.SeiWsseiBaseUrl;
        TxtCfgSeadUrl.Text = _config.SeiSeadBaseUrl;

        var defaultOrgaos = SeiOrgaoItem.DefaultOrgaos.ToList();
        CmbCfgOrgao.ItemsSource = defaultOrgaos;
        CmbCfgOrgao.SelectedItem = defaultOrgaos.FirstOrDefault(o =>
            string.Equals(o.Sigla, _config.SeiSeadOrgao, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(o.Id, _config.SeiWsseiOrgao, StringComparison.OrdinalIgnoreCase))
            ?? defaultOrgaos.FirstOrDefault(o => o.Sigla == "PGE-PI");

        var initialUnits = new List<SeiUnidadeItem>();
        if (!string.IsNullOrWhiteSpace(_config.SeiUnidade) || !string.IsNullOrWhiteSpace(_config.SeiUnidadeSigla))
        {
            initialUnits.Add(new SeiUnidadeItem(
                _config.SeiUnidade,
                string.IsNullOrWhiteSpace(_config.SeiUnidadeSigla) ? _config.SeiUnidade : _config.SeiUnidadeSigla,
                string.Empty));
        }
        CmbCfgUnidade.ItemsSource = initialUnits;
        if (initialUnits.Count > 0)
        {
            CmbCfgUnidade.SelectedIndex = 0;
        }

        _ = LoadOrgaosFromPortalAsync();
        _ = LoadUnitsFromSeiAsync(forceRefresh: false);

        TxtCfgUsuario.Text = _config.SeiWsseiUsuario;
        TxtCfgNomeUsuario.Text = _config.SeiUsuarioNome;
        TxtCfgKeywords.Text = _config.UserKeywordsRaw;

        TxtCfgIntervaloMinutos.Text = _config.CheckIntervalMinutes.ToString();
        ChkCfgDesktopFence.IsChecked = _config.DesktopFenceEnabled;
        ChkCfgTaskbarWidget.IsChecked = _config.TaskbarWidgetEnabled;
        ChkCfgStartWithWindows.IsChecked = Config.StartWithWindows;
        ChkCfgStartMinimized.IsChecked = _config.StartMinimized;
        ChkCfgCloseToTray.IsChecked = _config.CloseToTray;
        SldFenceOpacity.Value = _config.FenceOpacity;

        var (u, p) = CredentialStore.LoadCredential();
        if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p))
        {
            bool matchesUser = string.Equals(TxtCfgUsuario.Text, u, StringComparison.OrdinalIgnoreCase);
            TxtCredStatus.Text = matchesUser ? $"🔒 Credencial ativa para '{u}'" : "⚠️ Credencial padrão salva para outro usuário";
            PassCfgSenha.Password = matchesUser ? p : "";
        }
        else
        {
            TxtCredStatus.Text = "⚠️ Nenhuma credencial registrada no cofre";
        }
    }

    private void OnSaveCredentialsClick(object sender, RoutedEventArgs e)
    {
        string u = TxtCfgUsuario.Text.Trim();
        string p = PassCfgSenha.Password;

        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
        {
            MessageBox.Show("Informe usuário e senha para salvar no Credential Manager.", "Validação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool ok = CredentialStore.SaveCredential(CredentialStore.DefaultTarget, u, p);
        if (ok)
        {
            _config.SeiWsseiUsuario = u;
            TxtCredStatus.Text = $"🔒 Credencial salva para '{u}'";
            _ = LoadUnitsFromSeiAsync(forceRefresh: true);
            MessageBox.Show("Credenciais salvas com segurança no Windows Credential Manager!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Falha ao salvar credencial no cofre do Windows.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDeleteCredentialsClick(object sender, RoutedEventArgs e)
    {
        CredentialStore.DeleteCredential(CredentialStore.DefaultTarget);
        PassCfgSenha.Password = "";
        TxtCredStatus.Text = "⚠️ Nenhuma credencial registrada no cofre";
        MessageBox.Show("Credenciais excluídas do cofre nativo do Windows.", "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task LoadOrgaosFromPortalAsync()
    {
        try
        {
            var orgaos = await _client.FetchOrgaosFromPortalAsync();
            if (orgaos.Count > 0)
            {
                var currentSigla = (CmbCfgOrgao.SelectedItem as SeiOrgaoItem)?.Sigla ?? _config.SeiSeadOrgao;
                CmbCfgOrgao.ItemsSource = orgaos;
                CmbCfgOrgao.SelectedItem = orgaos.FirstOrDefault(o =>
                    string.Equals(o.Sigla, currentSigla, StringComparison.OrdinalIgnoreCase))
                    ?? orgaos[0];
            }
        }
        catch
        {
            // Mantém DefaultOrgaos em caso de falha de rede
        }
    }

    private async void OnLoadUnitsClick(object sender, RoutedEventArgs e)
    {
        if (CmbCfgOrgao.SelectedItem is SeiOrgaoItem selectedOrgao)
        {
            _config.SeiWsseiOrgao = selectedOrgao.Id;
            _config.SeiSeadOrgao = selectedOrgao.Sigla;
        }
        await LoadUnitsFromSeiAsync(forceRefresh: true, showFeedback: true);
    }

    private async Task LoadUnitsFromSeiAsync(bool forceRefresh, bool showFeedback = false)
    {
        var (u, p) = CredentialStore.LoadCredential();
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
        {
            if (showFeedback)
            {
                MessageBox.Show("Salve usuário e senha primeiro para carregar suas unidades do SEI.", "Credenciais", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        try
        {
            BtnLoadUnits.IsEnabled = false;
            BtnLoadUnits.Content = "Carregando...";
            var units = await _client.GetUserUnitsAsync(forceRefresh);
            if (units.Count > 0)
            {
                CmbCfgUnidade.ItemsSource = units;
                var match = units.FirstOrDefault(x =>
                    (!string.IsNullOrWhiteSpace(_config.SeiUnidade) && x.Id == _config.SeiUnidade) ||
                    (!string.IsNullOrWhiteSpace(_config.SeiUnidadeSigla) && string.Equals(x.Sigla, _config.SeiUnidadeSigla, StringComparison.OrdinalIgnoreCase)));
                CmbCfgUnidade.SelectedItem = match ?? units[0];

                if (string.IsNullOrWhiteSpace(_config.SeiUnidade) && CmbCfgUnidade.SelectedItem is SeiUnidadeItem selected)
                {
                    _config.SeiUnidade = selected.Id;
                    _config.SeiUnidadeSigla = selected.Sigla;
                    LoadHeaderInfo();
                }

                if (showFeedback)
                {
                    MessageBox.Show($"{units.Count} unidade(s) carregada(s) do SEI!", "Unidades", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else if (showFeedback)
            {
                MessageBox.Show("Nenhuma unidade retornada pelo SEI.", "Unidades", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (showFeedback)
            {
                MessageBox.Show($"Falha ao carregar unidades do SEI: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            BtnLoadUnits.IsEnabled = true;
            BtnLoadUnits.Content = "🔄 Carregar Unidades";
        }
    }

    private void OnSaveAllSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.SeiWsseiBaseUrl = TxtCfgWsseiUrl.Text.Trim();
            _config.SeiSeadBaseUrl = TxtCfgSeadUrl.Text.Trim();
            if (CmbCfgUnidade.SelectedItem is SeiUnidadeItem selectedUnit)
            {
                _config.SeiUnidade = selectedUnit.Id;
                _config.SeiUnidadeSigla = selectedUnit.Sigla;
            }
            if (CmbCfgOrgao.SelectedItem is SeiOrgaoItem selectedOrgao)
            {
                _config.SeiWsseiOrgao = selectedOrgao.Id;
                _config.SeiSeadOrgao = selectedOrgao.Sigla;
            }

            _config.SeiUsuarioNome = TxtCfgNomeUsuario.Text.Trim();
            _config.UserKeywordsRaw = TxtCfgKeywords.Text.Trim();

            if (int.TryParse(TxtCfgIntervaloMinutos.Text.Trim(), out var interval))
                _config.CheckIntervalMinutes = interval;

            _config.DesktopFenceEnabled = ChkCfgDesktopFence.IsChecked == true;
            _config.TaskbarWidgetEnabled = ChkCfgTaskbarWidget.IsChecked == true;
            _config.StartMinimized = ChkCfgStartMinimized.IsChecked == true;
            _config.CloseToTray = ChkCfgCloseToTray.IsChecked == true;
            _config.FenceOpacity = SldFenceOpacity.Value;
            Config.StartWithWindows = ChkCfgStartWithWindows.IsChecked == true;

            LoadHeaderInfo();
            MessageBox.Show("Todas as configurações foram salvas com sucesso!", "Configurações", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao salvar configurações: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config == null) return;
        if (CmbCfgTheme?.SelectedItem is ComboBoxItem item)
        {
            string theme = item.Content?.ToString() ?? "Dark";
            _config.Theme = theme;
            if (Application.Current != null)
                Theme.Apply(Application.Current.Resources, theme);
            else
                Theme.Apply(Resources, theme);
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var auth = await _client.AuthenticateAsync(forceRenew: true);
            MessageBox.Show($"Autenticação bem-sucedida via {auth.SourceApi}!\nToken obtido em {auth.AcquiredAt:HH:mm:ss}.", "Teste Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha na comunicação com o SEI:\n{ex.Message}", "Falha no Teste", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==========================================
    // 5. Aba Logs
    // ==========================================
    private void RefreshLogs()
    {
        _allLogs = _db.GetLogs(limit: 200);
        ApplyLogFilters();
    }

    private void ApplyLogFilters()
    {
        if (DgLogs == null || _allLogs == null) return;

        string search = TxtSearchLogs?.Text?.Trim() ?? "";
        string selectedLevel = (CmbLogLevelFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TODOS";

        IEnumerable<LogEntry> query = _allLogs;

        if (!selectedLevel.StartsWith("TODOS", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(l => string.Equals(l.Level, selectedLevel, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(l => l.Message.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        DgLogs.ItemsSource = query.ToList();
    }

    private void OnSearchLogsTextChanged(object sender, TextChangedEventArgs e) => ApplyLogFilters();
    private void OnLogLevelFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyLogFilters();
    private void OnRefreshLogsClick(object sender, RoutedEventArgs e) => RefreshLogs();

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Deseja realmente limpar todos os logs operacionais?", "Limpar Logs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _db.ClearLogs();
            RefreshLogs();
        }
    }

    // ==========================================
    // Poller Callbacks & DetailWindow Dispatcher
    // ==========================================
    private void OnPollerProcessChanged(ProcessChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(RefreshAll);
    }

    private void OnPollerSyncCompleted(SyncResult result)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RefreshAll();
            LoadHeaderInfo();
        });
    }

    private void OnPollerSyncStatusChanged(bool isSyncing, string? message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            BtnGlobalSync.IsEnabled = !isSyncing;
            TxtBottomStatus.Text = isSyncing ? (message ?? "Sincronizando...") : "SEI Monitor Operacional";
        });
    }

    private async void OnGlobalSyncClick(object sender, RoutedEventArgs e)
    {
        BtnGlobalSync.IsEnabled = false;
        try
        {
            await _poller.SyncNowAsync();
        }
        finally
        {
            BtnGlobalSync.IsEnabled = true;
        }
    }

    private void OnOpenSeiPortalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = new Uri(_config.SeiWsseiBaseUrl);
            var url = $"{uri.Scheme}://{uri.Authority}/sei";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao abrir portal SEI: {ex.Message}");
        }
    }

    public void OpenProcessInBrowser(string idProcedimento)
    {
        try
        {
            var uri = new Uri(_config.SeiWsseiBaseUrl);
            var url = $"{uri.Scheme}://{uri.Authority}/sei/controlador.php?acao=procedimento_trabalhar&id_procedimento={Uri.EscapeDataString(idProcedimento)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao abrir processo no navegador: {ex.Message}");
        }
    }

    public void OpenDetailWindow(MonitoredProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (_openDetailWindows.TryGetValue(process.Id, out var existingWindow) && existingWindow.IsLoaded)
        {
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }

        var detailWin = new DetailWindow(process, _config, _db, _client, _dispatcher)
        {
            Owner = this
        };
        detailWin.Closed += (s, e) => _openDetailWindows.Remove(process.Id);
        _openDetailWindows[process.Id] = detailWin;
        detailWin.Show();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl tc)
        {
            if (tc.SelectedItem == TabOverview) _viewModel.SelectedTab = MainTab.Overview;
            else if (tc.SelectedItem == TabInbox) _viewModel.SelectedTab = MainTab.Inbox;
            else if (tc.SelectedItem == TabSpecial) _viewModel.SelectedTab = MainTab.SpecialMonitoring;
            else if (tc.SelectedItem == TabSettings) _viewModel.SelectedTab = MainTab.Settings;
            else if (tc.SelectedItem == TabLogs) _viewModel.SelectedTab = MainTab.Logs;

            RefreshAll();
        }
    }

    private string? Prompt(string title, string message, string defaultText = "")
    {
        var win = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("Bg1")
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8), Foreground = (Brush)FindResource("Fg") });
        var tb = new TextBox { Text = defaultText, Margin = new Thickness(0, 0, 0, 12), Background = (Brush)FindResource("Bg2"), Foreground = (Brush)FindResource("Fg") };
        sp.Children.Add(tb);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOk = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var btnCancel = new Button { Content = "Cancelar", Width = 70, IsCancel = true };
        string? result = null;
        btnOk.Click += (s, e) => { result = tb.Text; win.DialogResult = true; win.Close(); };
        btnCancel.Click += (s, e) => { win.DialogResult = false; win.Close(); };
        bp.Children.Add(btnOk);
        bp.Children.Add(btnCancel);
        sp.Children.Add(bp);
        win.Content = sp;
        tb.Focus();
        tb.SelectAll();
        return win.ShowDialog() == true ? result : null;
    }
}
