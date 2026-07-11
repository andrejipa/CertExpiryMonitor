# AGENTS.md — CertExpiryMonitor

Guia para agentes de IA (Codex, Copilot, Claude etc.) que trabalham neste repositório.

## O que é este projeto

Aplicativo Windows de bandeja do sistema (.NET 8 WinForms) que monitora certificados digitais A1
instalados no repositório `CurrentUser\My` do X.509 e envia notificações toast quando há
certificados próximos do vencimento. Sem rede, sem admin, sem banco externo; usa SQLite local
apenas para diagnóstico redigido.

## Arquitetura em uma linha

`Program.cs` compõe serviços → `TrayApplicationContext` orquestra UI + timer → `CertificateCheckService`
executa a lógica de verificação → `ExpiryEvaluator` decide quem notificar → `ToastNotifierService`
exibe o toast → `JsonStateStore` / `JsonSettingsStore` persistem estado em JSON no
`%LOCALAPPDATA%\CertExpiryMonitor\` → `DiagnosticEventStore` registra trilha técnica redigida
em `diagnostics.db`.

## Mapa de arquivos críticos

| Arquivo | Responsabilidade |
|---|---|
| `Program.cs` | Composição (DI manual), mutex de instância única |
| `Services/TrayApplicationContext.cs` | Orquestração UI, timer, menu, callbacks do DetailsForm |
| `Services/CertificateCheckService.cs` | Lógica de verificação, guards de skip (horário/data/hash) |
| `Services/NotificationCheckCoordinator.cs` | Coordena check → notificação → MarkNotified → persistência; retorna status explícito e retry |
| `Services/NotificationPresenter.cs` | Encapsula toast/fallback e registra o canal de notificacao usado |
| `Services/FallbackNotificationWindow.cs` | UI WinForms isolada do popup proprio de fallback |
| `Services/ToastActionDispatcher.cs` | Despacha ativacoes do protocolo para detalhes/dismiss |
| `Services/SettingsUpdateCoordinator.cs` | Atualiza configuracoes sem promover falha de leitura a defaults salvaveis |
| `Services/CertificateStateActions.cs` | Persiste dismiss/restore com telemetria e diagnostico |
| `Services/DurableFileWriter.cs` | Escrita atomica duravel compartilhada por settings/state/telemetria |
| `Models/CertificateReadResult.cs` | Resultado da leitura X.509: sucesso, falha do store ou falha parcial |
| `Services/ExpiryEvaluator.cs` | Decisão de bucket, `BuildPlan` / `BuildReminderPlan` |
| `Services/ToastNotifierService.cs` | Toast XML via WinRT unpackaged, atalho COM, toast compacto de lembrete |
| `Services/DetailsForm.cs` | Janela de detalhes + configurações; delega helpers para `CertificateDocumentHelpers` |
| `Services/CertificateDocumentHelpers.cs` | `FormatDocument`, `ParseHolder`, `GetCommonNameFallback` (interno + testável) |
| `Services/CertificateStatusHelpers.cs` | `GetStatusText`, `GetStatusCategory` para a grade — recebem `ExpiryThresholds` (interno + testável) |
| `Services/JsonStateStore.cs` | Lê/salva estado com envelope versionado; suporta formato legado (array puro) |
| `Services/JsonSettingsStore.cs` | Lê/salva AppSettings |
| `Services/DiagnosticEventStore.cs` | SQLite local `diagnostics.db`: eventos técnicos mínimos, observações redigidas de certificados, retenção |
| `Services/DiagnosticRedactor.cs` | Redige detalhes antes de persistir diagnóstico estruturado |
| `Services/StartupRegistration.cs` | Registra inicialização: Task Scheduler primeiro, HKCU\\Run como fallback |
| `Services/AppPaths.cs` | Caminhos de dados; aceita `rootOverride` para testes |
| `Services/FileLogger.cs` | Log rotativo; `Error()` inclui stack trace completo |
| `Services/DiagnosticsBundleService.cs` | Exporta `.zip` de diagnóstico local com logs, SQLite, métricas, settings redigido, startup e resumo seguro de certificados |
| `Models/ExpiryThresholds.cs` | Limites configuráveis por faixa; `Normalized()` garante ordering |
| `Models/AppSettings.cs` | Configurações do usuário, incluindo `Thresholds` |
| `installer/CertExpiryMonitor.iss` | Inno Setup 6, `PrivilegesRequired=lowest` |
| `scripts/Install-CurrentUser.ps1` | Instalação manual sem Inno Setup |
| `scripts/Install-FromUrl.ps1` | Instalação remota; exige HTTPS e SHA256 por padrão, opt-in explícito para exceções |
| `scripts/Uninstall-CurrentUser.ps1` | Desinstalação manual |
| `scripts/Run-BugHunt.ps1` | Rodada E2E agressiva: publish isolado, certificado de teste, startup/toast/UI, snapshot e cleanup |
| `scripts/CaptureUi.ps1` | Automação de captura PNG da UI via `PrintWindow` + dump UIA (Win32 GDI, não exige desktop interativo) |
| `scripts/GenerateAppIcon.ps1` | Gera o `.ico` do app desenhando programaticamente (7 tamanhos 16-256px) |
| `assets/CertExpiryMonitor.ico` | Ícone embedded — referenciado em `<ApplicationIcon>` e via `AppIcon.cs` helper |
| `Services/AppIcon.cs` | Helper que carrega o ícone embedded uma vez com cache estático |
| `Services/TelemetryService.cs` | Coleta de métricas anônimas locais (opt-in). 11 contadores em `telemetry.json` v1 |
| `Services/TelemetryWindow.cs` | Janela "Ver estatísticas de uso" — tabela dos contadores + botão limpar |
| `Models/AppSettings.LogFormat` | Enum `Text`/`Json` para formato do `monitor.log` |
| `Models/AppSettings.EventLogEnabled` | Espelha ERROR para Windows Event Log |
| `Models/AppSettings.TelemetryEnabled` | Liga/desliga coleta de telemetria local |
| `tests/…/ExpiryEvaluatorTests.cs` | 19 testes de lógica de notificação (thresholds padrão) |
| `tests/…/ExpiryEvaluatorThresholdsTests.cs` | Testes com thresholds customizados |
| `tests/…/JsonStateStoreTests.cs` | Persistência, migração de formato legado, robustez |
| `tests/…/ExpiryThresholdsTests.cs` | `Normalized()` com valores inválidos / invertidos |
| `tests/…/CertificateCheckServiceTests.cs` | Guards de skip, hash de snapshot |
| `tests/…/NotificationCheckCoordinatorTests.cs` | Matriz do ciclo de notificação e falhas de persistência |
| `tests/…/StoreReadFailureTests.cs` | Timeout/IO dos stores sem sobrescrita de dados válidos |
| `tests/…/CertificateDocumentHelpersTests.cs` | `FormatDocument` (CPF/CNPJ), `ParseHolder`, `GetCommonNameFallback` |
| `tests/…/CertificateStatusHelpersTests.cs` | `GetStatusText` e `GetStatusCategory` — thresholds padrão e customizados, boundaries, estados Dismissed/Notified |
| `tests/…/PropertyBasedTests.cs` | FsCheck fuzz/property tests de parsing, thresholds, stores JSON, helpers e toast XML |
| `tests/…/DiagnosticEventStoreTests.cs` | Garante schema SQLite, redaction, corrupção, retenção e snapshot |
| `tests/…/DiagnosticsBundleServiceTests.cs` | Garante pacote de diagnóstico, SQLite e redaction de hash/documento |

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
- **Toast compacto**: evitar botões visíveis no toast; o corpo do aviso abre a janela de detalhes.
- **Sem aviso duplicado**: usar popup próprio topmost apenas como fallback se o Windows rejeitar o toast.
- **Diagnóstico exportado** deve ser local e redigido: nunca exportar chave privada, PFX, senha, thumbprint puro, subject completo ou documento completo de titular.
- **SQLite local não é fonte de verdade**: `diagnostics.db` é apenas histórico técnico; settings/state continuam nos JSONs.

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

Para mutation testing local:
```bash
dotnet tool restore
dotnet tool run dotnet-stryker
```

Para bug hunt E2E agressivo no Windows real:
```bash
powershell -ExecutionPolicy Bypass -File .\scripts\Run-BugHunt.ps1 -Maximum -KeepArtifacts
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
| Automacao direta de `TrayApplicationContext` limitada | Baixo | `DetailsForm` possui integracao STA; o host da bandeja e coberto pelos colaboradores extraidos e pelo BugHunt/UIA em Windows real. |
| ~~Race mutex/events em `Program.cs`~~ | Verificado | Falso positivo. `AutoReset` sem waiter mantém estado signaled até alguém esperar (Win32 spec). `HandleActivationRequests` drena via `WaitOne(0)` no timer 500ms. Sinal não é perdido. |
| `JsonStateStore`/`JsonSettingsStore` temp-file não-fsync | Baixo | `File.Move` em primeira escrita não força flush; crash brusco do SO pode perder o arquivo. Próximo save recria. |
| `.bak` files em `%LOCALAPPDATA%\CertExpiryMonitor` | Baixo | `File.Replace` sobrescreve a cada save — máximo 2 arquivos. Não acumulam, mas tampouco são removidos. |
| Versão duplicada no `.csproj` e `.iss` | Baixo | Mitigado por `scripts/Publish-Release.ps1`. Risco subsiste em edição manual. |
| Navegação por teclado completa em `DetailsForm` | Baixo | `AccessibleName`/`AccessibleDescription` agora setados nos controles principais; navegação Tab/Enter ainda não auditada manualmente. |
| Dark mode | Baixo | Disponível apenas em .NET 9+ (`Application.SetColorMode`). Postergado até upgrade. |

