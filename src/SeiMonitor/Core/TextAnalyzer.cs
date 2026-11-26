using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SeiMonitor.Core;

/// <summary>
/// Analisador de texto e regras de negócio para monitoramento de processos no SEI.
/// Implementa as heurísticas de detecção de abertura por terceiros na caixa compartilhada GINFO
/// e identificação de processos destinados nominalmente ao usuário logado.
/// </summary>
public static class TextAnalyzer
{
    private static readonly string[] OpenActivityIndicators =
    [
        "aberto na unidade",
        "processo aberto",
        "recebido na unidade",
        "processo recebido",
        "consulta a processo",
        "visualizado",
        "acesso concedido",
        "reabertura de processo"
    ];

    private static readonly string[] ConcludingIndicators =
    [
        "concluido",
        "conclusao",
        "despacho",
        "despachado",
        "remetido",
        "remessa",
        "arquivado",
        "arquivamento",
        "juntado",
        "juntada",
        "encaminhado",
        "encaminhamento",
        "anexado",
        "anexacao",
        "sobrestado",
        "sobrestamento"
    ];

    private static readonly HashSet<string> KnownHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "area", "article", "aside", "b", "base", "bdi", "bdo",
        "blockquote", "body", "br", "button", "caption", "cite", "code", "col", "colgroup",
        "data", "dd", "del", "details", "dfn", "dialog", "div", "dl", "dt", "em", "embed",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "head", "header", "hr", "html", "i", "iframe", "img", "input",
        "ins", "kbd", "label", "legend", "li", "main", "mark", "meta", "meter", "nav",
        "noscript", "object", "ol", "optgroup", "option", "output", "p", "picture", "pre",
        "progress", "q", "rb", "rp", "rt", "ruby", "s", "samp", "script", "section",
        "select", "small", "source", "span", "strong", "style", "sub", "summary", "sup",
        "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time",
        "title", "tr", "track", "u", "ul", "var", "video", "wbr", "font"
    };

    // =========================================================================
    // 1. Regra AbertoPorTerceiro (Caixa Compartilhada GINFO)
    // =========================================================================

    /// <summary>
    /// Avalia a lista de atividades recentes do processo no SEI (ordenando defensivamente por timestamp/id)
    /// e determina se o processo foi visualizado ou recebido por colega de unidade sem despacho conclusivo posterior.
    /// </summary>
    public static bool IsOpenedByThirdParty(IEnumerable<SeiActivity>? activities, string? currentUserName)
    {
        if (activities == null) return false;

        var list = activities as IList<SeiActivity> ?? activities.ToList();
        if (list.Count == 0) return false;

        SeiActivity latest;
        if (list.Count == 1)
        {
            latest = list[0];
        }
        else
        {
            latest = list
                .OrderByDescending(ParseActivityTimestamp)
                .ThenByDescending(a => long.TryParse(a.Id, out var id) ? id : 0)
                .First();
        }

        return IsOpenedByThirdParty(latest.Usuario, latest.Descricao, currentUserName);
    }

    /// <summary>
    /// Sobrecarga para avaliação direta a partir do último usuário e última descrição da atividade.
    /// </summary>
    public static bool IsOpenedByThirdParty(string? lastUser, string? lastActivityDesc, string? currentUserName)
    {
        if (string.IsNullOrWhiteSpace(lastUser) || string.IsNullOrWhiteSpace(currentUserName))
            return false;

        if (string.IsNullOrWhiteSpace(lastActivityDesc))
            return false;

        string normLatestUser = NormalizeText(lastUser);
        string normCurrentUser = NormalizeText(currentUserName);

        // Se o último usuário for o próprio usuário logado (considerando domínios AD, UPN e separadores), não é terceiro
        if (IsSameUser(normLatestUser, normCurrentUser))
            return false;

        string normDesc = NormalizeText(lastActivityDesc);
        if (string.IsNullOrEmpty(normDesc))
            return false;

        // Se a última ação for conclusiva ou resolutiva (despacho, juntada, conclusão, remessa), não está pendente
        foreach (var ci in ConcludingIndicators)
        {
            if (normDesc.Contains(ci, StringComparison.Ordinal))
                return false;
        }

        // Se a última ação for típica de abertura ou recebimento sem despacho conclusivo
        foreach (var oi in OpenActivityIndicators)
        {
            if (normDesc.Contains(oi, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsSameUser(string userA, string userB)
    {
        if (string.Equals(userA, userB, StringComparison.OrdinalIgnoreCase))
            return true;

        string baseA = ExtractBaseUser(userA);
        string baseB = ExtractBaseUser(userB);

        if (string.IsNullOrEmpty(baseA) || string.IsNullOrEmpty(baseB))
            return false;

        return string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBaseUser(string user)
    {
        if (string.IsNullOrEmpty(user)) return string.Empty;

        int slashIdx = user.LastIndexOfAny(['\\', '/']);
        string candidate = slashIdx >= 0 && slashIdx < user.Length - 1 ? user[(slashIdx + 1)..] : user;

        int atIdx = candidate.IndexOf('@');
        if (atIdx >= 0)
        {
            candidate = candidate[..atIdx];
        }

        return candidate.Trim();
    }

    private static DateTime ParseActivityTimestamp(SeiActivity activity)
    {
        if (string.IsNullOrWhiteSpace(activity.Data))
            return DateTime.MinValue;

        string combined = string.IsNullOrWhiteSpace(activity.Hora)
            ? activity.Data.Trim()
            : $"{activity.Data.Trim()} {activity.Hora.Trim()}";

        string[] formats = ["dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd/MM/yyyy", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(combined, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt;
        }
        if (DateTime.TryParse(combined, CultureInfo.InvariantCulture, out dt) || DateTime.TryParse(combined, out dt))
        {
            return dt;
        }
        return DateTime.MinValue;
    }

    // =========================================================================
    // 2. Regra DestinadoAoUsuario (Palavras-Chave)
    // =========================================================================

    /// <summary>
    /// Avalia se alguma palavra-chave do usuário aparece no título, especificação ou último despacho/documento.
    /// Executa normalização sem acentos, case-insensitive e verificação de fronteira de palavras para prevenir falsos positivos.
    /// </summary>
    public static bool IsDestinedToUser(string? title, string? specification, string? lastDispatchText, IEnumerable<string>? keywords)
    {
        if (keywords == null) return false;

        var cleanKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => NormalizeText(k))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToList();

        if (cleanKeywords.Count == 0) return false;

        string combined = $"{title ?? string.Empty} {specification ?? string.Empty} {lastDispatchText ?? string.Empty}";
        string normalizedCombined = NormalizeText(combined);

        if (string.IsNullOrEmpty(normalizedCombined)) return false;

        foreach (var keyword in cleanKeywords)
        {
            if (ContainsKeywordWithWordBoundary(normalizedCombined, keyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sobrecarga recebendo o modelo MonitoredProcess e o texto do último despacho.
    /// </summary>
    public static bool IsDestinedToUser(MonitoredProcess? process, string? lastDispatchText, IEnumerable<string>? keywords)
    {
        if (process == null) return false;
        return IsDestinedToUser(process.Descricao, process.TipoProcesso, lastDispatchText, keywords);
    }

    /// <summary>
    /// Sobrecarga recebendo o modelo SeiProcess e as palavras-chave.
    /// </summary>
    public static bool IsDestinedToUser(SeiProcess? process, IEnumerable<string>? keywords)
    {
        if (process == null) return false;
        return IsDestinedToUser(process.Descricao, process.TipoProcesso, null, keywords);
    }

    /// <summary>
    /// Sobrecarga recebendo o modelo SeiProcess, o texto do último despacho e as palavras-chave.
    /// </summary>
    public static bool IsDestinedToUser(SeiProcess? process, string? lastDispatchText, IEnumerable<string>? keywords)
    {
        if (process == null) return false;
        return IsDestinedToUser(process.Descricao, process.TipoProcesso, lastDispatchText, keywords);
    }

    // =========================================================================
    // 3. Normalização, Higienização e Algoritmo de Fronteira de Palavra
    // =========================================================================

    /// <summary>
    /// Remove tags HTML, remove acentos/diacríticos, caracteres de controle/formatação e converte para minúsculas invariantes.
    /// </summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // 1. Remove tags HTML substituindo por espaço para evitar fusão de palavras
        string stripped = StripHtml(text);
        if (string.IsNullOrEmpty(stripped)) return string.Empty;

        // 2. Decomposição Unicode FormD para isolamento de acentos e filtros de controle
        string normalized = stripped.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (char c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                sb.Append(' ');
                continue;
            }

            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark &&
                uc != UnicodeCategory.Format &&
                uc != UnicodeCategory.Control)
            {
                sb.Append(c);
            }
        }

        // 3. Recomposição FormC e minúsculas invariantes
        string unaccented = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // 4. Compacta múltiplos espaços em branco
        return CollapseWhitespace(unaccented);
    }

    /// <summary>
    /// Remove tags HTML genuínas preservando textos entre colchetes angulares (ex: &lt;Victor&gt;, &lt;email@gov&gt;, operadores relacionais &lt;).
    /// </summary>
    public static string StripHtml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        if (input.IndexOf('<') < 0 && input.IndexOf('&') < 0) return input;

        var sb = new StringBuilder(input.Length);
        int len = input.Length;

        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            if (c == '<')
            {
                int closeIdx = input.IndexOf('>', i + 1);
                // Se não há '>' de fechamento ou a distância é anômala (> 1024 chars), preserva o '<' como texto literal
                if (closeIdx == -1 || closeIdx - i > 1024)
                {
                    sb.Append(c);
                    continue;
                }

                ReadOnlySpan<char> tagSpan = input.AsSpan(i + 1, closeIdx - i - 1).Trim();

                // Se vazio (<>) ou contiver email (@), não é tag HTML
                if (tagSpan.IsEmpty || tagSpan.IndexOf('@') >= 0)
                {
                    sb.Append(' ').Append(tagSpan).Append(' ');
                    i = closeIdx;
                    continue;
                }

                // Comentário HTML <!-- ... --> ou DOCTYPE <!DOCTYPE ...>
                if (tagSpan.StartsWith("!--") || tagSpan.StartsWith("!doctype", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(' ');
                    i = closeIdx;
                    continue;
                }

                // Tag de fechamento </tag>
                bool isClosing = tagSpan.StartsWith("/");
                ReadOnlySpan<char> candidate = isClosing ? tagSpan[1..].TrimStart() : tagSpan;

                int nameEnd = 0;
                while (nameEnd < candidate.Length && char.IsLetterOrDigit(candidate[nameEnd]))
                {
                    nameEnd++;
                }

                if (nameEnd > 0)
                {
                    string tagName = candidate[..nameEnd].ToString();
                    if (KnownHtmlTags.Contains(tagName))
                    {
                        sb.Append(' ');
                        i = closeIdx;
                        continue;
                    }
                }

                // Não é tag HTML conhecida (ex: <Victor>, <email@gov>, <1000>): preserva conteúdo interno
                sb.Append(' ').Append(tagSpan).Append(' ');
                i = closeIdx;
                continue;
            }

            sb.Append(c);
        }

        string result = sb.ToString();
        if (result.IndexOf('&') >= 0)
        {
            result = result
                .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
                .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
                .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Verifica se a palavra-chave está presente no texto respeitando fronteiras de caracteres alfanuméricos.
    /// Impede falsos positivos como "tor" em "setor" ou "diretoria", preservando pontuações ("Victor,", "(Victor)").
    /// </summary>
    public static bool ContainsKeywordWithWordBoundary(string text, string keyword)
    {
        int kwLen = keyword.Length;
        int textLen = text.Length;
        if (kwLen == 0 || kwLen > textLen) return false;

        bool checkLeft = char.IsLetterOrDigit(keyword[0]);
        bool checkRight = char.IsLetterOrDigit(keyword[^1]);

        int startIndex = 0;
        while (startIndex <= textLen - kwLen)
        {
            int index = text.IndexOf(keyword, startIndex, StringComparison.Ordinal);
            if (index < 0) return false;

            bool leftOk = !checkLeft || (index == 0 || !char.IsLetterOrDigit(text[index - 1]));
            bool rightOk = !checkRight || (index + kwLen == textLen || !char.IsLetterOrDigit(text[index + kwLen]));

            if (leftOk && rightOk)
            {
                return true;
            }

            startIndex = index + 1;
        }

        return false;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inWhitespace = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(c);
                inWhitespace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
