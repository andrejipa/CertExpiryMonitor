# Changelog

Todas as mudanças notáveis neste projeto. Formato baseado em
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/).

---

## [1.0.0] — 2026-05-10

### UI / UX (validado via screenshots reais)

- **Acentuação correta em PT-BR** em toda a interface (aba "Configurações", "Horário do popup", "Faixas de notificação", "Faixa média", "Salvar horário", "Validos" → "Válidos", "Até X dias", "Situação", "usuário atual", "Atualizado às HH:mm", balloon tip "Nenhum certificado próximo do vencimento", toast title, tray menu "Executar verificação agora", tooltip "Última verificação"). Antes: strings ASCII intencionais por preocupação histórica de encoding — irrelevante em .NET 8 com source files UTF-8.
- **Status text com acentos**: `CertificateStatusHelpers.GetStatusText` agora retorna "Próximo do vencimento" / "Válido" (testes atualizados em conjunto).
- **Coluna "Titular" não trunca mais nomes corporativos**: `FillWeight = 150` (era default 100). Validado com 3 certs reais: "PORTAL COMERCIO E DISTRIBUIDORA LTDA" e "PIONEIRA COMERCIO DE ALIMENTOS LTDA" agora aparecem por extenso.
- **Tooltips explicativos** nos campos de "Faixas de notificação" — clarifica o que cada faixa significa (Faixa longa = primeiro aviso, Urgente = último aviso crítico).
- **Mensagens de erro com acentos**: todos os `MessageBox.Show("Nao foi possivel...")` corrigidos para "Não foi possível...".
- **Toast actions "Não lembrar este" / "Não lembrar nenhum"** com acentos.
- **Nova flag `--details`** no `Program.cs`/`TrayApplicationContext`: abre a janela direto na aba Certificados (`--configure` já existia para abrir em Configurações). Útil para shortcuts no menu Iniciar e para automação/testing.
- **Script `scripts/CaptureUi.ps1`**: automação que inicia o app, captura PNG das duas abas via `PrintWindow` (Win32 GDI — funciona mesmo em sessão sem desktop interativo) e dumpa árvore UIA para análise de acessibilidade.

### Round 4 — Telemetria + Logs estruturados + EventLog + Enum semantic

**Telemetria local opt-in** (`Services/TelemetryService.cs`)

- Coleta **anônima e local** (sem rede): 11 contadores agregados em `telemetry.json` (envelope v1).
- Métricas: `TotalChecks`, `ChecksWithPlan`, `ChecksSkipped`, `ManualChecks`, `NotificationsShown`, `NotificationFailures`, `DismissOne`, `DismissAll`, `Restore`, `ThresholdsChanged`, `ScheduleChanged`.
- **Privacidade by design**: nunca grava thumbprints, nomes, documentos ou caminhos. Apenas contadores numéricos.
- **Opt-in via `AppSettings.TelemetryEnabled`** (default `false`). Quando desabilitado, `Increment()` é no-op (não cria arquivo nem persiste).
- Visualização via menu de bandeja **"Ver estatísticas de uso..."** → `TelemetryWindow` (mini-form com tabela dos contadores + botão "Limpar estatísticas" + data de início da coleta).
- **6 testes** em `TelemetryServiceTests`: no-op quando disabled, persistence, load default, reset, UpdatedAt avança, CreatedAt preservado entre updates.

**Logs estruturados JSON** (`FileLogger`)

- Nova propriedade `LogFormat` em `AppSettings` (`Text` default | `Json`). `FileLogger.ApplySettings(settings)` aplica em runtime sem precisar reiniciar.
- Formato JSON Lines (JSONL — uma linha por evento): `{"ts":"...","level":"...","message":"...","exceptionType":"...","exceptionMessage":"...","stackTrace":"..."}`. Pronto para ingestão em Splunk/ELK/Sentinel/Azure Monitor.
- Formato Text legado mantido como default (compatibilidade com qualquer parser que já leia logs antigos).

**Windows EventLog opcional** (`FileLogger`)

- Nova propriedade `EventLogEnabled` em `AppSettings` (default `false`). Quando ativa, eventos ERROR são espelhados para Windows Event Log (canal Application, EventID 1000).
- Tenta criar source `CertExpiryMonitor` (precisa admin uma vez); fallback para source genérica `Application` (sempre disponível, sem elevação).
- Falha do EventLog é silenciosa (best-effort) — nunca quebra o app.

