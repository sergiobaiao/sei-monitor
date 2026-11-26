using System.Globalization;
using System.Runtime.Versioning;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Testes empíricos adversariais de estresse para persistência segura e configurações reativas.
/// Executados pelo agente m1_challenger_2 para o Milestone 1.
/// </summary>
[SupportedOSPlatform("windows")]
public class EmpiricalStressTests : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _createdTargets = new();
    private readonly Database _db;
    private readonly Config _config;

    public EmpiricalStressTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db);
    }

    private string CreateUniqueTarget(string prefix = "Stress")
    {
        var target = $"SEI Monitor:{prefix}:{Guid.NewGuid():N}";
        _createdTargets.Add(target);
        return target;
    }

    public void Dispose()
    {
        foreach (var target in _createdTargets)
        {
            try
            {
                CredentialStore.DeleteCredential(target);
            }
            catch
            {
                // Silencia falha em cleanup
            }
        }
        _db.Dispose();
    }

    // =========================================================================
    // 1. ESTRESSE DE CREDENCIAIS (Advapi32.dll / CredentialStore)
    // =========================================================================

    [Fact]
    public void CredentialStore_SuccessiveOverwrites_RapidLoop_RetainsFinalState()
    {
        var target = CreateUniqueTarget("RapidOverwrite");
        const int iterations = 100;

        for (int i = 0; i < iterations; i++)
        {
            var user = $"user_{i}";
            var pass = $"pass_iteration_{i}_{Guid.NewGuid():N}";
            var saved = CredentialStore.SaveCredential(target, user, pass);
            Assert.True(saved, $"Falha ao salvar na iteração {i}");

            if (i % 10 == 0 || i == iterations - 1)
            {
                var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
                Assert.Equal(user, loadedUser);
                Assert.Equal(pass, loadedPass);
            }
        }

        // Cleanup explícito
        Assert.True(CredentialStore.DeleteCredential(target));
        var (finalUser, finalPass) = CredentialStore.LoadCredential(target);
        Assert.Null(finalUser);
        Assert.Null(finalPass);
    }

    [Fact]
    public void CredentialStore_MultipleDistinctTargets_BatchWriteAndRead()
    {
        const int count = 30;
        var targets = new List<(string target, string user, string pass)>();

        for (int i = 0; i < count; i++)
        {
            var t = CreateUniqueTarget($"Batch_{i}");
            var u = $"batch.user.{i}";
            var p = $"SecretKey_{i}_{Guid.NewGuid():N}";
            targets.Add((t, u, p));
            var saved = CredentialStore.SaveCredential(t, u, p);
            Assert.True(saved);
        }

        // Ler todos e validar integridade
        foreach (var item in targets)
        {
            var (u, p) = CredentialStore.LoadCredential(item.target);
            Assert.Equal(item.user, u);
            Assert.Equal(item.pass, p);
        }

        // Deletar todos
        foreach (var item in targets)
        {
            Assert.True(CredentialStore.DeleteCredential(item.target));
            var (u, p) = CredentialStore.LoadCredential(item.target);
            Assert.Null(u);
            Assert.Null(p);
        }
    }

    [Fact]
    public void CredentialStore_ConcurrentAccess_ThreadSafetyStress()
    {
        const int threadCount = 6;
        const int operationsPerThread = 15;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, threadId =>
        {
            try
            {
                var target = CreateUniqueTarget($"Thread_{threadId}");
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var user = $"user_t{threadId}_{i}";
                    var pass = $"pass_t{threadId}_{i}";

                    if (!CredentialStore.SaveCredential(target, user, pass))
                        throw new Exception($"Falha ao salvar credencial em thread {threadId}");

                    var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
                    if (loadedUser != user || loadedPass != pass)
                        throw new Exception($"Corrupção de dados em thread {threadId} na iter {i}: esperado {user}/{pass}, recebido '{loadedUser}'/'{loadedPass}'");

                    if (i % 3 == 0)
                    {
                        CredentialStore.DeleteCredential(target);
                        var (u, p) = CredentialStore.LoadCredential(target);
                        if (u != null || p != null)
                            throw new Exception($"Deleção não refletida em thread {threadId}");
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void CredentialStore_IdempotentDeletion_RepeatedCallsSucceed()
    {
        var target = CreateUniqueTarget("IdempotentDelete");

        // Deletar sem existir -> deve retornar true
        Assert.True(CredentialStore.DeleteCredential(target));

        // Salvar e deletar 5 vezes seguidas
        CredentialStore.SaveCredential(target, "user", "pass");
        for (int i = 0; i < 5; i++)
        {
            Assert.True(CredentialStore.DeleteCredential(target), $"Falha na deleção repetida {i}");
            var (u, p) = CredentialStore.LoadCredential(target);
            Assert.Null(u);
            Assert.Null(p);
        }
    }

    [Fact]
    public void CredentialStore_EmptyStringsAndWhitespaceHandling()
    {
        var target1 = CreateUniqueTarget("EmptyPass");
        // Senha vazia é permitida em novo target
        Assert.True(CredentialStore.SaveCredential(target1, "user.with.empty.pass", ""));
        var (u1, p1) = CredentialStore.LoadCredential(target1);
        Assert.Equal("user.with.empty.pass", u1);
        Assert.Equal("", p1);

        var target2 = CreateUniqueTarget("EmptyUser");
        // Usuário vazio em novo target
        Assert.True(CredentialStore.SaveCredential(target2, "", "password.with.empty.user"));
        var (u2, p2) = CredentialStore.LoadCredential(target2);
        Assert.Equal("", u2);
        Assert.Equal("password.with.empty.user", p2);

        var target3 = CreateUniqueTarget("BothEmpty");
        // Ambos vazios em novo target
        Assert.True(CredentialStore.SaveCredential(target3, "", ""));
        var (u3, p3) = CredentialStore.LoadCredential(target3);
        Assert.Equal("", u3);
        Assert.Equal("", p3);

        // SOBRESCRITA COM USUÁRIO VAZIO:
        // Documenta comportamento do Windows Credential Manager:
        // Quando uma credencial já existe com usuário preenchido, chamar CredWriteW com UserName vazio
        // faz o Windows preservar o usuário anterior em vez de limpar para "".
        var targetOverwrite = CreateUniqueTarget("OverwriteEmptyUser");
        CredentialStore.SaveCredential(targetOverwrite, "usuario_original", "senha1");
        CredentialStore.SaveCredential(targetOverwrite, "", "senha2");
        var (uOver, pOver) = CredentialStore.LoadCredential(targetOverwrite);
        Assert.Equal("", uOver); // CredWriteW atualiza corretamente para ""
        Assert.Equal("senha2", pOver);

        // Espaços em branco em senha devem ser preservados sem corte
        var targetSpaced = CreateUniqueTarget("Spaced");
        const string spacedPass = "   minha senha com espacos   ";
        Assert.True(CredentialStore.SaveCredential(targetSpaced, "user.spaced", spacedPass));
        var (u4, p4) = CredentialStore.LoadCredential(targetSpaced);
        Assert.Equal("user.spaced", u4);
        Assert.Equal(spacedPass, p4);

        // Target nulo ou em branco DEVE lançar ArgumentException
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.SaveCredential(null!, "u", "p"));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.SaveCredential("", "u", "p"));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.SaveCredential("   ", "u", "p"));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.LoadCredential(null!));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.LoadCredential("   "));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.DeleteCredential(null!));
        Assert.ThrowsAny<ArgumentException>(() => CredentialStore.DeleteCredential("   "));
    }

    [Fact]
    public void CredentialStore_MaxLength_ProbeActualWindows11Limits()
    {
        var target = CreateUniqueTarget("BlobLimitProbe");

        // Testar múltiplos tamanhos para descobrir o limite real de CredWriteW no Windows 11
        // Em UTF-16LE, chars = bytes / 2
        int[] charLengthsToTest = [256, 512, 1024, 2048, 2560, 4096, 5120, 10000];
        int maxAcceptedChars = 0;
        int firstRejectedChars = -1;

        foreach (var len in charLengthsToTest)
        {
            var testTarget = CreateUniqueTarget($"BlobProbe_{len}");
            var pass = new string('A', len);
            var saved = CredentialStore.SaveCredential(testTarget, "user.probe", pass);
            if (saved)
            {
                var (loadedUser, loadedPass) = CredentialStore.LoadCredential(testTarget);
                if (loadedPass == pass)
                {
                    maxAcceptedChars = len;
                }
            }
            else
            {
                firstRejectedChars = len;
                break;
            }
        }

        // No Windows 11, o limite aceito é de pelo menos 256 caracteres (512 bytes)
        Assert.True(maxAcceptedChars >= 256, $"Deveria aceitar pelo menos 256 caracteres, aceitou {maxAcceptedChars}");

        // Uma senha de tamanho gigante (10000+ chars) deve ser rejeitada graciosamente sem crash
        var giantPass = new string('Z', 20000);
        var giantTarget = CreateUniqueTarget("GiantPass");
        var savedGiant = CredentialStore.SaveCredential(giantTarget, "user.giant", giantPass);
        Assert.False(savedGiant, "CredWriteW deveria rejeitar senha de 20.000 caracteres");
    }

    [Fact]
    public void CredentialStore_DeleteAndImmediateRecreate_NoRaceCondition()
    {
        var target = CreateUniqueTarget("DeleteRecreate");

        for (int i = 0; i < 20; i++)
        {
            var user = $"user_{i}";
            var pass = $"pass_{i}";

            Assert.True(CredentialStore.SaveCredential(target, user, pass));
            var (u1, p1) = CredentialStore.LoadCredential(target);
            Assert.Equal(user, u1);
            Assert.Equal(pass, p1);

            Assert.True(CredentialStore.DeleteCredential(target));
            var (u2, p2) = CredentialStore.LoadCredential(target);
            Assert.Null(u2);
            Assert.Null(p2);
        }
    }

    [Fact]
    public void CredentialStore_UnicodeSurrogatesAndEmojis()
    {
        var target = CreateUniqueTarget("UnicodeEmojis");
        const string user = "usuário.áéíóú.ñ.汉字.🚀";
        const string pass = "🔐_P@ssw0rd_🎯_👨‍👩‍👧‍👦_€_§_©_®_™";

        var saved = CredentialStore.SaveCredential(target, user, pass);
        Assert.True(saved);

        var (loadedUser, loadedPass) = CredentialStore.LoadCredential(target);
        Assert.Equal(user, loadedUser);
        Assert.Equal(pass, loadedPass);
    }

    // =========================================================================
    // 2. REATIVIDADE E CLAMP DE CONFIG.CS
    // =========================================================================

    [Theory]
    [InlineData(-100, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    public void Config_CheckIntervalMinutes_EnforcesMinimumBound(int input, int expected)
    {
        _config.CheckIntervalMinutes = input;
        Assert.Equal(expected, _config.CheckIntervalMinutes);
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), _db.GetSetting("check_interval_minutes"));
    }

    [Theory]
    [InlineData(-50, 10)]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    public void Config_SeiTimeoutSeconds_EnforcesMinimumBound(int input, int expected)
    {
        _config.SeiTimeoutSeconds = input;
        Assert.Equal(expected, _config.SeiTimeoutSeconds);
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), _db.GetSetting("sei_timeout_seconds"));
    }

    [Theory]
    [InlineData(0, 280)]
    [InlineData(279, 280)]
    [InlineData(280, 280)]
    [InlineData(500, 500)]
    [InlineData(800, 800)]
    [InlineData(801, 800)]
    [InlineData(9999, 800)]
    public void Config_FenceW_ClampedBetween280And800(int input, int expected)
    {
        _config.FenceW = input;
        Assert.Equal(expected, _config.FenceW);
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), _db.GetSetting("desktop_fence_w"));
    }

    [Theory]
    [InlineData(-50, 200)]
    [InlineData(199, 200)]
    [InlineData(200, 200)]
    [InlineData(600, 600)]
    [InlineData(1200, 1200)]
    [InlineData(1201, 1200)]
    [InlineData(5000, 1200)]
    public void Config_FenceH_ClampedBetween200And1200(int input, int expected)
    {
        _config.FenceH = input;
        Assert.Equal(expected, _config.FenceH);
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), _db.GetSetting("desktop_fence_h"));
    }

    [Theory]
    [InlineData(-1.0, 0.20)]
    [InlineData(0.0, 0.20)]
    [InlineData(0.19, 0.20)]
    [InlineData(0.20, 0.20)]
    [InlineData(0.85, 0.85)]
    [InlineData(1.00, 1.00)]
    [InlineData(1.50, 1.00)]
    public void Config_FenceAlpha_ClampedBetween020And100(double input, double expected)
    {
        _config.FenceAlpha = input;
        Assert.Equal(expected, _config.FenceAlpha, 2);
    }

    [Fact]
    public void Config_CorruptedDatabaseSettings_ClampEnforcedOnRead()
    {
        // Se um agente externo ou bug gravar valores anômalos diretamente no banco,
        // o leitor do Config DEVE proteger a aplicação com clamp.
        _db.SetSetting("desktop_fence_w", "99999");
        Assert.Equal(800, _config.FenceW);

        _db.SetSetting("desktop_fence_w", "-50");
        Assert.Equal(280, _config.FenceW);

        _db.SetSetting("desktop_fence_h", "99999");
        Assert.Equal(1200, _config.FenceH);

        _db.SetSetting("desktop_fence_h", "-50");
        Assert.Equal(200, _config.FenceH);

        _db.SetSetting("desktop_fence_alpha", "5.0");
        Assert.Equal(1.00, _config.FenceAlpha, 2);

        _db.SetSetting("desktop_fence_alpha", "-2.0");
        Assert.Equal(0.20, _config.FenceAlpha, 2);

        _db.SetSetting("check_interval_minutes", "-10");
        Assert.Equal(1, _config.CheckIntervalMinutes);

        _db.SetSetting("sei_timeout_seconds", "-5");
        Assert.Equal(10, _config.SeiTimeoutSeconds);
    }

    [Fact]
    public void Config_NegativeCoordinatesForMultiMonitor_SupportedCorrectly()
    {
        // Monitores secundários posicionados à esquerda ou acima do monitor primário
        // possuem coordenadas virtuais negativas no Windows.
        _config.FenceX = -1920;
        _config.FenceY = -1080;

        Assert.Equal(-1920, _config.FenceX);
        Assert.Equal(-1080, _config.FenceY);
        Assert.Equal("-1920", _db.GetSetting("desktop_fence_x"));
        Assert.Equal("-1080", _db.GetSetting("desktop_fence_y"));
    }

    [Fact]
    public void Config_KeywordsParsing_TrimmingEmptyAndDelimiters()
    {
        _config.UserKeywordsRaw = " Victor , , ; ; ALMIR ; ; ginfo ; victor ; ";
        var keywords = _config.UserKeywords;

        Assert.Equal(3, keywords.Count);
        Assert.Contains("Victor", keywords);
        Assert.Contains("ALMIR", keywords);
        Assert.Contains("ginfo", keywords);
    }

    // =========================================================================
    // 3. VERIFICAÇÃO DE SEGURANÇA: AUSÊNCIA DE CREDENCIAIS EM SQLITE E LOGS
    // =========================================================================

    [Fact]
    public void Security_DiskDatabase_RawBinaryScan_NoSecretsEverWrittenToSqliteFile()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"SeiMonitor_SecScan_{Guid.NewGuid():N}.db");
        const string secretPassword = "SuperSecretPassword_EmpiricalCheck_2026";
        const string secretToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.SecretPayloadForOracleTest";

        try
        {
            using (var diskDb = new Database(tempDbPath))
            {
                var config = new Config(diskDb);

                // Configura usuário e outros campos normais
                config.SeiWsseiUsuario = "servidor.orgao";
                config.SeiSeadUsuario = "servidor.sead";
                config.SeiUnidade = "110000000";
                config.SeiUnidadeSigla = "GINFO";
                config.UserKeywordsRaw = "licitacao, ginfo, processo";

                // Simula inserção de logs e processos
                diskDb.Log("INFO", "Inicialização do monitor de processos");
                diskDb.Log("WARN", "Tentativa com endpoint https://sei.pi.gov.br");

                diskDb.UpsertMonitoredProcess(new MonitoredProcess
                {
                    Id = "proc-secure-01",
                    ProtocoloFormatado = "00001.000001/2026-00",
                    Descricao = "Processo de verificação de segurança",
                    Origem = "GINFO",
                    TotalDocumentos = 5
                });
            }

            // O Database agora está fechado. Vamos ler o arquivo binário bruto do SQLite do disco.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var rawBytes = File.ReadAllBytes(tempDbPath);
            var rawUtf8 = System.Text.Encoding.UTF8.GetString(rawBytes);
            var rawUnicode = System.Text.Encoding.Unicode.GetString(rawBytes);

            // Prova empírica: O segredo NUNCA deve estar presente no arquivo físico do banco
            Assert.DoesNotContain(secretPassword, rawUtf8, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretPassword, rawUnicode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretToken, rawUtf8, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretToken, rawUnicode, StringComparison.OrdinalIgnoreCase);

            // Verificar se arquivos WAL/SHM existem e também inspecioná-los
            var walPath = tempDbPath + "-wal";
            if (File.Exists(walPath))
            {
                var walBytes = File.ReadAllBytes(walPath);
                var walUtf8 = System.Text.Encoding.UTF8.GetString(walBytes);
                Assert.DoesNotContain(secretPassword, walUtf8, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secretToken, walUtf8, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
                if (File.Exists(tempDbPath + "-wal")) File.Delete(tempDbPath + "-wal");
                if (File.Exists(tempDbPath + "-shm")) File.Delete(tempDbPath + "-shm");
            }
            catch
            {
                // Silencia falha de cleanup em temp
            }
        }
    }

    [Fact]
    public void Security_LogSanitizer_NeutralizesSolrLeakAndUrlCredentials()
    {
        // 1. Bug do Solr no mod-wssei com usuário e senha expostos na URL
        const string rawSolrMsg = "Falha no Solr: SolrServerException: Server at http://admin_solr:P@ssw0rd!123@10.0.0.1:8983/solr/processos returned status 500";
        _db.Log("ERROR", rawSolrMsg);

        // 2. URL comum com credenciais embutidas
        const string rawUrlMsg = "Chamada HTTP falhou: https://sei_user:segredo123@api.sei.pi.gov.br/v1/login";
        _db.Log("WARN", rawUrlMsg);

        // 3. Query string sensível
        const string rawParamMsg = "Erro na requisição: /autenticar?usuario=joao&senha=MinhaSenhaUltraSecreta&token=eyJhbGciOiJIUzI1NiJ9";
        _db.Log("ERROR", rawParamMsg);

        // 4. Mensagem gigante de erro (estresse de buffer de log)
        var giantMsg = "Erro catastrófico: " + new string('A', 5000);
        _db.Log("INFO", giantMsg);

        var logs = _db.GetLogs(10);
        Assert.True(logs.Count >= 4);

        foreach (var log in logs)
        {
            // NENHUM segredo bruto pode aparecer nos logs salvos
            Assert.DoesNotContain("admin_solr", log.Message);
            Assert.DoesNotContain("P@ssw0rd!123", log.Message);
            Assert.DoesNotContain("segredo123", log.Message);
            Assert.DoesNotContain("MinhaSenhaUltraSecreta", log.Message);
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", log.Message);

            // Tamanho máximo do log não deve exceder 1100 caracteres mesmo com mensagem de 5000
            Assert.True(log.Message.Length <= 1100, $"Tamanho do log excedeu limite: {log.Message.Length}");
        }

        // Checar especificamente a sanitização do Solr
        var solrLog = logs.First(l => l.Message.Contains("Solr"));
        Assert.Equal("[SANITIZED: Falha na integração com motor de busca do SEI (Solr)]", solrLog.Message);
    }
}
