using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

public class SeiClientSanitizationTests
{
    private static readonly string[] SolrCredentialKeywords = new[]
    {
        "8983",
        "usuario_solr",
        "senha_secreta_solr",
        "10.0.0.1",
        "10.0.0.15",
        "org.apache.solr",
        "SolrServerException",
        "SolrException"
    };

    private static void AssertNoSolrLeak(string text, bool checkSolrLiteral = true)
    {
        Assert.NotNull(text);
        if (checkSolrLiteral)
        {
            Assert.DoesNotContain("solr", text, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var kw in SolrCredentialKeywords)
        {
            Assert.DoesNotContain(kw, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Wssei_SolrLeakInMensagem_IsCompletelyStripped()
    {
        string rawJson = """
        {
            "sucesso": false,
            "mensagem": "Falha no Solr: SolrServerException: Server at http://usuario_solr:senha_secreta_solr@10.0.0.1:8983/solr/processos returned status 500",
            "exception": "org.apache.solr.common.SolrException: Cannot connect to http://usuario_solr:senha_secreta_solr@10.0.0.1:8983"
        }
        """;

        using var doc = JsonDocument.Parse(rawJson);
        var ex = Assert.Throws<SeiException>(() => SeiClient.ValidateWsseiResponse(doc.RootElement, isSearch: true));

        Assert.Equal("SEI_FALHA_PESQUISA", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.FalhaPesquisa, ex.ErrorCode);
        AssertNoSolrLeak(ex.Message);
        AssertNoSolrLeak(ex.ToString(), checkSolrLiteral: false);
    }

    [Fact]
    public void Wssei_SolrLeakInException_StackTraceAndParamsStripped()
    {
        string rawJson = """
        {
            "sucesso": false,
            "mensagem": "Erro interno de busca",
            "exception": "org.apache.solr.common.SolrException: Connection refused to http://10.0.0.15:8983/solr/processos\n  at org.apache.solr.client.solrj.impl.HttpSolrClient.executeMethod(HttpSolrClient.java:585)"
        }
        """;

        using var doc = JsonDocument.Parse(rawJson);
        var ex = Assert.Throws<SeiException>(() => SeiClient.ValidateWsseiResponse(doc.RootElement, isSearch: false));

        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.AcessoNegado, ex.ErrorCode);
        AssertNoSolrLeak(ex.Message);
        AssertNoSolrLeak(ex.ToString(), checkSolrLiteral: false);
    }

    [Fact]
    public void Wssei_DeeplyNestedLeak_DiscardedBySanitizer()
    {
        string rawJson = """
        {
            "sucesso": false,
            "error": {
                "code": 500,
                "details": {
                    "solrUrl": "http://usuario_solr:senha_secreta_solr@10.0.0.1:8983/solr/processos",
                    "stack": "org.apache.solr.SolrException"
                }
            }
        }
        """;

        using var doc = JsonDocument.Parse(rawJson);
        var ex = Assert.Throws<SeiException>(() => SeiClient.ValidateWsseiResponse(doc.RootElement, isSearch: false));

        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        AssertNoSolrLeak(ex.Message);
        AssertNoSolrLeak(ex.ToString(), checkSolrLiteral: false);
    }

    [Fact]
    public void SeiException_ToString_NeverContainsLeak()
    {
        var ex = new SeiException(SeiErrorCode.FalhaPesquisa);

        string stringRep = ex.ToString();
        Assert.Contains("SEI_FALHA_PESQUISA", stringRep);
        AssertNoSolrLeak(stringRep, checkSolrLiteral: false);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    public async Task HttpStatus401_403_422_MapsToAcessoNegado(int statusCode)
    {
        using var db = new Database(":memory:");
        var config = new Config(db);

        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", (HttpStatusCode)statusCode, "{\"sucesso\": false}");

        using var client = new SeiClient(config, db, handler, ("user", "pass"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync(forceRenew: true));
        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.AcessoNegado, ex.ErrorCode);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task HttpStatus500_503_504_MapsToServicoIndisponivel(int statusCode)
    {
        using var db = new Database(":memory:");
        var config = new Config(db);

        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", (HttpStatusCode)statusCode, "Internal Error");
        handler.SetupResponse("/v1/orgaos/usuarios/login", (HttpStatusCode)statusCode, "Internal Error");

        using var client = new SeiClient(config, db, handler, ("user", "pass"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync(forceRenew: true));
        Assert.Equal("SEI_INDISPONIVEL", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.Indisponivel, ex.ErrorCode);
    }

    [Fact]
    public async Task HttpStatus404_MapsToProcessoNaoEncontrado_OrEmptyList()
    {
        using var db = new Database(":memory:");
        var config = new Config(db);

        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok123\"}}");
        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.NotFound, "{\"sucesso\": false}");

        using var client = new SeiClient(config, db, handler, ("user", "pass"));

        var result = await client.SearchProcessesAsync("110000001");
        Assert.NotNull(result);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task MalformedJson_ThrowsRespostaInvalida_WithoutLeakingRawBuffer()
    {
        using var db = new Database(":memory:");
        var config = new Config(db);

        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": false, \"mensagem\": \"http://usuario_solr:senha_secreta_solr@10.0.0.1:8983");

        using var client = new SeiClient(config, db, handler, ("user", "pass"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync(forceRenew: true));
        Assert.Equal("SEI_RESPOSTA_INVALIDA", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.RespostaInvalida, ex.ErrorCode);
        AssertNoSolrLeak(ex.Message);
        AssertNoSolrLeak(ex.ToString(), checkSolrLiteral: false);
    }

    [Fact]
    public void DatabaseLog_NeverStoresSolrCredentialsFromClient()
    {
        using var db = new Database(":memory:");

        string maliciousLeak = "Falha no Solr: http://usuario_solr:senha_secreta_solr@10.0.0.1:8983/solr/processos returned status 500";
        db.Log("ERROR", maliciousLeak);

        var logs = db.GetLogs(10);
        Assert.NotEmpty(logs);
        foreach (var log in logs)
        {
            AssertNoSolrLeak(log.Message, checkSolrLiteral: false);
            Assert.DoesNotContain("usuario_solr", log.Message);
            Assert.DoesNotContain("senha_secreta_solr", log.Message);
            Assert.DoesNotContain("http://", log.Message);
        }
    }

    [Fact]
    public void Concurrent_Sanitization_IsThreadSafe()
    {
        Parallel.For(0, 100, i =>
        {
            string leak = $$"""
            {
                "sucesso": false,
                "mensagem": "Falha no Solr: http://usuario_solr_{{i}}:senha_secreta_solr_{{i}}@10.0.0.1:8983/solr",
                "exception": "org.apache.solr.common.SolrException: Thread {{i}} refused"
            }
            """;

            using var doc = JsonDocument.Parse(leak);
            var ex = Assert.Throws<SeiException>(() => SeiClient.ValidateWsseiResponse(doc.RootElement, isSearch: true));

            Assert.Equal("SEI_FALHA_PESQUISA", ex.SafeErrorCode);
            AssertNoSolrLeak(ex.Message);
            AssertNoSolrLeak(ex.ToString(), checkSolrLiteral: false);
        });
    }
}
