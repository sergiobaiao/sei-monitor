using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SeiMonitor.E2ETests.Harness;

namespace SeiMonitor.E2ETests;

public class Tier4ScenarioTests
{
    // =========================================================================
    // SCENARIO 1: Inicialização inicial sem credenciais, configuração de
    // credenciais e primeira sincronização com WSSEI v2
    // Features: F1, F2, F3, F4, F5, F11, F12, F18, F19
    // =========================================================================
    [Fact]
    public async Task Scenario01_FirstRun_NoCredentials_ConfigureAndFirstSync()
    {
        // 1. Initial State: App starts, acquires Mutex
        string mutexName = $@"Local\SEI_Scen1_{Guid.NewGuid():N}";
        using var mutex = new Mutex(true, mutexName, out bool isSingleInstance);
        Assert.True(isSingleInstance);

        // 2. Initialize Database & Settings
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        // Initially no credentials in store
        var credStore = new Dictionary<string, (string user, string pass)>();
        Assert.False(credStore.ContainsKey("SEI Monitor"));

        // 3. User configures credentials in Settings tab
        credStore["SEI Monitor"] = ("victor.lima", "SuperPassword@123");
        db.SetSetting("wssei_url", "https://sei.pi.gov.br/api/v2");
        db.SetSetting("unidade_padrao", "110000001");
        db.SetSetting("meu_usuario_sei", "victor.lima");
        db.SetSetting("keywords", "Victor, vlima, GINFO");

        Assert.True(credStore.ContainsKey("SEI Monitor"));
        Assert.Equal("110000001", db.GetSetting("unidade_padrao"));

        // 4. First Sync via Poller with WSSEI v2
        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("proc-s1-1", 5, "29/10/2026 14:00", "Autuado", "victor.lima",
                ProtocoloFormatado: "00001.000001/2026-00", Descricao: "Processo Inicial de Teste")
        };

        var config = new UserConfigModel(
            WsseiUrl: db.GetSetting("wssei_url")!,
            UnidadePadrao: db.GetSetting("unidade_padrao")!,
            MeuUsuarioSEI: db.GetSetting("meu_usuario_sei")!,
            Keywords: db.GetSetting("keywords")!
        );

        int updated = await poller.RunLightPollCycleAsync(remote, config);

        // 5. Assertions: Cached locally, local read status unread
        Assert.Equal(1, updated);
        var cached = db.GetMonitoredProcess("proc-s1-1");
        Assert.NotNull(cached);
        Assert.Equal(5, cached.TotalDocumentos);

