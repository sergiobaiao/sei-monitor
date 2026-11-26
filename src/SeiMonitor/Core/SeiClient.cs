using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SeiMonitor.Core;

public sealed record SeiUnidadeItem(string Id, string Sigla, string Descricao)
{
    public string Display => string.IsNullOrWhiteSpace(Descricao)
        ? (string.IsNullOrWhiteSpace(Id) ? Sigla : $"{Sigla} ({Id})")
        : $"{Sigla} — {Descricao}";
}

public sealed record SeiOrgaoItem(string Id, string Sigla)
{
    public string Display => $"{Sigla} ({Id})";

    public static IReadOnlyList<SeiOrgaoItem> DefaultOrgaos { get; } = new SeiOrgaoItem[]
    {
        new("0", "GOV-PI"), new("49", "ADAPI-PI"), new("40", "ADH-PI"), new("17", "AGESPISA-PI"),
        new("59", "AGRESPI-PI"), new("76", "ALEPI-PI"), new("20", "BADESPI-PI"), new("14", "CBMEPI-PI"),
        new("41", "CCOM-PI"), new("65", "CDTER-PI"), new("79", "CEDDH-PI"), new("37", "CENDFOL-PI"),
        new("53", "CFLP-PI"), new("5", "CGE-PI"), new("27", "COJUV-PI"), new("60", "CVCI-PI"),
        new("48", "DER-PI"), new("16", "DETRAN-PI"), new("47", "DPE-PI"), new("29", "EMATER-PI"),
        new("11", "EMGERPI-PI"), new("2", "ETIPI-PI"), new("45", "FAPEPI-PI"), new("55", "FEPISERH-PI"),
        new("61", "FUAPI-PI"), new("9", "FUESPI-PI"), new("57", "FUNART-PI"), new("44", "FUNDESPI-PI"),
        new("34", "GAMIL-PI"), new("68", "GASPISA-PI"), new("58", "IASPI-PI"), new("33", "IDEPI-PI"),
        new("26", "IMEPI-PI"), new("7", "INTERPI-PI"), new("56", "INVESTEPIAUI-PI"), new("32", "ISBPI-PI"),
        new("23", "JUCEPI-PI"), new("75", "MUNICIPIO-PI"), new("30", "PC-PI"), new("3", "PGE-PI"),
        new("71", "PIAUILINK-PI"), new("21", "PIAUIPREV-PI"), new("73", "PIT-PI"), new("13", "PM-PI"),
        new("67", "PORTO-PI"), new("74", "REABILITAR-PI"), new("64", "SADA-PI"), new("42", "SAF-PI"),
        new("25", "SASC-PI"), new("28", "SDE-PI"), new("1", "SEAD-PI"), new("43", "SEAGRO-PI"),
        new("69", "SECEPI-PI"), new("52", "SECID-PI"), new("78", "SECOM-PI"), new("19", "SECULT-PI"),
        new("54", "SEDEC-PI"), new("8", "SEDUC-PI"), new("4", "SEFAZ-PI"), new("38", "SEFIR-PI"),
        new("31", "SEGOV-PI"), new("39", "SEID-PI"), new("35", "SEINFRA-PI"), new("18", "SEJUS-PI"),
        new("15", "SEMARH-PI"), new("24", "SEMPI-PI"), new("10", "SEPLAN-PI"), new("66", "SERES-PI"),
        new("36", "SESAPI-PI"), new("79", "SESB-PI"), new("50", "SETRANS-PI"), new("77", "SETRE-PI"),
        new("46", "SETUR-PI"), new("72", "SIA-PI"), new("51", "SIDERPI-PI"), new("12", "SSP-PI"),
        new("62", "SUPRES-PI"), new("6", "VICEGOV-PI"), new("63", "ZPE")
    };
}

public interface ISeiClient : IDisposable
{
    Task<SeiAuthResult> AuthenticateAsync(bool forceRenew = false, CancellationToken ct = default);
    Task<ProcessListResult> SearchProcessesAsync(string unidade, int start = 0, int limit = 50, CancellationToken ct = default);
    Task<LightProcessSummary> GetLightSummaryAsync(string idProcedimento, string unidade, CancellationToken ct = default);
    Task<ProcessDetail> GetProcessDetailsAsync(string idProcedimento, string unidade, CancellationToken ct = default);
    Task<LightProcessSummary> GetLightSummaryAsync(string idProcedimento, string unidade, string? protocolo, CancellationToken ct)
        => GetLightSummaryAsync(idProcedimento, unidade, ct);
    Task<ProcessDetail> GetProcessDetailsAsync(string idProcedimento, string unidade, string? protocolo, CancellationToken ct)
        => GetProcessDetailsAsync(idProcedimento, unidade, ct);
    Task<string?> ResolveUnitIdAsync(string? sigla, CancellationToken ct = default) => Task.FromResult<string?>(null);
    Task<List<SeiUnidadeItem>> GetUserUnitsAsync(bool forceRefresh = false, CancellationToken ct = default) => Task.FromResult(new List<SeiUnidadeItem>());
    Task<List<SeiOrgaoItem>> FetchOrgaosFromPortalAsync(CancellationToken ct = default) => Task.FromResult(new List<SeiOrgaoItem>(SeiOrgaoItem.DefaultOrgaos));
}

/// <summary>
/// Cliente resiliente de integração HTTP com o SEI (WSSEI v2 primário e SEAD fallback).
/// Implementa autenticação lazy com SemaphoreSlim, renovação automática no 401 ou 403,
/// injeção dos cabeçalhos obrigatórios e descarte incondicional de erros para sanitização contra o bug do Solr.
/// </summary>
public sealed class SeiClient : ISeiClient
{
    private readonly Config _config;
    private readonly Database _db;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly (string user, string pass)? _explicitCredentials;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private volatile SeiAuthResult? _session;
    private SeiAuthResult? _seadSession;

