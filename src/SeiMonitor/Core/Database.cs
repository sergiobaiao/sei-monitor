using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SeiMonitor.Core;

/// <summary>
/// Camada de persistência local SQLite do SEI Monitor.
/// Gerencia as tabelas settings, special_groups, monitored_processes, local_read_status e app_logs.
/// Obedece estritamente às regras de privacidade (nunca armazena conteúdo de documentos ou segredos).
/// </summary>
public sealed class Database : IDisposable
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SEI Monitor", "data", "sei_monitor.db"
    );

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAliveConnection;

    public Database(string? customPath = null)
    {
        var path = customPath ?? DefaultPath;
        if (path == ":memory:")
        {
            _connectionString = $"Data Source=SeiMonitorMem_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }
        else
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                DefaultTimeout = 10,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        InitializeDatabase();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void InitializeDatabase()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
            );

            CREATE TABLE IF NOT EXISTS special_groups (
                id TEXT PRIMARY KEY,
                nome TEXT NOT NULL,
                total_processos INTEGER DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS monitored_processes (
                id TEXT PRIMARY KEY,
                protocolo_formatado TEXT NOT NULL,
                descricao TEXT,
                tipo_processo TEXT,
                origem TEXT NOT NULL,
                id_grupo_especial TEXT,
                total_documentos INTEGER DEFAULT 0,
                ultima_atividade_data TEXT,
                ultima_atividade_desc TEXT,
                ultimo_usuario_nome TEXT,
                aberto_por_terceiro INTEGER DEFAULT 0,
                destinado_ao_usuario INTEGER DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS local_read_status (
                processo_id TEXT PRIMARY KEY,
                is_read_locally INTEGER DEFAULT 0,
                last_read_doc_count INTEGER DEFAULT 0,
                last_read_at TEXT
            );

            CREATE TABLE IF NOT EXISTS app_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
            );

            CREATE INDEX IF NOT EXISTS idx_monitored_origem ON monitored_processes(origem);
            CREATE INDEX IF NOT EXISTS idx_monitored_grupo ON monitored_processes(id_grupo_especial);
            CREATE INDEX IF NOT EXISTS idx_monitored_destinado ON monitored_processes(destinado_ao_usuario);
            CREATE INDEX IF NOT EXISTS idx_monitored_aberto ON monitored_processes(aberto_por_terceiro);
            CREATE INDEX IF NOT EXISTS idx_monitored_data ON monitored_processes(ultima_atividade_data);
            CREATE INDEX IF NOT EXISTS idx_monitored_updated ON monitored_processes(updated_at);

            CREATE INDEX IF NOT EXISTS idx_local_read_status_is_read ON local_read_status(is_read_locally);

            CREATE INDEX IF NOT EXISTS idx_logs_created ON app_logs(created_at);
            CREATE INDEX IF NOT EXISTS idx_logs_level ON app_logs(level);
            """;
        cmd.ExecuteNonQuery();
    }

    // --- Settings CRUD ---
    public string GetSetting(string key, string defaultValue = "")
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result != null && result != DBNull.Value ? (string)result : defaultValue;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, datetime('now', 'localtime'))
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value ?? "");
        cmd.ExecuteNonQuery();
    }

    public bool DeleteSetting(string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    public Dictionary<string, string> GetAllSettings()
    {
        var dict = new Dictionary<string, string>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetString(1);
        }
        return dict;
    }

    // --- Monitored Processes CRUD ---
    public void UpsertMonitoredProcess(MonitoredProcess proc)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monitored_processes (
                id, protocolo_formatado, descricao, tipo_processo, origem, id_grupo_especial,
                total_documentos, ultima_atividade_data, ultima_atividade_desc, ultimo_usuario_nome,
                aberto_por_terceiro, destinado_ao_usuario, updated_at
            ) VALUES (
                $id, $proto, $desc, $tipo, $origem, $grupo,
                $totDocs, $data, $atDesc, $usuario,
                $aberto, $dest, $upAt
            )
            ON CONFLICT(id) DO UPDATE SET
                protocolo_formatado = excluded.protocolo_formatado,
                descricao = excluded.descricao,
                tipo_processo = excluded.tipo_processo,
                origem = excluded.origem,
                id_grupo_especial = excluded.id_grupo_especial,
                total_documentos = excluded.total_documentos,
                ultima_atividade_data = excluded.ultima_atividade_data,
                ultima_atividade_desc = excluded.ultima_atividade_desc,
                ultimo_usuario_nome = excluded.ultimo_usuario_nome,
                aberto_por_terceiro = excluded.aberto_por_terceiro,
                destinado_ao_usuario = excluded.destinado_ao_usuario,
                updated_at = excluded.updated_at
            """;

        // Defesa ativa inegociável: limita tamanho de campos para nunca persistir corpo de documento
        string? safeDesc = proc.Descricao != null && proc.Descricao.Length > 500
            ? proc.Descricao[..500] : proc.Descricao;
        string? safeAtDesc = proc.UltimaAtividadeDesc != null && proc.UltimaAtividadeDesc.Length > 500
            ? proc.UltimaAtividadeDesc[..500] : proc.UltimaAtividadeDesc;

        cmd.Parameters.AddWithValue("$id", proc.Id);
        cmd.Parameters.AddWithValue("$proto", proc.ProtocoloFormatado);
        cmd.Parameters.AddWithValue("$desc", (object?)safeDesc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tipo", (object?)proc.TipoProcesso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origem", proc.Origem);
        cmd.Parameters.AddWithValue("$grupo", (object?)proc.IdGrupoEspecial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totDocs", proc.TotalDocumentos);
        cmd.Parameters.AddWithValue("$data", (object?)proc.UltimaAtividadeData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$atDesc", (object?)safeAtDesc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$usuario", (object?)proc.UltimoUsuarioNome ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aberto", proc.AbertoPorTerceiro ? 1 : 0);
        cmd.Parameters.AddWithValue("$dest", proc.DestinadoAoUsuario ? 1 : 0);
        cmd.Parameters.AddWithValue("$upAt", proc.UpdatedAt ?? DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    public MonitoredProcess? GetMonitoredProcess(string idProcedimento)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.protocolo_formatado, p.descricao, p.tipo_processo, p.origem,
                   p.id_grupo_especial, p.total_documentos, p.ultima_atividade_data,
                   p.ultima_atividade_desc, p.ultimo_usuario_nome, p.aberto_por_terceiro,
                   p.destinado_ao_usuario, p.updated_at,
                   COALESCE(l.is_read_locally, 0) AS is_read_locally
            FROM monitored_processes p
            LEFT JOIN local_read_status l ON l.processo_id = p.id
            WHERE p.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", idProcedimento);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapMonitoredProcess(r);
    }

    public List<MonitoredProcess> ListMonitoredProcesses(string? origem = null, string? idGrupo = null)
    {
        var list = new List<MonitoredProcess>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(origem))
        {
            conditions.Add("(p.origem = $origem OR p.origem = 'AMBOS')");
            cmd.Parameters.AddWithValue("$origem", origem);
        }
        if (!string.IsNullOrEmpty(idGrupo))
        {
            conditions.Add("p.id_grupo_especial = $grupo");
            cmd.Parameters.AddWithValue("$grupo", idGrupo);
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT p.id, p.protocolo_formatado, p.descricao, p.tipo_processo, p.origem,
                   p.id_grupo_especial, p.total_documentos, p.ultima_atividade_data,
                   p.ultima_atividade_desc, p.ultimo_usuario_nome, p.aberto_por_terceiro,
                   p.destinado_ao_usuario, p.updated_at,
                   COALESCE(l.is_read_locally, 0) AS is_read_locally
            FROM monitored_processes p
            LEFT JOIN local_read_status l ON l.processo_id = p.id
            {whereClause}
            ORDER BY p.destinado_ao_usuario DESC, is_read_locally ASC, p.ultima_atividade_data DESC
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(MapMonitoredProcess(r));
        }
        return list;
    }

    public bool DeleteMonitoredProcess(string idProcedimento)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM monitored_processes WHERE id = $id; DELETE FROM local_read_status WHERE processo_id = $id;";
        cmd.Parameters.AddWithValue("$id", idProcedimento);
        return cmd.ExecuteNonQuery() > 0;
    }

    // --- Local Read Status ---
    public void SetLocalReadStatus(string idProcedimento, bool isRead, int docCount = 0)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_read_status (processo_id, is_read_locally, last_read_doc_count, last_read_at)
            VALUES ($id, $isRead, $count, datetime('now', 'localtime'))
            ON CONFLICT(processo_id) DO UPDATE SET
                is_read_locally = excluded.is_read_locally,
                last_read_doc_count = excluded.last_read_doc_count,
                last_read_at = excluded.last_read_at
            """;
        cmd.Parameters.AddWithValue("$id", idProcedimento);
        cmd.Parameters.AddWithValue("$isRead", isRead ? 1 : 0);
        cmd.Parameters.AddWithValue("$count", docCount);
        cmd.ExecuteNonQuery();
    }

    public bool GetLocalReadStatus(string idProcedimento)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_read_locally FROM local_read_status WHERE processo_id = $id";
        cmd.Parameters.AddWithValue("$id", idProcedimento);
        var res = cmd.ExecuteScalar();
        if (res == null || res == DBNull.Value) return false;
        return Convert.ToInt32(res) == 1;
    }

    // --- Special Groups CRUD ---
    public void UpsertSpecialGroup(SpecialGroup group)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO special_groups (id, nome, total_processos, updated_at)
            VALUES ($id, $nome, $tot, $upAt)
            ON CONFLICT(id) DO UPDATE SET
                nome = excluded.nome,
                total_processos = excluded.total_processos,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", group.Id);
        cmd.Parameters.AddWithValue("$nome", group.Nome);
        cmd.Parameters.AddWithValue("$tot", group.TotalProcessos);
        cmd.Parameters.AddWithValue("$upAt", group.UpdatedAt ?? DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<SpecialGroup> ListSpecialGroups()
    {
        var list = new List<SpecialGroup>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, nome, total_processos, updated_at FROM special_groups ORDER BY nome ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SpecialGroup
            {
                Id = r.GetString(0),
                Nome = r.GetString(1),
                TotalProcessos = r.GetInt32(2),
                UpdatedAt = r.GetString(3)
            });
        }
        return list;
    }

    // --- Logs Sanitizados ---
    public void Log(string level, string message, string? stackTrace = null)
    {
        try
        {
            var combined = string.IsNullOrEmpty(stackTrace) ? message : $"{message} | StackTrace: {stackTrace}";
            var sanitized = SanitizeLogMessage(combined);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO app_logs (level, message, created_at) VALUES ($lvl, $msg, datetime('now', 'localtime'))";
            cmd.Parameters.AddWithValue("$lvl", (level ?? "INFO").ToUpperInvariant());
            cmd.Parameters.AddWithValue("$msg", sanitized);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Logging nunca derruba o aplicativo
        }
    }

    public List<LogEntry> GetLogs(int limit = 100)
    {
        var list = new List<LogEntry>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, level, message, created_at FROM app_logs ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LogEntry
            {
                Id = r.GetInt64(0),
                Level = r.GetString(1),
                Message = r.GetString(2),
                CreatedAt = r.GetString(3)
            });
        }
        return list;
    }

    public void ClearLogs()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_logs";
        cmd.ExecuteNonQuery();
    }

    public static string SanitizeLogMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 1. Detectar e expurgar vazamentos do Solr com credenciais na URL
        if (text.Contains("solr", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("exception", StringComparison.OrdinalIgnoreCase) || text.Contains("http", StringComparison.OrdinalIgnoreCase)))
        {
            return "[SANITIZED: Falha na integração com motor de busca do SEI (Solr)]";
        }

        // 2. Ocultar credenciais em URLs: http://user:pass@host -> [REDACTED_URL]
        var sanitized = Regex.Replace(text, @"https?://[^:@\s]+:[^:@\s]+@[^\s]+", "[REDACTED_URL]");

        // 3. Ocultar parâmetros sensíveis comuns
        sanitized = Regex.Replace(sanitized, @"(?i)(senha|password|token|secret|authorization)=[^&\s]+", "$1=[REDACTED]");

        if (sanitized.Length > 1000)
        {
            sanitized = sanitized[..1000] + "... [TRUNCATED]";
        }

        return sanitized;
    }

    private static MonitoredProcess MapMonitoredProcess(SqliteDataReader r)
    {
        return new MonitoredProcess
        {
            Id = r.GetString(0),
            ProtocoloFormatado = r.GetString(1),
            Descricao = r.IsDBNull(2) ? null : r.GetString(2),
            TipoProcesso = r.IsDBNull(3) ? null : r.GetString(3),
            Origem = r.GetString(4),
            IdGrupoEspecial = r.IsDBNull(5) ? null : r.GetString(5),
            TotalDocumentos = r.GetInt32(6),
            UltimaAtividadeData = r.IsDBNull(7) ? null : r.GetString(7),
            UltimaAtividadeDesc = r.IsDBNull(8) ? null : r.GetString(8),
            UltimoUsuarioNome = r.IsDBNull(9) ? null : r.GetString(9),
            AbertoPorTerceiro = r.GetInt32(10) == 1,
            DestinadoAoUsuario = r.GetInt32(11) == 1,
            UpdatedAt = r.GetString(12),
            IsReadLocally = r.GetInt32(13) == 1
        };
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
    }
}
