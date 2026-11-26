# SEI Desktop Monitor

Monitor desktop inteligente de processos do **SEI! (Sistema Eletrônico de Informações)** desenvolvido em **.NET 10 (C# / WPF)** para Windows, integrando widget translúcido fixado na Área de Trabalho (estilo *Stardock Fences*), widget acoplado na Barra de Tarefas com letreiro digital contínuo (*ticker* estilo Notícias e Clima do Windows), proteção para caixas de entrada compartilhadas (ex.: GINFO) e motor de sincronização leve com cache SQLite local.

---

## 🚀 Principais Funcionalidades

### 1. Widget Fences na Área de Trabalho (`FenceWindow`)
- **Fixação Nativa ao Desktop**: Janela translúcida sem bordas ancorada diretamente ao `Progman` do Windows via P/Invoke (`GWLP_HWNDPARENT`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, `HWND_BOTTOM`).
- **Imunidade a `Win + D`**: Permanece estável na camada de fundo sem ser minimizada ao minimizar todas as janelas e sem interferir na restauração do desktop.
- **Watchdog do Windows Explorer**: Monitoramento de 1s para reconectar o HWND caso o `explorer.exe` reinicie.
- **Controle de Acompanhamento Especial**: Seletor Dropdown de Grupos no cabeçalho com atualização instantânea da lista e das métricas.
- **Pills de Status em Tempo Real**:
  - 📂 **MONITORADOS**: Contagem do grupo selecionado.
  - ⚡ **NOVOS ANDAMENTOS**: Processos com movimentações ou novos documentos não lidos.
  - 🎯 **PARA MIM**: Processos destacados pelas palavras-chave do usuário.
  - ⚠️ **ATENÇÃO GINFO**: Processos abertos por terceiros sem despacho posterior.
- **Persistência de Geometria**: Posição na tela, dimensões e estado recolhido (50px) salvos no SQLite.

### 2. Widget da Barra de Tarefas & Flyout (`TaskbarWidgetWindow` & `TaskbarFlyoutWindow`)
- **Acoplamento à Barra de Tarefas**: Janela compacta acoplada automaticamente à `Shell_TrayWnd` ao lado da bandeja do sistema (via `SHAppBarMessage` / `ABM_GETTASKBARPOS`).
- **Letreiro Digital Contínuo (Horizontal Scrolling Marquee)**: Ticker animado exibindo as últimas atualizações recebidas por push (*ex: "⚡ Novo processo recebido — 00001.000000/2026-00"*, *"📄 Novo documento juntado..."*, *"🎯 Processo destinado a você..."*).
- **Interação Suave**: Pausa automática na rolagem ao passar o mouse (`MouseEnter`) e restauração no `MouseLeave`.
- **Flyout Dashboard Estilo Windows Widgets**: Clique no widget abre painel suspenso ancorado logo acima da barra de tarefas, com saudação contextual, cards informativos e fechamento automático ao perder o foco (`Deactivated`).

### 3. Proteção da Caixa Compartilhada (GINFO)
- **Status de Leitura Local (`IsReadLocallyByMe`)**: Controle individual por usuário mantido no banco SQLite; processos abertos por colegas no SEI Web continuam marcados como pendentes para você até que você marque como lido.
- **Heurística de Aberto por Terceiro (`AbertoPorTerceiro`)**: Identifica quando outro servidor abriu/recebeu o processo na unidade compartilhada sem elaborar um despacho conclusivo.
- **Heurística de Destinatário (`DestinadoAoUsuario`)**: Varredura inteligente de palavras-chave (nome do servidor, matrículas ou termos cadastrados) no título, especificação e texto do último despacho.

### 4. Motor de Polling Leve & Cache Local
- **Consulta Econômica (`limit=1`)**: Leitura rápida da contagem de documentos e timestamp da última atividade (`documento/listar?limit=1` e `atividade/listar?limit=1`).
- **Comparação Inteligente de Cache**: Apenas busca a árvore detalhada de documentos se houver divergência entre o SEI e o cache do SQLite, reduzindo o tempo de consulta de segundos para milissegundos.
- **Resiliência e Fallback**: Conexão primária via WSSEI v2 com suporte a fallback automático para API SEAD (`api.sei.pi.gov.br`) e renovação transparente de sessão no HTTP 401.
- **Sanitização Preventiva Anti-Vazamento Solr**: Supressão obrigatória de campos `mensagem` e `exception` em erros do WSSEI para impedir exposição acidental de credenciais.

### 5. Segurança & Gerenciamento de Instância
- **Armazenamento Seguro de Credenciais**: Integração nativa com o Windows Credential Manager via `Advapi32.dll` (`CredWriteW`, `CredReadW`, `CredDeleteW`) com isolamento UTF-16LE e exclusão de arquivo de texto plano.
- **Instância Única & IPC**: Proteção contra múltiplas instâncias via Mutex `Local\SEI_Monitor_Desktop` e reativação da janela ativa via `EventWaitHandle(Local\SEI_Monitor_Show)`.
- **Bandeja do Sistema (`NotifyIcon`)**: Acesso rápido com menu de contexto: Abrir Painel, Sincronizar Agora, Alternar Widget e Sair.

---

## 📁 Estrutura do Projeto

```
sei-monitor/
├── src/
│   └── SeiMonitor/
│       ├── App.xaml / App.xaml.cs                 # Ciclo de vida, Mutex de instância única e NotifyIcon
│       ├── Core/
│       │   ├── Config.cs                          # Configurações reativas da aplicação
│       │   ├── CredentialStore.cs                 # P/Invoke seguro com Windows Credential Manager
│       │   ├── Database.cs                        # SQLite (5 tabelas com queries tipadas e migração)
│       │   ├── Models.cs                          # DTOs e entidades do SEI e UI
│       │   ├── SeiClient.cs                       # Cliente HTTP WSSEI v2 + SEAD fallback
│       │   ├── SeiPoller.cs                       # Motor de verificação periódica leve em background
│       │   └── TextAnalyzer.cs                    # Heurísticas de texto e destinatário
│       └── Ui/
│           ├── DetailWindow.xaml / .cs            # Modal de detalhes e timeline do processo
│           ├── FenceWindow.xaml / .cs             # Janela translúcida fixada no desktop (Progman)
│           ├── MainWindow.xaml / .cs              # Painel principal com 5 abas de controle
│           ├── SingleInstanceController.cs        # IPC e Mutex de instância única
│           ├── TaskbarFlyoutWindow.xaml / .cs     # Flyout suspenso ancorado na barra de tarefas
│           ├── TaskbarWidgetWindow.xaml / .cs     # Widget com letreiro digital contínuo (ticker)
│           └── Theme.cs                           # Paleta e pincéis Dark Fluent Design
├── tests/
│   ├── SeiMonitor.Tests/                          # 455 testes unitários e de integração (xUnit)
│   └── SeiMonitor.E2ETests/                       # 226 testes E2E e adversariais (Tiers 1 a 5)
├── specs/
│   └── 001-sei-desktop-monitor/                   # Especificações Spec-Kit (spec, plan, tasks)
└── PROJECT.md                                     # Arquitetura, matriz de rastreabilidade e contratos
```

---

## Download para Windows

Os pacotes x64 incluem o runtime .NET. O instalador cria um atalho no menu Iniciar. Para usar a edição portátil, extraia o ZIP e abra `SeiMonitor.exe`. As duas edições guardam as configurações no perfil do Windows.

- [Instalador da versão mais recente](https://github.com/sergiobaiao/sei-monitor/releases/latest/download/SEI-Monitor-Setup-win-x64.exe)
- [ZIP portátil da versão mais recente](https://github.com/sergiobaiao/sei-monitor/releases/latest/download/SEI-Monitor-Portable-win-x64.zip)
- [Todas as versões](https://github.com/sergiobaiao/sei-monitor/releases)

O GitHub Actions executa os testes no Windows. Quando uma nova `<Version>` em `src/SeiMonitor/SeiMonitor.csproj` chega à branch `main`, o workflow publica os dois pacotes em `v<Version>`. A maior versão estável publicada fica marcada como a mais recente.

## 🛠️ Requisitos e Compilação

- **Ambiente**: Windows 10 / Windows 11 (x64)
- **SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/)

### Compilação da Solução

```powershell
# Restaurar e compilar em modo Release
dotnet build SeiMonitor.slnx -c Release

# Executar suite completa de testes automatizados (681 testes)
dotnet test SeiMonitor.slnx -c Release
```

### Executar a Aplicação

```powershell
# Execução direta via dotnet
dotnet run --project src/SeiMonitor/SeiMonitor.csproj -c Release

# Ou executar o binário compilado:
.\src\SeiMonitor\bin\Release\net10.0-windows\SeiMonitor.exe
```

---

## ⚙️ Configuração Inicial

Ao abrir o monitor pela primeira vez:
1. Abra a aba **Configurações** na janela principal (`MainWindow`).
2. Informe:
   - **URL do SEI / WSSEI**: Endpoint do WSSEI v2 ou da API SEAD.
   - **Unidade Padrão**: Sigla da unidade de lotação (ex.: `GINFO`).
   - **Usuário e Senha**: Salvos de forma segura no Windows Credential Manager.
   - **Palavras-chave do Usuário**: Seus nomes, apelidos ou matrículas separados por vírgula para ativar a heurística de "Destinado a Você".
   - **Intervalo de Polling**: Tempo em minutos entre cada verificação leve (padrão: 5 min).
3. Clique em **Salvar Credenciais & Configurações** e em seguida em **Sincronizar Agora**.
