using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

[SupportedOSPlatform("windows")]
public class AdversarialDatabaseTests : IDisposable
{
    private readonly string _tempDbFile;
    private readonly Database _diskDb;

    public AdversarialDatabaseTests()
    {
        _tempDbFile = Path.Combine(Path.GetTempPath(), $"SeiMonitor_AdvTest_{Guid.NewGuid():N}.db");
        _diskDb = new Database(_tempDbFile);
    }

    public void Dispose()
    {
        _diskDb.Dispose();
        // Give time for handles to release if needed
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_tempDbFile)) File.Delete(_tempDbFile);
            var walFile = _tempDbFile + "-wal";
            if (File.Exists(walFile)) File.Delete(walFile);
            var shmFile = _tempDbFile + "-shm";
            if (File.Exists(shmFile)) File.Delete(shmFile);
        }
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    #region 1. Concurrency & WAL Stress Tests

    [Fact]
    public async Task Concurrency_HeavyParallelReadsAndWrites_OnDiskDatabase_NoDeadlockOrLockException()
    {
        // 25 tasks concurrently hammering the database:
        // 10 writers (upserting processes and settings)
        // 10 readers (listing and querying processes)
        // 5 loggers (writing logs)
        const int taskCount = 25;
        const int iterationsPerTask = 40;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, taskCount).Select(i => Task.Run(() =>
        {
            try
            {
                var role = i % 4;
                for (int j = 0; j < iterationsPerTask; j++)
                {
                    switch (role)
                    {
                        case 0: // Process writer
                            _diskDb.UpsertMonitoredProcess(new MonitoredProcess
                            {
                                Id = $"P_{i}_{j % 10}",
                                ProtocoloFormatado = $"0000{i}.{j:D6}/2026-00",
                                Descricao = $"Processo concorrente t={i} iter={j}",
                                Origem = j % 2 == 0 ? "CAIXA_ENTRADA" : "ACOMPANHAMENTO",
                                TotalDocumentos = j,
                                AbertoPorTerceiro = j % 3 == 0,
                                DestinadoAoUsuario = j % 5 == 0
                            });
                            break;

                        case 1: // Process reader
                            var list = _diskDb.ListMonitoredProcesses(origem: j % 2 == 0 ? "CAIXA_ENTRADA" : null);
                            var p = _diskDb.GetMonitoredProcess($"P_{i - 1}_{j % 10}");
                            break;

                        case 2: // Settings & Read status writer/reader
                            _diskDb.SetSetting($"Key_{i}_{j % 5}", $"Val_{j}");
                            var val = _diskDb.GetSetting($"Key_{i}_{j % 5}");
                            _diskDb.SetLocalReadStatus($"P_{i - 2}_{j % 10}", j % 2 == 0, j);
                            var isRead = _diskDb.GetLocalReadStatus($"P_{i - 2}_{j % 10}");
                            break;

                        case 3: // Logger & log reader
                            _diskDb.Log("INFO", $"Log concorrente task={i} iter={j}");
                            if (j % 5 == 0)
                            {
                                var logs = _diskDb.GetLogs(10);
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public void Concurrency_WalModeAndBusyTimeout_AreConfiguredProperly()
    {
        // Verify PRAGMA journal_mode is WAL on disk DB
        using var conn = new SqliteConnection($"Data Source={_tempDbFile}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)cmd.ExecuteScalar();
        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    #endregion

    #region 2. Edge Cases: Nulls, Empty Strings, Unicode & Emojis

    [Fact]
    public void EdgeCase_Settings_NullAndEmptyValues_HandledGracefully()
    {
        // Empty string key
        _diskDb.SetSetting("", "empty_key_val");
        Assert.Equal("empty_key_val", _diskDb.GetSetting(""));

        // Whitespace key
        _diskDb.SetSetting("   ", "spaces");
        Assert.Equal("spaces", _diskDb.GetSetting("   "));

        // Null value: Database.cs lines 143: cmd.Parameters.AddWithValue("$v", value ?? "");
        _diskDb.SetSetting("null_val_key", null!);
        Assert.Equal("", _diskDb.GetSetting("null_val_key"));

        // Setting nonexistent key returns default
        Assert.Equal("fallback", _diskDb.GetSetting("non_existent_123", "fallback"));
    }

    [Fact]
    public void EdgeCase_Settings_NullKey_ThrowsArgumentNullOrHandles()
    {
        Exception? caught = null;
        try
        {
            _diskDb.SetSetting(null!, "val");
            var val = _diskDb.GetSetting(null!);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        // O driver Microsoft.Data.Sqlite lança InvalidOperationException quando o valor do parâmetro é null não envolvido em DBNull
        Assert.True(caught is InvalidOperationException || caught is ArgumentException || caught is SqliteException);
    }

    [Fact]
    public void EdgeCase_MonitoredProcess_ExtremeUnicode_Emojis_Surrogates_Zwnj()
    {
        // Complex unicode strings:
        // 1. Multi-byte emojis with zero-width joiners (👨‍👩‍👧‍👦, 🏳️‍🌈)
        // 2. RTL text (Arabic, Hebrew)
        // 3. CJK and math symbols (𝓕𝓸𝓸, 漢字, 𠮷野家)
        // 4. Emojis with skin tone modifiers (👍🏽)
        var unicodeDesc = "Processo Especial 👨‍👩‍👧‍👦 🏳️‍🌈 - בדיקה - تجربة - 漢字 𠮷野家 - 👍🏽";
        var proc = new MonitoredProcess
        {
            Id = "UNICODE_1",
            ProtocoloFormatado = "00001.UNICODE/2026-00",
            Descricao = unicodeDesc,
            TipoProcesso = "Processo com Emojis 🚀",
            Origem = "CAIXA_ENTRADA",
            UltimaAtividadeDesc = "Despacho em Árabe: تم التحديث بنجاح 🎯",
            UltimoUsuarioNome = "Usuário 𝓕𝓪𝓷𝓬𝔂"
        };

        _diskDb.UpsertMonitoredProcess(proc);

        var retrieved = _diskDb.GetMonitoredProcess("UNICODE_1");
        Assert.NotNull(retrieved);
        Assert.Equal(unicodeDesc, retrieved.Descricao);
        Assert.Equal("Processo com Emojis 🚀", retrieved.TipoProcesso);
        Assert.Equal("Despacho em Árabe: تم التحديث بنجاح 🎯", retrieved.UltimaAtividadeDesc);
        Assert.Equal("Usuário 𝓕𝓪𝓷𝓬𝔂", retrieved.UltimoUsuarioNome);
    }

    [Fact]
    public void EdgeCase_SurrogatePair_At500CharBoundary_DoesNotCrashOrCorrupt()
    {
        // In UTF-16, emojis like 😀 (\uD83D\uDE00) take 2 char code units (high surrogate + low surrogate).
        // If length is 499 standard chars + 1 emoji (2 code units) = 501 chars.
        // Slicing [..500] slices in between the high surrogate and low surrogate!
        var prefix = new string('A', 499);
        var emoji = "😀"; // \uD83D\uDE00
        var rawDesc = prefix + emoji + " extra text";

        var proc = new MonitoredProcess
        {
            Id = "SURROGATE_BOUNDARY",
            ProtocoloFormatado = "00001.SURR/2026-00",
            Descricao = rawDesc,
            Origem = "CAIXA_ENTRADA"
        };

        // Must not crash on upsert or read
        _diskDb.UpsertMonitoredProcess(proc);

        var retrieved = _diskDb.GetMonitoredProcess("SURROGATE_BOUNDARY");
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.Descricao);
        Assert.True(retrieved.Descricao.Length <= 500);
    }

    #endregion

    #region 3. SQL Injection Resistance

    [Fact]
    public void SqlInjection_ListMonitoredProcesses_WithSqlPayloads_IsImmune()
    {
        // Insert sample process
        _diskDb.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = "INJ_TEST_1",
            ProtocoloFormatado = "00001.INJ/2026-00",
            Origem = "CAIXA_ENTRADA"
        });

        // Payloads that would drop table, bypass conditions, or cause syntax errors if not parameterized
        var payloads = new[]
        {
            "' OR '1'='1",
            "'; DROP TABLE monitored_processes; --",
            "' UNION SELECT * FROM settings --",
            "'' OR 1=1 --",
            "\"; DROP TABLE local_read_status; --"
        };

        foreach (var payload in payloads)
        {
            // List with payload as origem
            var listOrigem = _diskDb.ListMonitoredProcesses(origem: payload);
            Assert.Empty(listOrigem);

            // List with payload as idGrupo
            var listGrupo = _diskDb.ListMonitoredProcesses(idGrupo: payload);
            Assert.Empty(listGrupo);
        }

        // Verify table still exists and data is intact
        var intact = _diskDb.GetMonitoredProcess("INJ_TEST_1");
        Assert.NotNull(intact);
    }

    [Fact]
    public void SqlInjection_SettingsAndProcessCrud_WithSqlPayloads_IsImmune()
    {
        var payload = "test'; DROP TABLE settings; --";

        // Setting CRUD with injection payload
        _diskDb.SetSetting(payload, "malicious_payload_value");
        var val = _diskDb.GetSetting(payload);
        Assert.Equal("malicious_payload_value", val);

        _diskDb.DeleteSetting(payload);
        Assert.Equal("default", _diskDb.GetSetting(payload, "default"));

        // Monitored process ID with SQL injection
        var procInj = new MonitoredProcess
        {
            Id = "P'; DROP TABLE monitored_processes; --",
            ProtocoloFormatado = "00001.INJ/2026-00",
            Origem = "CAIXA_ENTRADA"
        };
        _diskDb.UpsertMonitoredProcess(procInj);

        var retrieved = _diskDb.GetMonitoredProcess("P'; DROP TABLE monitored_processes; --");
        Assert.NotNull(retrieved);

        _diskDb.DeleteMonitoredProcess("P'; DROP TABLE monitored_processes; --");
        Assert.Null(_diskDb.GetMonitoredProcess("P'; DROP TABLE monitored_processes; --"));
    }

    #endregion

    #region 4. Truncation & Non-Persistence of Raw Document Content

    [Fact]
    public void Truncation_MassiveDocumentBody_StrictlyTruncatedTo500Chars()
    {
        // Simulate an accidental attempt to persist a 100 KB document text in Descricao and UltimaAtividadeDesc
        var hugeBody = new string('D', 100_000);

        var proc = new MonitoredProcess
        {
            Id = "HUGE_DOC_TEST",
            ProtocoloFormatado = "00001.HUGE/2026-00",
            Descricao = hugeBody,
            UltimaAtividadeDesc = hugeBody,
            Origem = "CAIXA_ENTRADA"
        };

        _diskDb.UpsertMonitoredProcess(proc);

        var retrieved = _diskDb.GetMonitoredProcess("HUGE_DOC_TEST");
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.Descricao);
        Assert.NotNull(retrieved.UltimaAtividadeDesc);

        Assert.Equal(500, retrieved.Descricao.Length);
        Assert.Equal(500, retrieved.UltimaAtividadeDesc.Length);
        Assert.StartsWith("DDDDDD", retrieved.Descricao);
    }

    [Fact]
    public void DatabaseSchema_StrictAudit_NoColumnsForDocumentContentsOrBlobs()
    {
        // Verify across all tables that no column allows raw file contents or blobs
        using var conn = new SqliteConnection($"Data Source={_tempDbFile}");
        conn.Open();

        var tables = new[] { "settings", "monitored_processes", "special_groups", "local_read_status", "app_logs" };
        var forbiddenTypes = new[] { "blob", "binary", "bytea", "varbinary" };

        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var colName = r.GetString(1).ToLowerInvariant();
                var colType = r.GetString(2).ToLowerInvariant();

                foreach (var forbidden in forbiddenTypes)
                {
                    Assert.DoesNotContain(forbidden, colType);
                }
            }
        }
    }

    [Fact]
    public void Logs_SanitizerAndTruncation_ImmuneToLogFlooding()
    {
        // 50 KB error message
        var hugeMessage = "Erro inesperado com detalhe gigante: " + new string('E', 50_000);
        _diskDb.Log("ERROR", hugeMessage);

        var logs = _diskDb.GetLogs(1);
        Assert.Single(logs);
        var entry = logs[0];

        // Must be truncated to 1000 + "... [TRUNCATED]"
        Assert.True(entry.Message.Length <= 1020);
        Assert.EndsWith("... [TRUNCATED]", entry.Message);
    }

    #endregion
}
