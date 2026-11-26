using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

public sealed class TestSeiClient : ISeiClient
{
    public List<string> SearchCalls { get; } = new();
    public List<(string Id, string Unidade)> LightSummaryCalls { get; } = new();
    public List<(string Id, string Unidade)> ProcessDetailsCalls { get; } = new();
    public List<bool> AuthenticateCalls { get; } = new();

    public ProcessListResult SearchResult { get; set; } = new(0, new List<SeiProcessItem>());
    public Dictionary<string, LightProcessSummary> LightSummaries { get; } = new();
    public Dictionary<string, ProcessDetail> ProcessDetails { get; } = new();

    public Func<CancellationToken, Task>? OnSearchAsync { get; set; }
    public Func<string, CancellationToken, Task>? OnLightSummaryAsync { get; set; }
    public Func<string, CancellationToken, Task>? OnProcessDetailsAsync { get; set; }

    public Task<SeiAuthResult> AuthenticateAsync(bool forceRenew = false, CancellationToken ct = default)
    {
        AuthenticateCalls.Add(forceRenew);
        return Task.FromResult(new SeiAuthResult("test_token_jwt", "WSSEI", DateTime.UtcNow));
    }

    public async Task<ProcessListResult> SearchProcessesAsync(string unidade, int start = 0, int limit = 50, CancellationToken ct = default)
    {
        SearchCalls.Add(unidade);
        if (OnSearchAsync != null) await OnSearchAsync(ct);
        return SearchResult;
    }

    public async Task<LightProcessSummary> GetLightSummaryAsync(string idProcedimento, string unidade, CancellationToken ct = default)
    {
        LightSummaryCalls.Add((idProcedimento, unidade));
        if (OnLightSummaryAsync != null) await OnLightSummaryAsync(idProcedimento, ct);

        if (LightSummaries.TryGetValue(idProcedimento, out var summary))
        {
            return summary;
        }

        return new LightProcessSummary(idProcedimento, 0, null, null, null, idProcedimento);
    }

    public async Task<ProcessDetail> GetProcessDetailsAsync(string idProcedimento, string unidade, CancellationToken ct = default)
    {
        ProcessDetailsCalls.Add((idProcedimento, unidade));
        if (OnProcessDetailsAsync != null) await OnProcessDetailsAsync(idProcedimento, ct);

        if (ProcessDetails.TryGetValue(idProcedimento, out var detail))
        {
            return detail;
        }

        return new ProcessDetail(
            idProcedimento,
            idProcedimento,
            "Descrição",
            "Processo Administrativo",
            "GINFO",
            0,
            new List<string>(),
            new List<SeiDocumentItem>(),
            new List<SeiActivityItem>()
        );
    }

    public void Dispose() { }
}

