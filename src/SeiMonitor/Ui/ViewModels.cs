using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

#region Comandos Reativos (MVVM)

/// <summary>
/// Comando síncrono para vinculação MVVM desacoplada de code-behind.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Comando assíncrono com controle de execução concorrente para vinculação MVVM.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

#endregion

#region Enums de Navegação e Filtro

public enum MainTab
{
    Overview = 0,
    Inbox = 1,
    SpecialMonitoring = 2,
    Settings = 3,
    Logs = 4
}

public enum ProcessFilterMode
{
    All = 0,
    DestinedOnly = 1,
    ThirdPartyOnly = 2,
    UnreadOnly = 3
}

#endregion

#region Modelos de Apresentação (Child ViewModels)

/// <summary>
/// Wrapper observável de MonitoredProcess para apresentação na interface.
/// </summary>
public sealed class ProcessItemViewModel : INotifyPropertyChanged
{
    private readonly MonitoredProcess _model;
    private bool _isReadLocally;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProcessItemViewModel(MonitoredProcess model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _isReadLocally = model.IsReadLocally;
    }

    public MonitoredProcess Model => _model;

    public string Id => _model.Id;
    public string ProtocoloFormatado => _model.ProtocoloFormatado;
    public string? Descricao => _model.Descricao;
    public string? TipoProcesso => _model.TipoProcesso;
    public string Origem => _model.Origem;

    public string? IdGrupoEspecial
    {
        get => _model.IdGrupoEspecial;
        set
        {
            if (_model.IdGrupoEspecial != value)
            {
                _model.IdGrupoEspecial = value;
                OnPropertyChanged();
            }
        }
    }

    public int TotalDocumentos => _model.TotalDocumentos;
    public string? UltimaAtividadeData => _model.UltimaAtividadeData;
    public string? UltimaAtividadeDesc => _model.UltimaAtividadeDesc;
    public string? UltimoUsuarioNome => _model.UltimoUsuarioNome;
    public bool AbertoPorTerceiro => _model.AbertoPorTerceiro;
    public bool DestinadoAoUsuario => _model.DestinadoAoUsuario;