**Item 12 — `CertificateNotificationState` enum semantic**

- `Notified30 → NotifiedLong`, `Notified15 → NotifiedMedium`, `Notified7 → NotifiedShort`, `Notified1 → NotifiedUrgent`. Valores numéricos do enum (30, 15, 7, 1, 999) **mantidos** para preservar compatibilidade com `certificate-state.json` existentes em produção.
- Testes acompanhados (rename mecânico em 5 arquivos de teste).

**Item 13 — Race Program.cs mutex/events** — falso positivo

- Análise re-feita: `EventWaitHandle.AutoReset` SEM waiter mantém o estado signaled até alguém esperar (Win32 spec). O `HandleActivationRequests` faz `WaitOne(0)` a cada 500ms no message loop, drenando qualquer sinal pendente. **Sinal não é perdido.** Documentado em AGENTS.md como verificado.

**UI da aba Configurações — GroupBox "Avançado"**

- 3 CheckBoxes (LogFormat=Json, EventLogEnabled, TelemetryEnabled) com tooltips explicativos.
- Form cresceu `ClientSize 940×500 → 940×600` para acomodar a nova GroupBox.
- "Salvar configurações" agora salva 3 grupos atomicamente: Notificação + Faixas + Avançado.
- `FileLogger.ApplySettings` é chamado em runtime após Save — formato JSON e EventLog refletem instantaneamente sem restart.

### UI/UX — Round 3 (refactor estrutural + busca + identidade visual)

- **Cards do summary agora são clicáveis** para filtrar o grid: clicar em "⛔ Vencidos" aplica `Mostrar = Vencidos`, "⚠ Até 7 dias"/"⌛ Até 30 dias" aplica `Mostrar = A vencer`, etc. Cursor muda para hand-pointer ao hover, tooltip "Clique para filtrar a lista por esta categoria".
- **Busca textual no grid** (`TextBox` com `PlaceholderText`): filtra por `Holder` ou `Document` via `LIKE` no `DataTable.RowFilter`, case-insensitive, escapando apóstrofos para evitar SQL injection. Combina via `AND` com o filtro de categoria.
- **Coluna "Situação" some dinamicamente** quando um filtro de categoria específico está aplicado (era redundante — todas as linhas teriam o mesmo valor).
- **Refactor estrutural da aba Configurações**:
  - `GroupBox "Notificação"` agrupando descrição + horário + botão de teste + checkbox de som (CheckBox "Tocar som" era órfão entre seções).
  - `GroupBox "Faixas de notificação (dias antes do vencimento)"` com layout 1-coluna sequencial decrescente: longa → média → curta → urgente (era 2×2 forçando ziguezague).
  - Cada NumericUpDown agora tem sufixo "dias" em cinza reforçando a unidade.
  - Sublabel explicativo "Da faixa mais distante (primeiro aviso) à mais crítica (último aviso):".
  - **Botão único "Salvar configurações"** no rodapé direito, substituindo os 2 botões duplicados (`Salvar horário` + `Salvar faixas`). Salva horário + som + faixas atomicamente.
- **Footer reordenado conforme convenção Windows**: `[Atualizar lista] [Ignorar certificado] [Fechar]` — Fechar mais à direita, isolada das ações de manipulação de dados.
- **Ícone próprio do app** (`assets/CertExpiryMonitor.ico`, 7 tamanhos 16/24/32/48/64/128/256 px) embarcado como `EmbeddedResource` e referenciado via `<ApplicationIcon>` no `.csproj`. Substituiu o `SystemIcons.Information` genérico em `NotifyIcon` (bandeja) e `DetailsForm.Icon` (title bar / Alt-Tab). Gerador: `scripts/GenerateAppIcon.ps1` desenha programaticamente um certificado estilizado com selo "A1".
- **`AppIcon` helper** (`Services/AppIcon.cs`): carrega o ícone embutido uma única vez (cache estático) com fallback para `SystemIcons.Information` se o resource não for encontrado.
- **DetailsForm cresceu** `ClientSize = 940×500` (era 940×460) para acomodar a linha de busca; `MinimumSize` cresceu proporcionalmente.

