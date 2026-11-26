using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

[SupportedOSPlatform("windows")]
public class DatabaseTests : IDisposable
{
    private readonly Database _db;

    public DatabaseTests()
    {
        _db = new Database(":memory:");
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public void InitializeDatabase_CreatesAllTablesAndIndices_Successfully()
    {
        // Act: recuperar tabelas
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = new SqliteConnection(GetConnectionString(_db)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // Assert: 5 tabelas obrigatórias
        Assert.Contains("settings", tables);
        Assert.Contains("monitored_processes", tables);
        Assert.Contains("special_groups", tables);
        Assert.Contains("local_read_status", tables);
        Assert.Contains("app_logs", tables);

        // Act: recuperar índices
        var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = new SqliteConnection(GetConnectionString(_db)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                indices.Add(reader.GetString(0));
            }
        }

        Assert.Contains("idx_monitored_origem", indices);
        Assert.Contains("idx_monitored_grupo", indices);
        Assert.Contains("idx_monitored_destinado", indices);
        Assert.Contains("idx_monitored_aberto", indices);
        Assert.Contains("idx_monitored_data", indices);
        Assert.Contains("idx_monitored_updated", indices);
        Assert.Contains("idx_local_read_status_is_read", indices);
        Assert.Contains("idx_logs_created", indices);
        Assert.Contains("idx_logs_level", indices);
    }

    [Fact]
    public void Settings_CrudOperations_WorkCorrectly()
    {
        // 1. Padrão
        var def = _db.GetSetting("chave_inexistente", "padrao");
        Assert.Equal("padrao", def);

        // 2. Set & Get
        _db.SetSetting("PollingInterval", "10");
        Assert.Equal("10", _db.GetSetting("PollingInterval"));

        // 3. Update (ON CONFLICT DO UPDATE)
        _db.SetSetting("PollingInterval", "15");
        Assert.Equal("15", _db.GetSetting("PollingInterval"));

        // 4. GetAllSettings
        _db.SetSetting("OutraChave", "Valor2");
        var all = _db.GetAllSettings();
        Assert.Equal("15", all["PollingInterval"]);
        Assert.Equal("Valor2", all["OutraChave"]);

        // 5. Delete
        var deleted = _db.DeleteSetting("PollingInterval");
        Assert.True(deleted);
        Assert.Equal("default", _db.GetSetting("PollingInterval", "default"));
    }

    [Fact]
    public void MonitoredProcesses_UpsertAndRetrieve_MaintainsIntegrity()
    {
        var proc = new MonitoredProcess
        {
            Id = "1001",
            ProtocoloFormatado = "00001.000001/2026-00",
            Descricao = "Processo de Teste Inicial",
            TipoProcesso = "Licitação",
            Origem = "CAIXA_ENTRADA",
            TotalDocumentos = 5,
            UltimaAtividadeData = "2026-10-30 10:00:00",
            UltimaAtividadeDesc = "Recebido na unidade",
            UltimoUsuarioNome = "joao.silva",
            AbertoPorTerceiro = true,
            DestinadoAoUsuario = false
        };

        _db.UpsertMonitoredProcess(proc);

        var retrieved = _db.GetMonitoredProcess("1001");
        Assert.NotNull(retrieved);
        Assert.Equal("1001", retrieved.Id);
        Assert.Equal("00001.000001/2026-00", retrieved.ProtocoloFormatado);
        Assert.Equal("Processo de Teste Inicial", retrieved.Descricao);
        Assert.Equal(5, retrieved.TotalDocumentos);
        Assert.True(retrieved.AbertoPorTerceiro);
        Assert.False(retrieved.DestinadoAoUsuario);
        Assert.False(retrieved.IsReadLocally);

        // Atualização
        proc.TotalDocumentos = 6;
        proc.DestinadoAoUsuario = true;
        _db.UpsertMonitoredProcess(proc);

        var updated = _db.GetMonitoredProcess("1001");
        Assert.NotNull(updated);
        Assert.Equal(6, updated.TotalDocumentos);
        Assert.True(updated.DestinadoAoUsuario);
    }

    [Fact]
    public void ConstitutionalRule_NeverPersistsRawDocumentContent()
    {
        // 1. Auditar colunas da tabela monitored_processes
        var forbiddenWords = new[] { "conteudo", "content", "corpo", "body", "raw", "anexo", "arquivo", "binary", "blob", "html" };
        using (var conn = new SqliteConnection(GetConnectionString(_db)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(monitored_processes)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var colName = r.GetString(1).ToLowerInvariant();
                foreach (var forbidden in forbiddenWords)
                {
                    Assert.DoesNotContain(forbidden, colName);
                }
            }
        }

        // 2. Trava de truncamento defensivo a 500 caracteres
        var longText = new string('X', 1200);
        var proc = new MonitoredProcess
        {
            Id = "9999",
            ProtocoloFormatado = "00009.999999/2026-99",
            Descricao = longText,
            UltimaAtividadeDesc = longText,
            Origem = "CAIXA_ENTRADA"
        };

        _db.UpsertMonitoredProcess(proc);

        var saved = _db.GetMonitoredProcess("9999");
        Assert.NotNull(saved);
        Assert.NotNull(saved.Descricao);
        Assert.NotNull(saved.UltimaAtividadeDesc);
        Assert.True(saved.Descricao.Length <= 500, "Descricao excede 500 caracteres");
        Assert.True(saved.UltimaAtividadeDesc.Length <= 500, "UltimaAtividadeDesc excede 500 caracteres");
    }

    [Fact]
    public void LocalReadStatus_DefaultsToUnread_AndUpdatesOnUserAction()
    {
        var proc = new MonitoredProcess
        {
            Id = "2001",
            ProtocoloFormatado = "00002.000001/2026-00",
            Origem = "CAIXA_ENTRADA",
            TotalDocumentos = 3
        };
        _db.UpsertMonitoredProcess(proc);

        // Não lido por padrão
        Assert.False(_db.GetLocalReadStatus("2001"));
        var loaded = _db.GetMonitoredProcess("2001");
        Assert.NotNull(loaded);
        Assert.False(loaded.IsReadLocally);

        // Marcar como lido
        _db.SetLocalReadStatus("2001", true, docCount: 3);
        Assert.True(_db.GetLocalReadStatus("2001"));
        var loadedRead = _db.GetMonitoredProcess("2001");
        Assert.NotNull(loadedRead);
        Assert.True(loadedRead.IsReadLocally);

        // Desmarcar
        _db.SetLocalReadStatus("2001", false);
        Assert.False(_db.GetLocalReadStatus("2001"));
    }

    [Fact]
    public void ListMonitoredProcesses_FiltersAndSortsCorrectly()
    {
        var p1 = new MonitoredProcess
        {
            Id = "P1",
            ProtocoloFormatado = "0001/2026",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = false,
            UltimaAtividadeData = "2026-10-30 08:00:00"
        };
        var p2 = new MonitoredProcess
        {
            Id = "P2",
            ProtocoloFormatado = "0002/2026",
            Origem = "ACOMPANHAMENTO",
            IdGrupoEspecial = "GRP_CONTRATOS",
            DestinadoAoUsuario = false,
            UltimaAtividadeData = "2026-10-30 09:00:00"
        };
        var p3 = new MonitoredProcess
        {
            Id = "P3",
            ProtocoloFormatado = "0003/2026",
            Origem = "CAIXA_ENTRADA",
            DestinadoAoUsuario = true, // Maior prioridade
            UltimaAtividadeData = "2026-10-30 07:00:00"
        };

        _db.UpsertMonitoredProcess(p1);
        _db.UpsertMonitoredProcess(p2);
        _db.UpsertMonitoredProcess(p3);

        // Filtro por origem: CAIXA_ENTRADA (deve retornar P3 e P1, com P3 em primeiro devido a DestinadoAoUsuario)
        var caixa = _db.ListMonitoredProcesses(origem: "CAIXA_ENTRADA");
        Assert.Equal(2, caixa.Count);
        Assert.Equal("P3", caixa[0].Id);
        Assert.Equal("P1", caixa[1].Id);

        // Filtro por grupo especial
        var acompanhamento = _db.ListMonitoredProcesses(idGrupo: "GRP_CONTRATOS");
        Assert.Single(acompanhamento);
        Assert.Equal("P2", acompanhamento[0].Id);
    }

    [Fact]
    public void DeleteMonitoredProcess_RemovesFromBothTables()
    {
        var proc = new MonitoredProcess
        {
            Id = "DEL1",
            ProtocoloFormatado = "DEL-01",
            Origem = "CAIXA_ENTRADA"
        };
        _db.UpsertMonitoredProcess(proc);
        _db.SetLocalReadStatus("DEL1", true);

        Assert.NotNull(_db.GetMonitoredProcess("DEL1"));
        Assert.True(_db.GetLocalReadStatus("DEL1"));

        var deleted = _db.DeleteMonitoredProcess("DEL1");
        Assert.True(deleted);

        Assert.Null(_db.GetMonitoredProcess("DEL1"));
        Assert.False(_db.GetLocalReadStatus("DEL1"));
    }

    [Fact]
    public void SpecialGroups_UpsertAndList_WorksCorrectly()
    {
        var g1 = new SpecialGroup { Id = "G1", Nome = "Licitações", TotalProcessos = 12 };
        var g2 = new SpecialGroup { Id = "G2", Nome = "Contratos", TotalProcessos = 4 };

        _db.UpsertSpecialGroup(g1);
        _db.UpsertSpecialGroup(g2);

        var list = _db.ListSpecialGroups();
        Assert.Equal(2, list.Count);
        // Ordenado por Nome ASC
        Assert.Equal("G2", list[0].Id); // Contratos
        Assert.Equal("G1", list[1].Id); // Licitações
        Assert.Equal(12, list[1].TotalProcessos);
    }

    [Fact]
    public void AppLogs_Sanitization_PreventsSolrCredentialLeakage()
    {
        const string rawSolrError = "Erro Solr: http://sei_admin:SuperSecretPass99@10.0.1.50:8983/solr/processos/select?q=teste";
        const string rawStackTrace = "at mod_wssei.SolrClient.Search(exception e)";

        _db.Log("ERROR", rawSolrError, rawStackTrace);

        var logs = _db.GetLogs(1);
        Assert.Single(logs);
        var entry = logs[0];

        Assert.Equal("ERROR", entry.Level);
        Assert.DoesNotContain("SuperSecretPass99", entry.Message);
        Assert.DoesNotContain("sei_admin", entry.Message);
        Assert.DoesNotContain("10.0.1.50:8983", entry.Message);
        Assert.Contains("SANITIZED", entry.Message);
    }

    [Fact]
    public void AppLogs_SanitizesSensitiveParametersAndUrls()
    {
        const string msg = "Falha ao chamar https://usuario:senha123@api.com/v1 | Parametros: token=xyz123456&senha=minhasenha";
        _db.Log("WARN", msg);

        var logs = _db.GetLogs(1);
        Assert.Single(logs);
        var entry = logs[0];

        Assert.DoesNotContain("senha123", entry.Message);
        Assert.DoesNotContain("xyz123456", entry.Message);
        Assert.DoesNotContain("minhasenha", entry.Message);
        Assert.Contains("[REDACTED_URL]", entry.Message);
        Assert.Contains("token=[REDACTED]", entry.Message);
    }

    [Fact]
    public void AppLogs_NeverThrows_OnNullOrDatabaseError()
    {
        // Não deve estourar exceção
        _db.Log(null!, null!, null!);
        _db.ClearLogs();
        var emptyLogs = _db.GetLogs();
        Assert.Empty(emptyLogs);
    }

    private static string GetConnectionString(Database db)
    {
        var field = typeof(Database).GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)field!.GetValue(db)!;
    }
}
