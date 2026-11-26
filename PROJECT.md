# Project: SEI Monitor (.NET 10 / C# / WPF)

## Architecture
- **Camada Data / Storage**: SQLite via `Microsoft.Data.Sqlite` (`Core/Database.cs`) gerenciando 5 tabelas (`settings`, `monitored_processes`, `special_groups`, `local_read_status`, `app_logs`).
- **Camada Security**: `Core/CredentialStore.cs` via P/Invoke nativo com `Advapi32.dll` (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`), target `SEI Monitor`, isolamento em UTF-16LE.
- **Camada Integration / HTTP**: `Core/SeiClient.cs` com sessão lazy com `SemaphoreSlim(1,1)`, injeção dos headers `unidade` e `token`, renovação no 401 via WSSEI v2 (primário) e API SEAD (fallback). Sanitização preventiva anti-vazamento Solr suprimindo `mensagem` e `exception`.
- **Camada Business Logic / Core**: `Core/Config.cs`, `Core/TextAnalyzer.cs` (heurísticas de `AbertoPorTerceiro` e `DestinadoAoUsuario`), `Core/SeiPoller.cs` (verificação leve periódica com `limit=1` comparando contagem e timestamps com o SQLite).
- **Camada UI / Shell Desktop**:
  - `Ui/FenceWindow.xaml` / `.cs`: translúcida, ancorada ao desktop via `Progman` (`GWLP_HWNDPARENT`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, `HWND_BOTTOM`, timer de 1s para reconexão do Explorer, sobrevivência a `Win + D`).
  - `Ui/TaskbarWidgetWindow.xaml` / `.cs` e `Ui/TaskbarFlyoutWindow.xaml` / `.cs`: widget de taskbar com flyout ancorado e fechamento em `Deactivated`.
  - `Ui/MainWindow.xaml` / `.cs` e `Ui/DetailWindow.xaml` / `.cs`: painel de controle com abas completas e detalhes do processo.
- **Camada Lifecycle / IPC**: `App.xaml.cs` com Mutex `Local\SEI_Monitor_Desktop`, IPC `EventWaitHandle(Local\SEI_Monitor_Show)`, `Forms.NotifyIcon` na bandeja.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Setup Projeto .NET 10 | SeiMonitor.csproj configurado para net10.0-windows, UseWPF, UseWindowsForms, app.manifest PerMonitorV2 | M1 | survey |
| 2 | Banco SQLite & Migrações | Criação e migração das 5 tabelas (settings, monitored_processes, special_groups, local_read_status, app_logs) | M1 | survey |
| 3 | CredentialStore Advapi32 | Persistência segura no Windows Credential Manager via P/Invoke (CredReadW, CredWriteW) | M1 | survey |
| 4 | Configurações Reativas | Core/Config.cs sincronizado com settings (polling interval, keywords, unidade, endpoints) | M1 | survey |
| 5 | Projeto de Testes Unitários | SeiMonitor.Tests.csproj configurado para net10.0 com xUnit | M1 | survey |
| 6 | Autenticação Lazy SEI | Login preguiçoso em SeiClient com SemaphoreSlim(1,1) e cache de token | M2 | survey |
| 7 | Injeção Headers SEI | Headers obrigatórios 'unidade' e 'token' em todas as chamadas WSSEI v2 | M2 | survey |
| 8 | Renovação Token 401 | Interceptação de HTTP 401, renovação automática de token e re-tentativa imediata | M2 | survey |
| 9 | Fallback API SEAD | Chaveamento para API da SEAD em caso de indisponibilidade da WSSEI v2 | M2 | survey |
| 10 | Sanitização Erro Solr | Descarte incondicional dos campos 'mensagem' e 'exception' em falhas do WSSEI | M2 | survey |
| 11 | Heurística AbertoPorTerceiro | Detecção de abertura/recebimento por colega sem despacho conclusivo no TextAnalyzer | M2 | survey |
| 12 | Heurística DestinadoAoUsuario | Verificação de keywords do usuário no título, especificação e último despacho | M2 | survey |
| 13 | Polling Leve (limit=1) | Consulta com limit=1 de docs e andamentos para checagem rápida de contagem e data | M3 | survey |
| 14 | Cache Local SQLite | Comparação de total e timestamp com monitored_processes antes de requisitar detalhes | M3 | survey |
| 15 | Status Leitura Local | Controle independente de IsReadLocallyByMe na tabela local_read_status | M3 | survey |
| 16 | Poller Periódico em Background | Timer de polling desacoplado da UI com intervalo configurável e disparo manual | M3 | survey |
| 17 | Desktop Fence Window | Janela WPF translúcida estilo Stardock Fences redimensionável e arrastável | M4 | survey |
| 18 | Ancoragem Win32 Progman | Owner=Progman via GWLP_HWNDPARENT, WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE, HWND_BOTTOM | M4 | survey |
| 19 | Sobrevivência Win+D | Widget permanece no desktop sem ser minimizado e sem quebrar o Win+D do usuário | M4 | survey |
| 20 | Watchdog Explorer 1s | Timer de 1s que detecta novo HWND do Progman em caso de crash/restart do Explorer | M4 | survey |
| 21 | Taskbar Widget Window | Mini-widget fixado sobre a barra de tarefas (ABM_GETTASKBARPOS), push de notificações com ticker / letreiro deslizante horizontal suave das últimas atualizações (pausa ao mouse hover/click) e abertura do Flyout | M4 | survey + user update |
| 22 | Taskbar Flyout Window | Janela flyout suspensa estilo Windows Widgets ancorada à barra de tarefas | M4 | survey |
| 23 | Auto-Close Flyout Deactivated | Fechamento imediato do flyout ao transferir foco para outra aplicação | M4 | survey |
| 24 | Mutex Instância Única | Controle via Mutex Local\SEI_Monitor_Desktop impedindo processos duplicados | M5 | survey |
| 25 | IPC Restaurar Janela | EventWaitHandle Local\SEI_Monitor_Show para focar instância ativa ao tentar 2ª abertura | M5 | survey |
| 26 | Bandeja do Sistema (NotifyIcon) | Ícone na barra com Menu: Abrir, Sincronizar Agora, Alternar Widget, Sair | M5 | survey |
| 27 | MainWindow com Abas | Abas: Visão Geral, Caixa de Entrada GINFO, Acompanhamento Especial, Config, Logs | M5 | survey |
| 28 | DetailWindow do Processo | Janela com metadados do processo, lista de andamentos e documentos | M5 | survey |
| 29 | E2E Test Suite Tiers 1-4 | Cobertura integral das 28 features (Feature, Boundary, Combinatorial, Scenarios) | M6 | survey |
| 30 | Adversarial Hardening Tier 5 | Testes adversariais white-box contra edge cases e estresse concorrente | M6 | survey |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Setup Projeto, SQLite & Credenciais | SeiMonitor.csproj, app.manifest, Core/Database.cs, Core/CredentialStore.cs, Core/Config.cs, testes unitários de banco e credenciais | none | DONE |
| M2 | Integração SEI Resiliente & TextAnalyzer | Core/SeiClient.cs (lazy auth, 401 renewal, fallback SEAD, sanitização Solr), Core/TextAnalyzer.cs (AbertoPorTerceiro, DestinadoAoUsuario), testes de integração e mocks (485 testes automatizados aprovados) | M1 | DONE |
| M3 | Motor de Polling Leve & Cache Local | Core/SeiPoller.cs (limit=1, cache SQLite, local_read_status, background timer, eventos de UI), testes do poller e estresse (519 testes automatizados aprovados) | M1, M2 | DONE |
| M4 | Shell Desktop Fence & Taskbar Widget com Flyout | Ui/FenceWindow.xaml/.cs (Win32 Progman pinning, HWND_BOTTOM, Win+D, 1s watchdog), Ui/TaskbarWidgetWindow.xaml/.cs (ticker horizontal contínuo com push de atualizações, pausa em hover/click), Ui/TaskbarFlyoutWindow.xaml/.cs (Deactivated auto-close, 615 testes aprovados) | M1, M3 | DONE |
| M5 | Painel Principal, Bandeja do Sistema & Instância Única | App.xaml/.cs (Mutex Local\SEI_Monitor_Desktop, IPC EventWaitHandle, NotifyIcon), Ui/MainWindow.xaml/.cs, Ui/DetailWindow.xaml/.cs | M1, M2, M3, M4 | DONE |
| M6 | Final: E2E Test Suite Pass (Tiers 1-4) & Adversarial Hardening (Tier 5) | Validação 100% de testes E2E opacos e ciclo adversarial de hardening com Challengers (681 testes automatizados aprovados) | M1-M5, TEST_READY.md | DONE |

## Interface Contracts
### Core/Database.cs ↔ Core/SeiPoller.cs & Core/Config.cs
- `void InitializeDatabase()`: DDL com 5 tabelas e índices.
- `Setting GetSetting(string key, string defaultValue)` / `void SetSetting(string key, string value)`.
- `void UpsertMonitoredProcess(MonitoredProcess proc)`: nunca salva corpo bruto de documentos.
- `MonitoredProcess? GetMonitoredProcess(string idProcedimento)`.
- `void SetLocalReadStatus(string idProcedimento, bool isRead)`.
- `bool GetLocalReadStatus(string idProcedimento)`.
- `void Log(string level, string message, string? stackTrace = null)`: estritamente sanitizado sem credenciais ou URLs Solr.

### Core/CredentialStore.cs ↔ Core/Config.cs & Ui/MainWindow.xaml.cs
- `bool SaveCredential(string target, string username, string password)`.
- `(string? username, string? password) LoadCredential(string target)`.
- `bool DeleteCredential(string target)`.

### Core/SeiClient.cs ↔ Core/SeiPoller.cs
- `Task<SeiAuthResult> AuthenticateAsync(bool forceRenew = false)`.
- `Task<ProcessListResult> SearchProcessesAsync(string unidade, int start = 0, int limit = 50)`.
- `Task<LightProcessSummary> GetLightSummaryAsync(string idProcedimento, string unidade)`: executa `documento/listar/{id}?limit=1` e `atividade/listar?limit=1`.
- `Task<ProcessDetail> GetProcessDetailsAsync(string idProcedimento, string unidade)`.
- Exceção tratada: `SeiException` com `ErrorCode` seguro (sem mensagem crua do Solr).

### Core/TextAnalyzer.cs ↔ Core/SeiPoller.cs
- `bool IsOpenedByThirdParty(IEnumerable<SeiActivity> activities, string currentUserName)`.
- `bool IsDestinedToUser(SeiProcess process, IEnumerable<string> userKeywords)`.

### Shell / App ↔ Ui
- `SingleInstance.Initialize()`: `Mutex(true, @"Local\SEI_Monitor_Desktop", out bool isFirstInstance)`.
- `SingleInstance.WaitForShowSignal(Action onShow)`: `EventWaitHandle(@"Local\SEI_Monitor_Show")`.
- `SingleInstance.SignalExistingInstance()`: sinaliza e encerra.

## Code Layout
```
src/
  SeiMonitor/
    SeiMonitor.csproj
    app.manifest
    App.xaml
    App.xaml.cs
    Core/
      Database.cs
      CredentialStore.cs
      Config.cs
      SeiClient.cs
      SeiPoller.cs
      TextAnalyzer.cs
      Models.cs
    Ui/
      FenceWindow.xaml
      FenceWindow.xaml.cs
      TaskbarWidgetWindow.xaml
      TaskbarWidgetWindow.xaml.cs
      TaskbarFlyoutWindow.xaml
      TaskbarFlyoutWindow.xaml.cs
      MainWindow.xaml
      MainWindow.xaml.cs
      DetailWindow.xaml
      DetailWindow.xaml.cs
      Theme.cs
tests/
  SeiMonitor.Tests/
    SeiMonitor.Tests.csproj
    DatabaseTests.cs
    CredentialStoreTests.cs
    SeiClientSanitizationTests.cs
    TextAnalyzerTests.cs
    SeiPollerLightSummaryTests.cs
  SeiMonitor.E2ETests/
    SeiMonitor.E2ETests.csproj
    Tier1FeatureCoverageTests.cs
    Tier2BoundaryTests.cs
    Tier3CombinatorialTests.cs
    Tier4ScenarioTests.cs
```
