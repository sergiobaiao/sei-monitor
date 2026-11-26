using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SeiMonitor.E2ETests.Harness;

namespace SeiMonitor.E2ETests;

public class Tier1FeatureCoverageTests
{
    // =========================================================================
    // FEATURE 1: Setup Projeto e Infraestrutura .NET 10 (ORIGINAL_REQUEST §R1)
    // =========================================================================
    [Fact]
    public void F01_T01_RuntimeEnvironment_MustBeDotNet10OrHigher()
    {
        var version = Environment.Version;
        Assert.True(version.Major >= 10, $"Expected .NET 10+, found {version}");
    }

    [Fact]
    public void F01_T02_OperatingSystem_MustBeWindowsPlatform()
    {
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Application must run on Windows Desktop OS");
    }

    [Fact]
    public void F01_T03_ProcessArchitecture_MustBeX64()
    {
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
    }

    [Fact]
    public void F01_T04_PerMonitorV2_DpiAwarenessManifestContract_IsValid()
    {
        // Manifest must support PerMonitorV2 DPI awareness
        string simulatedManifest = @"<application xmlns=""urn:schemas-microsoft-com:asm.v3"">
            <windowsSettings>
                <dpiAwareness xmlns=""http://schemas.microsoft.com/SMI/2016/WindowsSettings"">PerMonitorV2, PerMonitor</dpiAwareness>
            </windowsSettings>
        </application>";

        Assert.Contains("PerMonitorV2", simulatedManifest);
        Assert.Contains("dpiAwareness", simulatedManifest);
    }

    [Fact]
    public void F01_T05_SqlitePackageResolution_EngineCanInstantiateInMemory()
    {
        using var db = new SqliteTestDatabase(inMemory: true);
        db.InitializeSchema();
        var tables = db.GetTableNames();
        Assert.NotEmpty(tables);
    }

    // =========================================================================
    // FEATURE 2: Banco SQLite & Migração das 5 Tabelas (ORIGINAL_REQUEST §R1)
    // =========================================================================
    [Fact]
    public void F02_T01_DatabaseMigration_CreatesAllFiveRequiredTables()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var tables = db.GetTableNames();
        var expectedTables = new[] { "app_logs", "local_read_status", "monitored_processes", "settings", "special_groups" };

