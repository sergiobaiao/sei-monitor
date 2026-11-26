namespace SeiMonitor.Core;

public sealed class MonitoredProcess
{
    public string Id { get; set; } = string.Empty;
    public string ProtocoloFormatado { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    public string? TipoProcesso { get; set; }
    public string Origem { get; set; } = "CAIXA_ENTRADA"; // CAIXA_ENTRADA, ACOMPANHAMENTO, AMBOS
    public string? IdGrupoEspecial { get; set; }
    public int TotalDocumentos { get; set; }
    public string? UltimaAtividadeData { get; set; }
    public string? UltimaAtividadeDesc { get; set; }
    public string? UltimoUsuarioNome { get; set; }
    public bool AbertoPorTerceiro { get; set; }
    public bool DestinadoAoUsuario { get; set; }
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    // Preenchido via LEFT JOIN com local_read_status
    public bool IsReadLocally { get; set; }
}

public sealed class SpecialGroup
{
    public string Id { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public int TotalProcessos { get; set; }
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

public sealed class LogEntry
{
    public long Id { get; set; }
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class LocalReadStatus
{
    public string ProcessoId { get; set; } = string.Empty;
    public bool IsReadLocally { get; set; }
    public int LastReadDocCount { get; set; }
    public string? LastReadAt { get; set; }
}

public sealed class SeiActivity
{
    public string Id { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public string Hora { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string? Unidade { get; set; }
    public string? Usuario { get; set; }

    public SeiActivity() { }

    public SeiActivity(string id, string data, string hora, string descricao, string? unidade, string? usuario)
    {
        Id = id;
        Data = data;
        Hora = hora;
        Descricao = descricao;
        Unidade = unidade;
        Usuario = usuario;
    }
}

public sealed class SeiProcess
{
    public string IdProcedimento { get; set; } = string.Empty;
    public string ProtocoloFormatado { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    public string? TipoProcesso { get; set; }
    public string? UnidadeSigla { get; set; }
    public int? NivelAcessoGlobal { get; set; }
    public List<string> UnidadesAbertas { get; set; } = new();

    public SeiProcess() { }

    public SeiProcess(string idProcedimento, string protocoloFormatado, string? descricao = null, string? tipoProcesso = null, string? unidadeSigla = null, int? nivelAcessoGlobal = null, List<string>? unidadesAbertas = null)
    {
        IdProcedimento = idProcedimento;
        ProtocoloFormatado = protocoloFormatado;
        Descricao = descricao;
        TipoProcesso = tipoProcesso;
        UnidadeSigla = unidadeSigla;
        NivelAcessoGlobal = nivelAcessoGlobal;
        if (unidadesAbertas != null) UnidadesAbertas = unidadesAbertas;
    }
}

public sealed record SeiAuthResult(
    string Token,
    string SourceApi,
    DateTime AcquiredAt
);

public sealed record LightProcessSummary(
    string IdProcedimento,
    int TotalDocumentos,
    string? UltimaAtividadeData,
    string? UltimaAtividadeDesc,
    string? UltimoUsuarioNome,
    string ProtocoloFormatado
);

public sealed record SeiProcessItem(
    string IdProcedimento,
    string ProtocoloFormatado,
    string? Descricao,
    string? TipoProcesso,
    string? UnidadeSigla,
    int? NivelAcessoGlobal,
    List<string>? UnidadesAbertas
);

public sealed record ProcessListResult(
    int Total,
    List<SeiProcessItem> Items
);

public sealed record SeiDocumentItem(
    string Id,
    string ProtocoloFormatado,
    string NomeComposto,
    string Tipo,
    string MimeType,
    string TipoDocumento,
    string? IdUnidade,
    bool PodeVisualizar,
    bool Cancelado,
    bool Sigiloso,
    string? DataInclusao
);

public sealed record SeiActivityItem(
    string Id,
    string Data,
    string Hora,
    string Descricao,
    string? Unidade,
    string? Usuario
);

public sealed record ProcessDetail(
    string IdProcedimento,
    string ProtocoloFormatado,
    string? Descricao,
    string? TipoProcesso,
    string? UnidadeSigla,
    int NivelAcessoGlobal,
    List<string> UnidadesAbertas,
    List<SeiDocumentItem> Documentos,
    List<SeiActivityItem> Atividades
);

public enum SeiErrorCode
{
    Indisponivel,
    AcessoNegado,
    FalhaPesquisa,
    ProcessoNaoEncontrado,
    RespostaInvalida,
    NaoConfigurado
}

public class SeiException : Exception
{
    public SeiErrorCode ErrorCode { get; }
    public string SafeErrorCode { get; }

    public SeiException(string errorCode)
        : base(GetSafeDefaultMessage(FromSafeCodeString(errorCode)))
    {
        SafeErrorCode = errorCode;
        ErrorCode = FromSafeCodeString(errorCode);
    }

    public SeiException(string errorCode, Exception innerException)
        : base(GetSafeDefaultMessage(FromSafeCodeString(errorCode)), innerException)
    {
        SafeErrorCode = errorCode;
        ErrorCode = FromSafeCodeString(errorCode);
    }

    public SeiException(SeiErrorCode errorCode)
        : base(GetSafeDefaultMessage(errorCode))
    {
        ErrorCode = errorCode;
        SafeErrorCode = ToSafeCodeString(errorCode);
    }

    public SeiException(SeiErrorCode errorCode, string safeCustomMessage)
        : base(safeCustomMessage)
    {
        ErrorCode = errorCode;
        SafeErrorCode = ToSafeCodeString(errorCode);
    }

    public SeiException(SeiErrorCode errorCode, Exception innerException)
        : base(GetSafeDefaultMessage(errorCode), innerException)
    {
        ErrorCode = errorCode;
        SafeErrorCode = ToSafeCodeString(errorCode);
    }

    public static string ToSafeCodeString(SeiErrorCode code) => code switch
    {
        SeiErrorCode.Indisponivel => "SEI_INDISPONIVEL",
        SeiErrorCode.AcessoNegado => "SEI_ACESSO_NEGADO",
        SeiErrorCode.FalhaPesquisa => "SEI_FALHA_PESQUISA",
        SeiErrorCode.ProcessoNaoEncontrado => "PROCESSO_NAO_ENCONTRADO",
        SeiErrorCode.RespostaInvalida => "SEI_RESPOSTA_INVALIDA",
        SeiErrorCode.NaoConfigurado => "SEI_NAO_CONFIGURADO",
        _ => "SEI_INDISPONIVEL"
    };

    public static SeiErrorCode FromSafeCodeString(string code) => code switch
    {
        "SEI_INDISPONIVEL" => SeiErrorCode.Indisponivel,
        "SEI_ACESSO_NEGADO" => SeiErrorCode.AcessoNegado,
        "SEI_FALHA_PESQUISA" => SeiErrorCode.FalhaPesquisa,
        "PROCESSO_NAO_ENCONTRADO" => SeiErrorCode.ProcessoNaoEncontrado,
        "SEI_RESPOSTA_INVALIDA" => SeiErrorCode.RespostaInvalida,
        "SEI_NAO_CONFIGURADO" => SeiErrorCode.NaoConfigurado,
        _ => SeiErrorCode.Indisponivel
    };

    public static string GetSafeDefaultMessage(SeiErrorCode code) => code switch
    {
        SeiErrorCode.Indisponivel => "O serviço do SEI está temporariamente indisponível.",
        SeiErrorCode.AcessoNegado => "Acesso negado ao recurso ou unidade informada no SEI.",
        SeiErrorCode.FalhaPesquisa => "Falha na execução da pesquisa no motor de busca do SEI.",
        SeiErrorCode.ProcessoNaoEncontrado => "O processo solicitado não foi encontrado no SEI.",
        SeiErrorCode.RespostaInvalida => "A resposta retornada pela API do SEI possui formato inválido.",
        SeiErrorCode.NaoConfigurado => "As configurações ou credenciais de acesso ao SEI não foram informadas.",
        _ => "Erro na comunicação com o SEI."
    };

    public override string ToString()
    {
        return $"{GetType().FullName}: [{SafeErrorCode}] {Message}\n{StackTrace}";
    }
}

public enum UpdateType
{
    NewProcess,
    NewDocument,
    NewActivity,
    MetadataChanged,
    StatusChanged
}

public enum ProcessUpdateType
{
    NewProcess = 0,
    NewDocument = 1,
    NewActivity = 2,
    MetadataChanged = 3
}

public sealed class ProcessChangedEventArgs : EventArgs
{
    public MonitoredProcess Process { get; }
    public UpdateType Type { get; }
    public ProcessUpdateType ProcessType => (ProcessUpdateType)(int)Type;
    public int PreviousDocCount { get; }
    public int NewDocCount { get; }
    public string? PreviousActivityDate { get; }
    public string? NewActivityDate { get; }

    public ProcessChangedEventArgs(
        MonitoredProcess process,
        UpdateType type,
        int previousDocCount = 0,
        int newDocCount = 0,
        string? previousActivityDate = null,
        string? newActivityDate = null)
    {
        Process = process;
        Type = type;
        PreviousDocCount = previousDocCount;
        NewDocCount = newDocCount;
        PreviousActivityDate = previousActivityDate;
        NewActivityDate = newActivityDate;
    }

    public void Deconstruct(out MonitoredProcess process, out UpdateType type)
    {
        process = Process;
        type = Type;
    }
}

public sealed record SyncResult
{
    public int TotalChecked { get; init; }
    public int ChangedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int SkippedCount => UnchangedCount;
    public int NewDocumentsCount { get; init; }
    public int NewActivitiesCount { get; init; }
    public int NewProcessesCount { get; init; }
    public int ErrorsCount { get; init; }
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public SyncResult(
        int totalChecked = 0,
        int changedCount = 0,
        int unchangedCount = 0,
        int newDocumentsCount = 0,
        int newActivitiesCount = 0,
        int newProcessesCount = 0,
        int errorsCount = 0,
        DateTime? timestamp = null,
        bool success = true,
        string? errorMessage = null)
    {
        TotalChecked = totalChecked;
        ChangedCount = changedCount;
        UnchangedCount = unchangedCount;
        NewDocumentsCount = newDocumentsCount;
        NewActivitiesCount = newActivitiesCount;
        NewProcessesCount = newProcessesCount;
        ErrorsCount = errorsCount;
        Timestamp = timestamp ?? DateTime.UtcNow;
        Success = success;
        ErrorMessage = errorMessage;
    }

    public SyncResult(int totalChecked, int changedCount, int skippedCount, DateTime timestamp, bool success = true, string? errorMessage = null)
        : this(totalChecked, changedCount, unchangedCount: skippedCount, 0, 0, 0, 0, timestamp, success, errorMessage)
    {
    }

    public static implicit operator int(SyncResult r) => r.ChangedCount;
}

public sealed record SyncStatusEventArgs(
    bool IsSyncing,
    string? Message = null,
    int ChangedCount = 0,
    int TotalChecked = 0,
    DateTime? LastSyncTime = null
);

public interface ISeiPoller : IDisposable, IAsyncDisposable
{
    bool IsRunning { get; }
    DateTime? LastSyncTime { get; }
    SyncResult? LastSyncResult { get; }

    event Action<ProcessChangedEventArgs>? ProcessChanged;
    event Action<bool, string?>? SyncStatusChanged;
    event Action<SyncResult>? SyncCompleted;
    event Action<string>? NotificationPushed;
    event Action<string, string>? NotificationDetailPushed;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<SyncResult> PollOnceAsync(CancellationToken ct = default);
    Task<SyncResult> SyncNowAsync(CancellationToken ct = default);
}

