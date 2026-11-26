using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SeiMonitor.E2ETests.Harness;

public static class TextAnalyzerHarness
{
    private static readonly string[] OpenActivityIndicators = new[]
    {
        "aberto na unidade",
        "processo aberto",
        "recebido na unidade",
        "acesso concedido",
        "visualizado"
    };

    private static readonly string[] ConcludingIndicators = new[]
    {
        "concluído",
        "concluido",
        "remetido",
        "arquivado",
        "despacho",
        "juntado",
        "encaminhado"
    };

    public static bool IsOpenedByThirdParty(IEnumerable<AtividadeModel>? activities, string currentUserName)
    {
        if (activities == null || !activities.Any()) return false;

        var latest = activities.First();
        if (string.IsNullOrWhiteSpace(latest.Usuario)) return false;

        string normalizedLatestUser = NormalizeString(latest.Usuario);
        string normalizedCurrentUser = NormalizeString(currentUserName);

        // If latest action was by the current user, it is not "aberto por terceiro"
        if (normalizedLatestUser.Equals(normalizedCurrentUser, StringComparison.OrdinalIgnoreCase) ||
            normalizedLatestUser.EndsWith("\\" + normalizedCurrentUser, StringComparison.OrdinalIgnoreCase) ||
            normalizedCurrentUser.EndsWith("\\" + normalizedLatestUser, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string desc = NormalizeString(latest.Descricao ?? string.Empty);

        // Check if it is a concluding action
        if (ConcludingIndicators.Any(ci => desc.Contains(NormalizeString(ci), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Check if it indicates opening/receiving without conclusion
        return OpenActivityIndicators.Any(oi => desc.Contains(NormalizeString(oi), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDestinedToUser(string? title, string? specification, string? lastDispatchText, IEnumerable<string>? keywords)
    {
        if (keywords == null) return false;

        var cleanKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        if (!cleanKeywords.Any()) return false;

        string combinedText = $"{title ?? string.Empty} {specification ?? string.Empty} {lastDispatchText ?? string.Empty}";
        string normalizedCombined = NormalizeString(combinedText);

        foreach (var keyword in cleanKeywords)
        {
            string normalizedKeyword = NormalizeString(keyword);
            if (string.IsNullOrWhiteSpace(normalizedKeyword)) continue;

            // Pattern boundary match or literal substring
            if (Regex.IsMatch(normalizedCombined, @"\b" + Regex.Escape(normalizedKeyword) + @"\b", RegexOptions.IgnoreCase) ||
                normalizedCombined.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeString(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Strip HTML tags if any
        text = Regex.Replace(text, "<.*?>", string.Empty);

        // Normalize accents
        string normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