### Operacional — repositório Git

- **`git init -b main`** + primeiro commit "Initial commit: CertExpiryMonitor v1.0.0" criado.
- **`.gitignore` atualizado** para incluir `assets/preview-*.png` (gerados pelo script de preview do ícone).
- **Remote `origin`** configurado para `https://github.com/andrejipa/CertExpiryMonitor.git`. Credenciais HTTPS já estão no Windows Credential Manager (auth chegou ao GitHub e respondeu "Repository not found").
- **README com 4 badges** (CI build, tests passing, .NET 8, Windows 10/11) — URLs apontando para `andrejipa/CertExpiryMonitor`.
- **Instruções no README** sobre como completar o push (criar repo em `github.com/new` + `git push -u origin main`).

### UI/UX — Round 2 (validado via screenshots + auditoria adversarial)

- **`HighDpiMode = PerMonitorV2`** no `.csproj` (`<ApplicationHighDpiMode>`): textos nítidos em monitores 4K e setups multi-monitor com DPIs diferentes. Sem isso, o app fica borrado em DPI > 100%.
- **`ApplicationUseCompatibleTextRendering = false`** + **`ApplicationVisualStyles = true`**: garante renderização GDI+ moderna e visual styles do Windows 10/11.
- **Ícones Unicode BMP nos cards de contagem** (⛔ ⚠ ⌛ ✓ ⊘): resolve acessibilidade de cor (WCAG — daltônicos não dependem mais só de matiz). Usados caracteres do Basic Multilingual Plane porque emojis do Supplementary Plane (🔕, ⏰) não renderizam consistentemente em fontes WinForms.
- **Coluna "Vencimento" simplificada para `dd/MM/yyyy`** (sem hora): minutos não agregam valor — usuário não age sobre eles. Reduz ruído cognitivo.
- **"Dias restantes" alinhado à direita** com padding-right de 10px: comparação numérica visual (73 vs 275 vs 364) agora flui naturalmente.
- **Zebra striping visível**: cor passou de `RGB(250,250,250)` (quase invisível) para `RGB(244,247,250)` (sutil mas perceptível). Aplicado em `AlternatingRowsDefaultCellStyle` E no `ApplyRowStyles`.
- **Tooltips em todos os botões** ("Ignorar certificado", "Atualizar lista", "Fechar"): explica o que cada um faz, com ênfase no estado desabilitado do "Ignorar".
- **Estado vazio com CTA**: `FormatCountLabel` retorna mensagem singular/plural correta e, quando 0 certificados, exibe "Nenhum certificado A1 encontrado. Instale um certificado .pfx no Windows para começar a monitorar."
- **AccessibleName e AccessibleDescription** em controles principais (DataGridView, ComboBox de filtro, 3 botões do footer, 4 NumericUpDown de faixas). Validado via dump UIA: leitores de tela agora lêem "Filtro de exibição de certificados" em vez de herdar nome do controle adjacente.

### Infraestrutura

- **CI no GitHub Actions** (`.github/workflows/build.yml`): job `build-test` (windows-latest, .NET 8 SDK, restore→build→test com cobertura, upload de TRX) e job `publish-smoke` (publish single-file win-x64, upload do `.exe` como artefato). Garante que regressões de build/test/publish quebrem o PR.
- **`.gitignore`**: cobre `bin/`, `obj/`, `publish/`, `TestResults/`, `installer-output/`, `.dotnet-local/`, IDEs e arquivos sensíveis.
- **Envelope versionado em `settings.json`**: `{ "version": 1, "settings": { ... } }`, com leitura retrocompatível ao formato legado e migração automática no próximo Save. Alinha com `JsonStateStore`. `PropertyNameCaseInsensitive=true` no JsonOptions evita bugs de case (PascalCase vs camelCase).

### Testes

- **104 testes verdes** (era 86 casos). Novos arquivos:
  - `JsonSettingsStoreTests` (8 casos): envelope, legado, migração, JSON corrompido, future version forward-compat.
  - `JsonStateStoreConcurrencyTests` (3 casos): `Parallel.For` Save+Load não corrompe, exceptions zero sob race.
  - Em `CertificateCheckServiceTests` (+6 casos): `FakeCertificateReader` (subclasse via `virtual`) elimina dependência do store X.509 real; teste de race do `Interlocked` no `_isChecking`; null guards.