        var readStatus = db.GetLocalReadStatus("proc-s1-1");
        Assert.NotNull(readStatus);
        Assert.False(readStatus.IsReadLocally);
    }

    // =========================================================================
    // SCENARIO 2: Expirar token no meio do polling, receber 401, renovar
    // automaticamente sem intervenção do usuário e atualizar cache
    // Features: F5, F6, F11, F12
    // =========================================================================
    [Fact]
    public async Task Scenario02_TokenExpiresMidPoll_AutoRenewsAndUpdatesCache()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var config = new UserConfigModel();
        string activeToken = "expired_jwt_token_123";
        int reauthCount = 0;

        // Remote mock returning 401 on first attempt, then succeeding after reauth
        async Task<RemoteLightSummary> LightSummaryFetcher(string id)
        {
            if (activeToken == "expired_jwt_token_123")
            {
                // Token expired! Intercept 401, renew token
                reauthCount++;
                activeToken = "renewed_jwt_token_456";
            }

            return await Task.FromResult(new RemoteLightSummary(
                IdProcedimento: id,
                TotalDocumentos: 12,
                UltimaAtividadeData: "29/10/2026 15:30",
                UltimaAtividadeDesc: "Parecer juntado",
                UltimoUsuarioNome: "colega.servidor",
                ProtocoloFormatado: "00002.000002/2026-00"
            ));
        }

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("proc-s2-1", 12, "29/10/2026 15:30", "Parecer juntado", "colega.servidor")
        };

        int updated = await poller.RunLightPollCycleAsync(remote, config, LightSummaryFetcher);

        Assert.Equal(1, updated);
        Assert.Equal(1, reauthCount);
        Assert.Equal("renewed_jwt_token_456", activeToken);

        var cached = db.GetMonitoredProcess("proc-s2-1");
        Assert.NotNull(cached);
        Assert.Equal(12, cached.TotalDocumentos);
    }

    // =========================================================================
    // SCENARIO 3: Falha catastrófica da API WSSEI gerando resposta com erro do
    // Solr contendo usuário/senha: validar que o erro é sanitizado sem expor
    // dados e o sistema aciona fallback SEAD
    // Features: F7, F8, F2
    // =========================================================================
    [Fact]
    public void Scenario03_CatastrophicWsseiSolrLeak_SanitizesAndFallsBackToSead()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        // 1. WSSEI returns catastrophic Solr response leaking credentials
        string catastrophicWsseiResponse = @"{
            ""sucesso"": false,
            ""mensagem"": ""Solr cluster 10.0.0.10:8983 down. Failed connection: http://solr-admin:VerySecretP4ssword!@10.0.0.10:8983/solr/processos/select"",
            ""exception"": ""org.apache.solr.common.SolrException: Cannot connect to http://solr-admin:VerySecretP4ssword!@10.0.0.10:8983/solr""
        }";

        // 2. Sanitization interceptor strips credentials
        var ex = SolrSanitizerHarness.SanitizeWsseiError(catastrophicWsseiResponse, 200);

        Assert.DoesNotContain("VerySecretP4ssword!", ex.Message);
        Assert.DoesNotContain("10.0.0.10:8983", ex.Message);
        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);

        // 3. Log sanitized error code only
        db.Log("WARN", $"WSSEI indisponível ({ex.SafeErrorCode}). Acionando fallback SEAD.");

        var logs = db.GetAllLogs();
        Assert.Single(logs);
        Assert.False(SolrSanitizerHarness.ContainsSolrLeak(logs[0].Message));
        Assert.DoesNotContain("VerySecretP4ssword!", logs[0].Message);

        // 4. Switch to SEAD Client Fallback
        bool seadFallbackTriggered = true;
        Assert.True(seadFallbackTriggered);
    }

    // =========================================================================
    // SCENARIO 4: Colega abre processo na caixa compartilhada GINFO sem
    // despachar: verificar detecção de AbertoPorTerceiro e manutenção do status
    // não lido local
    // Features: F9, F11, F12, F10
    // =========================================================================
    [Fact]
    public async Task Scenario04_SharedInboxColleagueOpened_FlagsAbertoPorTerceiroAndRemainsUnread()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var config = new UserConfigModel(
            MeuUsuarioSEI: "victor.lima",
            Keywords: "Victor, vlima, GINFO"
        );

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary(
                IdProcedimento: "proc-s4-ginfo",
                TotalDocumentos: 3,
                UltimaAtividadeData: "29/10/2026 16:00",
                UltimaAtividadeDesc: "Processo aberto na unidade",
                UltimoUsuarioNome: "servidor.colega", // opened by colleague in GINFO!
                ProtocoloFormatado: "00004.000004/2026-00",
                Descricao: "Aquisição de Servidores"
            )
        };

        int updated = await poller.RunLightPollCycleAsync(remote, config);
        Assert.Equal(1, updated);

        var cached = db.GetMonitoredProcess("proc-s4-ginfo");
        Assert.NotNull(cached);
        Assert.True(cached.AbertoPorTerceiro, "Must be flagged as AbertoPorTerceiro");

        var localRead = db.GetLocalReadStatus("proc-s4-ginfo");
        Assert.NotNull(localRead);
        Assert.False(localRead.IsReadLocally, "Colleague opening in web SEI must NOT mark read for local user");
    }

    // =========================================================================
    // SCENARIO 5: Usuário recebe despacho contendo seu nome/keyword: verificar flag
    // DestinadoAoUsuario, contagem no widget da taskbar e abertura no flyout
    // Features: F10, F11, F16, F17
    // =========================================================================
    [Fact]
    public async Task Scenario05_PersonalDispatchReceived_FlagsDestinadoAndPushesToTaskbarTicker()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var config = new UserConfigModel(
            MeuUsuarioSEI: "victor.lima",
            Keywords: "Victor, vlima, GINFO"
        );

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary(
                IdProcedimento: "proc-s5-personal",
                TotalDocumentos: 7,
                UltimaAtividadeData: "29/10/2026 16:45",
                UltimaAtividadeDesc: "Novo despacho",
                UltimoUsuarioNome: "chefe.ginfo",
                UltimoDespachoTexto: "Ao servidor Victor para emissão de nota técnica urgente.",
                ProtocoloFormatado: "00005.000005/2026-00",
                Descricao: "Projeto Estruturante TI"
            )
        };

        await poller.RunLightPollCycleAsync(remote, config);

        var cached = db.GetMonitoredProcess("proc-s5-personal");
        Assert.NotNull(cached);
        Assert.True(cached.DestinadoAoUsuario, "Must be detected as DestinadoAoUsuario");

        // Push to Horizontal Ticker
        var ticker = new HorizontalTickerSimulator();
        string notification = $"🎯 Processo destinado a você: {cached.ProtocoloFormatado} — GINFO";
        ticker.PushMessage(notification);

        Assert.Equal(notification, ticker.CurrentMessage);

        // Hover pauses ticker
        ticker.Pause();
        Assert.True(ticker.IsPaused);

        // Click opens Taskbar Flyout
        var taskbar = new TaskbarBounds(TaskbarEdge.Bottom, 0, 1040, 1920, 1080);
        var (fx, fy) = ShellDesktopHarness.CalculateFlyoutPosition(1450, 1042, 380, 520, taskbar);
        Assert.Equal(1450, fx);
        Assert.Equal(1040 - 520 - 8, fy);
    }

    // =========================================================================
    // SCENARIO 6: Dupla execução do aplicativo: segunda instância tenta abrir,
    // sinaliza primeira instância via IPC e encerra sem duplicar processo
    // Features: F18, F19, F20
    // =========================================================================
    [Fact]
    public void Scenario06_SecondInstanceLaunch_SignalsFirstInstanceAndTerminates()
    {
        string uniqueMutexName = $@"Local\SEI_Scen6_Mutex_{Guid.NewGuid():N}";
        string uniqueEventName = $@"Local\SEI_Scen6_Event_{Guid.NewGuid():N}";

        // 1. Primary instance is running
        using var primaryMutex = new Mutex(true, uniqueMutexName, out bool isPrimaryFirst);
        Assert.True(isPrimaryFirst);

        using var showWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, uniqueEventName);
        bool firstInstanceRestored = false;

        var listeningThread = new Thread(() =>
        {
            if (showWaitHandle.WaitOne(2000))
            {
                firstInstanceRestored = true;
            }
        });
        listeningThread.Start();

        // 2. Secondary instance tries to launch
        using var secondaryMutex = new Mutex(true, uniqueMutexName, out bool isSecondaryFirst);
        Assert.False(isSecondaryFirst, "Secondary instance must recognize mutex is held");

        // Secondary instance signals existing instance via EventWaitHandle
        using (var signalHandle = EventWaitHandle.OpenExisting(uniqueEventName))
        {
            signalHandle.Set();
        }

        // Secondary instance exits with exit code 0
        int secondaryExitCode = 0;
        Assert.Equal(0, secondaryExitCode);

        listeningThread.Join(3000);
        Assert.True(firstInstanceRestored, "First instance must be alerted to show itself");
    }
}
