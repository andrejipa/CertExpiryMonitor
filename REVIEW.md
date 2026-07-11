# Revisao critica

## Checklist solicitado

1. Verificacao apenas 1 vez por dia: atendido para execucao automatica e manual. `RunCheck` retorna se `LastCheckDate` for a data atual.
2. Horario escolhido respeitado: atendido. O timer agenda a proxima execucao com base em `DailyCheckTime`; horarios invalidos voltam para 09:00.
3. PC ligado apos horario: atendido. Apos login, o app aguarda `InitialDelayMinutes`; se o horario ja passou e `LastCheckDate` ainda nao e hoje, executa.
4. Sem duplicacao de alertas por certificado/faixa: atendido. O plano usa `HashSet` por thumbprint e o estado impede repetir faixa ja notificada.
5. Lembrar novamente: atendido como fluxo normal. A acao nao muda estado para `Dismissed`; o certificado segue para a proxima faixa aplicavel.
6. Nao lembrar mais este certificado: atendido. Persiste `Dismissed` por thumbprint, criando registro minimo se necessario.
7. Nao lembrar mais nenhum: atendido para os certificados do popup. O toast inclui os thumbprints do lote nos argumentos da acao.
8. Certificados sem chave privada ignorados: atendido em `CertificateReader` com `HasPrivateKey == false`.
9. Sem acesso/exportacao de chave privada: atendido. O codigo nao chama `Export`, `PrivateKey`, `Get*PrivateKey` ou APIs de assinatura/decriptacao.
10. Fallback quando Toast falhar: atendido. Se o Windows rejeitar a tentativa, o app abre popup proprio topmost com `Ver detalhes` e `Fechar aviso`; o BugHunt automatiza as duas acoes e verifica ausencia de duplicacao.

## Refatoracoes consolidadas

- `NotificationCheckCoordinator` concentra check, tentativa de notificacao, `MarkNotified` e persistencia final.
- `NotificationPresenter`, `ToastActionDispatcher`, `SettingsUpdateCoordinator` e `CertificateStateActions` retiram regras testaveis do host WinForms.
- `TrayApplicationContext` ficou restrito principalmente a lifecycle, timers, menu e feedback visual.
- Settings, estado e telemetria usam `DurableFileWriter` com temp, `WriteThrough`, `Flush(true)` e replace atomico.
- `DetailsForm` possui testes de integracao reais em STA; helpers e colaboradores permanecem cobertos isoladamente.

## Limitacoes conhecidas

- O app depende de sessao de usuario logada; nao roda como servico.
- Toast Notifications em app unpackaged dependem de AppUserModelID, atalho no Start Menu e politicas do Windows.
- O Windows nao oferece confirmacao confiavel de exibicao para toast unpackaged: o app distingue submissao aceita de exibicao e mantem popup proprio quando a tentativa e rejeitada.
- A durabilidade de escrita foi reforcada, mas falha fisica abrupta ainda depende das garantias do filesystem/dispositivo.
- Nao havia certificado local de code signing valido com chave privada na preparacao da v1.0.10; o instalador interno permanece sem assinatura.

## Verificacoes posteriores (atualizacao)

- **Build e testes executados localmente** com .NET 8 SDK 8.0.420: 442/442 testes; cobertura `65,79%` de linhas / `63,37%` de branches.
- **DetailsForm exercitado em STA** e fluxo de fallback ampliado no BugHunt para as duas acoes do popup.
- **Publish single-file** permanece sujeito ao gate de release de `< 77 MiB`.
- **CI configurada** em `.github/workflows/build.yml` para validar build+test+publish em cada push/PR.
- Em maquinas sem SDK global, ver README "Em maquinas sem o .NET 8 SDK instalado globalmente" para instalacao local via `dotnet-install.ps1`.