- **`CertificateReader.ReadCurrentUserPersonalCertificates` virtualizado** (sem criar interface — respeita a convenção do projeto). Permite testes determinísticos sem violar a convenção "classes concretas diretas".

### Adicionado

- **Faixas de notificação configuráveis** (`ExpiryThresholds`): usuário pode ajustar os limites de 30/15/7/1 dias diretamente na aba Configurações do DetailsForm; `Normalized()` garante ordem crescente e valores ≥ 1.
- **Task Scheduler para startup**: `StartupRegistration` tenta `schtasks /create /sc ONLOGON /rl LIMITED` antes de usar `HKCU\Run`; stderr logado em caso de falha; fallback automático para `HKCU\Run`.
- **Hash de snapshot de certificados**: SHA-256 de thumbprints+validades para detectar mudanças no store sem re-executar verificação completa no mesmo dia.
- **`CertificateCheckService`**: toda a lógica de verificação extraída de `TrayApplicationContext`; guard `_isChecking`, propriedade `LastPlan`, método público `MarkNotified`.
- **`CertificateDocumentHelpers`**: classe `internal static` com `FormatDocument`, `ParseHolder`, `GetCommonNameFallback` — agora testáveis isoladamente.
- **`DetailsFormOptions`**: substituiu construtor com 13+ parâmetros em `DetailsForm`.
- **`GetThresholds` callback** em `DetailsFormOptions`: `WireAnalyzeButton` sempre usa thresholds atuais (corrigi captura stale do form).
- **Versão no binário**: `<Version>`, `<AssemblyVersion>`, `<FileVersion>` no `.csproj`; log `CertExpiryMonitor v{versao} starting` na inicialização.
- **Stack traces nos logs de erro**: `FileLogger.Error` usa `exception.ToString()` — inclui tipo, mensagem e stack trace completo.
- **Stderr do schtasks logado** quando ExitCode ≠ 0.
- **Feedback visual** quando verificação manual não encontra certificados pendentes (balloon tip "Nenhum certificado próximo do vencimento").
- **Tooltip dinâmico** do ícone de bandeja: versão e data da última verificação (respeitando limite de 63 chars do Win32).
- **Rotação de log em cascata**: até 3 backups (`monitor.log.1/.2/.3`); antes mantinha apenas 1.
- **`AppPaths` com `rootOverride`**: parâmetro opcional para testes isolados em diretórios temporários.
- **`InternalsVisibleTo("CertExpiryMonitor.Tests")`**: clases `internal` acessíveis nos testes.
- **coverlet** adicionado ao projeto de testes.
- **Novos testes** (~86 casos no total): `ExpiryThresholdsTests`, `CertificateDocumentHelpersTests`, `CertificateStatusHelpersTests` (faixas customizadas no grid), `JsonStateStoreTests`, `CertificateCheckServiceTests`, `ExpiryEvaluatorThresholdsTests`.
- **`CertificateStatusHelpers`**: `internal static` com `GetStatusText` e `GetStatusCategory` extraidos de `DetailsForm` — testaveis isoladamente, garantem que classificacao visual respeita thresholds customizados.
- **`AGENTS.md`**: guia completo para agentes de IA com mapa de arquivos, regras de negócio e backlog.
- **`scripts/Publish-Release.ps1`**: script de release que sincroniza versão no `.csproj` e no `.iss`, executa `dotnet publish` e opcionalmente chama o Inno Setup.
- Menu "Abrir pasta de logs" na bandeja do sistema.
- `SimpleName` via `GetNameInfo(X509NameType.SimpleName)` em `CertificateReader` — parsing correto de CNs com vírgulas escapadas.
- Atalho COM recriado somente quando executável é mais novo que o atalho existente (`EnsureShortcut`).
- Envelope versionado em `JsonStateStore` (`{ "version": 1, "records": [...] }`) com leitura retrocompatível de formato legado (array puro).

### Corrigido

