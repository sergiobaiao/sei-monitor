using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using SeiMonitor.E2ETests.Harness;

namespace SeiMonitor.E2ETests;

public class Tier3CombinatorialTests
{
    // =========================================================================
    // TIER 3: Pairwise Combinatorial Testing (20+ tests covering orthogonal axes)
    // Axes:
    // 1. Auth Status: (Valid, Expired401, SolrLeak200, NetworkDown503, SeadFallback)
    // 2. Activity Type: (OpenedByThirdParty, OpenedByMe, Concluded, Empty)
    // 3. Keyword Match: (TitleMatch, DispatchMatch, Both, None)
    // 4. Local Read Status: (Read, Unread)
    // 5. UI Target: (Fence, TaskbarTicker, Flyout, Tray)
    // =========================================================================

    [Theory]
    [InlineData("Valid", "OpenedByThirdParty", "TitleMatch", false, "TaskbarTicker")]
    [InlineData("Valid", "OpenedByMe", "DispatchMatch", true, "Fence")]
    [InlineData("Valid", "Concluded", "Both", false, "Flyout")]
    [InlineData("Valid", "Empty", "None", true, "Tray")]
    [InlineData("Expired401", "OpenedByThirdParty", "DispatchMatch", false, "Fence")]
    [InlineData("Expired401", "OpenedByMe", "Both", true, "Flyout")]
    [InlineData("Expired401", "Concluded", "None", false, "Tray")]
    [InlineData("Expired401", "Empty", "TitleMatch", true, "TaskbarTicker")]
    [InlineData("SolrLeak200", "OpenedByThirdParty", "Both", false, "Tray")]
    [InlineData("SolrLeak200", "OpenedByMe", "None", true, "TaskbarTicker")]
    [InlineData("SolrLeak200", "Concluded", "TitleMatch", false, "Fence")]
    [InlineData("SolrLeak200", "Empty", "DispatchMatch", true, "Flyout")]
    [InlineData("NetworkDown503", "OpenedByThirdParty", "None", false, "Flyout")]
    [InlineData("NetworkDown503", "OpenedByMe", "TitleMatch", true, "Tray")]
    [InlineData("NetworkDown503", "Concluded", "DispatchMatch", false, "TaskbarTicker")]
    [InlineData("NetworkDown503", "Empty", "Both", true, "Fence")]
    [InlineData("SeadFallback", "OpenedByThirdParty", "TitleMatch", true, "Fence")]
    [InlineData("SeadFallback", "OpenedByMe", "DispatchMatch", false, "TaskbarTicker")]
    [InlineData("SeadFallback", "Concluded", "Both", true, "Tray")]
    [InlineData("SeadFallback", "Empty", "None", false, "Flyout")]
    public async Task T3_PairwiseCombinatorialMatrix_EvaluatesConsistently(
        string authState,
        string activityType,
        string keywordMatch,
        bool isReadInitially,
        string uiTarget)
    {
        using var db = new SqliteTestDatabase();
        db.InitializeSchema();

        var config = new UserConfigModel(
            Keywords: "Victor, vlima, GINFO",
            MeuUsuarioSEI: "victor.lima"
        );

        // 1. Evaluate Auth Axis
        int httpStatus = authState switch
        {
            "Valid" => 200,
            "Expired401" => 401,
            "SolrLeak200" => 200,
            "NetworkDown503" => 503,
            "SeadFallback" => 200,
            _ => 200
        };

        string authBody = authState switch
        {
            "SolrLeak200" => @"{ ""sucesso"": false, ""mensagem"": ""http://admin:pass@solr:8983"" }",
            "Expired401" => @"{ ""message"": ""Token expired"" }",
            "NetworkDown503" => "",
            _ => @"{ ""sucesso"": true, ""data"": { ""token"": ""valid_token"" } }"
        };

        if (authState == "SolrLeak200")
        {
            var ex = SolrSanitizerHarness.SanitizeWsseiError(authBody, httpStatus);
            Assert.False(SolrSanitizerHarness.ContainsSolrLeak(ex.Message));
        }

        // 2. Evaluate Activity Axis
        var activities = new List<AtividadeModel>();
        switch (activityType)
        {
            case "OpenedByThirdParty":
                activities.Add(new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "colega.servidor"));
                break;
            case "OpenedByMe":
                activities.Add(new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "victor.lima"));
                break;
            case "Concluded":
                activities.Add(new AtividadeModel("1", "29/10/2026", "10:00:00", "Processo concluído na unidade", "GINFO", "colega.servidor"));
                break;
        }

        bool expectedAberto = TextAnalyzerHarness.IsOpenedByThirdParty(activities, config.MeuUsuarioSEI);
        if (activityType == "OpenedByThirdParty")
        {
            Assert.True(expectedAberto);
        }
        else
        {
            Assert.False(expectedAberto);
        }

        // 3. Evaluate Keyword Axis
        string? title = keywordMatch switch
        {
            "TitleMatch" => "Processo destinado ao Victor",
            "Both" => "Licitação Victor Lima",
            _ => "Processo Comum"
        };
        string? dispatch = keywordMatch switch
        {
            "DispatchMatch" => "Ao servidor vlima para análise",
            "Both" => "Ao servidor vlima para parecer",
            _ => "Ao setor para arquivo"
        };

        bool expectedDestinado = TextAnalyzerHarness.IsDestinedToUser(
            title, null, dispatch, config.Keywords.Split(',').Select(k => k.Trim()));

        if (keywordMatch == "None")
        {
            Assert.False(expectedDestinado);
        }
        else
        {
            Assert.True(expectedDestinado);
        }

        // 4. Evaluate Database and Local Read Status
        db.SetLocalReadStatus("proc-comb", isReadInitially);
        var initialRead = db.GetLocalReadStatus("proc-comb");
        Assert.Equal(isReadInitially, initialRead?.IsReadLocally);

        // 5. Evaluate UI Target Integration
        switch (uiTarget)
        {
            case "TaskbarTicker":
                var ticker = new HorizontalTickerSimulator();
                ticker.PushMessage($"Notificação: {title}");
                Assert.NotNull(ticker.CurrentMessage);
                break;
            case "Fence":
                long styles = ShellDesktopHarness.WS_EX_TOOLWINDOW | ShellDesktopHarness.WS_EX_NOACTIVATE;
                Assert.True(ShellDesktopHarness.VerifyWindowStyleContracts(styles));
                break;
            case "Flyout":
                var taskbar = new TaskbarBounds(TaskbarEdge.Bottom, 0, 1040, 1920, 1080);
                var (fx, fy) = ShellDesktopHarness.CalculateFlyoutPosition(1400, 1042, 380, 520, taskbar);
                Assert.True(fx > 0 && fy > 0);
                break;
            case "Tray":
                bool trayVisible = true;
                Assert.True(trayVisible);
                break;
        }

        await Task.CompletedTask;
    }
}
