using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Bateria de testes empíricos de estresse e validação adversarial para o Core/TextAnalyzer.cs.
/// Executado pelo agente m2_challenger_2 (teamwork_challenger) para o Milestone 2.
/// </summary>
public class TextAnalyzerStressTests
{
    // =========================================================================
    // 1. DESAFIOS DE AbertoPorTerceiro: ORDEM, AUTORES E ESTADOS
    // =========================================================================

    [Fact]
    public void AbertoPorTerceiro_InvertedChronologicalOrder_DemonstratesContractVulnerability()
    {
        // Se a lista vier em ordem cronológica (mais antigo primeiro, como pode ocorrer em APIs ou queries asc):
        // Atividade 0: Processo aberto às 10:00 por colega
        // Atividade 1: Processo concluído às 11:00 por colega
        var chronologicalAscendingActivities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega"),
            new SeiActivity("2", "29/10/2026", "11:00:00", "Processo concluído na unidade", "GINFO", "servidor.colega")
        };

        bool resultAscending = TextAnalyzer.IsOpenedByThirdParty(chronologicalAscendingActivities, "victor.lima");
        
        // Em ordem descendente estrita (mais recente primeiro):
        var descendingActivities = chronologicalAscendingActivities.Reverse().ToArray();
        bool resultDescending = TextAnalyzer.IsOpenedByThirdParty(descendingActivities, "victor.lima");

