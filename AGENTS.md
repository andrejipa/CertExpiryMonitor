# AGENTS.md — CertExpiryMonitor

Guia para agentes de IA (Codex, Copilot, Claude etc.) que trabalham neste repositório.

## O que é este projeto

Aplicativo Windows de bandeja do sistema (.NET 8 WinForms) que monitora certificados digitais A1
instalados no repositório `CurrentUser\My` do X.509 e envia notificações toast quando há
certificados próximos do vencimento. Sem rede, sem admin, sem banco de dados.

## Arquitetura em uma linha

`Program.cs` compõe serviços → `TrayApplicationContext` orquestra UI + timer → `CertificateCheckService`
executa a lógica de verificação → `ExpiryEvaluator` decide quem notificar → `ToastNotifierService`
exibe o toast → `JsonStateStore` / `JsonSettingsStore` persistem estado em JSON no
`%LOCALAPPDATA%\CertExpiryMonitor\`.

## Mapa de arquivos críticos

| Arquivo | Responsabilidade |
|---|---|
| `Program.cs` | Composição (DI manual), mutex de instância única |
| `Services/TrayApplicationContext.cs` | Orquestração UI, timer, menu, callbacks do DetailsForm |
| `Services/CertificateCheckService.cs` | Lógica de verificação, guards de skip (horário/data/hash) |
| `Services/ExpiryEvaluator.cs` | Decisão de bucket, `BuildPlan` / `BuildReminderPlan` |
| `Services/ToastNotifierService.cs` | Toast XML via WinRT unpackaged, atalho COM |
| `Services/DetailsForm.cs` | Janela de detalhes + configurações; delega helpers para `CertificateDocumentHelpers` |
| `Services/CertificateDocumentHelpers.cs` | `FormatDocument`, `ParseHolder`, `GetCommonNameFallback` (interno + testável) |
| `Services/CertificateStatusHelpers.cs` | `GetStatusText`, `GetStatusCategory` para a grade — recebem `ExpiryThresholds` (interno + testável) |
| `Services/JsonStateStore.cs` | Lê/salva estado com envelope versionado; suporta formato legado (array puro) |
| `Services/JsonSettingsStore.cs` | Lê/salva AppSettings |
| `Services/StartupRegistration.cs` | Registra inicialização: Task Scheduler primeiro, HKCU\\Run como fallback |
| `Services/AppPaths.cs` | Caminhos de dados; aceita `rootOverride` para testes |
| `Services/FileLogger.cs` | Log rotativo; `Error()` inclui stack trace completo |
| `Models/ExpiryThresholds.cs` | Limites configuráveis por faixa; `Normalized()` garante ordering |
| `Models/AppSettings.cs` | Configurações do usuário, incluindo `Thresholds` |
| `installer/CertExpiryMonitor.iss` | Inno Setup 6, `PrivilegesRequired=lowest` |
| `scripts/Install-CurrentUser.ps1` | Instalação manual sem Inno Setup |
| `scripts/Uninstall-CurrentUser.ps1` | Desinstalação manual |
| `scripts/CaptureUi.ps1` | Automação de captura PNG da UI via `PrintWindow` + dump UIA (Win32 GDI, não exige desktop interativo) |
| `scripts/GenerateAppIcon.ps1` | Gera o `.ico` do app desenhando programaticamente (7 tamanhos 16-256px) |
| `assets/CertExpiryMonitor.ico` | Ícone embedded — referenciado em `<ApplicationIcon>` e via `AppIcon.cs` helper |
| `Services/AppIcon.cs` | Helper que carrega o ícone embedded uma vez com cache estático |
| `Services/TelemetryService.cs` | Coleta de métricas anônimas locais (opt-in). 11 contadores em `telemetry.json` v1 |
| `Services/TelemetryWindow.cs` | Janela "Ver estatísticas de uso" — tabela dos contadores + botão limpar |
| `Models/AppSettings.LogFormat` | Enum `Text`/`Json` para formato do `monitor.log` |
| `Models/AppSettings.EventLogEnabled` | Espelha ERROR para Windows Event Log |
| `Models/AppSettings.TelemetryEnabled` | Liga/desliga coleta de telemetria local |
| `tests/…/ExpiryEvaluatorTests.cs` | 13 testes de lógica de notificação (thresholds padrão) |
| `tests/…/ExpiryEvaluatorThresholdsTests.cs` | Testes com thresholds customizados |
| `tests/…/JsonStateStoreTests.cs` | Persistência, migração de formato legado, robustez |
| `tests/…/ExpiryThresholdsTests.cs` | `Normalized()` com valores inválidos / invertidos |
| `tests/…/CertificateCheckServiceTests.cs` | Guards de skip, hash de snapshot |
| `tests/…/CertificateDocumentHelpersTests.cs` | `FormatDocument` (CPF/CNPJ), `ParseHolder`, `GetCommonNameFallback` |
| `tests/…/CertificateStatusHelpersTests.cs` | `GetStatusText` e `GetStatusCategory` — thresholds padrão e customizados, boundaries, estados Dismissed/Notified |

## Flags de linha de comando

| Flag | Comportamento |
|---|---|
| (nenhuma) | App roda em bandeja apenas; primeira instância faz check inicial. Segunda instância sinaliza `_activationEvent` na primeira. |
| `--background` | Idêntico a sem flag, mas sem sinalizar activation event (usado pelo Task Scheduler ONLOGON e `HKCU\Run`). |
| `--configure` | Abre `DetailsForm` na aba **Configurações**. |
| `--details` | Abre `DetailsForm` na aba **Certificados**. |

## Regras de negócio essenciais

- **Faixas padrão**: 30 / 15 / 7 / 1 dias antes do vencimento. Configuráveis pelo usuário.
- **`Normalized()`** é chamado em todo lugar que usa thresholds — nunca use `Thresholds` sem normalizar.
- **Certificado expirado** (daysRemaining < 0) **não notifica** — o usuário já perdeu o prazo.
- **Hash de snapshot**: SHA-256 dos thumbprints+NotAfter ordenados; se igual ao do último check no mesmo dia, o check é pulado.
- **Instância única**: mutex `Local\CertExpiryMonitor.CurrentUser`. Segunda instância sinaliza a primeira via `EventWaitHandle`.
- **Toast com 5 botões no máximo** (limitação do Windows Action Center).

## Convenções de código

- **Sem IoC container** — DI manual em `Program.cs`.
- **Sem interface para serviços** — classes concretas diretas (projeto pequeno).
- **Sem async/await nos serviços de domínio** — apenas na UI (`WireAnalyzeButton` em DetailsForm).
- **Escrita atômica** via `File.Replace` com temp + `.bak` em ambos os stores JSON.
- **`InternalsVisibleTo("CertExpiryMonitor.Tests")`** — classes `internal` são visíveis para testes.
- **Comentários em português** — mensagens de log e comentários de código em PT-BR.

## Como adicionar um novo campo de configuração

1. Adicionar propriedade em `Models/AppSettings.cs` com valor padrão.
2. Expor na aba de configurações em `Services/DetailsForm.cs` (`BuildSettingsTab`).
3. Adicionar callback `SaveXxx` em `TrayApplicationContext.cs` e passá-lo via `DetailsFormOptions`.
4. Escrever teste de round-trip em `JsonStateStoreTests.cs` ou equivalente.

## Como rodar os testes

```bash
dotnet test "tests/CertExpiryMonitor.Tests/CertExpiryMonitor.Tests.csproj"
```

Para cobertura:
```bash
dotnet test --collect:"XPlat Code Coverage" --results-directory coverage
```

## Riscos conhecidos / backlog técnico

### UI/UX — convenções

- **Caracteres Unicode em strings de UI**: usar apenas BMP (U+0000..U+FFFF). Emojis do Supplementary Plane (🔕, ⏰) não renderizam em Segoe UI default — use equivalentes BMP: ⛔ ⚠ ⌛ ✓ ⊘ ◐ ●.
- **AccessibleName explícito** em qualquer controle cujo Name UIA seria inferido incorretamente do label adjacente (ComboBox, NumericUpDown em FlowLayoutPanel). Valide com `scripts/CaptureUi.ps1` que dumpa árvore UIA.
- **Cores nunca sozinhas como sinalizador semântico** (WCAG): sempre acompanhar de ícone + texto.
- **Mensagens com singular/plural correto** via helper (ver `FormatCountLabel`).

### Em aberto

| Item | Impacto | Observação |
|---|---|---|
| ~~`CertificateNotificationState` enum semantic~~ | Resolvido | Valores `Notified30/15/7/1` renomeados para `NotifiedLong/Medium/Short/Urgent`. Valores numéricos do enum (30, 15, 7, 1, 999) preservados para compat JSON. |
| Testes de UI (DetailsForm, TrayApplicationContext) ausentes | Médio | UI WinForms difícil de testar sem headless runner. Mitigado parcialmente pela extração para helpers testáveis (`CertificateStatusHelpers`, `CertificateDocumentHelpers`). |
| ~~Race mutex/events em `Program.cs`~~ | Verificado | Falso positivo. `AutoReset` sem waiter mantém estado signaled até alguém esperar (Win32 spec). `HandleActivationRequests` drena via `WaitOne(0)` no timer 500ms. Sinal não é perdido. |
| `JsonStateStore`/`JsonSettingsStore` temp-file não-fsync | Baixo | `File.Move` em primeira escrita não força flush; crash brusco do SO pode perder o arquivo. Próximo save recria. |
| `.bak` files em `%LOCALAPPDATA%\CertExpiryMonitor` | Baixo | `File.Replace` sobrescreve a cada save — máximo 2 arquivos. Não acumulam, mas tampouco são removidos. |
| Versão duplicada no `.csproj` e `.iss` | Baixo | Mitigado por `scripts/Publish-Release.ps1`. Risco subsiste em edição manual. |
| Sem code signing | Médio (produção) | `.exe` e instalador sem assinatura disparam SmartScreen warning. Fora do escopo da v1.0 (requer cert). |
| Sem suporte multi-idioma | Baixo | Strings PT-BR hardcoded. Aceitável para escopo SMB/uso pessoal brasileiro. |
| Navegação por teclado completa em `DetailsForm` | Baixo | `AccessibleName`/`AccessibleDescription` agora setados nos controles principais; navegação Tab/Enter ainda não auditada manualmente. |
| Dark mode | Baixo | Disponível apenas em .NET 9+ (`Application.SetColorMode`). Postergado até upgrade. |
| Push para GitHub (`git push origin main`) | Operacional | Repo local existe e remote está configurado. Falta o usuário criar o repo em `github.com/new` (uma vez) — depois disso, push e CI rodam automaticamente. |

### Resolvidos nesta versão

| Item | Solução |
|---|---|
| Thresholds hardcoded no DetailsForm (`<= 7`, `<= 30`) | Extraído para `CertificateStatusHelpers`; usa `ExpiryThresholds.Level7`/`Level30` normalizado. |
| Summary panel não atualizava ao salvar thresholds | Callback `onThresholdsSaved` reclassifica linhas + refaz cores e contagens. |
| `AppPaths.IDisposable` vestigial | Removido; `Program.cs` usa `var paths`. |
| Deadlock potencial em `StartupRegistration` (stdout buffer cheio) | `ReadToEndAsync` antes do `WaitForExit`; `Kill(entireProcessTree)` em timeout. |
| `_isChecking` race entre timer e UI thread | `Interlocked.CompareExchange/Exchange` substituindo `bool`. Coberto por `RunCheck_IsCheckingGuardSerializesParallelCalls`. |
| `Application.ExecutablePath` em single-file publish | Trocado por `Environment.ProcessPath` (fallback). |
| COM RCW leak em `ToastNotifierService.EnsureShortcut` | `Marshal.FinalReleaseComObject` no `finally`. |
| Rotação de log fatal | Falha agora é não-fatal — preserva escrita do log. |
| Métodos públicos sem null guard | `ArgumentNullException.ThrowIfNull` em `RunCheck`, `MarkNotified`, `ComputeSnapshotHash`, `Save`. |
| README dizendo "inicialização via HKCU\\Run" | Corrigido para Task Scheduler + fallback. |
| `_singleInstanceMutex` nunca era `Dispose()` | Handle agora liberado no `finally` do `Program.cs`. |
| `CertificateReader` não-testável (lia store real do SO) | Método `ReadCurrentUserPersonalCertificates` virtualizado; `FakeCertificateReader` no test project. |
| Mutex `JsonStateStore` sob concorrência sem teste | `JsonStateStoreConcurrencyTests` com `Parallel.For` valida que mutex serializa Save/Load sem corrupção. |
| `settings.json` sem versionamento de schema | Envelope `{ version, settings }` v1, com compat retro ao formato legado. |
| Sem CI / build não-validado automaticamente | `.github/workflows/build.yml` com build, test (cobertura) e publish single-file em cada PR. |
| Sem `.gitignore` | Criado, cobre artefatos de build, IDE, SDK local e arquivos sensíveis. |
| Bug encontrado pelos testes — case-sensitivity do envelope | `JsonDocument.TryGetProperty` é case-sensitive; substituído por `EnumerateObject` insensitive e `PropertyNameCaseInsensitive=true` no JsonOptions. |