- **Duplo startup**: instalador e scripts não gravam mais `HKCU\Run` — toda gestão de startup centralizada em `StartupRegistration`.
- **Task Scheduler órfão no uninstall**: `[UninstallRun]` do Inno Setup e `Uninstall-CurrentUser.ps1` removem a tarefa agendada.
- **Thresholds stale no DetailsForm**: "Atualizar lista" usava thresholds capturados na abertura do form; corrigido via `GetThresholds` callback.
- **Overflow em `FormatDocument`**: `Convert.ToUInt64` substituído por `ulong.TryParse` — sem `OverflowException` para números de série com prefixo hexadecimal.
- **`Application.DoEvents()` removido**: botão "Atualizar lista" usa `async/await` + `Task.Run`.
- **Cast overflow em timer**: `Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue)` impede overflow ao agendar delays > 24 dias.
- **Botões de dismiss ausentes no toast**: adicionados "Nao lembrar este" e "Nao lembrar nenhum" (thumbprints URI-encoded nos argumentos).
- **Thresholds hardcoded no DetailsForm**: `GetStatusCategory` e `ApplyRowStyle` usavam dias fixos (7/30) em vez dos thresholds configurados pelo usuário; corrigido para usar `ExpiryThresholds.Level7`/`Level30` normalizados; `ApplyRowStyle` reescrito para usar `StatusCategory` pré-calculado, eliminando dupla avaliação.
- **Summary panel desatualizado ao mudar thresholds**: rótulos "Ate X dias" e cores do grid não refletiam thresholds salvos até reabrir o form. Adicionado callback `onThresholdsSaved` que reclassifica linhas em memória, atualiza Tag dos labels Critical/Warning e refaz cores via `ApplyRowStyles`.
- **Deadlock potencial em `StartupRegistration`**: `WaitForExit` sem leitura assíncrona de stdout/stderr podia travar se buffer do schtasks enchesse (>4KB). Streams agora são drenados via `ReadToEndAsync` antes do `WaitForExit`; timeout dispara `Kill(entireProcessTree)`.
- **Race em `CertificateCheckService._isChecking`**: campo `bool` substituído por `int` com `Interlocked.CompareExchange/Exchange` — guard atômico entre timer thread e UI thread.
- **`Application.ExecutablePath` → `Environment.ProcessPath`**: mais previsível em publish single-file; aplicado em `StartupRegistration` e `ToastNotifierService.EnsureShortcut`.
- **COM RCW leak em `ToastNotifierService`**: `CShellLink` (com 3 casts subsequentes a `IShellLinkW`/`IPropertyStore`/`IPersistFile`) agora é liberado via `Marshal.FinalReleaseComObject` em `finally` — evita acúmulo de ref counts em apps tray longevos.
- **Rotação de log não-fatal**: falha em `File.Move` (antivirus, lock) não interrompe mais a gravação; logs continuam sendo persistidos mesmo que o cap de tamanho não seja respeitado.
- **Null guards públicos**: `CertificateCheckService.RunCheck`, `MarkNotified` e `ComputeSnapshotHash` agora rejeitam `null` com `ArgumentNullException.ThrowIfNull`.
- **README desatualizado**: linha "inicialização via HKCU\\Run" corrigida para refletir a prioridade do Task Scheduler.
- **`_singleInstanceMutex` nunca era `Dispose()`**: handle vazado no shutdown. Agora `Dispose()` é chamado no `finally`; `ReleaseMutex` envolto em try/catch para não mascarar exceção original.
- **Bug detectado pelos novos testes**: `JsonSettingsStore` deserializava envelope com `JsonDocument.TryGetProperty` case-sensitive (`"version"` vs `"Version"`). Corrigido com `EnumerateObject` insensitive + `PropertyNameCaseInsensitive=true` no JsonOptions.
- **Teste flaky descoberto rodando perto da meia-noite**: `RunCheck_SkipsWhenTimeNotReachedAndFlagIsFalse` usava `DailyCheckTime = 23:59`; quebrou ao executar exatamente às `23:59:xx` (hora atual > horário configurado). Substituído por `TimeSpan.FromHours(48)`, garantidamente inatingível (`now.TimeOfDay < 24h` sempre).

### Documentação

- `ExpiryBucket` e `CertificateNotificationState` receberam XML docs explicando acoplamento semântico com thresholds padrão.
- `README.md` atualizado: arquitetura, regras, Task Scheduler, Publish-Release, testes.
