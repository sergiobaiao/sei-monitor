using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SeiMonitor.Core;

/// <summary>
/// Motor central de background polling leve (limit=1) e cache local SQLite do SEI Monitor.
/// Executa varreduras periódicas com zero overhead para processos inalterados,
/// detecção inteligente de deltas, enriquecimento com heurísticas de negócio (TextAnalyzer),
/// controle de leitura local independente e proteção de concorrência com SemaphoreSlim(1,1).
/// </summary>
public sealed class SeiPoller : ISeiPoller
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly ISeiClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _stateLock = new();

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private volatile bool _isRunning;
    private bool _disposed;

    public bool IsRunning => _isRunning;
    public DateTime? LastSyncTime { get; private set; }
    public SyncResult? LastSyncResult { get; private set; }

    public event Action<ProcessChangedEventArgs>? ProcessChanged;
    public event Action<bool, string?>? SyncStatusChanged;
    public event Action<SyncResult>? SyncCompleted;
    public event Action<string>? NotificationPushed;
    public event Action<string, string>? NotificationDetailPushed;

    public SeiPoller(Config config, Database db)
        : this(config, db, new SeiClient(config, db))
    {
        _ownsClient = true;
    }

    public SeiPoller(Config config, Database db, ISeiClient client)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _client = client ?? throw new ArgumentNullException(nameof(client));

        _config.SettingChanged += OnSettingChanged;
    }

    // =========================================================================
    // 1. Ciclo de Vida do Poller em Background (.NET 10 PeriodicTimer)
    // =========================================================================

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_isRunning) return Task.CompletedTask;
            _isRunning = true;
        }

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _config.CheckIntervalMinutes)));
        _backgroundTask = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);
        _db.Log("INFO", "Motor de polling leve iniciado em segundo plano.");

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (!_isRunning) return;
            _isRunning = false;
        }

        _cts?.Cancel();

        if (_backgroundTask != null)
        {
            try
            {
                await Task.WhenAny(_backgroundTask, Task.Delay(5000, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _db.Log("WARN", $"Exceção ao aguardar encerramento do background worker: {ex.Message}");
            }
        }

        _timer?.Dispose();
        _timer = null;
        _cts?.Dispose();
        _cts = null;

        _db.Log("INFO", "Motor de polling leve encerrado.");
    }

    private void OnSettingChanged(string key)
    {
        if ((string.Equals(key, "check_interval_minutes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "CheckIntervalMinutes", StringComparison.OrdinalIgnoreCase)) && _timer != null)
        {
            try
            {
                var newInterval = TimeSpan.FromMinutes(Math.Max(1, _config.CheckIntervalMinutes));
                _timer.Period = newInterval;
                _db.Log("INFO", $"Intervalo do poller reconfigurado para {newInterval.TotalMinutes} minutos.");
            }
            catch (Exception ex)
            {
                _db.Log("WARN", $"Falha ao reconfigurar intervalo do timer: {ex.Message}");
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _db.Log("ERROR", $"Falha no ciclo inicial do poller: {Database.SanitizeLogMessage(ex.Message)}");
        }

        while (!ct.IsCancellationRequested && _timer != null)
        {
            try
            {
                bool ticked = await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                if (!ticked) break;

                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _db.Log("ERROR", $"Erro não tratado no loop periódico do poller: {Database.SanitizeLogMessage(ex.Message)}");
                SyncStatusChanged?.Invoke(false, "Falha temporária no poller");
            }
        }

        _isRunning = false;
    }

    // =========================================================================
    // 2. Motor de Sincronização e Detecção Leve de Deltas (limit=1)
    // =========================================================================

    public Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        return PollOnceAsync(ct);
    }

    public async Task<SyncResult> PollOnceAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SyncStatusChanged?.Invoke(true, "Sincronizando com o SEI...");

            string unidade = _config.SeiUnidade;
            if (string.IsNullOrWhiteSpace(unidade))
            {
                var resolved = await _client.ResolveUnitIdAsync(_config.SeiUnidadeSigla, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    unidade = resolved;
                    _config.SeiUnidade = resolved;
                    _db.Log("INFO", $"ID da unidade '{_config.SeiUnidadeSigla}' resolvido automaticamente: {resolved}.");
                }
            }

            if (string.IsNullOrWhiteSpace(unidade))
            {
                _db.Log("WARN", "Unidade do SEI não configurada para polling.");
                SyncStatusChanged?.Invoke(false, "Unidade do SEI não configurada");
                var unconfiguredResult = new SyncResult(0, 0, 0, DateTime.UtcNow, false, "Unidade não configurada");
                LastSyncResult = unconfiguredResult;
                return unconfiguredResult;
            }

            int totalChecked = 0;
            int changedCount = 0;
            int unchangedCount = 0;
            int newDocumentsCount = 0;
            int newActivitiesCount = 0;
            int newProcessesCount = 0;
            int errorsCount = 0;

            var candidates = new Dictionary<string, CandidateProcess>(StringComparer.OrdinalIgnoreCase);

            // 1. Carregar processos já cadastrados no SQLite local (Caixa e Acompanhamento Especial)
            try
            {
                var dbProcesses = _db.ListMonitoredProcesses();
                foreach (var p in dbProcesses)
                {
                    candidates[p.Id] = new CandidateProcess(
                        p.Id,
                        p.ProtocoloFormatado,
                        p.Descricao,
                        p.TipoProcesso,
                        p.Origem,
                        p.IdGrupoEspecial
                    );
                }
            }
            catch (Exception ex)
            {
                _db.Log("WARN", $"Falha ao ler processos do cache SQLite: {Database.SanitizeLogMessage(ex.Message)}");
            }

            // 2. Consultar Caixa de Entrada da Unidade no SEI
            try
            {
                var searchResult = await _client.SearchProcessesAsync(unidade, start: 0, limit: 50, ct).ConfigureAwait(false);
                if (searchResult?.Items != null)
                {
                    foreach (var item in searchResult.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.IdProcedimento)) continue;

                        if (candidates.TryGetValue(item.IdProcedimento, out var existing))
                        {
                            string resolvedOrigem = (existing.Origem == "ACOMPANHAMENTO" || existing.Origem == "AMBOS")
                                ? "AMBOS"
                                : "CAIXA_ENTRADA";

                            candidates[item.IdProcedimento] = existing with
                            {
                                ProtocoloFormatado = !string.IsNullOrWhiteSpace(item.ProtocoloFormatado) ? item.ProtocoloFormatado : existing.ProtocoloFormatado,
                                Descricao = item.Descricao ?? existing.Descricao,
                                TipoProcesso = item.TipoProcesso ?? existing.TipoProcesso,
                                Origem = resolvedOrigem
                            };
                        }
                        else
                        {
                            candidates[item.IdProcedimento] = new CandidateProcess(
                                item.IdProcedimento,
                                item.ProtocoloFormatado,
                                item.Descricao,
                                item.TipoProcesso,
                                "CAIXA_ENTRADA",
                                null
                            );
                        }
                    }
                }
            }
            catch (SeiException ex)
            {
                errorsCount++;
                _db.Log("WARN", $"Falha ao consultar Caixa de Entrada do SEI: {ex.SafeErrorCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errorsCount++;
                _db.Log("WARN", $"Erro inesperado ao consultar Caixa de Entrada: {Database.SanitizeLogMessage(ex.Message)}");
            }

            // 3. Varredura com verificação leve (limit=1) por processo candidato
            foreach (var candidate in candidates.Values)
            {
                ct.ThrowIfCancellationRequested();
                totalChecked++;

                LightProcessSummary summary;
                try
                {
                    summary = await _client.GetLightSummaryAsync(candidate.Id, unidade, candidate.ProtocoloFormatado, ct).ConfigureAwait(false);
                }
                catch (SeiException ex)
                {
                    errorsCount++;
                    _db.Log("WARN", $"Falha ao consultar resumo leve do processo {candidate.Id}: {ex.SafeErrorCode}");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errorsCount++;
                    _db.Log("WARN", $"Erro ao consultar resumo leve do processo {candidate.Id}: {Database.SanitizeLogMessage(ex.Message)}");
                    continue;
                }

                var cached = _db.GetMonitoredProcess(candidate.Id);

                bool isNew = cached == null;
                bool docCountChanged = cached != null && cached.TotalDocumentos != summary.TotalDocumentos;
                bool activityChanged = cached != null && !string.Equals(cached.UltimaAtividadeData, summary.UltimaAtividadeData, StringComparison.Ordinal);

                bool hasDelta = isNew || docCountChanged || activityChanged;

                if (!hasDelta)
                {
                    // Zero overhead: nenhuma requisição adicional, preserva status local
                    unchangedCount++;
                    continue;
                }

                // Delta detectado: buscar detalhes completos (metadados apenas, nunca corpo HTML bruto)
                changedCount++;
                UpdateType updateType;
                if (isNew)
                {
                    updateType = UpdateType.NewProcess;
                    newProcessesCount++;
                }
                else if (docCountChanged)
                {
                    updateType = UpdateType.NewDocument;
                    newDocumentsCount++;
                }
                else if (activityChanged)
                {
                    updateType = UpdateType.NewActivity;
                    newActivitiesCount++;
                }
                else
                {
                    updateType = UpdateType.MetadataChanged;
                }

                ProcessDetail detail;
                try
                {
                    detail = await _client.GetProcessDetailsAsync(candidate.Id, unidade, candidate.ProtocoloFormatado, ct).ConfigureAwait(false);
                }
                catch (SeiException ex)
                {
                    errorsCount++;
                    _db.Log("WARN", $"Falha ao recuperar detalhes do processo {candidate.Id}: {ex.SafeErrorCode}");
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errorsCount++;
                    _db.Log("WARN", $"Erro ao recuperar detalhes do processo {candidate.Id}: {Database.SanitizeLogMessage(ex.Message)}");
                    continue;
                }

                // Avaliação heurística com TextAnalyzer
                var latestActivity = detail.Atividades?.FirstOrDefault();
                string? lastUser = latestActivity?.Usuario ?? summary.UltimoUsuarioNome ?? cached?.UltimoUsuarioNome;
                string? lastDesc = latestActivity?.Descricao ?? summary.UltimaAtividadeDesc ?? cached?.UltimaAtividadeDesc;
                string? lastData = !string.IsNullOrEmpty(latestActivity?.Data)
                    ? (string.IsNullOrEmpty(latestActivity.Hora) ? latestActivity.Data.Trim() : $"{latestActivity.Data.Trim()} {latestActivity.Hora.Trim()}")
                    : (summary.UltimaAtividadeData ?? cached?.UltimaAtividadeData);

                bool abertoPorTerceiro = detail.Atividades != null && detail.Atividades.Count > 0
                    ? TextAnalyzer.IsOpenedByThirdParty(
                        detail.Atividades.Select(a => new SeiActivity(a.Id, a.Data, a.Hora, a.Descricao, a.Unidade, a.Usuario)),
                        _config.SeiUsuarioNome)
                    : TextAnalyzer.IsOpenedByThirdParty(lastUser, lastDesc, _config.SeiUsuarioNome);

                string combinedTitle = string.Join(" ", new[] { detail.ProtocoloFormatado, candidate.ProtocoloFormatado, detail.Descricao, candidate.Descricao, cached?.Descricao }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                string? spec = detail.TipoProcesso ?? candidate.TipoProcesso ?? cached?.TipoProcesso;
                string? lastDispatch = lastDesc ?? detail.Documentos?.FirstOrDefault()?.NomeComposto;

                bool destinadoAoUsuario = TextAnalyzer.IsDestinedToUser(
                    combinedTitle,
                    spec,
                    lastDispatch,
                    _config.UserKeywords
                );

                // Regra de leitura local independente:
                // Se a ação foi realizada pelo próprio usuário logado e o processo já estava lido, preserva leitura
                bool isUserOwnAction = !string.IsNullOrWhiteSpace(lastUser) &&
                                       !string.IsNullOrWhiteSpace(_config.SeiUsuarioNome) &&
                                       TextAnalyzer.NormalizeText(lastUser).Equals(TextAnalyzer.NormalizeText(_config.SeiUsuarioNome), StringComparison.OrdinalIgnoreCase);

                bool newReadStatus = !isNew && isUserOwnAction && (cached?.IsReadLocally == true);

                string origemFinal = candidate.Origem;
                if (cached != null)
                {
                    if (cached.Origem == "AMBOS" ||
                        (cached.Origem == "ACOMPANHAMENTO" && candidate.Origem == "CAIXA_ENTRADA") ||
                        (cached.Origem == "CAIXA_ENTRADA" && candidate.Origem == "ACOMPANHAMENTO"))
                    {
                        origemFinal = "AMBOS";
                    }
                }

                var updatedProc = new MonitoredProcess
                {
                    Id = candidate.Id,
                    ProtocoloFormatado = !string.IsNullOrWhiteSpace(detail.ProtocoloFormatado)
                        ? detail.ProtocoloFormatado
                        : (!string.IsNullOrWhiteSpace(summary.ProtocoloFormatado)
                            ? summary.ProtocoloFormatado
                            : (!string.IsNullOrWhiteSpace(candidate.ProtocoloFormatado) ? candidate.ProtocoloFormatado : candidate.Id)),
                    Descricao = detail.Descricao ?? candidate.Descricao ?? cached?.Descricao,
                    TipoProcesso = detail.TipoProcesso ?? candidate.TipoProcesso ?? cached?.TipoProcesso,
                    Origem = origemFinal,
                    IdGrupoEspecial = candidate.IdGrupoEspecial ?? cached?.IdGrupoEspecial,
                    TotalDocumentos = summary.TotalDocumentos,
                    UltimaAtividadeData = lastData,
                    UltimaAtividadeDesc = lastDesc,
                    UltimoUsuarioNome = lastUser,
                    AbertoPorTerceiro = abertoPorTerceiro,
                    DestinadoAoUsuario = destinadoAoUsuario,
                    UpdatedAt = DateTime.UtcNow.ToString("o"),
                    IsReadLocally = newReadStatus
                };

                _db.UpsertMonitoredProcess(updatedProc);
                _db.SetLocalReadStatus(updatedProc.Id, isRead: newReadStatus, docCount: updatedProc.TotalDocumentos);

                // Disparo de eventos ricos para a UI
                var eventArgs = new ProcessChangedEventArgs(
                    updatedProc,
                    updateType,
                    previousDocCount: cached?.TotalDocumentos ?? 0,
                    newDocCount: summary.TotalDocumentos,
                    previousActivityDate: cached?.UltimaAtividadeData,
                    newActivityDate: lastData
                );
                ProcessChanged?.Invoke(eventArgs);

                // Notificação no Ticker da Barra de Tarefas
                string notifText = updatedProc.DestinadoAoUsuario
                    ? $"🎯 Processo destinado a você: {updatedProc.ProtocoloFormatado} — {_config.SeiUnidadeSigla}"
                    : updateType switch
                    {
                        UpdateType.NewProcess => $"⚡ Novo processo recebido — {updatedProc.ProtocoloFormatado} — {_config.SeiUnidadeSigla}",
                        UpdateType.NewDocument => $"📄 Novo documento juntado — {updatedProc.ProtocoloFormatado}",
                        _ => $"Nova movimentação no processo — {updatedProc.ProtocoloFormatado}"
                    };
                NotificationPushed?.Invoke(notifText);
                NotificationDetailPushed?.Invoke(updatedProc.ProtocoloFormatado, notifText);
            }

            LastSyncTime = DateTime.UtcNow;
            var result = new SyncResult(
                totalChecked: totalChecked,
                changedCount: changedCount,
                unchangedCount: unchangedCount,
                newDocumentsCount: newDocumentsCount,
                newActivitiesCount: newActivitiesCount,
                newProcessesCount: newProcessesCount,
                errorsCount: errorsCount,
                timestamp: LastSyncTime.Value,
                success: errorsCount == 0 || totalChecked > 0,
                errorMessage: errorsCount > 0 ? $"{errorsCount} erro(s) durante a sincronização" : null
            );
            LastSyncResult = result;

            SyncCompleted?.Invoke(result);

            string statusMessage = (errorsCount > 0 && totalChecked == 0)
                ? "Falha na sincronização com o SEI"
                : $"Atualizado em {LastSyncTime.Value.ToLocalTime():HH:mm:ss}";

            SyncStatusChanged?.Invoke(false, statusMessage);

            _db.Log("INFO", $"Sincronização concluída: {changedCount} alterado(s), {unchangedCount} sem alteração, {errorsCount} erro(s) de {totalChecked} verificado(s).");

            return result;
        }
        catch (OperationCanceledException)
        {
            SyncStatusChanged?.Invoke(false, "Sincronização cancelada");
            throw;
        }
        catch (Exception ex)
        {
            _db.Log("ERROR", $"Falha geral no ciclo de sincronização: {Database.SanitizeLogMessage(ex.Message)}");
            SyncStatusChanged?.Invoke(false, "Erro na sincronização");
            var errResult = new SyncResult(
                totalChecked: 0,
                changedCount: 0,
                unchangedCount: 0,
                errorsCount: 1,
                timestamp: DateTime.UtcNow,
                success: false,
                errorMessage: Database.SanitizeLogMessage(ex.Message)
            );
            LastSyncResult = errResult;
            return errResult;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _config.SettingChanged -= OnSettingChanged;
        _isRunning = false;
        _cts?.Cancel();
        _timer?.Dispose();
        _cts?.Dispose();
        _syncLock.Dispose();

        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _config.SettingChanged -= OnSettingChanged;
        await StopAsync().ConfigureAwait(false);
        _syncLock.Dispose();

        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record CandidateProcess(
        string Id,
        string? ProtocoloFormatado,
        string? Descricao,
        string? TipoProcesso,
        string Origem,
        string? IdGrupoEspecial
    );
}
