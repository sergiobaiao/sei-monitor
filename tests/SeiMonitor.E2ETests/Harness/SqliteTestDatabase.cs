using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SeiMonitor.E2ETests.Harness;

public class SqliteTestDatabase : IDisposable
{
    public string ConnectionString { get; }
    public string? FilePath { get; }
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase(bool inMemory = true)
    {
        if (inMemory)
        {
            ConnectionString = "Data Source=:memory:;Cache=Shared";
            FilePath = null;
        }
        else
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"sei_test_{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={FilePath}";
        }

        _connection = new SqliteConnection(ConnectionString);
        _connection.Open();
    }

    private readonly object _lock = new();

    public void InitializeSchema()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
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
                    created_at TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
    }

    public List<string> GetTableNames()
    {
        lock (_lock)
        {
            var tables = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
            return tables;
        }
    }

    public List<string> GetColumnsForTable(string tableName)
    {
        lock (_lock)
        {
            var columns = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1)); // column name
            }
            return columns;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO settings (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = @value;
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key, string? defaultValue = null)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = @key;";
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToString(result) : defaultValue;
        }
    }

    public void UpsertMonitoredProcess(ProcessoModel proc)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO monitored_processes (
                    id, protocolo_formatado, descricao, tipo_processo, origem,
                    id_grupo_especial, total_documentos, ultima_atividade_data,
                    ultima_atividade_desc, ultimo_usuario_nome, aberto_por_terceiro,
                    destinado_ao_usuario, updated_at
                ) VALUES (
                    @id, @proto, @desc, @tipo, @origem,
                    @grupo, @total, @data,
                    @descAtiv, @usuario, @aberto,
                    @destinado, @updated
                )
                ON CONFLICT(id) DO UPDATE SET
                    protocolo_formatado = @proto,
                    descricao = @desc,
                    tipo_processo = @tipo,
                    origem = @origem,
                    id_grupo_especial = @grupo,
                    total_documentos = @total,
                    ultima_atividade_data = @data,
                    ultima_atividade_desc = @descAtiv,
                    ultimo_usuario_nome = @usuario,
                    aberto_por_terceiro = @aberto,
                    destinado_ao_usuario = @destinado,
                    updated_at = @updated;
            ";
            cmd.Parameters.AddWithValue("@id", proc.Id);
            cmd.Parameters.AddWithValue("@proto", proc.ProtocoloFormatado);
            cmd.Parameters.AddWithValue("@desc", (object?)proc.Descricao ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tipo", (object?)proc.TipoProcesso ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@origem", proc.Origem);
            cmd.Parameters.AddWithValue("@grupo", (object?)proc.IdGrupoEspecial ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@total", proc.TotalDocumentos);
            cmd.Parameters.AddWithValue("@data", (object?)proc.UltimaAtividadeData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@descAtiv", (object?)proc.UltimaAtividadeDesc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@usuario", (object?)proc.UltimoUsuarioNome ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aberto", proc.AbertoPorTerceiro ? 1 : 0);
            cmd.Parameters.AddWithValue("@destinado", proc.DestinadoAoUsuario ? 1 : 0);
            cmd.Parameters.AddWithValue("@updated", proc.UpdatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public ProcessoModel? GetMonitoredProcess(string id)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, protocolo_formatado, descricao, tipo_processo, origem, id_grupo_especial, total_documentos, ultima_atividade_data, ultima_atividade_desc, ultimo_usuario_nome, aberto_por_terceiro, destinado_ao_usuario, updated_at FROM monitored_processes WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new ProcessoModel(
                Id: reader.GetString(0),
                ProtocoloFormatado: reader.GetString(1),
                Descricao: reader.IsDBNull(2) ? null : reader.GetString(2),
                TipoProcesso: reader.IsDBNull(3) ? null : reader.GetString(3),
                Origem: reader.GetString(4),
                IdGrupoEspecial: reader.IsDBNull(5) ? null : reader.GetString(5),
                TotalDocumentos: reader.GetInt32(6),
                UltimaAtividadeData: reader.IsDBNull(7) ? null : reader.GetString(7),
                UltimaAtividadeDesc: reader.IsDBNull(8) ? null : reader.GetString(8),
                UltimoUsuarioNome: reader.IsDBNull(9) ? null : reader.GetString(9),
                AbertoPorTerceiro: reader.GetInt32(10) == 1,
                DestinadoAoUsuario: reader.GetInt32(11) == 1,
                UpdatedAt: reader.GetString(12)
            );
        }
    }

    public void SetLocalReadStatus(string processoId, bool isRead, int lastReadDocCount = 0, string? lastReadAt = null)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO local_read_status (processo_id, is_read_locally, last_read_doc_count, last_read_at)
                VALUES (@id, @isRead, @docCount, @readAt)
                ON CONFLICT(processo_id) DO UPDATE SET
                    is_read_locally = @isRead,
                    last_read_doc_count = @docCount,
                    last_read_at = @readAt;
            ";
            cmd.Parameters.AddWithValue("@id", processoId);
            cmd.Parameters.AddWithValue("@isRead", isRead ? 1 : 0);
            cmd.Parameters.AddWithValue("@docCount", lastReadDocCount);
            cmd.Parameters.AddWithValue("@readAt", (object?)lastReadAt ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public LocalReadStatusModel? GetLocalReadStatus(string processoId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT processo_id, is_read_locally, last_read_doc_count, last_read_at FROM local_read_status WHERE processo_id = @id;";
            cmd.Parameters.AddWithValue("@id", processoId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new LocalReadStatusModel(
                ProcessoId: reader.GetString(0),
                IsReadLocally: reader.GetInt32(1) == 1,
                LastReadDocCount: reader.GetInt32(2),
                LastReadAt: reader.IsDBNull(3) ? null : reader.GetString(3)
            );
        }
    }

    public void Log(string level, string message)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO app_logs (level, message, created_at) VALUES (@lvl, @msg, @time);";
            cmd.Parameters.AddWithValue("@lvl", level);
            cmd.Parameters.AddWithValue("@msg", message);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public List<LogEntryModel> GetAllLogs()
    {
        lock (_lock)
        {
            var logs = new List<LogEntryModel>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, level, message, created_at FROM app_logs ORDER BY id ASC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                logs.Add(new LogEntryModel(
                    Id: reader.GetInt64(0),
                    Level: reader.GetString(1),
                    Message: reader.GetString(2),
                    CreatedAt: reader.GetString(3)
                ));
            }
            return logs;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Close();
            _connection.Dispose();
            if (FilePath != null && File.Exists(FilePath))
            {
                try { File.Delete(FilePath); } catch { }
            }
        }
    }
}
