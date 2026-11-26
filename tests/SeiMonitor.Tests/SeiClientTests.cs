using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Predicate, Func<HttpRequestMessage, HttpResponseMessage> Responder)> _routes = new();
    private readonly object _lock = new();

    public List<HttpRequestMessage> RequestsReceived { get; } = new();
    public int TotalRequests
    {
        get { lock (_lock) { return RequestsReceived.Count; } }
    }

    public void Setup(Func<HttpRequestMessage, bool> predicate, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        lock (_lock)
        {
            _routes.Add((predicate, responder));
        }
    }

    public void SetupResponse(string urlSubstring, HttpStatusCode status, string jsonBody)
    {
        Setup(
            req => req.RequestUri?.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase) == true,
            req => new HttpResponseMessage(status)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            }
        );
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            RequestsReceived.Add(request);

            // Match in reverse order (most recent rule wins)
            for (int i = _routes.Count - 1; i >= 0; i--)
            {
                if (_routes[i].Predicate(request))
                {
                    return Task.FromResult(_routes[i].Responder(request));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"sucesso\": false, \"mensagem\": \"Mock route not configured\"}")
            });
        }
    }
}

public class SeiClientTests
{
    private readonly Database _db;
    private readonly Config _config;

    public SeiClientTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db);
        _config.SeiWsseiBaseUrl = "https://sei.pi.gov.br/api/v2";
        _config.SeiSeadBaseUrl = "https://api.sei.pi.gov.br";
        _config.SeiWsseiUsuario = "usuario.servico";
        _config.SeiWsseiOrgao = "0";
    }

    [Fact]
    public void T01_LazyAuth_NoHttpCallUntilFirstAction()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        Assert.Equal(0, handler.TotalRequests);
        Assert.Null(client.CachedToken);
    }

    [Fact]
    public async Task Authentication_DoesNotUsePasswordFromDifferentAccount()
    {
        _config.SeiWsseiUsuario = $"sei-test-{Guid.NewGuid():N}";
        var target = CredentialStore.BuildTarget(_config.SeiWsseiUsuario);
        Assert.True(CredentialStore.SaveCredential(target, "outra-conta", "senha-teste"));
        try
        {
            var handler = new MockHttpMessageHandler();
            using var client = new SeiClient(_config, _db, handler);

            var error = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync());

            Assert.Equal(SeiErrorCode.NaoConfigurado, error.ErrorCode);
            Assert.Equal(0, handler.TotalRequests);
        }
        finally
        {
            CredentialStore.DeleteCredential(target);
        }
    }

    [Fact]
    public async Task T02_LazyAuth_FirstCallPerformsLogin()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"jwt_token_123\"}}");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var auth = await client.AuthenticateAsync();

        Assert.NotNull(auth);
        Assert.Equal("jwt_token_123", auth.Token);
        Assert.Equal("WSSEI", auth.SourceApi);
        Assert.Equal("jwt_token_123", client.CachedToken);
        Assert.Equal(1, handler.TotalRequests);

        // Chamada subsequente sem forceRenew não gera nova requisição HTTP
        var auth2 = await client.AuthenticateAsync();
        Assert.Equal("jwt_token_123", auth2.Token);
        Assert.Equal(1, handler.TotalRequests);
    }

    [Fact]
    public async Task T03_ConcurrentAuth_SemaphoreGuardsSingleLogin()
    {
        var handler = new MockHttpMessageHandler();
        int loginCount = 0;

        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/autenticar") == true,
            req =>
            {
                Interlocked.Increment(ref loginCount);
                Thread.Sleep(50); // Simula latência de rede
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\": true, \"data\": {\"token\": \"jwt_shared_token\"}}", Encoding.UTF8, "application/json")
                };
            }
        );

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => client.AuthenticateAsync())).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("jwt_shared_token", r.Token));
        Assert.Equal(1, loginCount);
    }

    [Fact]
    public async Task T04_MandatoryHeaders_InjectedOnEveryRequest()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"token_alpha\"}}");
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.OK, "{\"sucesso\": true, \"total\": 0, \"data\": []}");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        await client.SearchProcessesAsync("110000001");

        var searchReq = handler.RequestsReceived.FirstOrDefault(r => r.RequestUri?.ToString().Contains("/processo/pesquisar") == true);
        Assert.NotNull(searchReq);

        Assert.True(searchReq.Headers.Contains("token"));
        Assert.Equal("token_alpha", searchReq.Headers.GetValues("token").First());

        Assert.True(searchReq.Headers.Contains("unidade"));
        Assert.Equal("110000001", searchReq.Headers.GetValues("unidade").First());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task T05_RejectedToken_AutomaticRenewalAndRetrySucceeds(HttpStatusCode rejectedStatus)
    {
        var handler = new MockHttpMessageHandler();
        int authCalls = 0;
        int searchCalls = 0;

        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/autenticar") == true,
            req =>
            {
                int count = Interlocked.Increment(ref authCalls);
                string token = count == 1 ? "expired_token" : "fresh_token";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sucesso\": true, \"data\": {{\"token\": \"{token}\"}}}}", Encoding.UTF8, "application/json")
                };
            }
        );

        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/processo/pesquisar") == true,
            req =>
            {
                int count = Interlocked.Increment(ref searchCalls);
                if (count == 1)
                {
                    return new HttpResponseMessage(rejectedStatus);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\": true, \"total\": 1, \"data\": [{\"idProcedimento\": \"10\", \"protocoloFormatado\": \"0001/2026\"}]}", Encoding.UTF8, "application/json")
                };
            }
        );

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var result = await client.SearchProcessesAsync("110000001");

        Assert.NotNull(result);
        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
        Assert.Equal("fresh_token", client.CachedToken);

        // 1 login inicial + 1 busca rejeitada + 1 renovação + 1 nova busca.
        Assert.Equal(2, authCalls);
        Assert.Equal(2, searchCalls);
    }

    [Fact]
    public async Task T06_ConsecutiveHttp401_ThrowsSeiAcessoNegado()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");
        // Busca sempre responde 401
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.Unauthorized, "{\"sucesso\": false}");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.SearchProcessesAsync("110000001"));

        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.AcessoNegado, ex.ErrorCode);
    }

    [Fact]
    public async Task T07_SolrLeakInPayload_SanitizesAndNeverLeaksCredentials()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");

        string solrLeakPayload = """
        {
            "sucesso": false,
            "mensagem": "Falha no Solr: SolrServerException: Server at http://usuario_solr:senha_secreta_solr@10.0.0.1:8983/solr/processos returned status 500",
            "exception": "org.apache.solr.common.SolrException: Cannot connect to http://usuario_solr:senha_secreta_solr@10.0.0.1:8983"
        }
        """;
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.OK, solrLeakPayload);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.SearchProcessesAsync("110000001"));

        Assert.Equal("SEI_FALHA_PESQUISA", ex.SafeErrorCode);
        Assert.DoesNotContain("usuario_solr", ex.Message);
        Assert.DoesNotContain("senha_secreta_solr", ex.Message);
        Assert.DoesNotContain("8983", ex.Message);
        Assert.DoesNotContain("10.0.0.1", ex.Message);
    }

    [Fact]
    public async Task T08_WsseiTimeout_TriggersFallbackToSead()
    {
        var handler = new MockHttpMessageHandler();

        // WSSEI /processo/pesquisar falha por 500
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"wssei_tok\"}}");
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.InternalServerError, "Server Error");

        // SEAD autenticação e consulta têm sucesso
        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.OK, "{\"Token\": \"sead_tok_456\"}");
        string seadConsultaResponse = """
        [
            {
                "IdProcedimento": "555",
                "ProcedimentoFormatado": "00055.000555/2026-99",
                "TipoProcedimento": { "Nome": "Processo Administrativo" },
                "Especificacao": "Processo recuperado via SEAD"
            }
        ]
        """;
        handler.Setup(
            req => req.RequestUri?.AbsolutePath.EndsWith("/procedimentos/consulta") == true,
            req =>
            {
                Assert.Equal("sead_tok_456", Assert.Single(req.Headers.GetValues("token")));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(seadConsultaResponse, Encoding.UTF8, "application/json")
                };
            });

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var result = await client.SearchProcessesAsync("110000001");

        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.Equal("555", result.Items[0].IdProcedimento);
        Assert.Equal("00055.000555/2026-99", result.Items[0].ProtocoloFormatado);
    }

    [Fact]
    public async Task WsseiRenewalFailure_ContinuesOnSeadAndRenewsItsToken()
    {
        var handler = new MockHttpMessageHandler();
        int wsseiLogins = 0;
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/autenticar") == true, _ =>
            ++wsseiLogins == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"token":"wssei_token"}}""")
                }
                : new HttpResponseMessage(HttpStatusCode.Forbidden));
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/processo/pesquisar") == true, req =>
        {
            Assert.Equal("wssei_token", Assert.Single(req.Headers.GetValues("token")));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        int seadLogins = 0;
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/usuarios/login") == true, _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"Token":"sead_token_{{++seadLogins}}"}""")
            });
        var seadTokens = new List<string>();
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/procedimentos/consulta") == true, req =>
        {
            seadTokens.Add(Assert.Single(req.Headers.GetValues("token")));
            return seadTokens.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        var result = await client.SearchProcessesAsync("110000001");

        Assert.Empty(result.Items);
        Assert.Equal(2, wsseiLogins);
        Assert.Equal(new[] { "sead_token_1", "sead_token_2" }, seadTokens);
        var auth = await client.AuthenticateAsync();
        Assert.Equal("SEAD", auth.SourceApi);
        Assert.Equal("sead_token_2", auth.Token);
    }

    [Fact]
    public async Task ChangingPrimaryApi_ReplacesCachedSessionAndUsesSeadFirst()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, """{"data":{"token":"wssei_token"}}""");
        handler.SetupResponse("/usuarios/login", HttpStatusCode.OK, """{"Token":"sead_token"}""");
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/procedimentos") == true, req =>
        {
            Assert.Equal("sead_token", Assert.Single(req.Headers.GetValues("token")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        Assert.Equal("WSSEI", (await client.AuthenticateAsync()).SourceApi);

        _config.PrimaryApi = "SEAD";
        await client.SearchProcessesAsync("110000001");

        Assert.Equal("SEAD", client.ActiveApiSource);
        Assert.Equal(3, handler.TotalRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeadAsPrimary_UsesWsseiFallbackOnLoginOrOperationFailure(bool failLogin)
    {
        _config.PrimaryApi = "SEAD";
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/usuarios/login", failLogin ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
            """{"Token":"sead_token"}""");
        handler.SetupResponse("/procedimentos", HttpStatusCode.ServiceUnavailable, "");
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, """{"data":{"token":"wssei_token"}}""");
        handler.Setup(req => req.RequestUri?.AbsolutePath.EndsWith("/processo/pesquisar") == true, req =>
        {
            Assert.Equal("wssei_token", Assert.Single(req.Headers.GetValues("token")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"sucesso":true,"total":0,"data":[]}""")
            };
        });

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        await client.SearchProcessesAsync("110000001");

        Assert.EndsWith("/usuarios/login", handler.RequestsReceived[0].RequestUri!.AbsolutePath);
        Assert.Equal(failLogin ? 3 : 4, handler.TotalRequests);
    }

    [Fact]
    public async Task WsseiLogin_LoadsUnitsInConfiguredOrder()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK,
            """{"data":{"token":"wssei_token","unidades":[{"id":"110000001","sigla":"GINFO"}]}}""");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        var unit = Assert.Single(await client.GetUserUnitsAsync());
        Assert.Equal("110000001", unit.Id);
        Assert.Equal(1, handler.TotalRequests);
    }

    [Fact]
    public async Task SeadLogin_LoadsUnitsFromIdField()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/usuarios/login", HttpStatusCode.OK,
            """{"Token":"sead_token","Unidades":[{"Id":"110000001","Sigla":"GINFO","Descricao":"Gerência"}]}""");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        var unit = Assert.Single(await client.GetUserUnitsAsync());

        Assert.Equal("110000001", unit.Id);
        Assert.Equal("GINFO", unit.Sigla);
        Assert.Equal("GINFO (110000001) — Gerência", unit.Display);
    }

    [Fact]
    public async Task SeadReadsUseFormattedProtocolAndPreserveInternalId()
    {
        const string id = "123";
        const string protocolo = "00000.000123/2026-45";
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.Forbidden, "");
        handler.SetupResponse("/usuarios/login", HttpStatusCode.OK, """{"Token":"sead_token"}""");
        handler.SetupResponse("documentos?protocolo_procedimento=00000.000123%2F2026-45", HttpStatusCode.OK,
            """{"Documentos":[]}""");
        handler.SetupResponse("andamentos?protocolo_procedimento=00000.000123%2F2026-45", HttpStatusCode.OK,
            """{"Andamentos":[]}""");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));
        var summary = await client.GetLightSummaryAsync(id, "110000001", protocolo, CancellationToken.None);

        _db.UpsertMonitoredProcess(new MonitoredProcess { Id = id, ProtocoloFormatado = protocolo });
        var detail = await client.GetProcessDetailsAsync(id, "110000001");

        Assert.Equal(id, summary.IdProcedimento);
        Assert.Equal(protocolo, summary.ProtocoloFormatado);
        Assert.Equal(id, detail.IdProcedimento);
        Assert.Equal(protocolo, detail.ProtocoloFormatado);
    }

    [Fact]
    public async Task T09_BothWsseiAndSeadFail_BubblesWsseiPrimaryError()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.InternalServerError, "WSSEI down");
        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.InternalServerError, "SEAD down");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync(forceRenew: true));
        Assert.Equal("SEI_INDISPONIVEL", ex.SafeErrorCode);
    }

    [Fact]
    public async Task T10_GetLightSummaryAsync_UsesLimit1()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");

        handler.SetupResponse("/documento/listar/123", HttpStatusCode.OK, "{\"sucesso\": true, \"total\": 15, \"data\": []}");
        handler.SetupResponse("/atividade/listar", HttpStatusCode.OK, """
        {
            "sucesso": true,
            "total": 5,
            "data": [
                {
                    "id": "1",
                    "data": "29/10/2026",
                    "hora": "14:30:00",
                    "descricao": "Processo recebido na unidade",
                    "usuario": "servidor.colega"
                }
            ]
        }
        """);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var summary = await client.GetLightSummaryAsync("123", "110000001");

        Assert.NotNull(summary);
        Assert.Equal("123", summary.IdProcedimento);
        Assert.Equal(15, summary.TotalDocumentos);
        Assert.Equal("29/10/2026 14:30:00", summary.UltimaAtividadeData);
        Assert.Equal("Processo recebido na unidade", summary.UltimaAtividadeDesc);
        Assert.Equal("servidor.colega", summary.UltimoUsuarioNome);

        // Verifica que as requisições contêm limit=1
        var docReq = handler.RequestsReceived.First(r => r.RequestUri?.ToString().Contains("/documento/listar/123") == true);
        var ativReq = handler.RequestsReceived.First(r => r.RequestUri?.ToString().Contains("/atividade/listar") == true);

        Assert.Contains("limit=1", docReq.RequestUri?.Query);
        Assert.Contains("limit=1", ativReq.RequestUri?.Query);
    }

    [Fact]
    public async Task T11_GetProcessDetailsAsync_ReturnsCompleteDetail()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");

        handler.SetupResponse("/processo/999", HttpStatusCode.OK, """
        {
            "sucesso": true,
            "data": {
                "protocoloFormatado": "00099.000999/2026-00",
                "descricao": "Processo Detalhado",
                "tipoProcesso": "Aquisição",
                "unidade": { "sigla": "GINFO" },
                "status": { "nivelAcessoGlobal": 0 },
                "dadosAbertura": { "unidades": ["GINFO", "DTI"] }
            }
        }
        """);

        handler.SetupResponse("/documento/listar/999", HttpStatusCode.OK, """
        {
            "sucesso": true,
            "total": 1,
            "data": [
                {
                    "id": "doc1",
                    "protocoloFormatado": "Doc 101",
                    "nomeComposto": "Despacho 101",
                    "tipo": "Despacho",
                    "mimeType": "text/html",
                    "tipoDocumento": "G",
                    "idUnidade": "110000001",
                    "status": { "podeVisualizarDocumento": "S", "documentoCancelado": "N", "documentoSigiloso": "N" }
                }
            ]
        }
        """);

        handler.SetupResponse("/atividade/listar", HttpStatusCode.OK, """
        {
            "sucesso": true,
            "total": 1,
            "data": [
                {
                    "id": "at1",
                    "data": "29/10/2026",
                    "hora": "10:00:00",
                    "descricao": "Despacho emitido",
                    "unidade": { "sigla": "GINFO" },
                    "usuario": "victor.lima"
                }
            ]
        }
        """);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var detail = await client.GetProcessDetailsAsync("999", "110000001");

        Assert.NotNull(detail);
        Assert.Equal("999", detail.IdProcedimento);
        Assert.Equal("00099.000999/2026-00", detail.ProtocoloFormatado);
        Assert.Equal("Processo Detalhado", detail.Descricao);
        Assert.Equal("Aquisição", detail.TipoProcesso);
        Assert.Equal("GINFO", detail.UnidadeSigla);
        Assert.Equal(0, detail.NivelAcessoGlobal);
        Assert.Equal(2, detail.UnidadesAbertas.Count);
        Assert.Single(detail.Documentos);
        Assert.Single(detail.Atividades);
    }

    [Fact]
    public async Task T12_SearchProcesses_OmittedData_ReturnsEmptyList()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");
        // WSSEI sem resultados responde sucesso: true e total: 0, omitindo 'data'
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.OK, "{\"sucesso\": true, \"total\": 0}");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var result = await client.SearchProcessesAsync("110000001");

        Assert.NotNull(result);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}