    public bool IsReadLocally
    {
        get => _isReadLocally;
        set
        {
            if (_isReadLocally != value)
            {
                _isReadLocally = value;
                _model.IsReadLocally = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUnread));
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public bool IsUnread => !IsReadLocally;
    public string StatusBadgeText => IsReadLocally ? "Lido" : "Não Lido";
    public bool HasDestinedBadge => DestinadoAoUsuario;
    public bool HasAttentionBadge => AbertoPorTerceiro;

    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>
/// Modelo de apresentação para documentos do processo.
/// </summary>
public sealed class DocumentItemViewModel
{
    public string Id { get; }
    public string ProtocoloFormatado { get; }
    public string NomeComposto { get; }
    public string Tipo { get; }
    public string TipoDocumento => Tipo;
    public string MimeType { get; }
    public string? DataInclusao { get; }
    public bool PodeVisualizar { get; }
    public bool Sigiloso { get; }
    public bool Cancelado { get; }

    public string Glyph => Cancelado ? "❌" : Sigiloso ? "🔒" : "📄";

    public DocumentItemViewModel(SeiDocumentItem doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        Id = doc.Id;
        ProtocoloFormatado = doc.ProtocoloFormatado;
        NomeComposto = string.IsNullOrWhiteSpace(doc.NomeComposto) ? doc.ProtocoloFormatado : doc.NomeComposto;
        Tipo = doc.TipoDocumento ?? doc.Tipo;
        MimeType = doc.MimeType;
        DataInclusao = doc.DataInclusao;
        PodeVisualizar = doc.PodeVisualizar;
        Sigiloso = doc.Sigiloso;
        Cancelado = doc.Cancelado;
    }
}

/// <summary>
/// Modelo de apresentação para andamentos do processo.
/// </summary>
public sealed class ActivityItemViewModel
{
    public string Id { get; }
    public string Data { get; }
    public string Hora { get; }
    public string Descricao { get; }
    public string? Unidade { get; }
    public string? Usuario { get; }
    public bool IsThirdPartyWithoutDespacho { get; }
    public bool IsFromCurrentUser { get; }

    public string TimestampFormatted => !string.IsNullOrEmpty(Hora) ? $"{Data} {Hora}" : Data;

    public ActivityItemViewModel(
        SeiActivityItem act,
        string? currentUserName = null)
    {
        ArgumentNullException.ThrowIfNull(act);
        Id = act.Id;
        Data = act.Data;
        Hora = act.Hora;
        Descricao = act.Descricao;
        Unidade = act.Unidade;
        Usuario = act.Usuario;

        IsThirdPartyWithoutDespacho = TextAnalyzer.IsOpenedByThirdParty(act.Usuario, act.Descricao, currentUserName);
        IsFromCurrentUser = !string.IsNullOrWhiteSpace(act.Usuario) &&
                            !string.IsNullOrWhiteSpace(currentUserName) &&
                            act.Usuario.Trim().Equals(currentUserName.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Modelo de apresentação para grupos de acompanhamento especial.
/// </summary>
public sealed class SpecialGroupViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public int TotalProcessos { get; set; }
    public string DisplayText => $"{Nome} ({TotalProcessos})";
}

/// <summary>
/// Modelo de apresentação para entradas de log operacional.
/// </summary>
public sealed class LogEntryViewModel
{
    public long Id { get; }
    public string Level { get; }
    public string Message { get; }
    public string CreatedAt { get; }

    public LogEntryViewModel(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Id = entry.Id;
        Level = entry.Level;
        Message = entry.Message;
        CreatedAt = entry.CreatedAt;
    }
}

/// <summary>
/// Modelo de apresentação da timeline com estilo visual em destaque para visualização por colegas.
/// </summary>
public sealed class ActivityTimelineItem
{
    public string Id { get; set; } = string.Empty;
    public string DataHora { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string Unidade { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public bool IsOpenedByThirdParty { get; set; }

    public string IconMarker => IsOpenedByThirdParty ? "⚠️" : "●";
    public Brush MarkerColor => IsOpenedByThirdParty
        ? Theme.Brush("#f59e0b")
        : Theme.Brush("#3b82f6");

    public Thickness BorderThickness => new(IsOpenedByThirdParty ? 2 : 1);

    public Brush BorderBrush => IsOpenedByThirdParty
        ? Theme.Brush("#f59e0b")
        : (Brush)(Application.Current?.Resources["BorderBrush"] ?? Theme.Brush("#27272a"));

    public Brush BackgroundBrush => IsOpenedByThirdParty
        ? Theme.Brush("#1Af59e0b")
        : (Brush)(Application.Current?.Resources["Bg1"] ?? Theme.Brush("#18181b"));
}

#endregion

#region MainWindowViewModel

/// <summary>
/// ViewModel reativo completo para a tela principal (MainWindow) do SEI Monitor.
/// Gerencia as 5 abas operacionais, contadores de KPI, filtros, comandos assíncronos e desacoplamento de UI.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiPoller _poller;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISeiClient? _client;

    private MainTab _selectedTab = MainTab.Overview;
    private ProcessFilterMode _selectedFilter = ProcessFilterMode.All;
    private string _searchText = string.Empty;
    private string? _selectedMonitoringGroupId;

    private int _countTotal;
    private int _countUnread;
    private int _countDestined;
    private int _countAttention;
    private int _countCaixa;
    private int _countAcompanhamento;

    private bool _isSyncing;
    private string _lastSyncText = "Aguardando sincronização...";
    private string _statusMessage = string.Empty;

    // Configurações e Credenciais
    private string _seiWsseiBaseUrl = string.Empty;
    private string _seiSeadBaseUrl = string.Empty;
    private string _seiWsseiUsuario = string.Empty;
    private string _seiWsseiOrgao = string.Empty;
    private string _seiSeadUsuario = string.Empty;
    private string _seiSeadOrgao = string.Empty;
    private string _seiUnidade = string.Empty;
    private string _seiUnidadeSigla = string.Empty;
    private string _seiUsuarioNome = string.Empty;
    private string _userKeywordsRaw = string.Empty;
    private int _checkIntervalMinutes = 5;
    private int _seiTimeoutSeconds = 120;
    private bool _desktopFenceEnabled = true;
    private bool _taskbarWidgetEnabled = true;
    private bool _startMinimized = true;
    private bool _closeToTray = true;
    private bool _minimizeToTray = true;
    private string _theme = "Dark";

    private bool _hasSavedCredentials;
    private string _credentialPasswordMasked = string.Empty;
    private string _inputPassword = string.Empty;
    private string _configStatusMessage = string.Empty;
    private bool _hasConfigError;

    // Logs
    private string _logFilterLevel = "ALL";
    private string _logSearchText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProcessItemViewModel> DisplayedInboxProcesses { get; } = new();
    public ObservableCollection<ProcessItemViewModel> DisplayedMonitoringProcesses { get; } = new();
    public ObservableCollection<SpecialGroupViewModel> AvailableGroups { get; } = new();
    public ObservableCollection<LogEntryViewModel> Logs { get; } = new();

    public ICommand SyncNowCommand { get; }
    public ICommand MarkAsReadCommand { get; }
    public ICommand MarkAllAsReadCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand RemoveCredentialsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand RefreshLogsCommand { get; }
    public ICommand AddSpecialGroupCommand { get; }
    public ICommand DeleteSpecialGroupCommand { get; }

    public MainWindowViewModel(
        Config config,
        Database db,
        ISeiPoller poller,
        IUiDispatcher? dispatcher = null,
        ISeiClient? client = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _dispatcher = dispatcher ?? new ImmediateDispatcher();
        _client = client;

        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        MarkAsReadCommand = new RelayCommand(p => MarkAsRead(p as string));
        MarkAllAsReadCommand = new RelayCommand(MarkAllAsRead);
        SaveConfigCommand = new RelayCommand(SaveConfig);
        RemoveCredentialsCommand = new RelayCommand(RemoveCredentials);
        ClearLogsCommand = new RelayCommand(ClearLogs);
        RefreshLogsCommand = new RelayCommand(RefreshLogs);
        AddSpecialGroupCommand = new RelayCommand(p => AddSpecialGroup(p as string));
        DeleteSpecialGroupCommand = new RelayCommand(p => DeleteSpecialGroup(p as string));

        _poller.ProcessChanged += OnPollerProcessChanged;
        _poller.SyncCompleted += OnPollerSyncCompleted;
        _poller.SyncStatusChanged += OnPollerSyncStatusChanged;

        LoadConfigProperties();
        RefreshSpecialGroups();
        RefreshProcesses();
        RefreshLogs();
    }

    public MainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetField(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(SelectedTabIndex));
            }
        }
    }

    public int SelectedTabIndex
    {
        get => (int)_selectedTab;
        set => SelectedTab = (MainTab)value;
    }

    public ProcessFilterMode SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetField(ref _selectedFilter, value))
            {
                RefreshInboxList();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                RefreshInboxList();
            }
        }
    }

    public string? SelectedMonitoringGroupId
    {
        get => _selectedMonitoringGroupId;
        set
        {
            if (SetField(ref _selectedMonitoringGroupId, value))
            {
                RefreshMonitoringList();
            }
        }
    }

    public int CountTotal { get => _countTotal; private set => SetField(ref _countTotal, value); }
    public int CountUnread { get => _countUnread; private set => SetField(ref _countUnread, value); }
    public int CountDestined { get => _countDestined; private set => SetField(ref _countDestined, value); }
    public int CountAttention { get => _countAttention; private set => SetField(ref _countAttention, value); }
    public int CountCaixa { get => _countCaixa; private set => SetField(ref _countCaixa, value); }
    public int CountAcompanhamento { get => _countAcompanhamento; private set => SetField(ref _countAcompanhamento, value); }

    public bool IsSyncing { get => _isSyncing; private set => SetField(ref _isSyncing, value); }
    public string LastSyncText { get => _lastSyncText; private set => SetField(ref _lastSyncText, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    public string SeiWsseiBaseUrl { get => _seiWsseiBaseUrl; set => SetField(ref _seiWsseiBaseUrl, value); }
    public string SeiSeadBaseUrl { get => _seiSeadBaseUrl; set => SetField(ref _seiSeadBaseUrl, value); }
    public string SeiWsseiUsuario { get => _seiWsseiUsuario; set => SetField(ref _seiWsseiUsuario, value); }
    public string SeiWsseiOrgao { get => _seiWsseiOrgao; set => SetField(ref _seiWsseiOrgao, value); }
    public string SeiSeadUsuario { get => _seiSeadUsuario; set => SetField(ref _seiSeadUsuario, value); }
    public string SeiSeadOrgao { get => _seiSeadOrgao; set => SetField(ref _seiSeadOrgao, value); }
    public string SeiUnidade { get => _seiUnidade; set => SetField(ref _seiUnidade, value); }
    public string SeiUnidadeSigla { get => _seiUnidadeSigla; set => SetField(ref _seiUnidadeSigla, value); }
    public string SeiUsuarioNome { get => _seiUsuarioNome; set => SetField(ref _seiUsuarioNome, value); }
    public string UserKeywordsRaw { get => _userKeywordsRaw; set => SetField(ref _userKeywordsRaw, value); }
    public int CheckIntervalMinutes { get => _checkIntervalMinutes; set => SetField(ref _checkIntervalMinutes, value); }
    public int SeiTimeoutSeconds { get => _seiTimeoutSeconds; set => SetField(ref _seiTimeoutSeconds, value); }
    public bool DesktopFenceEnabled { get => _desktopFenceEnabled; set => SetField(ref _desktopFenceEnabled, value); }
    public bool TaskbarWidgetEnabled { get => _taskbarWidgetEnabled; set => SetField(ref _taskbarWidgetEnabled, value); }
    public bool StartMinimized { get => _startMinimized; set => SetField(ref _startMinimized, value); }
    public bool CloseToTray { get => _closeToTray; set => SetField(ref _closeToTray, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetField(ref _minimizeToTray, value); }
    public string Theme { get => _theme; set => SetField(ref _theme, value); }

    public bool HasSavedCredentials { get => _hasSavedCredentials; private set => SetField(ref _hasSavedCredentials, value); }
    public string CredentialPasswordMasked { get => _credentialPasswordMasked; private set => SetField(ref _credentialPasswordMasked, value); }
    public string InputPassword { get => _inputPassword; set => SetField(ref _inputPassword, value); }
    public string ConfigStatusMessage { get => _configStatusMessage; private set => SetField(ref _configStatusMessage, value); }
    public bool HasConfigError { get => _hasConfigError; private set => SetField(ref _hasConfigError, value); }

    public string LogFilterLevel
    {
        get => _logFilterLevel;
        set
        {
            if (SetField(ref _logFilterLevel, value))
            {
                RefreshLogs();
            }
        }
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetField(ref _logSearchText, value))
            {
                RefreshLogs();
            }
        }
    }

    public void LoadConfigProperties()
    {
        SeiWsseiBaseUrl = _config.SeiWsseiBaseUrl;
        SeiSeadBaseUrl = _config.SeiSeadBaseUrl;
        SeiWsseiUsuario = _config.SeiWsseiUsuario;
        SeiWsseiOrgao = _config.SeiWsseiOrgao;
        SeiSeadUsuario = _config.SeiSeadUsuario;
        SeiSeadOrgao = _config.SeiSeadOrgao;
        SeiUnidade = _config.SeiUnidade;
        SeiUnidadeSigla = _config.SeiUnidadeSigla;
        SeiUsuarioNome = _config.SeiUsuarioNome;
        UserKeywordsRaw = _config.UserKeywordsRaw;
        CheckIntervalMinutes = _config.CheckIntervalMinutes;
        SeiTimeoutSeconds = _config.SeiTimeoutSeconds;
        DesktopFenceEnabled = _config.DesktopFenceEnabled;
        TaskbarWidgetEnabled = _config.TaskbarWidgetEnabled;
        StartMinimized = _config.StartMinimized;
        CloseToTray = _config.CloseToTray;
        MinimizeToTray = _config.MinimizeToTray;
        Theme = _config.Theme;

        CheckSavedCredentials();
    }

    private void CheckSavedCredentials()
    {
        try
        {
            var (user, pass) = CredentialStore.LoadCredential();
            if (!string.IsNullOrEmpty(pass))
            {
                HasSavedCredentials = true;
                CredentialPasswordMasked = "••••••••••••";
            }
            else
            {
                HasSavedCredentials = false;
                CredentialPasswordMasked = string.Empty;
            }
        }
        catch
        {
            HasSavedCredentials = false;
            CredentialPasswordMasked = string.Empty;
        }
    }

    public void RefreshProcesses()
    {
        RefreshInboxList();
        RefreshMonitoringList();
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        try
        {
            var all = _db.ListMonitoredProcesses();
            CountTotal = all.Count;
            CountUnread = all.Count(p => !p.IsReadLocally);
            CountDestined = all.Count(p => p.DestinadoAoUsuario);
            CountAttention = all.Count(p => p.AbertoPorTerceiro);
            CountCaixa = all.Count(p => p.Origem == "CAIXA_ENTRADA" || p.Origem == "AMBOS");
            CountAcompanhamento = all.Count(p => p.Origem == "ACOMPANHAMENTO" || p.Origem == "AMBOS");
        }
        catch
        {
            // Resiliente a acessos concorrentes transitórios
        }
    }

    public void RefreshInboxList()
    {
        try
        {
            var processes = _db.ListMonitoredProcesses(origem: "CAIXA_ENTRADA");
            var filtered = processes.AsEnumerable();

            switch (SelectedFilter)
            {
                case ProcessFilterMode.DestinedOnly:
                    filtered = filtered.Where(p => p.DestinadoAoUsuario);
                    break;
                case ProcessFilterMode.ThirdPartyOnly:
                    filtered = filtered.Where(p => p.AbertoPorTerceiro);
                    break;
                case ProcessFilterMode.UnreadOnly:
                    filtered = filtered.Where(p => !p.IsReadLocally);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.Trim();
                filtered = filtered.Where(p =>
                    (p.ProtocoloFormatado != null && p.ProtocoloFormatado.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (p.Descricao != null && p.Descricao.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (p.UltimoUsuarioNome != null && p.UltimoUsuarioNome.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (p.TipoProcesso != null && p.TipoProcesso.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            DisplayedInboxProcesses.Clear();
            foreach (var proc in filtered)
            {
                DisplayedInboxProcesses.Add(new ProcessItemViewModel(proc));
            }
        }
        catch
        {
            // Resiliente
        }
    }

    public void RefreshMonitoringList()
    {
        try
        {
            var filterGroup = string.IsNullOrWhiteSpace(SelectedMonitoringGroupId) ? null : SelectedMonitoringGroupId;
            var processes = _db.ListMonitoredProcesses(origem: "ACOMPANHAMENTO", idGrupo: filterGroup);

            DisplayedMonitoringProcesses.Clear();
            foreach (var proc in processes)
            {
                DisplayedMonitoringProcesses.Add(new ProcessItemViewModel(proc));
            }
        }
        catch
        {
            // Resiliente
        }
    }

    public void RefreshSpecialGroups()
    {
        try
        {
            AvailableGroups.Clear();
            AvailableGroups.Add(new SpecialGroupViewModel { Id = "", Nome = "📁 Todos os Grupos", TotalProcessos = 0 });
            foreach (var grp in _db.ListSpecialGroups())
            {
                AvailableGroups.Add(new SpecialGroupViewModel
                {
                    Id = grp.Id,
                    Nome = grp.Nome,
                    TotalProcessos = grp.TotalProcessos
                });
            }
        }
        catch
        {
            // Resiliente
        }
    }

    public void RefreshLogs()
    {
        try
        {
            var entries = _db.GetLogs(limit: 200).AsEnumerable();

            if (!string.Equals(LogFilterLevel, "ALL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(LogFilterLevel, "TODOS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(LogFilterLevel, "TODOS OS NÍVEIS", StringComparison.OrdinalIgnoreCase))
            {
                entries = entries.Where(e => e.Level.Equals(LogFilterLevel, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(LogSearchText))
            {
                entries = entries.Where(e => e.Message.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase));
            }

            Logs.Clear();
            foreach (var entry in entries)
            {
                Logs.Add(new LogEntryViewModel(entry));
            }
        }
        catch
        {
            // Resiliente
        }
    }

    public void MarkAsRead(string? processId)
    {
        if (string.IsNullOrWhiteSpace(processId)) return;
        _db.SetLocalReadStatus(processId, true);

        foreach (var item in DisplayedInboxProcesses.ToList().Where(p => p != null && p.Id == processId))
        {
            if (item != null) item.IsReadLocally = true;
        }

        foreach (var item in DisplayedMonitoringProcesses.ToList().Where(p => p != null && p.Id == processId))
        {
            if (item != null) item.IsReadLocally = true;
        }

        RefreshCounts();
    }

    public void MarkAllAsRead()
    {
        foreach (var item in DisplayedInboxProcesses.ToList().Where(p => p != null && !p.IsReadLocally))
        {
            if (item != null)
            {
                _db.SetLocalReadStatus(item.Id, true);
                item.IsReadLocally = true;
            }
        }
        RefreshCounts();
    }

    public async Task SyncNowAsync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        StatusMessage = "Sincronizando com o SEI...";

        try
        {
            var result = await _poller.SyncNowAsync();
            LastSyncText = $"Atualizado: {DateTime.Now:HH:mm:ss}";
            StatusMessage = result.Success ? "Sincronização concluída." : "Falha na sincronização.";
            RefreshProcesses();
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao sincronizar.";
            _db.Log("ERROR", $"Falha no disparo de sincronização manual: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public void SaveConfig()
    {
        HasConfigError = false;

        // Validações
        if (!Uri.TryCreate(SeiWsseiBaseUrl, UriKind.Absolute, out var uriWssei) ||
            (uriWssei.Scheme != Uri.UriSchemeHttp && uriWssei.Scheme != Uri.UriSchemeHttps))
        {
            HasConfigError = true;
            ConfigStatusMessage = "URL do WSSEI v2 inválida. Informe uma URL HTTP/HTTPS absoluta.";
            return;
        }

        if (CheckIntervalMinutes < 1)
        {
            HasConfigError = true;
            ConfigStatusMessage = "O intervalo de verificação deve ser de no mínimo 1 minuto.";
            return;
        }

        try
        {
            _config.SeiWsseiBaseUrl = SeiWsseiBaseUrl;
            _config.SeiSeadBaseUrl = SeiSeadBaseUrl;
            _config.SeiWsseiUsuario = SeiWsseiUsuario;
            _config.SeiWsseiOrgao = SeiWsseiOrgao;
            _config.SeiSeadUsuario = SeiSeadUsuario;
            _config.SeiSeadOrgao = SeiSeadOrgao;
            _config.SeiUnidade = SeiUnidade;
            _config.SeiUnidadeSigla = SeiUnidadeSigla;
            _config.SeiUsuarioNome = SeiUsuarioNome;
            _config.UserKeywordsRaw = UserKeywordsRaw;
            _config.CheckIntervalMinutes = CheckIntervalMinutes;
            _config.SeiTimeoutSeconds = SeiTimeoutSeconds;
            _config.DesktopFenceEnabled = DesktopFenceEnabled;
            _config.TaskbarWidgetEnabled = TaskbarWidgetEnabled;
            _config.StartMinimized = StartMinimized;
            _config.CloseToTray = CloseToTray;
            _config.MinimizeToTray = MinimizeToTray;
            _config.Theme = Theme;

            if (!string.IsNullOrWhiteSpace(InputPassword))
            {
                CredentialStore.SaveCredential(CredentialStore.DefaultTarget, SeiWsseiUsuario, InputPassword);
                InputPassword = string.Empty;
                CheckSavedCredentials();
            }

            ConfigStatusMessage = "Configurações salvas com sucesso!";
        }
        catch (Exception ex)
        {
            HasConfigError = true;
            ConfigStatusMessage = $"Falha ao salvar configurações: {ex.Message}";
        }
    }

    public void RemoveCredentials()
    {
        try
        {
            CredentialStore.DeleteCredential(CredentialStore.DefaultTarget);
            HasSavedCredentials = false;
            CredentialPasswordMasked = string.Empty;
            ConfigStatusMessage = "Credenciais removidas do cofre do Windows.";
        }
        catch (Exception ex)
        {
            HasConfigError = true;
            ConfigStatusMessage = $"Falha ao remover credenciais: {ex.Message}";
        }
    }

    public void ClearLogs()
    {
        _db.ClearLogs();
        Logs.Clear();
    }

    public void AddSpecialGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = new SpecialGroup
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Nome = name.Trim(),
            TotalProcessos = 0
        };
        _db.UpsertSpecialGroup(group);
        RefreshSpecialGroups();
    }

    public void DeleteSpecialGroup(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            var field = typeof(Database).GetField("_connectionString", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(_db) is string cs)
            {
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM special_groups WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // Silencia
        }

        RefreshSpecialGroups();
    }

    private void OnPollerProcessChanged(ProcessChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(RefreshProcesses);
    }

    private void OnPollerSyncCompleted(SyncResult result)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RefreshProcesses();
            LastSyncText = $"Atualizado: {DateTime.Now:HH:mm:ss}";
        });
    }

    private void OnPollerSyncStatusChanged(bool isSyncing, string? message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IsSyncing = isSyncing;
            StatusMessage = isSyncing ? (message ?? "Sincronizando...") : "SEI Monitor Operacional";
        });
    }

    public void Dispose()
    {
        _poller.ProcessChanged -= OnPollerProcessChanged;
        _poller.SyncCompleted -= OnPollerSyncCompleted;
        _poller.SyncStatusChanged -= OnPollerSyncStatusChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }
}

#endregion

#region DetailWindowViewModel

/// <summary>
/// ViewModel reativo para a janela de detalhes (DetailWindow) do processo.
/// Carrega e apresenta a árvore de documentos, a linha do tempo cronológica de andamentos,
/// metadados e destaque automático de terceiro sem despacho.
/// </summary>
public sealed class DetailWindowViewModel : INotifyPropertyChanged
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly ISeiClient? _client;
    private readonly IUiDispatcher _dispatcher;

    private string _processId = string.Empty;
    private string _protocoloFormatado = string.Empty;
    private string? _descricao;
    private string? _tipoProcesso;
    private string? _unidadeSigla;
    private int _totalDocumentos;
    private string? _ultimaAtividadeData;
    private string? _ultimaAtividadeDesc;
    private string? _ultimoUsuarioNome;
    private bool _abertoPorTerceiro;
    private bool _destinadoAoUsuario;
    private bool _isReadLocally;

    private bool _isLoading;
    private string? _errorMessage;
    private bool _hasError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DocumentItemViewModel> Documents { get; } = new();
    public ObservableCollection<ActivityItemViewModel> Activities { get; } = new();
    public ObservableCollection<ActivityTimelineItem> TimelineItems { get; } = new();

    public ICommand ToggleReadStatusCommand { get; }

    public DetailWindowViewModel(
        Database db,
        Config config,
        ISeiClient? client = null,
        IUiDispatcher? dispatcher = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _client = client;
        _dispatcher = dispatcher ?? new ImmediateDispatcher();

        ToggleReadStatusCommand = new RelayCommand(ToggleReadStatus);
    }

    public string ProcessId { get => _processId; set => SetField(ref _processId, value); }
    public string ProtocoloFormatado { get => _protocoloFormatado; set => SetField(ref _protocoloFormatado, value); }
    public string? Descricao { get => _descricao; set => SetField(ref _descricao, value); }
    public string? TipoProcesso { get => _tipoProcesso; set => SetField(ref _tipoProcesso, value); }
    public string? UnidadeSigla { get => _unidadeSigla; set => SetField(ref _unidadeSigla, value); }
    public int TotalDocumentos { get => _totalDocumentos; set => SetField(ref _totalDocumentos, value); }
    public string? UltimaAtividadeData { get => _ultimaAtividadeData; set => SetField(ref _ultimaAtividadeData, value); }
    public string? UltimaAtividadeDesc { get => _ultimaAtividadeDesc; set => SetField(ref _ultimaAtividadeDesc, value); }
    public string? UltimoUsuarioNome { get => _ultimoUsuarioNome; set => SetField(ref _ultimoUsuarioNome, value); }
    public bool AbertoPorTerceiro { get => _abertoPorTerceiro; set => SetField(ref _abertoPorTerceiro, value); }
    public bool DestinadoAoUsuario { get => _destinadoAoUsuario; set => SetField(ref _destinadoAoUsuario, value); }

    public bool IsReadLocally
    {
        get => _isReadLocally;
        set
        {
            if (SetField(ref _isReadLocally, value))
            {
                OnPropertyChanged(nameof(ReadStatusText));
            }
        }
    }

    public string ReadStatusText => IsReadLocally ? "Lido" : "Não Lido";
    public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }
    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }
    public bool HasError { get => _hasError; set => SetField(ref _hasError, value); }

    public async Task LoadDetailsAsync(string processId, string? unidade = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ProcessId = processId;
        IsLoading = true;
        ErrorMessage = null;
        HasError = false;

        // 1. Carrega dados básicos do cache SQLite imediato
        var cached = _db.GetMonitoredProcess(processId);
        if (cached != null)
        {
            ProtocoloFormatado = cached.ProtocoloFormatado;
            Descricao = cached.Descricao;
            TipoProcesso = cached.TipoProcesso;
            TotalDocumentos = cached.TotalDocumentos;
            UltimaAtividadeData = cached.UltimaAtividadeData;
            UltimaAtividadeDesc = cached.UltimaAtividadeDesc;
            UltimoUsuarioNome = cached.UltimoUsuarioNome;
            AbertoPorTerceiro = cached.AbertoPorTerceiro;
            DestinadoAoUsuario = cached.DestinadoAoUsuario;
            IsReadLocally = cached.IsReadLocally;
        }

        // 2. Se houver client HTTP conectado, busca lista completa de documentos e andamentos
        if (_client != null)
        {
            try
            {
                var targetUnidade = !string.IsNullOrWhiteSpace(unidade) ? unidade : _config.SeiUnidade;
                var details = await _client.GetProcessDetailsAsync(processId, targetUnidade, ct);

                _dispatcher.Invoke(() =>
                {
                    ProtocoloFormatado = details.ProtocoloFormatado;
                    Descricao = details.Descricao ?? Descricao;
                    TipoProcesso = details.TipoProcesso ?? TipoProcesso;
                    UnidadeSigla = details.UnidadeSigla;

                    Documents.Clear();
                    foreach (var doc in details.Documentos)
                    {
                        Documents.Add(new DocumentItemViewModel(doc));
                    }

                    Activities.Clear();
                    TimelineItems.Clear();

                    var sortedActivities = details.Atividades
                        .OrderByDescending(a => a.Data)
                        .ThenByDescending(a => a.Hora)
                        .ThenByDescending(a => long.TryParse(a.Id, out var id) ? id : 0);

                    foreach (var act in sortedActivities)
                    {
                        var actVm = new ActivityItemViewModel(act, _config.SeiUsuarioNome);
                        Activities.Add(actVm);

                        string dtStr = !string.IsNullOrWhiteSpace(act.Hora) ? $"{act.Data} {act.Hora}" : act.Data;
                        TimelineItems.Add(new ActivityTimelineItem
                        {
                            Id = act.Id,
                            DataHora = dtStr,
                            Descricao = act.Descricao,
                            Unidade = act.Unidade ?? "",
                            Usuario = act.Usuario ?? "",
                            IsOpenedByThirdParty = actVm.IsThirdPartyWithoutDespacho
                        });
                    }
                });
            }
            catch (SeiException ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                _db.Log("WARN", $"Falha sanitizada ao carregar detalhes do processo {processId}: [{ex.SafeErrorCode}]");
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = "Erro de conexão ao carregar documentos e andamentos.";
                _db.Log("ERROR", $"Exceção ao buscar detalhes do processo {processId}: {ex.Message}");
            }
        }

        IsLoading = false;
    }

    public void ToggleReadStatus()
    {
        IsReadLocally = !IsReadLocally;
        _db.SetLocalReadStatus(ProcessId, IsReadLocally, TotalDocumentos);
    }

    public void MarkAsRead()
    {
        IsReadLocally = true;
        _db.SetLocalReadStatus(ProcessId, true, TotalDocumentos);
    }

    public void MarkAsUnread()
    {
        IsReadLocally = false;
        _db.SetLocalReadStatus(ProcessId, false, TotalDocumentos);
    }

    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        return true;
    }
}

