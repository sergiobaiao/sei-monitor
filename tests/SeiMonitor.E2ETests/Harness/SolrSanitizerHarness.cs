using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeiMonitor.E2ETests.Harness;

public class SeiSanitizedException : Exception
{
    public string SafeErrorCode { get; }

    public SeiSanitizedException(string safeErrorCode) : base(safeErrorCode)
    {
        SafeErrorCode = safeErrorCode;
    }
}

public static class SolrSanitizerHarness
{
    // Safe allowed error codes per constitution §I and APIS-SEI.md
    public static readonly string[] AllowedSafeErrorCodes = new[]
    {
        "SEI_INDISPONIVEL",
        "SEI_ACESSO_NEGADO",
        "PROCESSO_NAO_ENCONTRADO",
        "SEI_RESPOSTA_INVALIDA",
        "SEI_NAO_CONFIGURADO"
    };

    public static SeiSanitizedException SanitizeWsseiError(string rawJsonResponseBody, int httpStatusCode)
    {
        if (httpStatusCode == 401 || httpStatusCode == 403 || httpStatusCode == 422)
        {
            return new SeiSanitizedException("SEI_ACESSO_NEGADO");
        }

        if (httpStatusCode == 404)
        {
            return new SeiSanitizedException("PROCESSO_NAO_ENCONTRADO");
        }

        if (httpStatusCode >= 500)
        {
            return new SeiSanitizedException("SEI_INDISPONIVEL");
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJsonResponseBody);
            var root = doc.RootElement;

            bool sucesso = true;
            if (root.TryGetProperty("sucesso", out var sucessoProp))
            {
                sucesso = sucessoProp.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(sucessoProp.GetString(), out var b) && b,
                    _ => true
                };
            }

            if (!sucesso)
            {
                // Discard mensagem and exception regardless of content (Anti-Leak Solr protection)
                // Determine category from HTTP or standard code
                return new SeiSanitizedException("SEI_ACESSO_NEGADO");
            }

            return new SeiSanitizedException("SEI_RESPOSTA_INVALIDA");
        }
        catch (JsonException)
        {
            return new SeiSanitizedException("SEI_RESPOSTA_INVALIDA");
        }
    }

    public static bool ContainsSolrLeak(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // Check for Solr endpoints, basic auth in URLs, or credentials
        var solrRegex = new Regex(@"(solr|:[0-9]{4}/solr|http://[^:]+:[^@]+@)", RegexOptions.IgnoreCase);
        return solrRegex.IsMatch(text);
    }
}
