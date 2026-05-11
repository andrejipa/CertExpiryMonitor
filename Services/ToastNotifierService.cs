using System.Runtime.InteropServices;
using System.Windows.Forms;
using CertExpiryMonitor.Models;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CertExpiryMonitor.Services;

public sealed class ToastActionEventArgs : EventArgs
{
    public ToastActionEventArgs(string arguments)
    {
        Arguments = arguments;
    }

    public string Arguments { get; }
}

public sealed class ToastNotifierService
{
    public const string AppUserModelId = "CertExpiryMonitor.Windows";
    private readonly FileLogger _logger;
    private bool _isShortcutReady;

    public ToastNotifierService(FileLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<ToastActionEventArgs>? Activated;

    public void EnsureShortcut()
    {
        try
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            var programs = Path.Combine(startMenu, "Programs");
            Directory.CreateDirectory(programs);

            var shortcutPath = Path.Combine(programs, "CertExpiryMonitor.lnk");
            var executable   = Environment.ProcessPath ?? Application.ExecutablePath;

            // Recria o atalho somente se o executavel foi atualizado desde a ultima criacao.
            if (File.Exists(shortcutPath))
            {
                var shortcutModified = File.GetLastWriteTimeUtc(shortcutPath);
                var exeModified      = File.GetLastWriteTimeUtc(executable);
                if (shortcutModified >= exeModified)
                {
                    _isShortcutReady = true;
                    return;
                }
            }

            // ShellLink/PropertyStore/PersistFile sao RCWs sobre a MESMA instancia COM.
            // Liberamos explicitamente no finally para evitar acumular ref counts entre
            // restarts do app (atalho e recriado quando o exe e atualizado).
            var shellLinkObject = (object)new CShellLink();
            try
            {
                var shellLink = (IShellLinkW)shellLinkObject;
                shellLink.SetPath(executable);
                shellLink.SetArguments("--background");
                shellLink.SetWorkingDirectory(AppContext.BaseDirectory);

                var propertyStore = (IPropertyStore)shellLinkObject;
                using var appId = new PropVariant(AppUserModelId);
                propertyStore.SetValue(PropertyKeys.AppUserModelId, appId);
                propertyStore.Commit();

                var persistFile = (IPersistFile)shellLinkObject;
                persistFile.Save(shortcutPath, true);
                _isShortcutReady = true;
            }
            finally
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(shellLinkObject))
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shellLinkObject);
                }
            }
        }
        catch (Exception ex)
        {
            _isShortcutReady = false;
            _logger.Error(ex, "Failed to create Start Menu shortcut for toast notifications");
        }
    }

    public bool Show(NotificationPlan plan, ExpiryThresholds thresholds)
    {
        if (!plan.HasItems || !_isShortcutReady)
        {
            return false;
        }

        try
        {
            var nearest = plan.DueCertificates
                .OrderBy(item => item.DaysRemaining)
                .ThenBy(item => item.Certificate.NotAfter)
                .First();
            var allThumbprints = string.Join(
                ";",
                plan.DueCertificates
                    .Select(item => item.Certificate.Thumbprint)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            var document = new XmlDocument();
            document.LoadXml(BuildToastXml(plan, nearest.Certificate.Thumbprint, allThumbprints, thresholds));

            var toast = new ToastNotification(document);
            toast.Activated += (_, args) =>
            {
                try
                {
                    if (args is ToastActivatedEventArgs activated)
                    {
                        Activated?.Invoke(this, new ToastActionEventArgs(activated.Arguments));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process toast activation");
                }
            };

            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to show toast notification");
            return false;
        }
    }

    private static string BuildToastXml(NotificationPlan plan, string nearestThumbprint, string allThumbprints, ExpiryThresholds thresholds)
    {
        var line = string.Join(" | ", new[]
        {
            FormatBucket($"até {thresholds.Level30} dias", plan.Count(ExpiryBucket.Days30)),
            FormatBucket($"até {thresholds.Level15} dias", plan.Count(ExpiryBucket.Days15)),
            FormatBucket($"até {thresholds.Level7} dias",  plan.Count(ExpiryBucket.Days7)),
            FormatBucket($"{thresholds.Level1} dia",       plan.Count(ExpiryBucket.Days1))
        }.Where(text => text.Length > 0));

        var title = EscapeXml("Certificados digitais próximos do vencimento");
        var summary = EscapeXml($"{plan.DueCertificates.Count} certificado(s) requerem atencao.");
        var buckets = EscapeXml(line);
        var thumbprint = EscapeXml(Uri.EscapeDataString(nearestThumbprint));
        var thumbprints = EscapeXml(Uri.EscapeDataString(allThumbprints));

        return $"""
        <toast launch="action=view-details">
          <visual>
            <binding template="ToastGeneric">
              <text>{title}</text>
              <text>{summary}</text>
              <text>{buckets}</text>
            </binding>
          </visual>
          <actions>
            <action content="Lembrar depois" arguments="action=remind-later" activationType="foreground" />
            <action content="Não lembrar este" arguments="action=dismiss-one&amp;thumbprint={thumbprint}" activationType="foreground" />
            <action content="Não lembrar nenhum" arguments="action=dismiss-all&amp;thumbprints={thumbprints}" activationType="foreground" />
            <action content="Configurar horario" arguments="action=configure-time" activationType="foreground" />
            <action content="Ver detalhes" arguments="action=view-details" activationType="foreground" />
          </actions>
        </toast>
        """;
    }

    private static string FormatBucket(string label, int count)
    {
        return count == 0 ? string.Empty : $"{label}: {count}";
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class CShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] string pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] string pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] string pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] string pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000138-0000-0000-C000-000000000046")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(PropertyKey key, PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey
    {
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    private static class PropertyKeys
    {
        public static readonly PropertyKey AppUserModelId = new(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            5);
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class PropVariant : IDisposable
    {
        private ushort _valueType;
        private ushort _wReserved1;
        private ushort _wReserved2;
        private ushort _wReserved3;
        private IntPtr _value;
        private IntPtr _value2;

        public PropVariant(string value)
        {
            _valueType = 31;
            _value = Marshal.StringToCoTaskMemUni(value);
        }

        ~PropVariant()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_value);
                _value = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}
