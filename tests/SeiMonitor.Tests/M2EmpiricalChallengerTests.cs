using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suite de testes empíricos adversariais do agente m2_challenger_1.
/// Estressa concorrência de token (SemaphoreSlim), 401 consecutivo, fallback SEAD (500/502/503/timeout)
/// e sanitização incondicional de vazamento Solr.
/// </summary>
public class M2EmpiricalChallengerTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;

    public M2EmpiricalChallengerTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db)
        {
            SeiWsseiBaseUrl = "https://sei.pi.gov.br/api/v2",
            SeiSeadBaseUrl = "https://api.sei.pi.gov.br",
            SeiWsseiUsuario = "usuario.servico",
            SeiWsseiOrgao = "0",
            SeiSeadOrgao = "PGE-PI"
        };
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // =========================================================================
    // 1. ESTRESSE DE CONCORRÊNCIA NA EXPIRAÇÃO DE TOKEN (SEMAPHORESLIM)
    // =========================================================================

    [Fact]
    public async Task Challenger_ConcurrentCalls_DuringTokenExpiration_AllSucceed()
    {
        var handler = new MockHttpMessageHandler();
        int loginCallCount = 0;
        int searchCallCount = 0;
        var tokensObserved = new ConcurrentBag<string>();

        // Autenticação inicial e renovações
        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/autenticar") == true,
            req =>
            {
                int count = Interlocked.Increment(ref loginCallCount);
                string token = $"tok_v{count}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sucesso\": true, \"data\": {{\"token\": \"{token}\"}}}}", Encoding.UTF8, "application/json")
                };
            }
        );

        // Busca de processos: rejeita token inicial "tok_v1" com 401; aceita "tok_v2" em diante
        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/processo/pesquisar") == true,
            req =>
            {
                Interlocked.Increment(ref searchCallCount);
                string? tokenHeader = req.Headers.TryGetValues("token", out var vals) ? vals.FirstOrDefault() : null;
                if (tokenHeader != null) tokensObserved.Add(tokenHeader);

                if (tokenHeader == "tok_v1")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"sucesso\": false, \"mensagem\": \"Token expirado\"}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\": true, \"total\": 1, \"data\": [{\"idProcedimento\": \"100\", \"protocoloFormatado\": \"000100/2026\"}]}", Encoding.UTF8, "application/json")
                };
            }
        );

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        // Faz primeiro login manual para popular cache com token que expirará
        var initialAuth = await client.AuthenticateAsync();
        Assert.Equal("tok_v1", initialAuth.Token);
        Assert.Equal(1, loginCallCount);

        // Dispara 20 requisições simultâneas concorrentes enquanto tok_v1 expira
        const int concurrentTasks = 20;
        var tasks = Enumerable.Range(0, concurrentTasks)
            .Select(_ => Task.Run(async () => await client.SearchProcessesAsync("110000001")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Todas as 20 chamadas devem ter concluído com sucesso e recuperado o resultado
        Assert.Equal(concurrentTasks, results.Length);
        foreach (var res in results)
        {
            Assert.NotNull(res);
            Assert.Equal(1, res.Total);
            Assert.Single(res.Items);
            Assert.Equal("100", res.Items[0].IdProcedimento);
        }

        // Verifica que o token em cache atualizado é válido
        Assert.StartsWith("tok_v", client.CachedToken);
        Assert.NotEqual("tok_v1", client.CachedToken);
    }

    [Fact]
    public async Task Challenger_ConcurrentCalls_MeasuresLoginCount_StampedeAnalysis()
    {
        var handler = new MockHttpMessageHandler();
        int loginCallCount = 0;

        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/autenticar") == true,
            req =>
            {
                int count = Interlocked.Increment(ref loginCallCount);
                // Simula pequeno delay de rede para permitir contenção no SemaphoreSlim
                Thread.Sleep(20);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(count == 1
                        ? """{"sucesso":true,"data":{"token":"tok_initial_expired"}}"""
                        : """{"sucesso":true,"data":{"token":"tok_renewed"}}""", Encoding.UTF8, "application/json")
                };
            }
        );

        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/processo/pesquisar") == true,
            req =>
            {
                string? token = req.Headers.TryGetValues("token", out var vals) ? vals.FirstOrDefault() : null;
                if (token == "tok_initial_expired")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\": true, \"total\": 1, \"data\": []}", Encoding.UTF8, "application/json")
                };
            }
        );

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        // Força token expirado no cache
        await client.AuthenticateAsync();

        // 10 chamadas paralelas
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => client.SearchProcessesAsync("110000001")))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);

        // Um login inicial e uma renovação compartilhada entre as chamadas.
        Assert.Equal(2, loginCallCount);
    }

    // =========================================================================
    // 2. SIMULAÇÃO DE HTTP 401 REPETIDO (RENOVAÇÃO E SEI_ACESSO_NEGADO)
    // =========================================================================

    [Fact]
    public async Task Challenger_Repeated401_FirstRenews_SecondConsecutiveThrowsAcessoNegado()
    {
        var handler = new MockHttpMessageHandler();
        int authCalls = 0;
        int searchCalls = 0;

        // Login sempre gera token
        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/autenticar") == true,
            req =>
            {
                int count = Interlocked.Increment(ref authCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sucesso\": true, \"data\": {{\"token\": \"tok_{count}\"}}}}", Encoding.UTF8, "application/json")
                };
            }
        );

        // Pesquisa sempre devolve 401 Unauthorized (ex.: usuário sem permissão no órgão)
        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/processo/pesquisar") == true,
            req =>
            {
                Interlocked.Increment(ref searchCalls);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"sucesso\": false, \"mensagem\": \"Acesso negado para a unidade\"}", Encoding.UTF8, "application/json")
                };
            }
        );

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.SearchProcessesAsync("110000001"));

        // Contrato estrito: 2º 401 consecutivo lança SeiException("SEI_ACESSO_NEGADO")
        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.AcessoNegado, ex.ErrorCode);

        // Prova empírica: 1 login inicial + 1 chamada de busca (401) + 1 renovação de login + 1 chamada retry (401) = 2 logins, 2 buscas
        Assert.Equal(2, authCalls);
        Assert.Equal(2, searchCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)422)]
    public async Task Challenger_DirectAuthRejection_ThrowsAcessoNegadoImmediately(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", statusCode, "{\"sucesso\": false, \"mensagem\": \"Credenciais invalidas\"}");

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha_errada"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.AuthenticateAsync(forceRenew: true));

        Assert.Equal("SEI_ACESSO_NEGADO", ex.SafeErrorCode);
        Assert.Equal(SeiErrorCode.AcessoNegado, ex.ErrorCode);
    }

    // =========================================================================
    // 3. FALLBACK PARA API DA SEAD EM HTTP 500, 502, 503 OU TIMEOUT
    // =========================================================================

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Challenger_SearchProcesses_WsseiFails5xx_FallsBackToSead(int statusCode)
    {
        var handler = new MockHttpMessageHandler();

        // WSSEI autentica mas a busca retorna 5xx
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok_wssei\"}}");
        handler.SetupResponse("/processo/pesquisar", (HttpStatusCode)statusCode, "{\"sucesso\": false, \"mensagem\": \"Erro interno\"}");

        // SEAD configurada e pronta para responder com sucesso
        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.OK, "{\"Token\": \"tok_sead_active\"}");
        string seadJson = """
        [
            {
                "IdProcedimento": "888",
                "ProcedimentoFormatado": "00088.000888/2026-11",
                "TipoProcedimento": { "Nome": "Processo SEAD Fallback" },
                "Especificacao": "Recuperado via SEAD após falha 5xx"
            }
        ]
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/consulta", HttpStatusCode.OK, seadJson);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var result = await client.SearchProcessesAsync("110000001");

        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.Equal("888", result.Items[0].IdProcedimento);
        Assert.Equal("00088.000888/2026-11", result.Items[0].ProtocoloFormatado);
    }

    [Fact]
    public async Task Challenger_SearchProcesses_WsseiTimeout_FallsBackToSead()
    {
        var handler = new MockHttpMessageHandler();

        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok_wssei\"}}");
        handler.Setup(
            req => req.RequestUri?.ToString().Contains("/processo/pesquisar") == true,
            req => throw new TaskCanceledException("Simulated HTTP Timeout on WSSEI")
        );

        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.OK, "{\"Token\": \"tok_sead_active\"}");
        string seadJson = """
        [
            {
                "IdProcedimento": "999",
                "ProcedimentoFormatado": "00099.000999/2026-22",
                "TipoProcedimento": { "Nome": "Processo Timeout Fallback" },
                "Especificacao": "Recuperado via SEAD após timeout"
            }
        ]
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/consulta", HttpStatusCode.OK, seadJson);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var result = await client.SearchProcessesAsync("110000001");

        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.Equal("999", result.Items[0].IdProcedimento);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Challenger_GetLightSummary_WsseiFails5xx_InvestigateFallbackBehavior(int statusCode)
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok_wssei\"}}");

        // WSSEI retorna 5xx tanto em documentos quanto em atividades
        handler.SetupResponse("/documento/listar/777", (HttpStatusCode)statusCode, "Internal Error");
        handler.SetupResponse("/atividade/listar", (HttpStatusCode)statusCode, "Internal Error");

        // SEAD configurada com dados válidos
        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.OK, "{\"Token\": \"tok_sead\"}");
        string seadDocsJson = """
        {
            "Info": { "TotalItens": 42 },
            "Documentos": [
                { "IdDocumento": "d1", "DocumentoFormatado": "Doc 1", "Serie": { "Nome": "Despacho" }, "Numero": "1" }
            ]
        }
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/documentos", HttpStatusCode.OK, seadDocsJson);

        string seadAtivJson = """
        {
            "Andamentos": [
                { "DataHora": "30/10/2026 10:00:00", "Descricao": "Andamento via SEAD", "Unidade": { "Sigla": "GINFO" } }
            ]
        }
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/andamentos", HttpStatusCode.OK, seadAtivJson);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var summary = await client.GetLightSummaryAsync("777", "110000001");

        // Verificação empírica: Se o fallback funcionou, TotalDocumentos deve ser 42 vindo da SEAD.
        // Se TotalDocumentos for 0, o WSSEI engoliu o erro 5xx sem acionar o fallback!
        Assert.True(summary.TotalDocumentos == 42,
            $"BUG DETECTADO: GetLightSummaryAsync não acionou o fallback SEAD no status HTTP {statusCode}. Retornou TotalDocumentos={summary.TotalDocumentos} em vez de 42 da SEAD.");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Challenger_GetProcessDetails_WsseiFails5xx_InvestigateFallbackBehavior(int statusCode)
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok_wssei\"}}");

        // WSSEI falha com 5xx
        handler.SetupResponse("/processo/777", (HttpStatusCode)statusCode, "Server Error");
        handler.SetupResponse("/documento/listar/777", (HttpStatusCode)statusCode, "Server Error");
        handler.SetupResponse("/atividade/listar", (HttpStatusCode)statusCode, "Server Error");

        // SEAD configurada
        handler.SetupResponse("/v1/orgaos/usuarios/login", HttpStatusCode.OK, "{\"Token\": \"tok_sead\"}");
        string seadDocsJson = """
        {
            "Documentos": [
                { "IdDocumento": "sead_d1", "DocumentoFormatado": "Doc SEAD 100", "Serie": { "Nome": "Parecer" }, "Numero": "100" }
            ]
        }
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/documentos", HttpStatusCode.OK, seadDocsJson);

        string seadAtivJson = """
        {
            "Andamentos": [
                { "DataHora": "30/10/2026 12:00:00", "Descricao": "Parecer emitido via SEAD", "Unidade": { "Sigla": "GINFO" } }
            ]
        }
        """;
        handler.SetupResponse("/v1/unidades/110000001/procedimentos/andamentos", HttpStatusCode.OK, seadAtivJson);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var detail = await client.GetProcessDetailsAsync("777", "110000001");

        // Verificação empírica: Se o fallback funcionou, detail deve ter os documentos da SEAD.
        Assert.True(detail.Documentos.Count > 0,
            $"BUG DETECTADO: GetProcessDetailsAsync não acionou fallback SEAD no status HTTP {statusCode}. Documentos retornados: {detail.Documentos.Count}.");
    }

    // =========================================================================
    // 4. SANITIZAÇÃO DE ERROS DO SOLR (ZERO VAZAMENTO DE MENSAGEM OU EXCEPTION)
    // =========================================================================

    [Fact]
    public async Task Challenger_SolrCredentialsLeak_NeverExposedInAnyFieldOrLog()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetupResponse("/autenticar", HttpStatusCode.OK, "{\"sucesso\": true, \"data\": {\"token\": \"tok\"}}");

        const string solrInternalUrl = "http://sei_solr_user:SeiSolrUltraSecretPass999@10.240.12.88:8983/solr/processos";
        const string solrExceptionTrace = "org.apache.solr.client.solrj.impl.HttpSolrClient$RemoteSolrException: Error from server at " + solrInternalUrl;

        string maliciousSolrResponse = $$"""
        {
            "sucesso": false,
            "mensagem": "Falha de comunicacao Solr: {{solrInternalUrl}} retornou HTTP 500",
            "exception": "{{solrExceptionTrace}}"
        }
        """;

        handler.SetupResponse("/processo/pesquisar", HttpStatusCode.OK, maliciousSolrResponse);

        using var client = new SeiClient(_config, _db, handler, ("usuario.servico", "senha123"));

        var ex = await Assert.ThrowsAsync<SeiException>(() => client.SearchProcessesAsync("110000001"));

        // 1. Mensagem de exceção NÃO pode conter credenciais do Solr
        Assert.DoesNotContain("sei_solr_user", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SeiSolrUltraSecretPass999", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.240.12.88", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("8983", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 2. ToString() da exceção NÃO pode conter credenciais
        string exStr = ex.ToString();
        Assert.DoesNotContain("sei_solr_user", exStr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SeiSolrUltraSecretPass999", exStr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.240.12.88", exStr, StringComparison.OrdinalIgnoreCase);

        // 3. Código seguro padronizado
        Assert.Equal("SEI_FALHA_PESQUISA", ex.SafeErrorCode);

        // 4. Nenhum log salvo no banco pode conter credenciais
        var logs = _db.GetLogs(50);
        foreach (var l in logs)
        {
            Assert.DoesNotContain("sei_solr_user", l.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SeiSolrUltraSecretPass999", l.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("10.240.12.88", l.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