    public SeiClient(
        Config config,
        Database db,
        HttpMessageHandler? customHandler = null,
        (string user, string pass)? explicitCredentials = null)
    {
        _config = config;
        _db = db;
        _explicitCredentials = explicitCredentials;
        _disposeHttpClient = true;
        _httpClient = customHandler != null
            ? new HttpClient(customHandler)
            : new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

        _httpClient.Timeout = TimeSpan.FromSeconds(_config.SeiTimeoutSeconds);
    }

    public SeiClient(
        Config config,
        Database db,
        HttpClient httpClient,
        (string user, string pass)? explicitCredentials = null)
    {
        _config = config;
        _db = db;
        _explicitCredentials = explicitCredentials;
        _disposeHttpClient = false;
        _httpClient = httpClient;
    }

    public string? CachedToken => _session?.Token;
    public string ActiveApiSource => _session?.SourceApi ?? "WSSEI";

    // =========================================================================
    // 1. Autenticação Lazy e SemaphoreSlim(1,1) Double-Checked Locking
    // =========================================================================

    public async Task<SeiAuthResult> AuthenticateAsync(bool forceRenew = false, CancellationToken ct = default)
    {
        if (!forceRenew && _session is { } session)
        {
            return session;
        }

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRenew && _session is { } cached)
            {
                return cached;
            }

            if (forceRenew)
            {
                _session = null;
                _seadSession = null;
            }

            return await AuthenticateInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<SeiAuthResult> AuthenticateInternalAsync(CancellationToken ct)
    {
        Exception? wsseiError = null;
        try
        {
            var token = await AuthenticateWsseiAsync(ct).ConfigureAwait(false);
            return _session = new SeiAuthResult(token, "WSSEI", DateTime.UtcNow);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (IsTransientOrDown(ex) ||
            ex is SeiException { ErrorCode: SeiErrorCode.AcessoNegado or SeiErrorCode.RespostaInvalida }))
        {
            wsseiError = ex;
            _db.Log("WARN", "Autenticação no WSSEI v2 falhou. Tentando fallback SEAD...");
        }

        if (wsseiError != null)
        {
            try
            {
                var token = await AuthenticateSeadAsync(ct).ConfigureAwait(false);
                return _session = _seadSession = new SeiAuthResult(token, "SEAD", DateTime.UtcNow);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _db.Log("ERROR", "Falha também na autenticação de fallback da SEAD.");
                // Conforme APIS-SEI.md e especificação: lança a exceção primária do WSSEI
                throw wsseiError;
            }
        }

        throw new SeiException(SeiErrorCode.Indisponivel);
    }

    private async Task<string> AuthenticateWsseiAsync(CancellationToken ct)
    {
        var (user, pass) = GetCredentials();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new SeiException(SeiErrorCode.NaoConfigurado);
        }

