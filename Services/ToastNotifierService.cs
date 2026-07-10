using System.Runtime.InteropServices;
using System.Windows.Forms;
using CertExpiryMonitor.Models;
using Microsoft.Win32;
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
    internal const string ProtocolScheme = "cert-expiry-monitor";
    internal const string DetailsProtocolUri = $"{ProtocolScheme}://details";

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
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;

            // ShellLink/PropertyStore/PersistFile sao RCWs sobre a MESMA instancia COM.
            // Liberamos explicitamente no finally para evitar acumular ref counts entre
            // restarts do app. Recriamos sempre: um .lnk pre-existente pode ter sido
            // tocado por outro processo, apontar para exe antigo ou nao conter AppUserModelID.
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
                EnsureProtocolHandler(executable);
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

    internal static string BuildProtocolCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Any(char.IsControl) || executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Caminho do executavel invalido para protocolo.", nameof(executable));
        }

        return $"\"{executable}\" --details \"%1\"";
    }

    private static void EnsureProtocolHandler(string executable)
    {
        using var protocolKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolScheme}");
        protocolKey?.SetValue(string.Empty, "URL:CertExpiryMonitor");
        protocolKey?.SetValue("URL Protocol", string.Empty);

        using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolScheme}\shell\open\command");
        commandKey?.SetValue(string.Empty, BuildProtocolCommand(executable));
    }

    public bool Show(NotificationPlan plan, ExpiryThresholds thresholds, bool soundEnabled)
    {
        if (!plan.HasItems || !_isShortcutReady)
        {
            return false;
        }

        try
        {
            var document = new XmlDocument();
            document.LoadXml(BuildToastXml(plan, thresholds, soundEnabled));

            var toast = new ToastNotification(document)
            {
                Priority = ToastNotificationPriority.High
            };
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

            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            if (!CanAttemptToast(notifier))
            {
                return false;
            }

            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to show toast notification");
            return false;
        }
    }

    private bool CanAttemptToast(ToastNotifier notifier)
    {
        try
        {
            var setting = notifier.Setting;
            if (setting == NotificationSetting.Enabled)
            {
                return true;
            }

            _logger.Info($"Windows toast notifications are not enabled for this app. Setting={setting}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Info(
                $"Windows toast notification setting could not be read; attempting toast anyway. " +
                $"Exception={ex.GetType().Name}; HResult=0x{ex.HResult:X8}");
            return true;
        }
    }

    internal static string BuildToastXml(NotificationPlan plan, ExpiryThresholds thresholds, bool soundEnabled = true)
        => ToastXmlBuilder.Build(plan, thresholds, soundEnabled);

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
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
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
