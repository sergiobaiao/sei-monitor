using System;
using System.Collections.Generic;
using System.Diagnostics;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

public class TextAnalyzerTests
{
    // =========================================================================
    // 1. Testes da Heurística AbertoPorTerceiro
    // =========================================================================

    [Fact]
    public void IsOpenedByThirdParty_ColleagueOpensProcess_ReturnsTrue()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueReceivesProcess_ReturnsTrue()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:15:00", "Processo recebido na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueViewsProcess_ReturnsTrue()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:30:00", "Consulta a processo", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueReopensProcess_ReturnsTrue()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:45:00", "Reabertura de processo", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_CurrentUserOpensProcess_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "victor.lima")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_CurrentUserCaseInsensitive_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "VICTOR.LIMA")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_CurrentUserWithDomainPrefix_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", @"ESTADO\victor.lima")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_CurrentUserNameWithDomainPrefix_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "victor.lima")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, @"ESTADO\victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueConcludesProcess_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo concluído na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueDispatches_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Despacho encaminhado", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueAttachesDocument_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Documento juntado", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_ColleagueFilesProcess_ReturnsFalse()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo arquivado na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_MultipleActivities_EvaluatesMostRecentActivity()
    {
        var activities = new[]
        {
            // A atividade mais recente vem no índice 0 no retorno do SEI
            new SeiActivity("2", "29/10/2026", "11:00:00", "Despacho emitido", "GINFO", "servidor.colega"),
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_HtmlAndWhitespaceInDescription_HandledCleanly()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "  <b>Processo aberto na unidade</b> \r\n ", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.True(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_EmptyActivities_ReturnsFalseSafely()
    {
        bool result = TextAnalyzer.IsOpenedByThirdParty(Array.Empty<SeiActivity>(), "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_NullActivities_ReturnsFalseSafely()
    {
        bool result = TextAnalyzer.IsOpenedByThirdParty((IEnumerable<SeiActivity>?)null, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_NullUserField_ReturnsFalseSafely()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", null)
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_NullCurrentUserName_ReturnsFalseSafely()
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, null);
        Assert.False(result);
    }

    [Fact]
    public void IsOpenedByThirdParty_DirectOverload_EvaluatesCorrectly()
    {
        bool result = TextAnalyzer.IsOpenedByThirdParty("servidor.outro", "Recebido na unidade", "victor.lima");
        Assert.True(result);
    }

    // =========================================================================
    // 2. Testes da Heurística DestinadoAoUsuario
    // =========================================================================

    [Fact]
    public void IsDestinedToUser_KeywordInTitle_ReturnsTrue()
    {
        string title = "Solicitação de Acesso TI - Victor Lima";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzer.IsDestinedToUser(title, null, null, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_KeywordInSpecification_ReturnsTrue()
    {
        string spec = "Encaminhamento aos cuidados do servidor vlima.";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzer.IsDestinedToUser(null, spec, null, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_KeywordInLastDispatch_ReturnsTrue()
    {
        string dispatch = "Ao servidor Victor para manifestação conclusiva.";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_NoKeywordsMatch_ReturnsFalse()
    {
        string title = "Aquisição de Material de Escritório";
        string dispatch = "À CPL para providências de edital.";
        var keywords = new[] { "Victor", "vlima" };

        bool result = TextAnalyzer.IsDestinedToUser(title, null, dispatch, keywords);
        Assert.False(result);
    }

    [Fact]
    public void IsDestinedToUser_AccentInsensitive_KeywordWithoutAccent_MatchesAccentedText()
    {
        string dispatch = "Encaminhado ao servidor Sérgio para análise técnica.";
        var keywords = new[] { "sergio" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_AccentInsensitive_KeywordWithAccent_MatchesUnaccentedText()
    {
        string dispatch = "Encaminhado ao servidor sergio para analise.";
        var keywords = new[] { "Sérgio" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_CaseInsensitive_MatchesAllCasings()
    {
        string dispatch = "Ao servidor VICTOR para providências.";
        var keywords = new[] { "victor" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_WordBoundary_PreventsFalsePositives()
    {
        // Palavra-chave "tor" contida dentro de "setor" e "diretoria" NÃO deve casar
        string dispatch = "Encaminhado para a diretoria do setor financeiro.";
        var keywords = new[] { "tor" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(result);
    }

    [Fact]
    public void IsDestinedToUser_WordBoundary_StandaloneWordMatches()
    {
        // Palavra-chave "tor" isolada DEVE casar
        string dispatch = "Ao servidor Tor para providências.";
        var keywords = new[] { "tor" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_WordBoundary_PunctuationAdjacent_MatchesCleanly()
    {
        var keywords = new[] { "Victor" };

        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Ao servidor Victor, favor despachar.", keywords));
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Para (Victor).", keywords));
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Destinatário: [Victor]", keywords));
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Atenção: Victor!", keywords));
    }

    [Fact]
    public void IsDestinedToUser_KeywordsWithSpecialCharacters_TreatedLiterally()
    {
        string dispatch = "Encaminho à equipe GINFO/Sistemas (TI) para despacho.";
        var keywords = new[] { "GINFO/Sistemas (TI)", "CPL [1]" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_MultiWordKeyword_MatchesFullPhrase()
    {
        string dispatch = "Processo sob responsabilidade de Victor Lima na data de hoje.";
        var keywords = new[] { "Victor Lima" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_RegistrationNumberKeyword_MatchesWithoutFalsePositives()
    {
        var keywords = new[] { "123456" };

        // Deve casar com matrícula isolada
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Servidor matrícula 123456 designado.", keywords));

        // Não deve casar como subnúmero de um CPF ou outro código numérico longo
        Assert.False(TextAnalyzer.IsDestinedToUser(null, null, "Protocolo 9912345677 autuado.", keywords));
    }

    [Fact]
    public void IsDestinedToUser_HtmlTagsInDispatch_StripsHtmlWithoutFusingWords()
    {
        string htmlDispatch = "<p style='color:red;'>Ao servidor</p><p>Victor</p>";
        var keywords = new[] { "Victor" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, htmlDispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_HtmlEntitiesInDispatch_DecodesCleanly()
    {
        string htmlDispatch = "Ao&nbsp;servidor&nbsp;Victor&nbsp;para&nbsp;ciência.";
        var keywords = new[] { "Victor" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, htmlDispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_EmptyKeywordsList_ReturnsFalse()
    {
        string dispatch = "Ao servidor Victor para manifestação.";
        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, Array.Empty<string>());
        Assert.False(result);
    }

    [Fact]
    public void IsDestinedToUser_WhitespaceOnlyKeywords_IgnoredSafely()
    {
        string dispatch = "Qualquer despacho";
        var keywords = new[] { "   ", "\t", "" };

        bool result = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(result);
    }

    [Fact]
    public void IsDestinedToUser_AllNullInputs_ReturnsFalseSafely()
    {
        bool result = TextAnalyzer.IsDestinedToUser(null, null, null, new[] { "Victor" });
        Assert.False(result);
    }

    [Fact]
    public void IsDestinedToUser_MonitoredProcessOverload_MatchesCorrectly()
    {
        var proc = new MonitoredProcess
        {
            Id = "100",
            ProtocoloFormatado = "00001.000001/2026-00",
            Descricao = "Processo de Licitação de Servidores",
            TipoProcesso = "Aquisição TI"
        };

        var keywords = new[] { "Victor" };
        string dispatch = "Ao servidor Victor para análise da especificação.";

        bool result = TextAnalyzer.IsDestinedToUser(proc, dispatch, keywords);
        Assert.True(result);
    }

    [Fact]
    public void IsDestinedToUser_LargeTextDispatch_ProcessesQuickly()
    {
        string largeDispatch = "Relatório preliminar... " + new string('X', 60_000) + " Ao servidor Victor para ciência.";
        var keywords = new[] { "Victor" };

        var sw = Stopwatch.StartNew();
        bool matched = TextAnalyzer.IsDestinedToUser(null, null, largeDispatch, keywords);
        sw.Stop();

        Assert.True(matched);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Execution took {sw.ElapsedMilliseconds}ms, which exceeds 50ms");
    }
}