        var baseUrl = _config.SeiWsseiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/autenticar";

        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("usuario", user),
            new KeyValuePair<string, string>("senha", pass),
            new KeyValuePair<string, string>("orgao", _config.SeiWsseiOrgao)
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not SeiException && IsTransientOrDown(ex))
        {
            throw new SeiException(SeiErrorCode.Indisponivel, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                _db.Log("WARN", $"Login no WSSEI retornou HTTP {(int)response.StatusCode}.");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden ||
                response.StatusCode == (HttpStatusCode)422)
            {
                throw new SeiException(SeiErrorCode.AcessoNegado);
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new SeiException(SeiErrorCode.Indisponivel);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SeiException(SeiErrorCode.Indisponivel);
            }

            var jsonBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ExtractWsseiToken(jsonBytes);
        }
    }

    private static string ExtractWsseiToken(byte[] jsonBytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException)
        {
            throw new SeiException(SeiErrorCode.RespostaInvalida);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SeiException(SeiErrorCode.RespostaInvalida);

            ValidateWsseiResponse(root, isSearch: false);

            if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
            {
                if (dataProp.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
                {
                    var token = tokenProp.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }

            throw new SeiException(SeiErrorCode.RespostaInvalida);
        }
    }

    private async Task<string> AuthenticateSeadAsync(CancellationToken ct)
    {
        var (user, pass) = GetCredentials();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new SeiException(SeiErrorCode.NaoConfigurado);
        }

        var baseUrl = _config.SeiSeadBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1/orgaos/usuarios/login";

        var loginObj = new
        {
            Usuario = !string.IsNullOrWhiteSpace(_config.SeiSeadUsuario) ? _config.SeiSeadUsuario : user,
            Senha = pass,
            Orgao = _config.SeiSeadOrgao
        };

        var json = JsonSerializer.Serialize(loginObj);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not SeiException && IsTransientOrDown(ex))
        {
            throw new SeiException(SeiErrorCode.Indisponivel, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                _db.Log("WARN", $"Login na SEAD retornou HTTP {(int)response.StatusCode}.");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden ||
                response.StatusCode == (HttpStatusCode)422)
            {
                throw new SeiException(SeiErrorCode.AcessoNegado);
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new SeiException(SeiErrorCode.Indisponivel);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SeiException(SeiErrorCode.Indisponivel);
            }

            var jsonBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var token = ExtractSeadToken(jsonBytes, _config.SeiUnidadeSigla, out _resolvedSeadUnitId, out var units);
            if (units.Count > 0)
            {
                _cachedUserUnits = units;
            }
            return token;
        }
    }

    private string? _resolvedSeadUnitId;
    private List<SeiUnidadeItem> _cachedUserUnits = new();

    public async Task<string?> ResolveUnitIdAsync(string? sigla, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedSeadUnitId))
            return _resolvedSeadUnitId;

        try
        {
            await AuthenticateSeadAsync(ct).ConfigureAwait(false);
            return _resolvedSeadUnitId;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<SeiUnidadeItem>> GetUserUnitsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cachedUserUnits.Count > 0)
            return new List<SeiUnidadeItem>(_cachedUserUnits);

        await AuthenticateSeadAsync(ct).ConfigureAwait(false);
        return new List<SeiUnidadeItem>(_cachedUserUnits);
    }

    public async Task<List<SeiOrgaoItem>> FetchOrgaosFromPortalAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUri = new Uri(_config.SeiWsseiBaseUrl);
            var portalUrl = $"{baseUri.Scheme}://{baseUri.Authority}";
            using var resp = await _httpClient.GetAsync(portalUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new List<SeiOrgaoItem>(SeiOrgaoItem.DefaultOrgaos);

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var matches = System.Text.RegularExpressions.Regex.Matches(
                html,
                @"<option[^>]*value=[""']?(\d+)[""']?[^>]*>([^<]+)</option>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var parsed = new List<SeiOrgaoItem>();
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var id = m.Groups[1].Value.Trim();
                var sigla = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(sigla))
                    parsed.Add(new SeiOrgaoItem(id, sigla));
            }

            return parsed.Count > 0 ? parsed : new List<SeiOrgaoItem>(SeiOrgaoItem.DefaultOrgaos);
        }
        catch
        {
            return new List<SeiOrgaoItem>(SeiOrgaoItem.DefaultOrgaos);
        }
    }

    private static string ExtractSeadToken(
        byte[] jsonBytes,
        string? targetSigla,
        out string? resolvedUnitId,
        out List<SeiUnidadeItem> units)
    {
        resolvedUnitId = null;
        units = new List<SeiUnidadeItem>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException)
        {
            throw new SeiException(SeiErrorCode.RespostaInvalida);
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Tenta extrair lista de unidades do usuário e IdUnidade correspondente à sigla
            if (root.TryGetProperty("Unidades", out var unidadesProp) && unidadesProp.ValueKind == JsonValueKind.Array)
            {
                string? firstId = null;
                foreach (var u in unidadesProp.EnumerateArray())
                {
                    string? id = u.TryGetProperty("IdUnidade", out var idP) ? idP.ToString()
                        : u.TryGetProperty("idUnidade", out var idP2) ? idP2.ToString()
                        : u.TryGetProperty("Id", out var idP3) ? idP3.ToString() : null;
                    string? s = u.TryGetProperty("Sigla", out var sP) ? sP.GetString()
                        : (u.TryGetProperty("sigla", out var sP2) ? sP2.GetString() : null);
                    string? desc = u.TryGetProperty("Descricao", out var dP) ? dP.GetString()
                        : (u.TryGetProperty("descricao", out var dP2) ? dP2.GetString() : "");

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        units.Add(new SeiUnidadeItem(id, s?.Trim() ?? id, desc?.Trim() ?? ""));
                        firstId ??= id;
                        if (resolvedUnitId == null && !string.IsNullOrWhiteSpace(targetSigla) &&
                            string.Equals(s?.Trim(), targetSigla.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedUnitId = id;
                        }
                    }
                }
                resolvedUnitId ??= firstId;
            }

            if (string.IsNullOrWhiteSpace(resolvedUnitId) &&
                root.TryGetProperty("Login", out var loginProp) && loginProp.ValueKind == JsonValueKind.Object &&
                loginProp.TryGetProperty("IdUnidadeAtual", out var idAtualProp))
            {
                resolvedUnitId = idAtualProp.ToString();
            }

            if (root.TryGetProperty("Token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
            {
                var token = tokenProp.GetString();
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }

            if (root.TryGetProperty("token", out var tokenProp2) && tokenProp2.ValueKind == JsonValueKind.String)
            {
                var token = tokenProp2.GetString();
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }

            throw new SeiException(SeiErrorCode.RespostaInvalida);
        }
    }

    // =========================================================================
    // 2. Interceptor de Cabeçalhos e Renovação Automática no 401 ou 403
    // =========================================================================

    private async Task<HttpResponseMessage> SendWsseiWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string unidade,
        CancellationToken ct)
    {
        var auth = await AuthenticateAsync(forceRenew: false, ct).ConfigureAwait(false);
        if (auth.SourceApi == "SEAD")
        {
            throw new SeiException(SeiErrorCode.Indisponivel);
        }

        using var request = requestFactory();
        request.Headers.TryAddWithoutValidation("token", auth.Token);
        request.Headers.TryAddWithoutValidation("unidade", unidade);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not SeiException && IsTransientOrDown(ex))
        {
            throw new SeiException(SeiErrorCode.Indisponivel, ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // O WSSEI pode responder 401 ou 403 quando o token expira.
            response.Dispose();

            SeiAuthResult renewed;
            await _authLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session is { } current && !ReferenceEquals(current, auth))
                {
                    // Outra thread concorrente já obteve um novo token válido enquanto aguardávamos
                    renewed = current;
                }
                else
                {
                    _session = null;
                    renewed = await AuthenticateInternalAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _authLock.Release();
            }

            if (renewed.SourceApi == "SEAD")
                throw new SeiException(SeiErrorCode.Indisponivel);

            using var retryReq = requestFactory();
            retryReq.Headers.TryAddWithoutValidation("token", renewed.Token);
            retryReq.Headers.TryAddWithoutValidation("unidade", unidade);

            try
            {
                response = await _httpClient.SendAsync(retryReq, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not SeiException && IsTransientOrDown(ex))
            {
                throw new SeiException(SeiErrorCode.Indisponivel, ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden ||
                response.StatusCode == (HttpStatusCode)422)
            {
                response.Dispose();
                throw new SeiException(SeiErrorCode.AcessoNegado);
            }
        }

        return response;
    }

    // =========================================================================
    // 3. Sanitização Preventiva contra o Bug do Solr (mod-wssei)
    // =========================================================================

    public static void ValidateWsseiResponse(JsonElement root, bool isSearch = false)
    {
        if (root.TryGetProperty("sucesso", out var sucessoProp))
        {
            bool sucesso = sucessoProp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(sucessoProp.GetString(), out var b) && b,
                _ => true
            };

            if (!sucesso)
            {
                // DESCARTE INCONDICIONAL:
                // Jamais lê, armazena ou inspeciona os campos "mensagem" e "exception"
                if (isSearch)
                {
                    throw new SeiException(SeiErrorCode.FalhaPesquisa);
                }
                throw new SeiException(SeiErrorCode.AcessoNegado);
            }
        }
    }

    // =========================================================================
    // 4. Métodos Operacionais de Negócio com Fallback para SEAD
    // =========================================================================

    public async Task<ProcessListResult> SearchProcessesAsync(
        string unidade,
        int start = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        return await ExecuteWithFallbackAsync(
            async token => await SearchProcessesWsseiAsync(unidade, start, limit, token).ConfigureAwait(false),
            async token => await SearchProcessesSeadAsync(unidade, start, limit, token).ConfigureAwait(false),
            ct
        ).ConfigureAwait(false);
    }

    private async Task<ProcessListResult> SearchProcessesWsseiAsync(string unidade, int start, int limit, CancellationToken ct)
    {
        var baseUrl = _config.SeiWsseiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/processo/pesquisar?palavrasChave=&start={start}&limit={limit}";

        using var response = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), unidade, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)422)
            throw new SeiException(SeiErrorCode.AcessoNegado);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProcessListResult(0, new List<SeiProcessItem>());

        if ((int)response.StatusCode >= 500)
            throw new SeiException(SeiErrorCode.Indisponivel);

        if (!response.IsSuccessStatusCode)
            throw new SeiException(SeiErrorCode.Indisponivel);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new SeiException(SeiErrorCode.RespostaInvalida);
        }

        using (doc)
        {
            var root = doc.RootElement;
            ValidateWsseiResponse(root, isSearch: true);

            int total = 0;
            if (root.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var tVal))
            {
                total = tVal;
            }

            var items = new List<SeiProcessItem>();
            if (root.TryGetProperty("data", out var dataProp))
            {
                if (dataProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in dataProp.EnumerateArray())
                    {
                        var parsed = ParseWsseiProcessItem(elem);
                        if (parsed != null) items.Add(parsed);
                    }
                }
            }

            return new ProcessListResult(total > 0 ? total : items.Count, items);
        }
    }

    private static SeiProcessItem? ParseWsseiProcessItem(JsonElement elem)
    {
        string id = "";
        if (elem.TryGetProperty("idProcedimento", out var pId)) id = pId.GetString() ?? "";
        else if (elem.TryGetProperty("id", out var pId2)) id = pId2.GetString() ?? "";

        string protocolo = "";
        if (elem.TryGetProperty("protocoloFormatadoProcedimento", out var pProt)) protocolo = pProt.GetString() ?? "";
        else if (elem.TryGetProperty("protocoloFormatado", out var pProt2)) protocolo = pProt2.GetString() ?? "";
        else if (elem.TryGetProperty("atributos", out var aProp) && aProp.TryGetProperty("numero", out var anProp)) protocolo = anProp.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(protocolo))
            return null;

        string? desc = null;
        if (elem.TryGetProperty("descricao", out var dProp)) desc = dProp.GetString();
        else if (elem.TryGetProperty("especificacao", out var eProp)) desc = eProp.GetString();
        else if (elem.TryGetProperty("atributos", out var aProp) && aProp.TryGetProperty("descricao", out var adProp)) desc = adProp.GetString();

        string? tipo = null;
        if (elem.TryGetProperty("tipoProcesso", out var tProp)) tipo = tProp.GetString();
        else if (elem.TryGetProperty("atributos", out var aProp) && aProp.TryGetProperty("tipoProcesso", out var atProp)) tipo = atProp.GetString();

        string? sigla = null;
        if (elem.TryGetProperty("unidade", out var uProp))
        {
            if (uProp.ValueKind == JsonValueKind.Object && uProp.TryGetProperty("sigla", out var sProp)) sigla = sProp.GetString();
            else if (uProp.ValueKind == JsonValueKind.String) sigla = uProp.GetString();
        }
        else if (elem.TryGetProperty("atributos", out var aProp) && aProp.TryGetProperty("unidade", out var auProp) && auProp.TryGetProperty("sigla", out var asProp))
        {
            sigla = asProp.GetString();
        }

        int? nivel = null;
        if (elem.TryGetProperty("status", out var stProp) && stProp.TryGetProperty("nivelAcessoGlobal", out var nProp))
        {
            if (nProp.ValueKind == JsonValueKind.Number) nivel = nProp.GetInt32();
            else if (nProp.ValueKind == JsonValueKind.String && int.TryParse(nProp.GetString(), out var ni)) nivel = ni;
        }

        var unidadesAbertas = new List<string>();
        JsonElement unidadesEl = default;
        if (elem.TryGetProperty("dadosAbertura", out var daProp) && daProp.TryGetProperty("unidades", out unidadesEl))
        {
            if (unidadesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in unidadesEl.EnumerateArray())
                {
                    if (u.ValueKind == JsonValueKind.String) unidadesAbertas.Add(u.GetString() ?? "");
                    else if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("sigla", out var usProp)) unidadesAbertas.Add(usProp.GetString() ?? "");
                }
            }
        }

        return new SeiProcessItem(id, protocolo, desc, tipo, sigla, nivel, unidadesAbertas);
    }

    private async Task<ProcessListResult> SearchProcessesSeadAsync(string unidade, int start, int limit, CancellationToken ct)
    {
        var baseUrl = _config.SeiSeadBaseUrl.TrimEnd('/');
        int quantidade = Math.Clamp(limit, 1, 20);
        int pagina = (start / quantidade) + 1;
        var url = $"{baseUrl}/v1/unidades/{unidade}/procedimentos?pagina={pagina}&quantidade={quantidade}&sinal_ultimo_andamento=S";

        HttpResponseMessage response = await SendSeadWithRetryAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            response = await SendSeadWithRetryAsync($"{baseUrl}/v1/unidades/{unidade}/procedimentos/consulta", ct).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if (!response.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var items = new List<SeiProcessItem>();
            JsonElement arr = default;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.TryGetProperty("Procedimentos", out var pArr) && pArr.ValueKind == JsonValueKind.Array) arr = pArr;
            else if (root.TryGetProperty("Itens", out var iArr) && iArr.ValueKind == JsonValueKind.Array) arr = iArr;
            else if (root.TryGetProperty("procedimentos", out var pArr2) && pArr2.ValueKind == JsonValueKind.Array) arr = pArr2;

            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in arr.EnumerateArray())
                {
                    string id = elem.TryGetProperty("IdProcedimento", out var p1) ? p1.ToString() : "";
                    string prot = elem.TryGetProperty("ProcedimentoFormatado", out var p2) ? p2.GetString() ?? "" : "";
                    string? tipo = elem.TryGetProperty("TipoProcedimento", out var tp) && tp.ValueKind == JsonValueKind.Object && tp.TryGetProperty("Nome", out var tn) ? tn.GetString() : null;
                    string? spec = elem.TryGetProperty("Especificacao", out var sp) ? sp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(prot))
                    {
                        items.Add(new SeiProcessItem(string.IsNullOrWhiteSpace(id) ? prot : id, prot, spec, tipo, _config.SeiUnidadeSigla, null, new List<string>()));
                    }
                }
            }

            int total = items.Count;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Info", out var infoProp) && infoProp.ValueKind == JsonValueKind.Object &&
                infoProp.TryGetProperty("TotalItens", out var tiProp) && tiProp.TryGetInt32(out var tiVal))
            {
                total = tiVal;
            }

            return new ProcessListResult(total, items);
        }
    }

    public Task<LightProcessSummary> GetLightSummaryAsync(
        string idProcedimento,
        string unidade,
        CancellationToken ct = default) => GetLightSummaryAsync(idProcedimento, unidade, null, ct);

    public Task<LightProcessSummary> GetLightSummaryAsync(
        string idProcedimento,
        string unidade,
        string? protocolo,
        CancellationToken ct)
    {
        return ExecuteWithFallbackAsync(
            async token => await GetLightSummaryWsseiAsync(idProcedimento, unidade, token).ConfigureAwait(false),
            async token => await GetLightSummarySeadAsync(idProcedimento, unidade, ResolveSeadProtocol(idProcedimento, protocolo), token).ConfigureAwait(false),
            ct
        );
    }

    private async Task<LightProcessSummary> GetLightSummaryWsseiAsync(string idProcedimento, string unidade, CancellationToken ct)
    {
        var baseUrl = _config.SeiWsseiBaseUrl.TrimEnd('/');

        // 1. Total de documentos com limit=1
        var docsUrl = $"{baseUrl}/documento/listar/{idProcedimento}?start=0&limit=1";
        int totalDocumentos = 0;
        using (var responseDocs = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, docsUrl), unidade, ct).ConfigureAwait(false))
        {
            if (responseDocs.StatusCode == HttpStatusCode.Unauthorized || responseDocs.StatusCode == HttpStatusCode.Forbidden || responseDocs.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if ((int)responseDocs.StatusCode >= 500 || !responseDocs.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await responseDocs.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            ValidateWsseiResponse(doc.RootElement, isSearch: false);
            if (doc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var tVal))
            {
                totalDocumentos = tVal;
            }
        }

        // 2. Último andamento com limit=1
        var ativUrl = $"{baseUrl}/atividade/listar?procedimento={idProcedimento}&start=0&limit=1";
        string? ultData = null;
        string? ultDesc = null;
        string? ultUser = null;

        using (var responseAtiv = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, ativUrl), unidade, ct).ConfigureAwait(false))
        {
            if (responseAtiv.StatusCode == HttpStatusCode.Unauthorized || responseAtiv.StatusCode == HttpStatusCode.Forbidden || responseAtiv.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if ((int)responseAtiv.StatusCode >= 500 || !responseAtiv.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await responseAtiv.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            ValidateWsseiResponse(doc.RootElement, isSearch: false);
            if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataProp.EnumerateArray())
                {
                    string data = item.TryGetProperty("data", out var dP) ? dP.GetString() ?? "" : "";
                    string hora = item.TryGetProperty("hora", out var hP) ? hP.GetString() ?? "" : "";
                    ultData = $"{data} {hora}".Trim();
                    ultDesc = item.TryGetProperty("descricao", out var descP) ? descP.GetString() : null;
                    ultUser = item.TryGetProperty("usuario", out var uP) ? uP.GetString() : null;
                    break;
                }
            }
        }

        var cachedProc = _db.GetMonitoredProcess(idProcedimento);
        string protocoloFormatado = cachedProc?.ProtocoloFormatado ?? idProcedimento;

        return new LightProcessSummary(
            idProcedimento,
            totalDocumentos,
            ultData,
            ultDesc,
            ultUser,
            protocoloFormatado
        );
    }

    private async Task<LightProcessSummary> GetLightSummarySeadAsync(string idProcedimento, string unidade, string protocolo, CancellationToken ct)
    {
        var baseUrl = _config.SeiSeadBaseUrl.TrimEnd('/');
        var queryProtocol = Uri.EscapeDataString(protocolo);

        // Consulta de documentos na SEAD
        var docsUrl = $"{baseUrl}/v1/unidades/{unidade}/procedimentos/documentos?protocolo_procedimento={queryProtocol}&pagina=1&quantidade=10";

        int totalDocs = 0;
        using var respDocs = await SendSeadWithRetryAsync(docsUrl, ct).ConfigureAwait(false);
        if (respDocs.StatusCode == HttpStatusCode.Unauthorized || respDocs.StatusCode == HttpStatusCode.Forbidden || respDocs.StatusCode == (HttpStatusCode)422)
            throw new SeiException(SeiErrorCode.AcessoNegado);
        if (!respDocs.IsSuccessStatusCode)
            throw new SeiException(SeiErrorCode.Indisponivel);

        var bytesDocs = await respDocs.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(bytesDocs))
        {
            if (doc.RootElement.TryGetProperty("Info", out var infoProp) && infoProp.TryGetProperty("TotalItens", out var totProp) && totProp.TryGetInt32(out var tVal))
            {
                totalDocs = tVal;
            }
            else if (doc.RootElement.TryGetProperty("Documentos", out var dArr) && dArr.ValueKind == JsonValueKind.Array)
            {
                totalDocs = dArr.GetArrayLength();
            }
        }

        // Consulta de andamentos na SEAD
        var ativUrl = $"{baseUrl}/v1/unidades/{unidade}/procedimentos/andamentos?protocolo_procedimento={queryProtocol}&pagina=1&quantidade=10";

        string? ultData = null;
        string? ultDesc = null;
        using var respAtiv = await SendSeadWithRetryAsync(ativUrl, ct).ConfigureAwait(false);
        if (respAtiv.StatusCode == HttpStatusCode.Unauthorized || respAtiv.StatusCode == HttpStatusCode.Forbidden || respAtiv.StatusCode == (HttpStatusCode)422)
            throw new SeiException(SeiErrorCode.AcessoNegado);
        if (!respAtiv.IsSuccessStatusCode)
            throw new SeiException(SeiErrorCode.Indisponivel);

        var bytesAtiv = await respAtiv.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(bytesAtiv))
        {
            if (doc.RootElement.TryGetProperty("Andamentos", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in aArr.EnumerateArray())
                {
                    ultData = item.TryGetProperty("DataHora", out var dhP) ? dhP.GetString() : null;
                    ultDesc = item.TryGetProperty("Descricao", out var descP) ? descP.GetString() : null;
                    break;
                }
            }
        }

        return new LightProcessSummary(idProcedimento, totalDocs, ultData, ultDesc, null, protocolo);
    }

    public Task<ProcessDetail> GetProcessDetailsAsync(
        string idProcedimento,
        string unidade,
        CancellationToken ct = default) => GetProcessDetailsAsync(idProcedimento, unidade, null, ct);

    public Task<ProcessDetail> GetProcessDetailsAsync(
        string idProcedimento,
        string unidade,
        string? protocolo,
        CancellationToken ct)
    {
        return ExecuteWithFallbackAsync(
            async token => await GetProcessDetailsWsseiAsync(idProcedimento, unidade, token).ConfigureAwait(false),
            async token => await GetProcessDetailsSeadAsync(idProcedimento, unidade, ResolveSeadProtocol(idProcedimento, protocolo), token).ConfigureAwait(false),
            ct
        );
    }

    private async Task<ProcessDetail> GetProcessDetailsWsseiAsync(string idProcedimento, string unidade, CancellationToken ct)
    {
        var baseUrl = _config.SeiWsseiBaseUrl.TrimEnd('/');

        // 1. Metadados do processo
        var procUrl = $"{baseUrl}/processo/{idProcedimento}";
        string protocolo = idProcedimento;
        string? desc = null;
        string? tipo = null;
        string? sigla = null;
        int nivelAcesso = 0;
        var unidadesAbertas = new List<string>();

        using (var respProc = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, procUrl), unidade, ct).ConfigureAwait(false))
        {
            if (respProc.StatusCode == HttpStatusCode.Unauthorized || respProc.StatusCode == HttpStatusCode.Forbidden || respProc.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if ((int)respProc.StatusCode >= 500 || !respProc.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await respProc.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                ValidateWsseiResponse(doc.RootElement, isSearch: false);
                var root = doc.RootElement;
                var target = root.TryGetProperty("data", out var dProp) && dProp.ValueKind == JsonValueKind.Object ? dProp : root;

                if (target.TryGetProperty("protocoloFormatado", out var pP)) protocolo = pP.GetString() ?? protocolo;
                else if (target.TryGetProperty("protocoloFormatadoProcedimento", out var pP2)) protocolo = pP2.GetString() ?? protocolo;

                if (target.TryGetProperty("descricao", out var dP)) desc = dP.GetString();
                if (target.TryGetProperty("tipoProcesso", out var tP)) tipo = tP.GetString();

                if (target.TryGetProperty("unidade", out var uP) && uP.TryGetProperty("sigla", out var sP)) sigla = sP.GetString();
                if (target.TryGetProperty("status", out var stP) && stP.TryGetProperty("nivelAcessoGlobal", out var nP) && nP.TryGetInt32(out var nVal))
                    nivelAcesso = nVal;

                if (target.TryGetProperty("dadosAbertura", out var daP) && daP.TryGetProperty("unidades", out var uArr) && uArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in uArr.EnumerateArray())
                    {
                        if (u.ValueKind == JsonValueKind.String) unidadesAbertas.Add(u.GetString() ?? "");
                        else if (u.ValueKind == JsonValueKind.Object && u.TryGetProperty("sigla", out var usP)) unidadesAbertas.Add(usP.GetString() ?? "");
                    }
                }
        }

        // 2. Listar documentos
        var docsUrl = $"{baseUrl}/documento/listar/{idProcedimento}?start=0&limit=100";
        var documentos = new List<SeiDocumentItem>();
        using (var respDocs = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, docsUrl), unidade, ct).ConfigureAwait(false))
        {
            if (respDocs.StatusCode == HttpStatusCode.Unauthorized || respDocs.StatusCode == HttpStatusCode.Forbidden || respDocs.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if ((int)respDocs.StatusCode >= 500 || !respDocs.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await respDocs.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                ValidateWsseiResponse(doc.RootElement, isSearch: false);
                if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataProp.EnumerateArray())
                    {
                        string docId = item.TryGetProperty("id", out var dId) ? dId.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(docId) && item.TryGetProperty("idProtocolo", out var dId2)) docId = dId2.GetString() ?? "";

                        string docProt = item.TryGetProperty("protocoloFormatado", out var dPr) ? dPr.GetString() ?? "" : "";
                        string nomeComp = item.TryGetProperty("nomeComposto", out var ncP) ? ncP.GetString() ?? "" : "";
                        string docTipo = item.TryGetProperty("tipo", out var dtP) ? dtP.GetString() ?? "" : "";
                        string mime = item.TryGetProperty("mimeType", out var mP) ? mP.GetString() ?? "" : "";
                        string tipoDoc = item.TryGetProperty("tipoDocumento", out var tdP) ? tdP.GetString() ?? "" : "";
                        string? idUnidade = item.TryGetProperty("idUnidade", out var iuP) ? iuP.GetString() : null;

                        bool podeVisualizar = true;
                        bool cancelado = false;
                        bool sigiloso = false;

                        if (item.TryGetProperty("status", out var sProp))
                        {
                            if (sProp.TryGetProperty("podeVisualizarDocumento", out var pvP))
                                podeVisualizar = pvP.GetString()?.Equals("S", StringComparison.OrdinalIgnoreCase) == true;
                            if (sProp.TryGetProperty("documentoCancelado", out var dcP))
                                cancelado = dcP.GetString()?.Equals("S", StringComparison.OrdinalIgnoreCase) == true;
                            if (sProp.TryGetProperty("documentoSigiloso", out var dsP))
                                sigiloso = dsP.GetString()?.Equals("S", StringComparison.OrdinalIgnoreCase) == true;
                        }

                        string? dataInc = null;
                        if (item.TryGetProperty("anexo", out var anProp) && anProp.TryGetProperty("dataInclusao", out var diP))
                            dataInc = diP.GetString();

                        documentos.Add(new SeiDocumentItem(
                            docId, docProt, nomeComp, docTipo, mime, tipoDoc, idUnidade,
                            podeVisualizar, cancelado, sigiloso, dataInc
                        ));
                    }
                }
        }

        // 3. Listar andamentos
        var ativUrl = $"{baseUrl}/atividade/listar?procedimento={idProcedimento}&start=0&limit=100";
        var atividades = new List<SeiActivityItem>();
        using (var respAtiv = await SendWsseiWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, ativUrl), unidade, ct).ConfigureAwait(false))
        {
            if (respAtiv.StatusCode == HttpStatusCode.Unauthorized || respAtiv.StatusCode == HttpStatusCode.Forbidden || respAtiv.StatusCode == (HttpStatusCode)422)
                throw new SeiException(SeiErrorCode.AcessoNegado);

            if ((int)respAtiv.StatusCode >= 500 || !respAtiv.IsSuccessStatusCode)
                throw new SeiException(SeiErrorCode.Indisponivel);

            var bytes = await respAtiv.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                ValidateWsseiResponse(doc.RootElement, isSearch: false);
                if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataProp.EnumerateArray())
                    {
                        string ativId = item.TryGetProperty("id", out var aId) ? aId.GetString() ?? "" : "";
                        string data = item.TryGetProperty("data", out var dP) ? dP.GetString() ?? "" : "";
                        string hora = item.TryGetProperty("hora", out var hP) ? hP.GetString() ?? "" : "";
                        string ativDesc = item.TryGetProperty("descricao", out var adP) ? adP.GetString() ?? "" : "";
                        string? ativUnidade = item.TryGetProperty("unidade", out var auP) ? (auP.ValueKind == JsonValueKind.Object && auP.TryGetProperty("sigla", out var ausP) ? ausP.GetString() : auP.GetString()) : null;
                        string? ativUser = item.TryGetProperty("usuario", out var auserP) ? auserP.GetString() : null;

                        atividades.Add(new SeiActivityItem(ativId, data, hora, ativDesc, ativUnidade, ativUser));
                    }
                }
        }

        return new ProcessDetail(
            idProcedimento,
            protocolo,
            desc,
            tipo,
            sigla,
            nivelAcesso,
            unidadesAbertas,
            documentos,
            atividades
        );
    }

    private async Task<ProcessDetail> GetProcessDetailsSeadAsync(string idProcedimento, string unidade, string protocolo, CancellationToken ct)
    {
        var baseUrl = _config.SeiSeadBaseUrl.TrimEnd('/');
        var queryProtocol = Uri.EscapeDataString(protocolo);

        // Consulta de documentos
        var docsUrl = $"{baseUrl}/v1/unidades/{unidade}/procedimentos/documentos?protocolo_procedimento={queryProtocol}&pagina=1&quantidade=10";
        var documentos = new List<SeiDocumentItem>();
        using var respDocs = await SendSeadWithRetryAsync(docsUrl, ct).ConfigureAwait(false);
        if (respDocs.StatusCode == HttpStatusCode.Unauthorized || respDocs.StatusCode == HttpStatusCode.Forbidden || respDocs.StatusCode == (HttpStatusCode)422)
            throw new SeiException(SeiErrorCode.AcessoNegado);
        if (!respDocs.IsSuccessStatusCode)
            throw new SeiException(SeiErrorCode.Indisponivel);

        var bytesDocs = await respDocs.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(bytesDocs))
        {
            if (doc.RootElement.TryGetProperty("Documentos", out var dArr) && dArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dArr.EnumerateArray())
                {
                    string docId = item.TryGetProperty("IdDocumento", out var idProp) ? idProp.GetString() ?? "" : "";
                    string docProt = item.TryGetProperty("DocumentoFormatado", out var protProp) ? protProp.GetString() ?? "" : "";
                    string serieNome = item.TryGetProperty("Serie", out var sProp) && sProp.TryGetProperty("Nome", out var snProp) ? snProp.GetString() ?? "" : "";
                    string numero = item.TryGetProperty("Numero", out var numProp) ? numProp.GetString() ?? "" : "";

                    documentos.Add(new SeiDocumentItem(
                        docId, docProt, $"{serieNome} {numero}".Trim(), serieNome, "application/octet-stream", "SEAD", null,
                        true, false, false, null
                    ));
                }
            }
        }

        // Consulta de andamentos
        var ativUrl = $"{baseUrl}/v1/unidades/{unidade}/procedimentos/andamentos?protocolo_procedimento={queryProtocol}&pagina=1&quantidade=10";

        var atividades = new List<SeiActivityItem>();
        using var respAtiv = await SendSeadWithRetryAsync(ativUrl, ct).ConfigureAwait(false);
        if (respAtiv.StatusCode == HttpStatusCode.Unauthorized || respAtiv.StatusCode == HttpStatusCode.Forbidden || respAtiv.StatusCode == (HttpStatusCode)422)
            throw new SeiException(SeiErrorCode.AcessoNegado);
        if (!respAtiv.IsSuccessStatusCode)
            throw new SeiException(SeiErrorCode.Indisponivel);

        var bytesAtiv = await respAtiv.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(bytesAtiv))
        {
            if (doc.RootElement.TryGetProperty("Andamentos", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var item in aArr.EnumerateArray())
                {
                    idx++;
                    string dh = item.TryGetProperty("DataHora", out var dhProp) ? dhProp.GetString() ?? "" : "";
                    string desc = item.TryGetProperty("Descricao", out var descProp) ? descProp.GetString() ?? "" : "";
                    string? uSigla = item.TryGetProperty("Unidade", out var uProp) && uProp.TryGetProperty("Sigla", out var sProp) ? sProp.GetString() : null;

                    string datePart = dh;
                    string timePart = "";
                    var parts = dh.Split(' ', 2);
                    if (parts.Length == 2)
                    {
                        datePart = parts[0];
                        timePart = parts[1];
                    }

                    atividades.Add(new SeiActivityItem(idx.ToString(), datePart, timePart, desc, uSigla, null));
                }
            }
        }

        return new ProcessDetail(
            idProcedimento,
            protocolo,
            null,
            null,
            null,
            2, // Sigiloso por padrão na SEAD per APIS-SEI.md
            new List<string>(),
            documentos,
            atividades
        );
    }

    private string ResolveSeadProtocol(string idProcedimento, string? protocolo)
    {
        if (string.IsNullOrWhiteSpace(protocolo))
            protocolo = _db.GetMonitoredProcess(idProcedimento)?.ProtocoloFormatado;

        return string.IsNullOrWhiteSpace(protocolo) ? idProcedimento : protocolo;
    }

    private async Task<HttpResponseMessage> SendSeadWithRetryAsync(string url, CancellationToken ct)
    {
        var session = await GetSeadSessionAsync(rejectedSession: null, ct).ConfigureAwait(false);
        var response = await SendAsync(session).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return response;

        response.Dispose();
        var renewed = await GetSeadSessionAsync(session, ct).ConfigureAwait(false);
        return await SendAsync(renewed).ConfigureAwait(false);

        async Task<HttpResponseMessage> SendAsync(SeiAuthResult auth)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("token", auth.Token);
            return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    private async Task<SeiAuthResult> GetSeadSessionAsync(SeiAuthResult? rejectedSession, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_seadSession is { } session && !ReferenceEquals(session, rejectedSession))
                return session;

            var activeSead = _session?.SourceApi == "SEAD";
            if (activeSead)
                _session = null;
            _seadSession = null;
            var token = await AuthenticateSeadAsync(ct).ConfigureAwait(false);
            var renewed = _seadSession = new SeiAuthResult(token, "SEAD", DateTime.UtcNow);
            if (activeSead)
                _session = renewed;
            return renewed;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(
        Func<CancellationToken, Task<T>> wsseiAction,
        Func<CancellationToken, Task<T>> seadAction,
        CancellationToken ct)
    {
        var auth = await AuthenticateAsync(forceRenew: false, ct).ConfigureAwait(false);
        if (auth.SourceApi == "SEAD")
        {
            return await seadAction(ct).ConfigureAwait(false);
        }

        Exception? wsseiError = null;
        try
        {
            return await wsseiAction(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientOrDown(ex))
        {
            wsseiError = ex;
            _db.Log("WARN", "Operação no WSSEI v2 falhou temporariamente. Acionando fallback SEAD...");
        }

        if (wsseiError != null)
        {
            try
            {
                return await seadAction(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _db.Log("ERROR", "Falha também no fallback da SEAD.");
                throw wsseiError;
            }
        }

        throw new SeiException(SeiErrorCode.Indisponivel);
    }

    private static bool IsTransientOrDown(Exception ex) =>
        ex is HttpRequestException ||
        ex is TaskCanceledException ||
        ex is TimeoutException ||
        (ex is SeiException se && se.ErrorCode == SeiErrorCode.Indisponivel);

    private (string user, string pass) GetCredentials()
    {
        if (_explicitCredentials.HasValue && !string.IsNullOrWhiteSpace(_explicitCredentials.Value.user))
        {
            return _explicitCredentials.Value;
        }

        string user = _config.SeiWsseiUsuario;
        var (defaultUser, defaultPassword) = CredentialStore.LoadCredential();
        if (string.IsNullOrWhiteSpace(user))
            return (defaultUser ?? "", defaultPassword ?? "");

        // A tela de configurações salva a senha atual na entrada padrão.
        if (string.Equals(user, defaultUser, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(defaultPassword))
            return (user, defaultPassword);

        var (scopedUser, scopedPassword) = CredentialStore.LoadCredential(CredentialStore.BuildTarget(user));
        return (user, string.Equals(user, scopedUser, StringComparison.OrdinalIgnoreCase) ? scopedPassword ?? "" : "");
    }

    public void Dispose()
    {
        _authLock.Dispose();
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
