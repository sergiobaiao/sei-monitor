using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SeiMonitor.Core;

namespace SeiMonitor.Ui;

public partial class DetailWindow : Window
{
    private readonly MonitoredProcess _process;
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiClient _client;
    private readonly IUiDispatcher _dispatcher;
    private readonly DetailWindowViewModel _viewModel;

    private CancellationTokenSource? _cts;

    public DetailWindowViewModel ViewModel => _viewModel;

    public DetailWindow(
        MonitoredProcess process,
        Config config,
        Database db,
        ISeiClient? client = null,
        IUiDispatcher? dispatcher = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _client = client ?? new SeiClient(_config, _db);
        _dispatcher = dispatcher ?? new WpfDispatcher(Dispatcher);

        _viewModel = new DetailWindowViewModel(_db, _config, _client, _dispatcher);

        InitializeComponent();

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        PopulateCachedMetadata();
    }

    private void PopulateCachedMetadata()
    {
        Title = $"Processo {_process.ProtocoloFormatado} — SEI Monitor";
        TxtProtocolo.Text = _process.ProtocoloFormatado;
        TxtTipoProcesso.Text = $"Tipo: {(!string.IsNullOrEmpty(_process.TipoProcesso) ? _process.TipoProcesso : "Não informado")}";
        TxtDescricao.Text = $"Descrição: {(!string.IsNullOrEmpty(_process.Descricao) ? _process.Descricao : "Sem descrição")}";
        TxtUnidadeSigla.Text = $"Unidade: {_config.SeiUnidadeSigla}";

        BadgeDestined.Visibility = _process.DestinadoAoUsuario ? Visibility.Visible : Visibility.Collapsed;
        BadgeAttention.Visibility = _process.AbertoPorTerceiro ? Visibility.Visible : Visibility.Collapsed;

        UpdateReadStatusBadge(_process.IsReadLocally);
    }

    private void UpdateReadStatusBadge(bool isRead)
    {
        if (isRead)
        {
            TxtReadStatus.Text = "✓ LIDO";
            TxtReadStatus.Foreground = Theme.Brush("#10b981");
            BtnToggleRead.Content = "✉️ Marcar como Não Lido";
        }
        else
        {
            TxtReadStatus.Text = "⚡ NÃO LIDO";
            TxtReadStatus.Foreground = Theme.Brush("#3b82f6");
            BtnToggleRead.Content = "✓ Marcar como Lido";
        }
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await LoadProcessDetailsAsync();
    }

    private async Task LoadProcessDetailsAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        PbLoading.Visibility = Visibility.Visible;
        TxtFooterStatus.Text = "Consultando documentos e andamentos no SEI...";

        try
        {
            await _viewModel.LoadDetailsAsync(_process.Id, _config.SeiUnidade, ct);

            if (ct.IsCancellationRequested) return;

            // 1. Atualizar Metadados
            if (!string.IsNullOrEmpty(_viewModel.TipoProcesso))
                TxtTipoProcesso.Text = $"Tipo: {_viewModel.TipoProcesso}";
            if (!string.IsNullOrEmpty(_viewModel.Descricao))
                TxtDescricao.Text = $"Descrição: {_viewModel.Descricao}";
            if (!string.IsNullOrEmpty(_viewModel.UnidadeSigla))
                TxtUnidadeSigla.Text = $"Unidade: {_viewModel.UnidadeSigla}";

            // 2. Montar Linha do Tempo e Documentos
            TimelineList.ItemsSource = _viewModel.TimelineItems;
            DgDocuments.ItemsSource = _viewModel.Documents;

            if (_viewModel.HasError)
            {
                TxtFooterStatus.Text = _viewModel.ErrorMessage ?? "Erro ao consultar SEI. Exibindo dados locais.";
            }
            else
            {
                TxtFooterStatus.Text = $"{_viewModel.Documents.Count} documentos | {_viewModel.TimelineItems.Count} andamentos carregados com sucesso.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao carregar detalhes do processo {_process.Id}: {Database.SanitizeLogMessage(ex.Message)}");
            TxtFooterStatus.Text = "API do SEI indisponível no momento. Exibindo dados locais.";
        }
        finally
        {
            PbLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCopyNumberClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_process.ProtocoloFormatado);
        }
        catch
        {
            // Silencia caso a área de transferência esteja ocupada
        }
    }

    private void OnToggleReadClick(object sender, RoutedEventArgs e)
    {
        bool newStatus = !_process.IsReadLocally;
        _db.SetLocalReadStatus(_process.Id, newStatus, _process.TotalDocumentos);
        _process.IsReadLocally = newStatus;
        _viewModel.IsReadLocally = newStatus;
        UpdateReadStatusBadge(newStatus);
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = new Uri(_config.SeiWsseiBaseUrl);
            var url = $"{uri.Scheme}://{uri.Authority}/sei/controlador.php?acao=procedimento_trabalhar&id_procedimento={Uri.EscapeDataString(_process.Id)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _db.Log("WARN", $"Falha ao abrir processo no navegador: {ex.Message}");
        }
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        await LoadProcessDetailsAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