public class SeiPollerTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly TestSeiClient _client;
    private readonly SeiPoller _poller;

    public SeiPollerTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db)
        {
            SeiUnidade = "110000000",
            SeiUnidadeSigla = "GINFO",
            SeiUsuarioNome = "victor.lima",
            UserKeywordsRaw = "Victor; GINFO/Sistemas",
            CheckIntervalMinutes = 1
        };
        _client = new TestSeiClient();
        _poller = new SeiPoller(_config, _db, _client);
    }

    public void Dispose()
    {
        _poller.Dispose();
        _client.Dispose();
        _db.Dispose();
    }

    // =========================================================================
    // 1. POLLING LEVE SEM ALTERAÇÃO (SKIP DETAILS)
    // =========================================================================

    [Fact]
    public async Task T01_LightPolling_WhenNoDelta_SkipsGetProcessDetails()
    {
        // Arrange
        const string procId = "1001";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000001/2026-01",
            TotalDocumentos = 10,
            UltimaAtividadeData = "2026-10-29 10:00",
            UltimaAtividadeDesc = "Recebido na unidade",
            UltimoUsuarioNome = "servidor.a",
            Origem = "CAIXA_ENTRADA"
        });
        _db.SetLocalReadStatus(procId, isRead: true, docCount: 10);

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000001/2026-01", "Desc", "Tipo", "GINFO", null, null)
        });

        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: 10, // Idêntico
            UltimaAtividadeData: "2026-10-29 10:00", // Idêntico
            UltimaAtividadeDesc: "Recebido na unidade",
            UltimoUsuarioNome: "servidor.a",
            ProtocoloFormatado: "00001.000001/2026-01"
        );

        bool eventFired = false;
        _poller.ProcessChanged += _ => eventFired = true;

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Single(_client.LightSummaryCalls);
        Assert.Empty(_client.ProcessDetailsCalls); // NÃO pode chamar GetProcessDetailsAsync!
        Assert.False(eventFired);
        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(0, result.NewDocumentsCount);
        Assert.True(_db.GetLocalReadStatus(procId)); // Preserva status lido
    }

    // =========================================================================
    // 2. POLLING COM NOVO DOCUMENTO (FETCH DETAILS + EVENT)
    // =========================================================================

    [Fact]
    public async Task T02_LightPolling_WhenNewDocumentDetected_FetchesDetailsAndFiresEvent()
    {
        // Arrange
        const string procId = "1002";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000002/2026-02",
            TotalDocumentos = 5,
            UltimaAtividadeData = "2026-10-29 08:00",
            Origem = "CAIXA_ENTRADA"
        });
        _db.SetLocalReadStatus(procId, isRead: true, docCount: 5);

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000002/2026-02", "Desc", "Tipo", "GINFO", null, null)
        });

        // Retorna 6 documentos (novo documento inserido!)
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: 6,
            UltimaAtividadeData: "2026-10-29 08:00",
            UltimaAtividadeDesc: null,
            UltimoUsuarioNome: null,
            ProtocoloFormatado: "00001.000002/2026-02"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId,
            "00001.000002/2026-02",
            "Processo com novo anexo",
            "Processo Administrativo",
            "GINFO",
            0,
            new List<string> { "GINFO" },
            new List<SeiDocumentItem>
            {
                new("D6", "Doc 6", "Despacho", "G", "text/html", "Despacho", "1", true, false, false, "2026-10-29")
            },
            new List<SeiActivityItem>()
        );

        ProcessChangedEventArgs? capturedEvent = null;
        _poller.ProcessChanged += args => capturedEvent = args;

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Single(_client.LightSummaryCalls);
        Assert.Single(_client.ProcessDetailsCalls); // Buscou detalhes!
        Assert.Equal(1, result.NewDocumentsCount);
        Assert.NotNull(capturedEvent);
        Assert.Equal(UpdateType.NewDocument, capturedEvent!.Type);
        Assert.Equal(procId, capturedEvent.Process.Id);

        var updated = _db.GetMonitoredProcess(procId);
        Assert.NotNull(updated);
        Assert.Equal(6, updated!.TotalDocumentos);
        Assert.False(_db.GetLocalReadStatus(procId)); // Novo documento reseta para não lido!
    }

    // =========================================================================
    // 3. POLLING COM NOVA ATIVIDADE (CLASSIFICAÇÃO + EVENT)
    // =========================================================================

    [Fact]
    public async Task T03_LightPolling_WhenNewActivityByThirdParty_ClassifiesAbertoPorTerceiro()
    {
        // Arrange
        const string procId = "1003";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000003/2026-03",
            TotalDocumentos = 2,
            UltimaAtividadeData = "2026-10-28 14:00",
            AbertoPorTerceiro = false,
            Origem = "CAIXA_ENTRADA"
        });

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000003/2026-03", "Desc", "Tipo", "GINFO", null, null)
        });

        // Nova atividade realizada por colega
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: 2,
            UltimaAtividadeData: "2026-10-29 11:30",
            UltimaAtividadeDesc: "Processo aberto na unidade",
            UltimoUsuarioNome: "carlos.coleta",
            ProtocoloFormatado: "00001.000003/2026-03"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId,
            "00001.000003/2026-03",
            "Solicitação",
            "Tipo",
            "GINFO",
            0,
            new List<string> { "GINFO" },
            new List<SeiDocumentItem>(),
            new List<SeiActivityItem>
            {
                new("A1", "29/10/2026", "11:30", "Processo aberto na unidade", "GINFO", "carlos.coleta")
            }
        );

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Equal(1, result.NewActivitiesCount);
        var updated = _db.GetMonitoredProcess(procId);
        Assert.NotNull(updated);
        Assert.True(updated!.AbertoPorTerceiro); // Detectado abertura por terceiro!
        Assert.False(_db.GetLocalReadStatus(procId)); // Notificação pendente
    }

    [Fact]
    public async Task T04_LightPolling_WhenActivityMentionsUser_ClassifiesDestinadoAoUsuario()
    {
        // Arrange
        const string procId = "1004";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000004/2026-04",
            TotalDocumentos = 3,
            UltimaAtividadeData = "2026-10-28 10:00",
            DestinadoAoUsuario = false,
            Origem = "CAIXA_ENTRADA"
        });

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000004/2026-04", "Contrato", "Tipo", "GINFO", null, null)
        });

        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: 3,
            UltimaAtividadeData: "2026-10-29 16:45",
            UltimaAtividadeDesc: "Encaminhado para o servidor Victor para parecer",
            UltimoUsuarioNome: "chefe.unidade",
            ProtocoloFormatado: "00001.000004/2026-04"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId,
            "00001.000004/2026-04",
            "Contrato",
            "Tipo",
            "GINFO",
            0,
            new List<string> { "GINFO" },
            new List<SeiDocumentItem>(),
            new List<SeiActivityItem>
            {
                new("A2", "29/10/2026", "16:45", "Encaminhado para o servidor Victor para parecer", "GINFO", "chefe.unidade")
            }
        );

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Equal(1, result.NewActivitiesCount);
        var updated = _db.GetMonitoredProcess(procId);
        Assert.NotNull(updated);
        Assert.True(updated!.DestinadoAoUsuario); // Match de palavra-chave "Victor"
    }

    // =========================================================================
    // 4. PRESERVAÇÃO DE STATUS LIDO LOCAL
    // =========================================================================

    [Fact]
    public async Task T05_LocalReadStatus_ManualReadStatus_PreservedWhenNoChanges()
    {
        // Arrange
        const string procId = "1005";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000005/2026-05",
            TotalDocumentos = 8,
            UltimaAtividadeData = "2026-10-29 09:00",
            Origem = "CAIXA_ENTRADA"
        });
        _db.SetLocalReadStatus(procId, isRead: true, docCount: 8);

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000005/2026-05", "Desc", "Tipo", "GINFO", null, null)
        });
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId, 8, "2026-10-29 09:00", "Sem novidades", "usuario.x", "00001.000005/2026-05"
        );

        // Act
        await _poller.PollOnceAsync();

        // Assert
        Assert.True(_db.GetLocalReadStatus(procId)); // Permanece lido!
    }

    [Fact]
    public async Task T06_LocalReadStatus_UserOwnAction_DoesNotTriggerUnreadNotification()
    {
        // Arrange
        const string procId = "1006";
        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = "00001.000006/2026-06",
            TotalDocumentos = 4,
            UltimaAtividadeData = "2026-10-28 10:00",
            Origem = "CAIXA_ENTRADA"
        });
        _db.SetLocalReadStatus(procId, isRead: true, docCount: 4);

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000006/2026-06", "Desc", "Tipo", "GINFO", null, null)
        });

        // Ação praticada pelo próprio usuário logado ("victor.lima")
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId, 4, "2026-10-29 17:00", "Despacho emitido", "victor.lima", "00001.000006/2026-06"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, "00001.000006/2026-06", "Desc", "Tipo", "GINFO", 0, new List<string>(),
            new List<SeiDocumentItem>(),
            new List<SeiActivityItem>
            {
                new("A3", "29/10/2026", "17:00", "Despacho emitido", "GINFO", "victor.lima")
            }
        );

        // Act
        await _poller.PollOnceAsync();

        // Assert
        var updated = _db.GetMonitoredProcess(procId);
        Assert.False(updated!.AbertoPorTerceiro); // Não é aberto por terceiro
        Assert.True(_db.GetLocalReadStatus(procId)); // Preserva status lido por ser ação própria
    }

    // =========================================================================
    // 5. RESILIÊNCIA CONTRA FALHAS DE REDE (5XX, TIMEOUT)
    // =========================================================================

    [Fact]
    public async Task T07_NetworkResilience_When5xxError_CatchesExceptionAndLogsSecurely()
    {
        // Arrange
        _client.OnSearchAsync = _ => throw new SeiException(SeiErrorCode.Indisponivel);

        string? syncStatusMsg = null;
        bool isSyncing = true;
        _poller.SyncStatusChanged += (syncing, msg) =>
        {
            isSyncing = syncing;
            syncStatusMsg = msg;
        };

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ErrorsCount >= 1);
        Assert.False(isSyncing);
        Assert.NotNull(syncStatusMsg);

        var logs = _db.GetLogs();
        Assert.NotEmpty(logs);
        Assert.All(logs, l =>
        {
            Assert.DoesNotContain("senha", l.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", l.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task T08_NetworkResilience_BackgroundTimerSurvivesRepeatedErrors()
    {
        // Arrange
        int callCount = 0;
        _client.OnSearchAsync = _ =>
        {
            callCount++;
            if (callCount <= 2) throw new SeiException(SeiErrorCode.Indisponivel);
            return Task.CompletedTask;
        };

        // Act: 1º ciclo falha
        var r1 = await _poller.PollOnceAsync();
        Assert.Equal(1, r1.ErrorsCount);

        // 2º ciclo falha
        var r2 = await _poller.PollOnceAsync();
        Assert.Equal(1, r2.ErrorsCount);

        // 3º ciclo recupera
        var r3 = await _poller.PollOnceAsync();
        Assert.Equal(0, r3.ErrorsCount);
    }

    // =========================================================================
    // 6. CANCELAMENTO GRACIOSO & CONCORRÊNCIA
    // =========================================================================

    [Fact]
    public async Task T09_Cancellation_WhenTokenCancelled_ExitsCleanly()
    {
        using var cts = new CancellationTokenSource();
        await _poller.StartAsync(cts.Token);
        Assert.True(_poller.IsRunning);

        cts.Cancel();
        await _poller.StopAsync();
        Assert.False(_poller.IsRunning);
    }

    [Fact]
    public async Task T10_Concurrency_ConcurrentPolls_AreSerializedBySemaphore()
    {
        // Arrange
        int concurrentCount = 0;
        int maxConcurrentObserved = 0;

        _client.OnSearchAsync = async _ =>
        {
            var count = Interlocked.Increment(ref concurrentCount);
            if (count > maxConcurrentObserved) maxConcurrentObserved = count;
            await Task.Delay(50);
            Interlocked.Decrement(ref concurrentCount);
        };

        // Act
        var t1 = _poller.PollOnceAsync();
        var t2 = _poller.PollOnceAsync();
        await Task.WhenAll(t1, t2);

        // Assert: SemaphoreSlim deve garantir no máximo 1 em execução simultânea
        Assert.Equal(1, maxConcurrentObserved);
    }

    // =========================================================================
    // 7. REGRA CONSTITUCIONAL: NUNCA PERSISTIR CORPO BRUTO DE DOCUMENTOS
    // =========================================================================

    [Fact]
    public async Task T11_Constitutional_NeverPersistsDocumentBodyOrRawHtmlInSQLite()
    {
        // Arrange
        const string procId = "1011";
        var hugeHtmlBody = "<html><body>" + new string('X', 5000) + "</body></html>";

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.000011/2026-11", hugeHtmlBody, "Tipo", "GINFO", null, null)
        });

        _client.LightSummaries[procId] = new LightProcessSummary(
            procId, 1, "2026-10-29 10:00", hugeHtmlBody, "user", "00001.000011/2026-11"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, "00001.000011/2026-11", hugeHtmlBody, "Tipo", "GINFO", 0, new List<string>(),
            new List<SeiDocumentItem>(), new List<SeiActivityItem>()
        );

        // Act
        await _poller.PollOnceAsync();

        // Assert
        var saved = _db.GetMonitoredProcess(procId);
        Assert.NotNull(saved);
        Assert.True(saved!.Descricao?.Length <= 500);
        Assert.True(saved.UltimaAtividadeDesc?.Length <= 500);
    }

    // =========================================================================
    // 8. CENÁRIOS ADICIONAIS DE ROBUSTEZ (M3 EXTENDED)
    // =========================================================================

    [Fact]
    public async Task T12_SettingChanged_DynamicallyUpdatesTimerInterval()
    {
        // Arrange
        await _poller.StartAsync();
        Assert.True(_poller.IsRunning);

        // Act
        _config.CheckIntervalMinutes = 10;

        // Assert
        var logs = _db.GetLogs();
        Assert.Contains(logs, l => l.Message.Contains("10 minutos", StringComparison.OrdinalIgnoreCase));

        await _poller.StopAsync();
    }

    [Fact]
    public async Task T13_UnconfiguredUnidade_ReturnsCleanlyWithoutThrowing()
    {
        // Arrange
        _config.SeiUnidade = "";

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Unidade não configurada", result.ErrorMessage);
    }

    [Fact]
    public async Task T14_SingleProcessError_DoesNotAbortRemainingProcesses()
    {
        // Arrange: 2 processos na busca. O primeiro falha no GetLightSummaryAsync, o segundo tem sucesso.
        const string proc1 = "2001";
        const string proc2 = "2002";

        _client.SearchResult = new ProcessListResult(2, new List<SeiProcessItem>
        {
            new(proc1, "00001.002001/2026-01", "P1", "Tipo", "GINFO", null, null),
            new(proc2, "00001.002002/2026-02", "P2", "Tipo", "GINFO", null, null)
        });

        _client.OnLightSummaryAsync = (id, _) =>
        {
            if (id == proc1) throw new SeiException(SeiErrorCode.Indisponivel);
            return Task.CompletedTask;
        };

        _client.LightSummaries[proc2] = new LightProcessSummary(
            proc2, 3, "2026-10-29 12:00", "Andamento normal", "servidor", "00001.002002/2026-02"
        );
        _client.ProcessDetails[proc2] = new ProcessDetail(
            proc2, "00001.002002/2026-02", "P2", "Tipo", "GINFO", 0, new List<string>(),
            new List<SeiDocumentItem>(), new List<SeiActivityItem>()
        );

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert: proc1 deu erro, mas proc2 foi processado com sucesso!
        Assert.Equal(1, result.ErrorsCount);
        Assert.Equal(1, result.NewProcessesCount);
        Assert.NotNull(_db.GetMonitoredProcess(proc2));
        Assert.Null(_db.GetMonitoredProcess(proc1));
    }

    [Fact]
    public async Task T15_NewProcess_DiscoveredInSearch_UpsertsAndFiresNotification()
    {
        // Arrange
        const string procId = "3001";
        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.003001/2026-01", "Novo Processo Urgente", "Tipo", "GINFO", null, null)
        });

        _client.LightSummaries[procId] = new LightProcessSummary(
            procId, 1, "2026-10-29 15:00", "Autuado", "servidor.novo", "00001.003001/2026-01"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, "00001.003001/2026-01", "Novo Processo Urgente", "Tipo", "GINFO", 0,
            new List<string> { "GINFO" },
            new List<SeiDocumentItem>(), new List<SeiActivityItem>()
        );

        string? pushedNotification = null;
        _poller.NotificationPushed += text => pushedNotification = text;

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Equal(1, result.NewProcessesCount);
        Assert.NotNull(pushedNotification);
        Assert.Contains("00001.003001/2026-01", pushedNotification);

        var saved = _db.GetMonitoredProcess(procId);
        Assert.NotNull(saved);
        Assert.False(saved!.IsReadLocally);
    }
}