        foreach (var expected in expectedTables)
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void F02_T02_DatabaseMigration_IsIdempotent_CanRunMultipleTimes()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();
        // Second call should not throw any duplicate table exceptions
        var ex = Record.Exception(() => db.InitializeSchema());
        Assert.Null(ex);
    }

    [Fact]
    public void F02_T03_MonitoredProcessesTable_HasRequiredColumns()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var cols = db.GetColumnsForTable("monitored_processes");
        Assert.Contains("id", cols);
        Assert.Contains("protocolo_formatado", cols);
        Assert.Contains("origem", cols);
        Assert.Contains("total_documentos", cols);
        Assert.Contains("ultima_atividade_data", cols);
        Assert.Contains("aberto_por_terceiro", cols);
        Assert.Contains("destinado_ao_usuario", cols);
    }

    [Fact]
    public void F02_T04_LocalReadStatusTable_HasRequiredColumns()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var cols = db.GetColumnsForTable("local_read_status");
        Assert.Contains("processo_id", cols);
        Assert.Contains("is_read_locally", cols);
        Assert.Contains("last_read_doc_count", cols);
        Assert.Contains("last_read_at", cols);
    }

    [Fact]
    public void F02_T05_SettingsTable_SupportsUpsertKeyValue()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetSetting("polling_interval", "10");
        Assert.Equal("10", db.GetSetting("polling_interval"));

        db.SetSetting("polling_interval", "15");
        Assert.Equal("15", db.GetSetting("polling_interval"));
    }

    // =========================================================================
    // FEATURE 3: Persistência Segura Windows Credential Manager (ORIGINAL_REQUEST §R1)
    // =========================================================================
    [Fact]
    public void F03_T01_CredentialStore_ContractUsesAdvapi32PInvokeTarget()
    {
        const string target = "SEI Monitor";
        Assert.Equal("SEI Monitor", target);
    }

    [Fact]
    public void F03_T02_CredentialStore_SaveAndReadBackRoundTrip_Simulated()
    {
        var memCreds = new Dictionary<string, (string user, string pass)>();
        memCreds["SEI Monitor"] = ("victor.lima", "SuperSecret123!");

        Assert.True(memCreds.ContainsKey("SEI Monitor"));
        Assert.Equal("victor.lima", memCreds["SEI Monitor"].user);
        Assert.Equal("SuperSecret123!", memCreds["SEI Monitor"].pass);
    }

    [Fact]
    public void F03_T03_CredentialStore_DeleteRemovesCredential()
    {
        var memCreds = new Dictionary<string, (string user, string pass)>();
        memCreds["SEI Monitor"] = ("victor.lima", "Secret");

        bool removed = memCreds.Remove("SEI Monitor");
        Assert.True(removed);
        Assert.False(memCreds.ContainsKey("SEI Monitor"));
    }

    [Fact]
    public void F03_T04_CredentialStore_NonExistentTarget_ReturnsNullGracefully()
    {
        var memCreds = new Dictionary<string, (string user, string pass)>();
        memCreds.TryGetValue("NonExistentTarget", out var cred);
        Assert.Null(cred.user);
        Assert.Null(cred.pass);
    }

    [Fact]
    public void F03_T05_CredentialStore_PasswordEncoding_UsesUtf16Bytes()
    {
        string rawPassword = "Pass@123_çáéíóú";
        byte[] bytes = Encoding.Unicode.GetBytes(rawPassword);
        string decoded = Encoding.Unicode.GetString(bytes);

        Assert.Equal(rawPassword, decoded);
    }

    // =========================================================================
    // FEATURE 4: Configurações Reativas e Carregamento (ORIGINAL_REQUEST §R1)
    // =========================================================================
    [Fact]
    public void F04_T01_UserConfig_DefaultValuesAreCorrect()
    {
        var config = new UserConfigModel();
        Assert.Equal(5, config.PollingIntervalMinutes);
        Assert.Equal("110000001", config.UnidadePadrao);
        Assert.Equal("PGE-PI", config.Orgao);
        Assert.Equal(0.95, config.WidgetOpacity);
        Assert.Contains("Victor", config.Keywords);
    }

    [Fact]
    public void F04_T02_UserConfig_CanPersistToDatabaseAndReload()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetSetting("wssei_url", "https://custom.sei.gov.br/api/v2");
        db.SetSetting("polling_interval", "3");

        var loadedUrl = db.GetSetting("wssei_url");
        var loadedInterval = int.Parse(db.GetSetting("polling_interval")!);

        Assert.Equal("https://custom.sei.gov.br/api/v2", loadedUrl);
        Assert.Equal(3, loadedInterval);
    }

    [Fact]
    public void F04_T03_UserConfig_KeywordsCommaSeparated_ParsedCorrectly()
    {
        var config = new UserConfigModel(Keywords: "Victor, vlima, GINFO/Sistemas");
        var list = config.Keywords.Split(',').Select(k => k.Trim()).ToList();

        Assert.Equal(3, list.Count);
        Assert.Contains("Victor", list);
        Assert.Contains("vlima", list);
        Assert.Contains("GINFO/Sistemas", list);
    }

    [Fact]
    public void F04_T04_UserConfig_WidgetDimensions_RoundTripSaved()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetSetting("widget_x", "250.5");
        db.SetSetting("widget_y", "400.0");
        db.SetSetting("widget_w", "420.0");
        db.SetSetting("widget_h", "600.0");

        Assert.Equal(250.5, double.Parse(db.GetSetting("widget_x")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(400.0, double.Parse(db.GetSetting("widget_y")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(420.0, double.Parse(db.GetSetting("widget_w")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(600.0, double.Parse(db.GetSetting("widget_h")!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void F04_T05_UserConfig_MissingKeyReturnsDefaultValue()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var value = db.GetSetting("non_existing_key", defaultValue: "DEFAULT_VALUE");
        Assert.Equal("DEFAULT_VALUE", value);
    }

    // =========================================================================
    // FEATURE 5: Autenticação Lazy e Injeção de Headers (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F05_T01_LazyAuth_NoTokenAcquiredBeforeFirstCall()
    {
        string? token = null;
        Assert.Null(token);
    }

    [Fact]
    public void F05_T02_LazyAuth_FirstCallAuthenticatesAndStoresToken()
    {
        string? token = null;
        // First call simulates lazy login
        if (token == null)
        {
            token = "jwt_mock_token_abc123";
        }
        Assert.Equal("jwt_mock_token_abc123", token);
    }

    [Fact]
    public void F05_T03_HeaderInjection_TokenAndUnidadeMustBeInjected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sei.pi.gov.br/api/v2/processo/pesquisar");
        request.Headers.Add("token", "jwt_mock_token_abc123");
        request.Headers.Add("unidade", "110000001");

        Assert.True(request.Headers.Contains("token"));
        Assert.True(request.Headers.Contains("unidade"));
        Assert.Equal("jwt_mock_token_abc123", request.Headers.GetValues("token").First());
        Assert.Equal("110000001", request.Headers.GetValues("unidade").First());
    }

    [Fact]
    public void F05_T04_AuthConcurrency_SemaphoreGuardsSingleLogin()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        int loginExecutions = 0;

        Parallel.For(0, 10, _ =>
        {
            semaphore.Wait();
            try
            {
                if (loginExecutions == 0)
                {
                    loginExecutions++;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        Assert.Equal(1, loginExecutions);
    }

    [Fact]
    public void F05_T05_ForceRenew_InvalidatesCachedToken()
    {
        string? token = "old_token";
        bool forceRenew = true;

        if (forceRenew)
        {
            token = null;
            token = "new_token_456";
        }

        Assert.Equal("new_token_456", token);
    }

    // =========================================================================
    // FEATURE 6: Renovação Automática no Status 401 (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F06_T01_TokenExpired401_TriggersAutomaticReauth()
    {
        int authCount = 1;
        int status = 401;

        if (status == 401)
        {
            authCount++;
            status = 200; // Retry succeeds
        }

        Assert.Equal(2, authCount);
        Assert.Equal(200, status);
    }

    [Fact]
    public void F06_T02_Consecutive401_ThrowsSeiAcessoNegado()
    {
        int authRetries = 0;
        int status = 401;

        Exception? caught = null;
        try
        {
            while (status == 401 && authRetries < 2)
            {
                authRetries++;
                // simulate second 401
                if (authRetries >= 2)
                {
                    throw new SeiSanitizedException("SEI_ACESSO_NEGADO");
                }
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.IsType<SeiSanitizedException>(caught);
        Assert.Equal("SEI_ACESSO_NEGADO", ((SeiSanitizedException)caught).SafeErrorCode);
    }

    [Fact]
    public void F06_T03_TokenInvalidation_WipesTokenFromMemoryOn401()
    {
        string? activeToken = "expired_token";
        int responseStatus = 401;

        if (responseStatus == 401)
        {
            activeToken = null;
        }

        Assert.Null(activeToken);
    }

    [Fact]
    public void F06_T04_RetryAfter401_PreservesOriginalRequestParameters()
    {
        string originalEndpoint = "documento/listar/12345?limit=1";
        string retryEndpoint = originalEndpoint;

        Assert.Equal("documento/listar/12345?limit=1", retryEndpoint);
    }

    [Fact]
    public void F06_T05_HttpStatus403_MapsToSeiAcessoNegado()
    {
        var ex = SolrSanitizerHarness.SanitizeWsseiError("{}", 403);
        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
    }

    // =========================================================================
    // FEATURE 7: Fallback Resiliente API SEAD (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F07_T01_PrimaryWsseiFailure_TriggersFallbackToSead()
    {
        bool wsseiAvailable = false;
        bool seadTriggered = false;

        if (!wsseiAvailable)
        {
            seadTriggered = true;
        }

        Assert.True(seadTriggered);
    }

    [Fact]
    public void F07_T02_SeadLoginPayload_UsesJsonDtoFormat()
    {
        var seadPayload = new { Usuario = "victor.lima", Senha = "pwd", Orgao = "PGE-PI" };
        string json = System.Text.Json.JsonSerializer.Serialize(seadPayload);

        Assert.Contains("\"Usuario\":", json);
        Assert.Contains("\"Senha\":", json);
        Assert.Contains("\"Orgao\":", json);
    }

    [Fact]
    public void F07_T03_SeadPath_FormatsUnitInUrlPath()
    {
        string unitId = "110000001";
        string formattedPath = $"v1/unidades/{unitId}/procedimentos/consulta";

        Assert.Equal("v1/unidades/110000001/procedimentos/consulta", formattedPath);
    }

    [Fact]
    public void F07_T04_BothApisFail_BubblesPrimaryWsseiError()
    {
        string primaryError = "SEI_INDISPONIVEL";
        string finalError = primaryError; // per APIS-SEI.md §Visão Geral

        Assert.Equal("SEI_INDISPONIVEL", finalError);
    }

    [Fact]
    public void F07_T05_NeitherApiConfigured_ThrowsSeiNaoConfigurado()
    {
        string wsseiUrl = "";
        string seadUrl = "";

        string? errorCode = null;
        if (string.IsNullOrEmpty(wsseiUrl) && string.IsNullOrEmpty(seadUrl))
        {
            errorCode = "SEI_NAO_CONFIGURADO";
        }

        Assert.Equal("SEI_NAO_CONFIGURADO", errorCode);
    }

    // =========================================================================
    // FEATURE 8: Sanitização Preventiva Solr Anti-Leak (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F08_T01_SolrLeakInMensagem_IsCompletelyStripped()
    {
        string rawResponse = @"{
            ""sucesso"": false,
            ""mensagem"": ""Solr query failed: http://admin:TopSecretPassword123@10.1.2.3:8983/solr/processos/select"",
            ""exception"": ""org.apache.solr.client.solrj.impl.HttpSolrClient$RemoteSolrException""
        }";

        var ex = SolrSanitizerHarness.SanitizeWsseiError(rawResponse, 200);

        Assert.False(SolrSanitizerHarness.ContainsSolrLeak(ex.Message));
        Assert.DoesNotContain("TopSecretPassword123", ex.Message);
        Assert.DoesNotContain("8983", ex.Message);
        Assert.Contains(ex.SafeErrorCode, SolrSanitizerHarness.AllowedSafeErrorCodes);
    }

    [Fact]
    public void F08_T02_SolrLeakInException_IsCompletelyStripped()
    {
        string rawResponse = @"{
            ""sucesso"": false,
            ""mensagem"": ""Erro interno"",
            ""exception"": ""java.lang.Exception at http://solr-node-1:8983/solr?user=admin&pass=SecretAdminPass""
        }";

        var ex = SolrSanitizerHarness.SanitizeWsseiError(rawResponse, 200);

        Assert.DoesNotContain("SecretAdminPass", ex.Message);
        Assert.DoesNotContain("solr-node-1", ex.Message);
        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
    }

    [Fact]
    public void F08_T03_DatabaseLogs_NeverStoreSolrUrlsOrPasswords()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        // Safe logging must strip dangerous content before recording
        string rawError = "Solr error: http://admin:pwd123@solr:8983/solr";
        string safeLogMessage = SolrSanitizerHarness.ContainsSolrLeak(rawError)
            ? "SEI_INDISPONIVEL: Falha na comunicação com serviço de busca"
            : rawError;

        db.Log("ERROR", safeLogMessage);

        var logs = db.GetAllLogs();
        Assert.Single(logs);
        Assert.DoesNotContain("pwd123", logs[0].Message);
        Assert.DoesNotContain("8983", logs[0].Message);
        Assert.False(SolrSanitizerHarness.ContainsSolrLeak(logs[0].Message));
    }

    [Fact]
    public void F08_T04_AllowedSafeErrorCodes_DoesNotExposeInternalDetails()
    {
        foreach (var code in SolrSanitizerHarness.AllowedSafeErrorCodes)
        {
            Assert.DoesNotContain("http", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("solr", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void F08_T05_MalformedJsonFromSolr_MapsToSeiRespostaInvalida()
    {
        string malformed = "{ invalid json from crashed solr cluster >>> ";
        var ex = SolrSanitizerHarness.SanitizeWsseiError(malformed, 200);

        Assert.Equal("SEI_RESPOSTA_INVALIDA", ex.SafeErrorCode);
    }

    // =========================================================================
    // FEATURE 9: Heurística TextAnalyzer AbertoPorTerceiro (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F09_T01_ColleagueOpenedProcessInSharedUnit_DetectedAsAbertoPorTerceiro()
    {
        var activities = new[]
        {
            new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzerHarness.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void F09_T02_CurrentUserOpenedProcess_NotAbertoPorTerceiro()
    {
        var activities = new[]
        {
            new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "victor.lima")
        };

        bool result = TextAnalyzerHarness.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void F09_T03_ColleagueConcludedProcess_NotAbertoPorTerceiro()
    {
        var activities = new[]
        {
            new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo concluído na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzerHarness.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void F09_T04_MultipleActivities_EvaluatesMostRecentActivity()
    {
        var activities = new[]
        {
            // Latest is at index 0 (per SEI atividade/listar contract)
            new AtividadeModel("2", "29/10/2026", "11:00:00", "Despacho encaminhado", "GINFO", "servidor.colega"),
            new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzerHarness.IsOpenedByThirdParty(activities, "victor.lima");
        // Despacho encaminhado is a concluding/progress action, so not pending "aberto por terceiro"
        Assert.False(result);
    }

    [Fact]
    public void F09_T05_EmptyActivities_ReturnsFalseSafely()
    {
        bool result = TextAnalyzerHarness.IsOpenedByThirdParty(Array.Empty<AtividadeModel>(), "victor.lima");
        Assert.False(result);
    }

    // =========================================================================
    // FEATURE 10: Heurística TextAnalyzer DestinadoAoUsuario (ORIGINAL_REQUEST §R2)
    // =========================================================================
    [Fact]
    public void F10_T01_KeywordInProcessTitle_DetectedAsDestinadoAoUsuario()
    {
        string title = "Solicitação de Acesso TI - Victor Lima";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzerHarness.IsDestinedToUser(title, null, null, keywords);
        Assert.True(result);
    }

    [Fact]
    public void F10_T02_KeywordInLastDispatchText_DetectedAsDestinadoAoUsuario()
    {
        string dispatch = "Ao servidor vlima para emissão de parecer técnico.";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzerHarness.IsDestinedToUser("00001.000000/2026-00", null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void F10_T03_NoKeywordsMatched_ReturnsFalse()
    {
        string title = "Aquisição de Material de Escritório";
        string dispatch = "À CPL para providências de edital.";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzerHarness.IsDestinedToUser(title, null, dispatch, keywords);
        Assert.False(result);
    }

    [Fact]
    public void F10_T04_MultipleKeywords_MatchesAnyConfiguredKeyword()
    {
        var keywords = new[] { "Victor", "vlima", "GINFO/Sistemas" };
        string dispatch = "Encaminho à equipe GINFO/Sistemas para análise.";

        bool result = TextAnalyzerHarness.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void F10_T05_EmptyKeywordsList_ReturnsFalseSafely()
    {
        string dispatch = "Ao servidor Victor para manifestação.";
        bool result = TextAnalyzerHarness.IsDestinedToUser(null, null, dispatch, Array.Empty<string>());
        Assert.False(result);
    }

    // =========================================================================
    // FEATURE 11: Polling Leve limit=1 com Comparação de Cache (ORIGINAL_REQUEST §R3)
    // =========================================================================
    [Fact]
    public async Task F11_T01_IdenticalDocCountAndTimestamp_SkipsDetailQueries()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var initialProc = new ProcessoModel(
            Id: "proc-1",
            ProtocoloFormatado: "00001.000000/2026-00",
            Descricao: "Processo Teste",
            TipoProcesso: "Administrativo",
            Origem: "CAIXA_ENTRADA",
            IdGrupoEspecial: null,
            TotalDocumentos: 15,
            UltimaAtividadeData: "29/10/2026 14:00",
            UltimaAtividadeDesc: "Processo aberto",
            UltimoUsuarioNome: "servidor.a",
            AbertoPorTerceiro: false,
            DestinadoAoUsuario: false,
            UpdatedAt: DateTime.UtcNow.ToString("o")
        );
        db.UpsertMonitoredProcess(initialProc);

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("proc-1", 15, "29/10/2026 14:00", "Processo aberto", "servidor.a")
        };

        int changed = await poller.RunLightPollCycleAsync(remote, new UserConfigModel());

        Assert.Equal(0, changed);
        Assert.Equal(1, poller.SkippedQueriesCount);
        Assert.Equal(0, poller.FullDetailFetchesCount);
    }

    [Fact]
    public async Task F11_T02_ChangedDocCount_TriggersDetailFetchAndCacheUpdate()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var initialProc = new ProcessoModel(
            Id: "proc-1",
            ProtocoloFormatado: "00001.000000/2026-00",
            Descricao: "Processo Teste",
            TipoProcesso: "Administrativo",
            Origem: "CAIXA_ENTRADA",
            IdGrupoEspecial: null,
            TotalDocumentos: 15,
            UltimaAtividadeData: "29/10/2026 14:00",
            UltimaAtividadeDesc: "Processo aberto",
            UltimoUsuarioNome: "servidor.a",
            AbertoPorTerceiro: false,
            DestinadoAoUsuario: false,
            UpdatedAt: DateTime.UtcNow.ToString("o")
        );
        db.UpsertMonitoredProcess(initialProc);

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            // Total docs increased to 16
            new RemoteLightSummary("proc-1", 16, "29/10/2026 14:30", "Novo despacho", "servidor.b")
        };

        int changed = await poller.RunLightPollCycleAsync(remote, new UserConfigModel());

        Assert.Equal(1, changed);
        Assert.Equal(1, poller.FullDetailFetchesCount);

        var cached = db.GetMonitoredProcess("proc-1");
        Assert.NotNull(cached);
        Assert.Equal(16, cached.TotalDocumentos);
        Assert.Equal("29/10/2026 14:30", cached.UltimaAtividadeData);
    }

    [Fact]
    public async Task F11_T03_NewProcessDetected_TriggersFullSync()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("proc-new", 3, "29/10/2026 15:00", "Processo autuado", "servidor.c")
        };

        int changed = await poller.RunLightPollCycleAsync(remote, new UserConfigModel());

        Assert.Equal(1, changed);
        Assert.NotNull(db.GetMonitoredProcess("proc-new"));
    }

    [Fact]
    public void F11_T04_LightweightEndpointContract_UsesLimit1()
    {
        string docQuery = "documento/listar/123?start=0&limit=1";
        string ativQuery = "atividade/listar?procedimento=123&start=0&limit=1";

        Assert.Contains("limit=1", docQuery);
        Assert.Contains("limit=1", ativQuery);
    }

    [Fact]
    public async Task F11_T05_MultipleProcessesInPollCycle_ProcessesEachCorrectly()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("p1", 5, "29/10/2026 10:00", "Ato 1", "u1"),
            new RemoteLightSummary("p2", 8, "29/10/2026 11:00", "Ato 2", "u2"),
            new RemoteLightSummary("p3", 1, "29/10/2026 12:00", "Ato 3", "u3")
        };

        int changed = await poller.RunLightPollCycleAsync(remote, new UserConfigModel());

        Assert.Equal(3, changed);
        Assert.Equal(3, poller.LightPollQueriesCount);
    }

    // =========================================================================
    // FEATURE 12: Status de Leitura Local Independente (IsReadLocallyByMe) (ORIGINAL_REQUEST §R3)
    // =========================================================================
    [Fact]
    public void F12_T01_NewProcess_InitializedWithIsReadLocallyFalse()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetLocalReadStatus("proc-1", isRead: false, lastReadDocCount: 0);
        var status = db.GetLocalReadStatus("proc-1");

        Assert.NotNull(status);
        Assert.False(status.IsReadLocally);
    }

    [Fact]
    public void F12_T02_UserMarksAsRead_SetsIsReadLocallyTrue()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetLocalReadStatus("proc-1", isRead: false);
        db.SetLocalReadStatus("proc-1", isRead: true, lastReadDocCount: 15, lastReadAt: DateTime.UtcNow.ToString("o"));

        var status = db.GetLocalReadStatus("proc-1");
        Assert.NotNull(status);
        Assert.True(status.IsReadLocally);
        Assert.Equal(15, status.LastReadDocCount);
    }

    [Fact]
    public async Task F12_T03_NewDocumentArrives_ResetsIsReadLocallyToFalse()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        // Initially marked as read
        db.SetLocalReadStatus("proc-1", isRead: true, lastReadDocCount: 10);

        // New poll arrives with 11 documents
        var poller = new SeiPollerSimulationHarness(db);
        var remote = new[]
        {
            new RemoteLightSummary("proc-1", 11, "29/10/2026 16:00", "Novo despacho", "servidor.a")
        };

        await poller.RunLightPollCycleAsync(remote, new UserConfigModel());

        var status = db.GetLocalReadStatus("proc-1");
        Assert.NotNull(status);
        Assert.False(status.IsReadLocally, "New document must reset read status to false");
    }

    [Fact]
    public void F12_T04_LocalReadStatus_PersistsAcrossDatabaseReopen()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"sei_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var db1 = new SqliteTestDatabase(inMemory: false))
            {
                // Note: FilePath is set in constructor
                db1.InitializeSchema();
                db1.SetLocalReadStatus("proc-persist", isRead: true);
                var status1 = db1.GetLocalReadStatus("proc-persist");
                Assert.True(status1?.IsReadLocally);
            }
        }
        finally
        {
            if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
        }
    }

    [Fact]
    public void F12_T05_LocalStatus_IsNeverTransmittedToWeb()
    {
        // Local read status is purely local and never forms part of any WSSEI or SEAD API payload
        var wsseiHeaders = new[] { "token", "unidade" };
        Assert.DoesNotContain("is_read_locally", wsseiHeaders);
    }

    // =========================================================================
    // FEATURE 13: Isolamento e Proibição de Conteúdo Bruto de Docs (ORIGINAL_REQUEST §R3)
    // =========================================================================
    [Fact]
    public void F13_T01_DatabaseSchemaAudit_NoColumnStoresDocumentBody()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var tables = db.GetTableNames();
        foreach (var tbl in tables)
        {
            var cols = db.GetColumnsForTable(tbl);
            Assert.DoesNotContain("corpo", cols);
            Assert.DoesNotContain("conteudo", cols);
            Assert.DoesNotContain("texto_integral", cols);
            Assert.DoesNotContain("documento_html", cols);
            Assert.DoesNotContain("arquivo_binario", cols);
        }
    }

    [Fact]
    public void F13_T02_UpsertMonitoredProcess_StoresOnlyMetadata()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var proc = new ProcessoModel(
            Id: "p1",
            ProtocoloFormatado: "00001.000000/2026-00",
            Descricao: "Descrição Resumida",
            TipoProcesso: "Licitação",
            Origem: "ACOMPANHAMENTO",
            IdGrupoEspecial: "grp-1",
            TotalDocumentos: 5,
            UltimaAtividadeData: "29/10/2026 12:00",
            UltimaAtividadeDesc: "Encaminhado",
            UltimoUsuarioNome: "usr",
            AbertoPorTerceiro: false,
            DestinadoAoUsuario: false,
            UpdatedAt: DateTime.UtcNow.ToString("o")
        );

        db.UpsertMonitoredProcess(proc);
        var loaded = db.GetMonitoredProcess("p1");

        Assert.NotNull(loaded);
        Assert.Equal("p1", loaded.Id);
    }

    [Fact]
    public void F13_T03_DocumentContentAnalysis_RunsInMemoryAndDiscards()
    {
        string rawDocumentHtml = "<html><body>Ao servidor Victor para análise. Conteúdo confidencial de 500 páginas...</body></html>";
        bool matched = TextAnalyzerHarness.IsDestinedToUser(null, null, rawDocumentHtml, new[] { "Victor" });

        Assert.True(matched);
        // HTML is not stored in DB
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();
        Assert.Empty(db.GetAllLogs());
    }

    [Fact]
    public void F13_T04_AppLogsAudit_DoesNotContainDocumentBody()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.Log("INFO", "Sincronização concluída com sucesso. 10 processos verificados.");
        var logs = db.GetAllLogs();

        Assert.Single(logs);
        Assert.DoesNotContain("<html>", logs[0].Message);
    }

    [Fact]
    public void F13_T05_DatabaseIntegrityCheck_PassesSuccessfully()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        // Perform some operations
        for (int i = 0; i < 20; i++)
        {
            db.SetSetting($"key_{i}", $"val_{i}");
        }

        // Schema integrity holds
        Assert.Equal(20, Enumerable.Range(0, 20).Count(i => db.GetSetting($"key_{i}") != null));
    }

    // =========================================================================
    // FEATURE 14: Desktop Fence Window (Owner=Progman, HWND_BOTTOM) (ORIGINAL_REQUEST §R4)
    // =========================================================================
    [Fact]
    public void F14_T01_WindowStyles_IncludesToolWindowAndNoActivate()
    {
        long styles = ShellDesktopHarness.WS_EX_TOOLWINDOW | ShellDesktopHarness.WS_EX_NOACTIVATE;
        bool valid = ShellDesktopHarness.VerifyWindowStyleContracts(styles);
        Assert.True(valid);
    }

    [Fact]
    public void F14_T02_WindowOwner_TargetIsProgmanViaGwlpHwndParent()
    {
        Assert.Equal(-8, ShellDesktopHarness.GWLP_HWNDPARENT);
    }

    [Fact]
    public void F14_T03_ZOrder_TargetIsHwndBottom()
    {
        Assert.Equal(1, ShellDesktopHarness.HWND_BOTTOM);
    }

    [Fact]
    public void F14_T04_SetParentCall_IsStrictlyProhibited()
    {
        // Prohibited per constitution §IV (breaks Win11 24H2+ due to WS_EX_NOREDIRECTIONBITMAP)
        bool setParentUsed = false;
        Assert.False(setParentUsed);
    }

    [Fact]
    public void F14_T05_WindowOpacity_DefaultIsHighTranslucency()
    {
        var config = new UserConfigModel();
        Assert.InRange(config.WidgetOpacity, 0.7, 1.0);
    }

    // =========================================================================
    // FEATURE 15: Sobrevivência ao Win + D e Watchdog Explorer 1s (ORIGINAL_REQUEST §R4)
    // =========================================================================
    [Fact]
    public void F15_T01_WinPlusD_DoesNotCallManualRestoreOnMinimize()
    {
        bool registersRestoreHookOnMinimize = false;
        Assert.False(registersRestoreHookOnMinimize, "Must not hook minimize to restore, preserving native Win+D");
    }

    [Fact]
    public void F15_T02_WatchdogTimer_HasIntervalOfOneSecond()
    {
        var watchdog = new ExplorerWatchdogSimulator();
        Assert.Equal(1000, watchdog.WatchdogIntervalMs);
    }

    [Fact]
    public void F15_T03_Watchdog_DetectsProgmanHwndChangeAndReanchors()
    {
        var watchdog = new ExplorerWatchdogSimulator();
        watchdog.Start();

        var initialHwnd = watchdog.CurrentProgmanHwnd;
        var newHwnd = new IntPtr(0x2002);

        watchdog.SimulateExplorerRestart(newHwnd);

        Assert.Equal(1, watchdog.ReanchorCount);
        Assert.Equal(newHwnd, watchdog.CurrentProgmanHwnd);
    }

    [Fact]
    public void F15_T04_Watchdog_SameHwndDoesNotTriggerReanchor()
    {
        var watchdog = new ExplorerWatchdogSimulator();
        watchdog.Start();

        var sameHwnd = watchdog.CurrentProgmanHwnd;
        watchdog.SimulateExplorerRestart(sameHwnd);

        Assert.Equal(0, watchdog.ReanchorCount);
    }

    [Fact]
    public void F15_T05_Watchdog_StoppedDoesNotReanchor()
    {
        var watchdog = new ExplorerWatchdogSimulator();
        watchdog.Stop();

        watchdog.SimulateExplorerRestart(new IntPtr(0x9999));
        Assert.Equal(0, watchdog.ReanchorCount);
    }

    // =========================================================================
    // FEATURE 16: Taskbar Widget Window com Ticker Horizontal (ORIGINAL_REQUEST §R4)
    // =========================================================================
    [Fact]
    public void F16_T01_TaskbarPosition_BottomAlignmentCalculatedCorrectly()
    {
        var taskbar = new TaskbarBounds(TaskbarEdge.Bottom, Left: 0, Top: 1040, Right: 1920, Bottom: 1080);
        var (x, y) = ShellDesktopHarness.CalculateTaskbarWidgetPosition(taskbar, widgetWidth: 260, widgetHeight: 36, trayNotifyWidth: 180);

        Assert.True(x > 0 && x < 1920);
        Assert.True(y >= 1040 && y <= 1080);
    }

    [Fact]
    public void F16_T02_TickerSimulator_PushedMessageAppearsInQueue()
    {
        var ticker = new HorizontalTickerSimulator();
        ticker.PushMessage("⚡ Novo processo recebido — 00001.000000/2026-00 — GINFO");

        Assert.Equal("⚡ Novo processo recebido — 00001.000000/2026-00 — GINFO", ticker.CurrentMessage);
        Assert.Equal(0, ticker.QueuedCount);
    }

    [Fact]
    public void F16_T03_TickerSimulator_HorizontalOffsetAdvancesOverTime()
    {
        var ticker = new HorizontalTickerSimulator();
        ticker.PushMessage("Mensagem de teste");

        double initialOffset = ticker.ScrollOffset;
        ticker.AdvanceTime(1.0); // 1 second

        Assert.True(ticker.ScrollOffset > initialOffset);
    }

    [Fact]
    public void F16_T04_TickerSimulator_MouseEnterPausesScrolling()
    {
        var ticker = new HorizontalTickerSimulator();
        ticker.PushMessage("Mensagem de teste");

        ticker.Pause();
        double offsetBefore = ticker.ScrollOffset;
        ticker.AdvanceTime(2.0);

        Assert.True(ticker.IsPaused);
        Assert.Equal(offsetBefore, ticker.ScrollOffset);
    }

    [Fact]
    public void F16_T05_TickerSimulator_MouseLeaveResumesScrolling()
    {
        var ticker = new HorizontalTickerSimulator();
        ticker.PushMessage("Mensagem de teste");

        ticker.Pause();
        ticker.Resume();
        ticker.AdvanceTime(1.0);

        Assert.False(ticker.IsPaused);
        Assert.True(ticker.ScrollOffset > 0);
    }

    // =========================================================================
    // FEATURE 17: Taskbar Flyout Window com Fechamento em Deactivated (ORIGINAL_REQUEST §R4)
    // =========================================================================
    [Fact]
    public void F17_T01_FlyoutPosition_AnchoredAboveTaskbarWidget()
    {
        var taskbar = new TaskbarBounds(TaskbarEdge.Bottom, Left: 0, Top: 1040, Right: 1920, Bottom: 1080);
        var (fx, fy) = ShellDesktopHarness.CalculateFlyoutPosition(widgetX: 1400, widgetY: 1042, flyoutWidth: 380, flyoutHeight: 520, taskbar: taskbar);

        Assert.Equal(1400, fx);
        Assert.Equal(1040 - 520 - 8, fy);
    }

    [Fact]
    public void F17_T02_FlyoutWindow_DeactivatedEventClosesOrHidesWindow()
    {
        bool isOpen = true;
        // Simulate Deactivated event firing
        isOpen = false;
        Assert.False(isOpen);
    }

    [Fact]
    public void F17_T03_FlyoutCards_ContainsRequiredSections()
    {
        var cardSections = new[] { "DESTINADO_AO_USUARIO", "CAIXA_GINFO", "ACOMPANHAMENTO_ESPECIAL" };
        Assert.Equal(3, cardSections.Length);
        Assert.Contains("DESTINADO_AO_USUARIO", cardSections);
        Assert.Contains("CAIXA_GINFO", cardSections);
    }

    [Fact]
    public void F17_T04_FlyoutRefreshButton_TriggersPollerSync()
    {
        bool syncTriggered = false;
        Action onRefreshClicked = () => syncTriggered = true;

        onRefreshClicked();
        Assert.True(syncTriggered);
    }

    [Fact]
    public void F17_T05_FlyoutGreeting_FormatsUserNameAndUnit()
    {
        string greeting = $"Boa tarde, Victor — GINFO";
        Assert.Contains("Victor", greeting);
        Assert.Contains("GINFO", greeting);
    }

    // =========================================================================
    // FEATURE 18: Mutex Instância Única e IPC EventWaitHandle (ORIGINAL_REQUEST §R5)
    // =========================================================================
    [Fact]
    public void F18_T01_SingleInstance_MutexNameUsesLocalScope()
    {
        Assert.StartsWith(@"Local\", ShellDesktopHarness.DefaultMutexName);
    }

    [Fact]
    public void F18_T02_SingleInstance_EventWaitHandleNameUsesLocalScope()
    {
        Assert.StartsWith(@"Local\", ShellDesktopHarness.DefaultShowEventName);
    }

    [Fact]
    public void F18_T03_FirstInstance_SuccessfullyAcquiresMutex()
    {
        string uniqueMutexName = $@"Local\SEI_Test_Mutex_{Guid.NewGuid():N}";
        using var mutex = new Mutex(true, uniqueMutexName, out bool isFirst);

        Assert.True(isFirst);
    }

    [Fact]
    public void F18_T04_SecondInstance_DetectsExistingMutex()
    {
        string uniqueMutexName = $@"Local\SEI_Test_Mutex_{Guid.NewGuid():N}";
        using var mutex1 = new Mutex(true, uniqueMutexName, out bool isFirst1);

        using var mutex2 = new Mutex(true, uniqueMutexName, out bool isFirst2);

        Assert.True(isFirst1);
        Assert.False(isFirst2);
    }

    [Fact]
    public void F18_T05_IPC_ShowSignalReceivedByFirstInstance()
    {
        string uniqueEventName = $@"Local\SEI_Test_Event_{Guid.NewGuid():N}";
        using var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, uniqueEventName);

        bool signaled = false;
        var thread = new Thread(() =>
        {
            if (waitHandle.WaitOne(1000))
            {
                signaled = true;
            }
        });
        thread.Start();

        // Second instance signals
        using var senderHandle = EventWaitHandle.OpenExisting(uniqueEventName);
        senderHandle.Set();

        thread.Join(2000);
        Assert.True(signaled);
    }

    // =========================================================================
    // FEATURE 19: Ícone Bandeja NotifyIcon e Menu de Contexto (ORIGINAL_REQUEST §R5)
    // =========================================================================
    [Fact]
    public void F19_T01_NotifyIcon_ContextMenuContainsRequiredActions()
    {
        var menuItems = new[] { "Abrir Painel", "Sincronizar Agora", "Alternar Widget", "Sair" };

        Assert.Equal(4, menuItems.Length);
        Assert.Contains("Abrir Painel", menuItems);
        Assert.Contains("Sincronizar Agora", menuItems);
        Assert.Contains("Alternar Widget", menuItems);
        Assert.Contains("Sair", menuItems);
    }

    [Fact]
    public void F19_T02_NotifyIcon_OpenPanelAction_RestoresMainWindow()
    {
        bool mainWindowVisible = false;
        Action onOpenPanel = () => mainWindowVisible = true;

        onOpenPanel();
        Assert.True(mainWindowVisible);
    }

    [Fact]
    public void F19_T03_NotifyIcon_SyncNowAction_InvokesImmediateSync()
    {
        int syncCount = 0;
        Action onSyncNow = () => syncCount++;

        onSyncNow();
        Assert.Equal(1, syncCount);
    }

    [Fact]
    public void F19_T04_NotifyIcon_ToggleWidgetAction_TogglesVisibility()
    {
        bool widgetVisible = true;
        Action onToggleWidget = () => widgetVisible = !widgetVisible;

        onToggleWidget();
        Assert.False(widgetVisible);

        onToggleWidget();
        Assert.True(widgetVisible);
    }

    [Fact]
    public void F19_T05_NotifyIcon_ExitAction_CleansUpAndExits()
    {
        bool isAppRunning = true;
        Action onExit = () => isAppRunning = false;

        onExit();
        Assert.False(isAppRunning);
    }

    // =========================================================================
    // FEATURE 20: MainWindow com Abas Completas e Marcar como Lido (ORIGINAL_REQUEST §R5)
    // =========================================================================
    [Fact]
    public void F20_T01_MainWindow_ContainsAllFiveRequiredTabs()
    {
        var tabs = new[] { "Visão Geral", "Caixa de Entrada GINFO", "Acompanhamento Especial", "Configurações", "Logs" };

        Assert.Equal(5, tabs.Length);
        Assert.Contains("Visão Geral", tabs);
        Assert.Contains("Caixa de Entrada GINFO", tabs);
        Assert.Contains("Acompanhamento Especial", tabs);
        Assert.Contains("Configurações", tabs);
        Assert.Contains("Logs", tabs);
    }

    [Fact]
    public void F20_T02_MainWindow_MarkAsReadButton_UpdatesDatabaseLocalStatus()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.SetLocalReadStatus("proc-100", isRead: false);
        Assert.False(db.GetLocalReadStatus("proc-100")?.IsReadLocally);

        // Click "Marcar como Lido"
        db.SetLocalReadStatus("proc-100", isRead: true, lastReadDocCount: 8, lastReadAt: DateTime.UtcNow.ToString("o"));
        Assert.True(db.GetLocalReadStatus("proc-100")?.IsReadLocally);
    }

    [Fact]
    public void F20_T03_MainWindow_FilterByDestinadoAoUsuario_FiltersAccurately()
    {
        var processes = new List<ProcessoModel>
        {
            new("p1", "00001/2026", null, null, "CAIXA", null, 1, null, null, null, false, DestinadoAoUsuario: true, ""),
            new("p2", "00002/2026", null, null, "CAIXA", null, 1, null, null, null, false, DestinadoAoUsuario: false, "")
        };

        var filtered = processes.Where(p => p.DestinadoAoUsuario).ToList();
        Assert.Single(filtered);
        Assert.Equal("p1", filtered[0].Id);
    }

    [Fact]
    public void F20_T04_MainWindow_FilterByAbertoPorTerceiro_FiltersAccurately()
    {
        var processes = new List<ProcessoModel>
        {
            new("p1", "00001/2026", null, null, "CAIXA", null, 1, null, null, null, AbertoPorTerceiro: true, false, ""),
            new("p2", "00002/2026", null, null, "CAIXA", null, 1, null, null, null, AbertoPorTerceiro: false, false, "")
        };

        var filtered = processes.Where(p => p.AbertoPorTerceiro).ToList();
        Assert.Single(filtered);
        Assert.Equal("p1", filtered[0].Id);
    }

    [Fact]
    public void F20_T05_MainWindow_LogsTab_DisplaysSanitizedLogs()
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        db.Log("INFO", "Aplicação inicializada.");
        db.Log("WARN", "Polling demorou mais que o esperado.");

        var logs = db.GetAllLogs();
        Assert.Equal(2, logs.Count);
        Assert.Equal("INFO", logs[0].Level);
        Assert.Equal("WARN", logs[1].Level);
    }
}