        // Com a ordenação defensiva interna, a conclusão mais recente prevalece em ambos os casos
        Assert.False(resultAscending, "Em ordem cronológica não invertida, a ordenação defensiva pegou a conclusão mais recente");
        Assert.False(resultDescending, "Em ordem decrescente, a conclusão mais recente prevalece");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void AbertoPorTerceiro_NullOrWhitespaceAuthor_ReturnsFalseSafely(string? author)
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", author)
        };

        bool result1 = TextAnalyzer.IsOpenedByThirdParty(activities, "victor.lima");
        bool result2 = TextAnalyzer.IsOpenedByThirdParty(author, "Processo aberto na unidade", "victor.lima");

        Assert.False(result1);
        Assert.False(result2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbertoPorTerceiro_NullOrWhitespaceCurrentUser_ReturnsFalseSafely(string? currentUser)
    {
        var activities = new[]
        {
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        bool result = TextAnalyzer.IsOpenedByThirdParty(activities, currentUser);
        Assert.False(result);
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_DomainBackslash_MatchesCorrectly()
    {
        // Ambos os sentidos devem ser reconhecidos como o mesmo usuário (não terceiro)
        Assert.False(TextAnalyzer.IsOpenedByThirdParty(@"ESTADO\joao", "Processo aberto na unidade", "joao"));
        Assert.False(TextAnalyzer.IsOpenedByThirdParty("joao", "Processo aberto na unidade", @"ESTADO\joao"));
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_DifferentDomainPrefixesSameLogin_FailsRecognition()
    {
        // Quando ambos os usuários possuem prefixos de domínio mas de florestas/aliases distintos (ex: GOV\joao vs ESTADO\joao)
        // IsSameUser extrai a parte após a barra em ambos os lados, reconhecendo a equivalência
        bool isThirdParty = TextAnalyzer.IsOpenedByThirdParty(@"GOV\joao", "Processo aberto na unidade", @"ESTADO\joao");
        Assert.False(isThirdParty, "Diferentes prefixos de domínio para o mesmo login são reconhecidos como o mesmo usuário");
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_DomainForwardSlash_FailsRecognition()
    {
        // Avalia se barras comuns em ambientes Unix ou URLs ("ESTADO/joao") são tratadas
        bool isThirdPartyForwardSlash = TextAnalyzer.IsOpenedByThirdParty("ESTADO/joao", "Processo aberto na unidade", "joao");
        
        Assert.False(isThirdPartyForwardSlash, "Forward slash 'ESTADO/joao' é reconhecido como mesmo usuário que 'joao'");
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_EmailOrUpnFormat_FailsRecognition()
    {
        // Usuário no SEI logado com email institucional vs nome de usuário curto
        bool isThirdPartyEmail = TextAnalyzer.IsOpenedByThirdParty("joao@pi.gov.br", "Processo aberto na unidade", "joao");
        
        Assert.False(isThirdPartyEmail, "Email UPN 'joao@pi.gov.br' é reconhecido como mesmo usuário que 'joao'");
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_AccentedUserName_RecognizedAsSameUser()
    {
        // Normalização deve igualar 'João' e 'joao'
        bool result = TextAnalyzer.IsOpenedByThirdParty("João", "Processo aberto na unidade", "joao");
        Assert.False(result, "Usuário com acento 'João' deve ser reconhecido como mesmo usuário que 'joao'");
    }

    [Fact]
    public void AbertoPorTerceiro_UserFormats_PartialMatchCollision_Prevented()
    {
        // Não deve considerar igual usuários com prefixos/sufixos que contenham o nome
        Assert.True(TextAnalyzer.IsOpenedByThirdParty("pedrojoao", "Processo aberto na unidade", "joao"));
        Assert.True(TextAnalyzer.IsOpenedByThirdParty("joaovictor", "Processo aberto na unidade", "joao"));
        Assert.True(TextAnalyzer.IsOpenedByThirdParty("marcos.silva", "Processo aberto na unidade", "silva"));
    }

    [Fact]
    public void AbertoPorTerceiro_SameTimestamp_OpeningFollowedByConcluding_DependsOnOrder()
    {
        // Duas atividades no exato mesmo segundo
        var activitiesAbertoPrimeiro = new[]
        {
            new SeiActivity("2", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega"),
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo concluído na unidade", "GINFO", "servidor.colega")
        };

        var activitiesConcluidoPrimeiro = new[]
        {
            new SeiActivity("2", "29/10/2026", "10:00:00", "Processo concluído na unidade", "GINFO", "servidor.colega"),
            new SeiActivity("1", "29/10/2026", "10:00:00", "Processo aberto na unidade", "GINFO", "servidor.colega")
        };

        // Quando o array coloca o aberto primeiro, IsOpenedByThirdParty retorna true
        Assert.True(TextAnalyzer.IsOpenedByThirdParty(activitiesAbertoPrimeiro, "victor.lima"));
        // Quando o array coloca o concluído primeiro, retorna false
        Assert.False(TextAnalyzer.IsOpenedByThirdParty(activitiesConcluidoPrimeiro, "victor.lima"));
    }

    [Theory]
    [InlineData("Processo despachado na unidade")]
    [InlineData("Juntada de documento")]
    [InlineData("Conclusão na unidade")]
    [InlineData("Remessa externa")]
    [InlineData("Arquivamento do processo")]
    [InlineData("Sobrestamento de processo")]
    [InlineData("Encaminhamento para diretoria")]
    public void AbertoPorTerceiro_MorphologicalVariationsOfConcludingActions_Tested(string desc)
    {
        // Se a descrição for puramente uma dessas ações conclusivas
        bool result = TextAnalyzer.IsOpenedByThirdParty("servidor.colega", desc, "victor.lima");
        // Como a descrição não contém nenhum OpenActivityIndicator, ela retorna false por default
        Assert.False(result);
    }

    [Fact]
    public void AbertoPorTerceiro_MixedDescription_OpeningWithUnmatchedConcludingNoun_RevealsVulnerability()
    {
        // Caso em que a descrição cita abertura junto com uma ação substantivada contida nos ConcludingIndicators
        // Exemplo: "Processo aberto na unidade para juntada de petição"
        string desc = "Processo aberto na unidade para juntada de petição";
        bool result = TextAnalyzer.IsOpenedByThirdParty("servidor.colega", desc, "victor.lima");
        
        // "juntada" é reconhecida como indicador conclusivo, logo AbertoPorTerceiro = false
        Assert.False(result, "Substantivo 'juntada' é reconhecido como indicador conclusivo");
    }

    [Fact]
    public void AbertoPorTerceiro_MixedDescription_OpeningWithUnmatchedConclusao_RevealsVulnerability()
    {
        string desc = "Processo aberto na unidade para conclusão ao Diretor";
        bool result = TextAnalyzer.IsOpenedByThirdParty("servidor.colega", desc, "victor.lima");
        
        Assert.False(result, "'conclusão' é reconhecida como indicador conclusivo");
    }

    // =========================================================================
    // 2. DESAFIOS DE DestinadoAoUsuario: TEXTOS GIGANTES (>100KB, 500KB, 1MB)
    // =========================================================================

    [Fact]
    public void DestinadoAoUsuario_LargeText_150KB_WithKeywordAtBeginning()
    {
        var keywords = new[] { "Victor" };
        string largeBody = "Victor " + new string('A', 150_000);

        var sw = Stopwatch.StartNew();
        bool match = TextAnalyzer.IsDestinedToUser(null, null, largeBody, keywords);
        sw.Stop();

        Assert.True(match);
        Assert.True(sw.ElapsedMilliseconds < 100, $"150KB início levou {sw.ElapsedMilliseconds}ms (limite 100ms)");
    }

    [Fact]
    public void DestinadoAoUsuario_LargeText_150KB_WithKeywordInMiddle()
    {
        var keywords = new[] { "Victor" };
        string half = new string('B', 75_000);
        string largeBody = $"{half} Victor {half}";

        var sw = Stopwatch.StartNew();
        bool match = TextAnalyzer.IsDestinedToUser(null, null, largeBody, keywords);
        sw.Stop();

        Assert.True(match);
        Assert.True(sw.ElapsedMilliseconds < 100, $"150KB meio levou {sw.ElapsedMilliseconds}ms (limite 100ms)");
    }

    [Fact]
    public void DestinadoAoUsuario_LargeText_150KB_WithKeywordAtEnd()
    {
        var keywords = new[] { "Victor" };
        string largeBody = new string('C', 150_000) + " Victor";

        var sw = Stopwatch.StartNew();
        bool match = TextAnalyzer.IsDestinedToUser(null, null, largeBody, keywords);
        sw.Stop();

        Assert.True(match);
        Assert.True(sw.ElapsedMilliseconds < 100, $"150KB fim levou {sw.ElapsedMilliseconds}ms (limite 100ms)");
    }

    [Fact]
    public void DestinadoAoUsuario_LargeText_500KB_NoMatch_PerformanceStress()
    {
        var keywords = new[] { "Victor", "vlima", "123456" };
        // Texto de 500KB com várias palavras similares como "Victoria", "setor", "991234567"
        var sb = new StringBuilder(500_000);
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("setor diretoria victoria relatorio 991234567 ");
        }
        string text = sb.ToString();

        var sw = Stopwatch.StartNew();
        bool match = TextAnalyzer.IsDestinedToUser(null, null, text, keywords);
        sw.Stop();

        Assert.False(match);
        Assert.True(sw.ElapsedMilliseconds < 250, $"500KB sem match levou {sw.ElapsedMilliseconds}ms (limite 250ms)");
    }

    [Fact]
    public void DestinadoAoUsuario_HugeText_1MB_WithHtmlAndDiacritics()
    {
        var keywords = new[] { "Sérgio", "TI-Sistemas" };
        var sb = new StringBuilder(1_050_000);
        sb.Append("<html><body>");
        for (int i = 0; i < 15_000; i++)
        {
            sb.Append("<p>Parágrafo de teste com acentuação: atenção, expedição, não há pendências.</p>");
        }
        sb.Append("<div class='footer'>Encaminhado ao servidor Sérgio para homologação.</div>");
        sb.Append("</body></html>");
        string html = sb.ToString();

        var sw = Stopwatch.StartNew();
        bool match = TextAnalyzer.IsDestinedToUser("Título", "Especificação", html, keywords);
        sw.Stop();

        Assert.True(match);
        Assert.True(sw.ElapsedMilliseconds < 500, $"1MB HTML com acentos levou {sw.ElapsedMilliseconds}ms (limite 500ms)");
    }

    // =========================================================================
    // 3. DESAFIOS DE DestinadoAoUsuario: HTML MALFORMADO E EXTREMOS
    // =========================================================================

    [Fact]
    public void DestinadoAoUsuario_MalformedHtml_UnclosedTag_DropsTrailingText()
    {
        // Bug empírico descoberto no StripHtml:
        // Se houver uma tag sem fechamento (ex: "<p victor para ciência" ou "< 5 dias"),
        // a variável insideTag permanece true até o fim da string, engolindo todo o texto!
        string dispatchWithUnclosedTag = "Texto inicial <p victor para despacho";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatchWithUnclosedTag, keywords);

        Assert.True(match, "Tag HTML sem '>' não engole o texto subsequente");
    }

    [Fact]
    public void DestinadoAoUsuario_MalformedHtml_MathematicalLessThanSign_DropsText()
    {
        // Em despachos reais: "valores < 1000 reais aos cuidados de victor"
        string dispatch = "valores < 1000 reais aos cuidados de victor";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        
        Assert.True(match, "Sinal '<' não engole o texto subsequente");
    }

    [Fact]
    public void DestinadoAoUsuario_AngleBrackets_KeywordDirectlyEnclosedInAngleBrackets_SwallowedAsTag()
    {
        // Em documentos reais: "Encaminhado aos cuidados de <Victor> para análise."
        string dispatch = "Encaminhado aos cuidados de <Victor> para análise.";
        var keywords = new[] { "Victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        
        Assert.True(match, "<Victor> é preservado e casa perfeitamente");
    }

    [Fact]
    public void DestinadoAoUsuario_AngleBrackets_EmailInAngleBrackets_SwallowedAsTag()
    {
        // Em assinaturas: "Victor Lima <victor@pi.gov.br>"
        string dispatch = "Responsável: Victor Lima <victor@pi.gov.br>";
        var keywords = new[] { "victor@pi.gov.br" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        
        Assert.True(match, "Email entre <...> é preservado e casa perfeitamente");
    }

    [Fact]
    public void DestinadoAoUsuario_MalformedHtml_DoubleBrackets_Handled()
    {
        string dispatch = "Encaminhado <<script>>victor</script>> para análise";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match);
    }

    [Fact]
    public void DestinadoAoUsuario_MalformedHtml_UnclosedAttributeQuotes_Handled()
    {
        string dispatch = "<a href='http://invalid victor</a> victor para providências";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match);
    }

    [Fact]
    public void DestinadoAoUsuario_MalformedHtml_EntitiesTogether_Decoded()
    {
        string dispatch = "Ao&nbsp;&amp;&nbsp;servidor&nbsp;&quot;Victor&quot;&nbsp;para&nbsp;despacho.";
        var keywords = new[] { "Victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match);
    }

    // =========================================================================
    // 4. CARACTERES INVISÍVEIS, ZERO-WIDTH E CONTROLE UNICODE
    // =========================================================================

    [Fact]
    public void DestinadoAoUsuario_ZeroWidthSpace_AroundKeyword_MatchesWordBoundary()
    {
        // Zero-width space \u200B colado nas bordas da palavra-chave
        string dispatch = "Ao servidor \u200Bvictor\u200B para providências.";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match, "Zero-width space nas bordas não deve impedir match de fronteira");
    }

    [Fact]
    public void DestinadoAoUsuario_ZeroWidthJoinerAndBOM_AroundKeyword_MatchesWordBoundary()
    {
        // BOM \uFEFF e ZWJ \u200D
        string dispatch = "Ao servidor \uFEFFvictor\u200D favor analisar.";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match);
    }

    [Fact]
    public void DestinadoAoUsuario_ZeroWidthSpace_InsideKeyword_DoesNotMatchWithoutSanitizer()
    {
        // Texto copiado de PDF/RichText com caractere invisível inserido no meio da palavra
        string dispatch = "Ao servidor vic\u200Btor favor analisar.";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        
        Assert.True(match, "Caracteres de formatação/controle invisíveis dentro da palavra são filtrados e casam perfeitamente");
    }

    [Fact]
    public void DestinadoAoUsuario_SoftHyphen_InsideKeyword_DoesNotMatchWithoutSanitizer()
    {
        // Soft hyphen \u00AD inserido em quebras de linha de PDFs do SEI
        string dispatch = "Ao servidor vic\u00ADtor favor analisar.";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match, "Soft-hyphen \u00AD dentro da palavra é filtrado e casa perfeitamente");
    }

    [Fact]
    public void DestinadoAoUsuario_NonBreakingSpace_InMultiWordKeyword_MatchesCleanly()
    {
        // "Victor\u00A0Lima" no texto vs "Victor Lima" na palavra-chave
        string dispatch = "Ao servidor Victor\u00A0Lima para despacho.";
        var keywords = new[] { "Victor Lima" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match, "Non-breaking space deve ser colapsado como espaço comum pelo CollapseWhitespace");
    }

    // =========================================================================
    // 5. PONTUAÇÕES ACOPLADAS A PALAVRAS-CHAVE
    // =========================================================================

    [Theory]
    [InlineData("Ao servidor victor... favor analisar.")]
    [InlineData("Ao servidor (victor) favor analisar.")]
    [InlineData("Ao servidor [victor] favor analisar.")]
    [InlineData("Ao servidor {victor} favor analisar.")]
    [InlineData("Ao servidor \"victor\" favor analisar.")]
    [InlineData("Ao servidor 'victor' favor analisar.")]
    [InlineData("Ao servidor «victor» favor analisar.")]
    [InlineData("Ao servidor “victor” favor analisar.")]
    [InlineData("Ao servidor victor: favor analisar.")]
    [InlineData("Ao servidor victor; favor analisar.")]
    [InlineData("Ao servidor victor! favor analisar.")]
    [InlineData("Ao servidor victor? favor analisar.")]
    [InlineData("Ao servidor victor, favor analisar.")]
    [InlineData("Ao servidor\r\nvictor\r\nfavor analisar.")]
    [InlineData("Ao servidor\tvictor\tfavor analisar.")]
    [InlineData("Ao servidor victor/cpl favor analisar.")]
    [InlineData("Ao servidor cpl-victor favor analisar.")]
    public void DestinadoAoUsuario_PunctuationAdjacent_AllPunctuationVariations_Match(string dispatch)
    {
        var keywords = new[] { "victor" };
        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match, $"Falhou para pontuação no texto: {dispatch}");
    }

    [Fact]
    public void DestinadoAoUsuario_UnderscoreAdjacent_TreatedAsWordBoundary()
    {
        // Em C# char.IsLetterOrDigit('_') é false, logo 'victor_ginfo' dá match para keyword 'victor'
        string dispatch = "Processo sob responsabilidade do usuário victor_ginfo.";
        var keywords = new[] { "victor" };

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.True(match, "Underscore é delimitador pelo IsLetterOrDigit");
    }

    // =========================================================================
    // 6. AUSÊNCIA DE FALSOS POSITIVOS (SUBSTRINGS EM PALAVRAS MAIORES)
    // =========================================================================

    [Theory]
    [InlineData("setor")]
    [InlineData("diretoria")]
    [InlineData("relatorio")]
    [InlineData("motor")]
    [InlineData("reitor")]
    [InlineData("pastor")]
    public void DestinadoAoUsuario_KeywordTor_InLargerWords_DoesNotMatch(string word)
    {
        var keywords = new[] { "tor" };
        string dispatch = $"Encaminhado para {word} para ciência.";

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(match, $"Falso positivo detectado: 'tor' casou dentro de '{word}'");
    }

    [Theory]
    [InlineData("marco")]
    [InlineData("março")]
    [InlineData("marina")]
    [InlineData("tomar")]
    [InlineData("formar")]
    [InlineData("gramar")]
    [InlineData("comarca")]
    public void DestinadoAoUsuario_KeywordMar_InLargerWords_DoesNotMatch(string word)
    {
        var keywords = new[] { "mar" };
        string dispatch = $"Encaminhado em {word} para ciência.";

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(match, $"Falso positivo detectado: 'mar' casou dentro de '{word}'");
    }

    [Theory]
    [InlineData("silvano")]
    [InlineData("silvicultura")]
    [InlineData("brasilval")]
    public void DestinadoAoUsuario_KeywordSilva_InLargerWords_DoesNotMatch(string word)
    {
        var keywords = new[] { "silva" };
        string dispatch = $"Encaminhado ao servidor {word} para ciência.";

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(match, $"Falso positivo detectado: 'silva' casou dentro de '{word}'");
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("analise")]
    [InlineData("santana")]
    [InlineData("bacana")]
    [InlineData("semana")]
    public void DestinadoAoUsuario_KeywordAna_InLargerWords_DoesNotMatch(string word)
    {
        var keywords = new[] { "ana" };
        string dispatch = $"Processo referente a {word} para ciência.";

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(match, $"Falso positivo detectado: 'ana' casou dentro de '{word}'");
    }

    [Theory]
    [InlineData("noticia")]
    [InlineData("partida")]
    [InlineData("ativo")]
    [InlineData("certidao")]
    [InlineData("sentido")]
    [InlineData("gratificacao")]
    public void DestinadoAoUsuario_KeywordTi_InLargerWords_DoesNotMatch(string word)
    {
        var keywords = new[] { "ti" };
        string dispatch = $"Assunto: {word} do processo.";

        bool match = TextAnalyzer.IsDestinedToUser(null, null, dispatch, keywords);
        Assert.False(match, $"Falso positivo detectado: 'ti' casou dentro de '{word}'");
    }

    [Fact]
    public void DestinadoAoUsuario_NumericKeyword_InLongerNumberOrDate_DoesNotMatch()
    {
        var keywords = new[] { "123" };

        // Subnúmero em CPF ou protocolo não deve dar match
        Assert.False(TextAnalyzer.IsDestinedToUser(null, null, "Protocolo 991234567 autuado.", keywords));
        Assert.False(TextAnalyzer.IsDestinedToUser(null, null, "CPF 000.1234.000-00", keywords));
        
        // Número isolado DEVE dar match
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Portaria 123 de 2026.", keywords));
        Assert.True(TextAnalyzer.IsDestinedToUser(null, null, "Sala (123) do bloco A.", keywords));
    }

    // =========================================================================
    // 7. PERFORMANCE E ESTRESSE CONCORRENTE
    // =========================================================================

    [Fact]
    public void DestinadoAoUsuario_ConcurrentStress_1000ExecutionsOnMultiThreads()
    {
        var keywords = new[] { "Victor", "TI", "GINFO/Sistemas" };
        string sampleText = "Encaminho o presente expediente à Diretoria do setor para análise prévia. Ao servidor Victor para providências de TI.";

        var sw = Stopwatch.StartNew();
        Parallel.For(0, 1000, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            bool match = TextAnalyzer.IsDestinedToUser("Título", "Especificação", sampleText, keywords);
            Assert.True(match);
        });
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"1000 execuções concorrentes levaram {sw.ElapsedMilliseconds}ms (limite 1000ms)");
    }

    [Fact]
    public void TextAnalyzer_ContainsKeywordWithWordBoundary_HighVolumeThroughput()
    {
        string text = "o presente expediente foi analisado pelo setor de tecnologia da informacao e encaminhado para providencias cabiveis ao servidor victor lima";
        string kwMatch = "victor lima";
        string kwNoMatch = "tor";

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50_000; i++)
        {
            bool m1 = TextAnalyzer.ContainsKeywordWithWordBoundary(text, kwMatch);
            bool m2 = TextAnalyzer.ContainsKeywordWithWordBoundary(text, kwNoMatch);
            if (!m1 || m2) throw new InvalidOperationException("Falha na lógica de fronteira durante estresse");
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"50.000 iterações de ContainsKeyword levaram {sw.ElapsedMilliseconds}ms (esperado < 500ms)");
    }
}