### Decisões deliberadas — NÃO implementar

Estes itens já apareceram em auditorias anteriores e foram **explicitamente rejeitados** pelo dono do projeto. Não propor de novo em futuras auditorias.

| Item | Decisão | Motivo |
|---|---|---|
| Code signing (certificado EV) | **Não** | Uso interno de escritório; não é distribuído ao público; sem produção de instaladores assinados. |
| MSIX / Intune | **Não** | Inno Setup atual já entrega tudo (instalação per-user sem admin, Start Menu shortcut, uninstaller limpo). MSIX adicionaria sandbox + virtualização de registry + necessidade de code-signing sem ganho real. |
| ADMX/GPO templates | **Não** | Escritório sem Active Directory / Domain Controller; templates de policy não fazem sentido. |
| Multi-idioma (en-US/es-ES) | **Não** | 100% PT-BR é suficiente para o cenário de uso. |
| Testes High-DPI em 4K real | **Não** | Uso primário em 1080p; `PerMonitorV2` já está ativado no `.csproj` como medida preventiva. |

### Resolvidos nesta versão

| Item | Solução |
|---|---|
| Falha transitória de settings/state virava vazio/default salvável | Stores usam `TryLoad`; chamadores abortam mutações e checks fazem retry em 5 minutos. |
| Falha do store X.509 virava check vazio bem-sucedido | `CertificateReadResult` distingue sucesso, falha total e parcial; checks incompletos não consolidam fingerprint. |
| Fluxo de notificação preso ao TrayApplicationContext | Extraído para `NotificationCheckCoordinator`, coberto por testes sem WinForms. |
| Retenção/VACUUM em cada evento SQLite | `RunMaintenance` explícito executado em worker fora do caminho de INSERT. |
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
| Push para GitHub | `scripts/push-to-github.ps1` cria o repo via API + push, usando credencial do Windows Credential Manager (sem token hardcoded). CI rodou e passou no primeiro push. |