#endregion

#region ViewModels dos Widgets (Marco 4)

/// <summary>
/// ViewModel reativo para o Desktop Fence (FenceWindow).
/// Desacopla a lógica de negócio, contadores de pills, filtragem de grupos e ancoragem Win32 da view WPF.
/// </summary>
public sealed class FenceViewModel : INotifyPropertyChanged
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiPoller _poller;
    private readonly IUiDispatcher _dispatcher;
    private readonly IShellInterop _shell;

    private int _countMonitored;
    private int _countNewActivities;
    private int _countDestined;
    private int _countAttention;
    private bool _isCollapsed;
    private string? _selectedGroupId;
    private string _lastSyncText = "Aguardando sincronização...";

    public event PropertyChangedEventHandler? PropertyChanged;

    public FenceViewModel(
        Config config,
        Database db,
        ISeiPoller poller,
        IUiDispatcher? dispatcher = null,
        IShellInterop? shell = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _dispatcher = dispatcher ?? new ImmediateDispatcher();
        _shell = shell ?? Win32ShellInterop.Instance;

        _isCollapsed = _config.FenceCollapsed;
        _selectedGroupId = string.IsNullOrWhiteSpace(_config.FenceActiveGroup) ? null : _config.FenceActiveGroup;

        // Inscrição aos eventos do poller
        _poller.ProcessChanged += OnPollerProcessChanged;
        _poller.SyncCompleted += OnPollerSyncCompleted;

        RefreshGroups();
        RefreshCounts();
    }

    public int CountMonitored
    {
        get => _countMonitored;
        private set => SetField(ref _countMonitored, value);
    }

    public int CountNewActivities
    {
        get => _countNewActivities;
        private set => SetField(ref _countNewActivities, value);
    }

    public int CountDestined
    {
        get => _countDestined;
        private set => SetField(ref _countDestined, value);
    }

    public int CountAttention
    {
        get => _countAttention;
        private set => SetField(ref _countAttention, value);
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (SetField(ref _isCollapsed, value))
            {
                _config.FenceCollapsed = value;
            }
        }
    }

    public string? SelectedGroupId
    {
        get => _selectedGroupId;
        set
        {
            if (SetField(ref _selectedGroupId, value))
            {
                _config.FenceActiveGroup = value ?? "";
                RefreshCounts();
            }
        }
    }

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetField(ref _lastSyncText, value);
    }

    public ObservableCollection<SpecialGroup> AvailableGroups { get; } = new();
    public ObservableCollection<MonitoredProcess> DisplayedProcesses { get; } = new();

    private void OnPollerProcessChanged(ProcessChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() => RefreshCounts());
    }

    private void OnPollerSyncCompleted(SyncResult result)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RefreshCounts();
            LastSyncText = _poller.LastSyncTime.HasValue
                ? $"Atualizado: {_poller.LastSyncTime.Value.ToLocalTime():HH:mm:ss}"
                : "Atualizado agora";
        });
    }

    public void RefreshGroups()
    {
        try
        {
            AvailableGroups.Clear();
            AvailableGroups.Add(new SpecialGroup { Id = "", Nome = "📁 Todos os Grupos" });
            foreach (var grp in _db.ListSpecialGroups())
            {
                AvailableGroups.Add(grp);
            }
        }
        catch
        {
            // Resiliente a acesso concorrente
        }
    }

    public void RefreshCounts()
    {
        try
        {
            var filterGroup = string.IsNullOrWhiteSpace(SelectedGroupId) ? null : SelectedGroupId;
            var processes = _db.ListMonitoredProcesses(origem: null, idGrupo: filterGroup);

            CountMonitored = processes.Count;
            CountNewActivities = processes.Count(p => !p.IsReadLocally);
            CountDestined = processes.Count(p => p.DestinadoAoUsuario && !p.IsReadLocally);
            CountAttention = processes.Count(p => p.AbertoPorTerceiro && !p.IsReadLocally);

            DisplayedProcesses.Clear();
            foreach (var proc in processes.OrderByDescending(p => p.UltimaAtividadeData).Take(50))
            {
                DisplayedProcesses.Add(proc);
            }
        }
        catch
        {
            // Resiliente a locks temporários
        }
    }

    public void SelectGroup(string? groupId)
    {
        SelectedGroupId = groupId;
    }

    public void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    public bool PinWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var progman = _shell.FindProgmanWindow();
        if (progman == IntPtr.Zero) return false;

        _shell.ApplyToolWindowStyles(hwnd);
        _shell.SetWindowOwner(hwnd, progman);
        _shell.SendToBottom(hwnd);
        return true;
    }

    public bool MaintainPin(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var progman = _shell.FindProgmanWindow();
        if (progman == IntPtr.Zero) return false;

        var currentOwner = _shell.GetWindowOwner(hwnd);
        bool reanchored = false;
        if (currentOwner != progman)
        {
            _shell.SetWindowOwner(hwnd, progman);
            reanchored = true;
        }
        _shell.SendToBottom(hwnd);
        return reanchored;
    }

    public void Dispose()
    {
        _poller.ProcessChanged -= OnPollerProcessChanged;
        _poller.SyncCompleted -= OnPollerSyncCompleted;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>
/// ViewModel reativo para o widget da taskbar (TaskbarWidgetWindow).
/// Controla fila de notificações, rotação do letreiro marquee, hover pause e status do badge.
/// </summary>
public sealed class TaskbarWidgetViewModel : INotifyPropertyChanged
{
    private readonly ISeiPoller _poller;
    private readonly IUiDispatcher _dispatcher;
    private readonly IShellInterop _shell;
    private readonly ConcurrentQueue<string> _notificationsQueue = new();

    private string _currentMessage = "SEI Monitor — Carregando...";
    private string _currentIcon = "⚡";
    private int _badgeCount;
    private bool _isPaused;
    private bool _isOpen;
    private double _offset;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskbarWidgetViewModel(
        ISeiPoller poller,
        IUiDispatcher? dispatcher = null,
        IShellInterop? shell = null)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _dispatcher = dispatcher ?? new ImmediateDispatcher();
        _shell = shell ?? Win32ShellInterop.Instance;

        _poller.NotificationPushed += OnNotificationPushed;
    }

    public string CurrentMessage
    {
        get => _currentMessage;
        set => SetField(ref _currentMessage, value);
    }

    public string CurrentIcon
    {
        get => _currentIcon;
        set => SetField(ref _currentIcon, value);
    }

    public int BadgeCount
    {
        get => _badgeCount;
        set => SetField(ref _badgeCount, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => SetField(ref _isPaused, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetField(ref _isOpen, value);
    }

    public double Offset
    {
        get => _offset;
        set => SetField(ref _offset, value);
    }

    public int QueueCount => _notificationsQueue.Count;

    private void OnNotificationPushed(string notifText)
    {
        EnqueueNotification(notifText);
    }

    public void EnqueueNotification(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _notificationsQueue.Enqueue(text);
        _dispatcher.BeginInvoke(() =>
        {
            if (_notificationsQueue.TryDequeue(out var msg))
            {
                CurrentMessage = msg;
                CurrentIcon = msg.StartsWith("🎯") ? "🎯" : "⚡";
                Offset = 220; // reset offset
            }
        });
    }

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    public void StepScroll(double delta)
    {
        if (!IsPaused)
        {
            Offset -= delta;
        }
    }

    public void ToggleFlyout()
    {
        IsOpen = !IsOpen;
    }

    public void Dispose()
    {
        _poller.NotificationPushed -= OnNotificationPushed;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>
/// ViewModel reativo para o painel suspenso da barra de tarefas (TaskbarFlyoutWindow).
/// </summary>
public sealed class TaskbarFlyoutViewModel : INotifyPropertyChanged
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly ISeiPoller _poller;
    private readonly IUiDispatcher _dispatcher;

    private bool _isOpen;
    private string _greeting = "Olá";
    private string _subheader = "Unidade";
    private int _countDestinados;
    private int _countTerceiros;
    private int _countAcomp;
    private int _countCaixa;
    private string _lastSyncText = "Aguardando sincronização...";

    public DateTime LastDeactivatedTime { get; private set; } = DateTime.MinValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskbarFlyoutViewModel(
        Database db,
        Config config,
        ISeiPoller poller,
        IUiDispatcher? dispatcher = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _dispatcher = dispatcher ?? new ImmediateDispatcher();

        _poller.ProcessChanged += OnPollerProcessChanged;
        _poller.SyncCompleted += OnPollerSyncCompleted;

        RefreshData();
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetField(ref _isOpen, value);
    }

    public string Greeting
    {
        get => _greeting;
        private set => SetField(ref _greeting, value);
    }

    public string Subheader
    {
        get => _subheader;
        private set => SetField(ref _subheader, value);
    }

    public int CountDestinados
    {
        get => _countDestinados;
        private set => SetField(ref _countDestinados, value);
    }

    public int CountTerceiros
    {
        get => _countTerceiros;
        private set => SetField(ref _countTerceiros, value);
    }

    public int CountAcomp
    {
        get => _countAcomp;
        private set => SetField(ref _countAcomp, value);
    }

    public int CountCaixa
    {
        get => _countCaixa;
        private set => SetField(ref _countCaixa, value);
    }

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetField(ref _lastSyncText, value);
    }

    public ObservableCollection<MonitoredProcess> Destinados { get; } = new();
    public ObservableCollection<MonitoredProcess> UltimasMovimentacoes { get; } = new();

    private void OnPollerProcessChanged(ProcessChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(RefreshData);
    }

    private void OnPollerSyncCompleted(SyncResult result)
    {
        _dispatcher.BeginInvoke(RefreshData);
    }

    public void RefreshData()
    {
        try
        {
            var now = DateTime.Now;
            string greet = now.Hour switch
            {
                >= 5 and < 12 => "Bom dia",
                >= 12 and < 18 => "Boa tarde",
                _ => "Boa noite"
            };
            string user = !string.IsNullOrWhiteSpace(_config.SeiUsuarioNome) ? _config.SeiUsuarioNome : "Servidor";
            Greeting = $"{greet}, {user}";
            Subheader = $"Unidade: {_config.SeiUnidadeSigla}";

            var processes = _db.ListMonitoredProcesses();
            CountDestinados = processes.Count(p => p.DestinadoAoUsuario);
            CountTerceiros = processes.Count(p => p.AbertoPorTerceiro);
            CountAcomp = processes.Count(p => p.Origem == "ACOMPANHAMENTO" || p.Origem == "AMBOS");
            CountCaixa = processes.Count(p => p.Origem == "CAIXA_ENTRADA" || p.Origem == "AMBOS");

            Destinados.Clear();
            foreach (var p in processes.Where(p => p.DestinadoAoUsuario).Take(5))
            {
                Destinados.Add(p);
            }

            UltimasMovimentacoes.Clear();
            foreach (var p in processes.OrderByDescending(p => p.UltimaAtividadeData).Take(8))
            {
                UltimasMovimentacoes.Add(p);
            }

            LastSyncText = _poller.LastSyncTime.HasValue
                ? $"Último check: {_poller.LastSyncTime.Value.ToLocalTime():HH:mm:ss}"
                : "Aguardando sincronização...";
        }
        catch
        {
            // Resiliente
        }
    }

    public void OnDeactivated()
    {
        LastDeactivatedTime = DateTime.UtcNow;
        IsOpen = false;
    }

    public void Dispose()
    {
        _poller.ProcessChanged -= OnPollerProcessChanged;
        _poller.SyncCompleted -= OnPollerSyncCompleted;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

#endregion
