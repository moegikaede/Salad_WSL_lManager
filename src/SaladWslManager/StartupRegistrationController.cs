using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static partial class Program
{
    private const string StartupShortcutName = "Salad WSL Manager.lnk";

    private static void ToggleStartupRegistration()
    {
        try
        {
            if (IsStartupRegistered())
            {
                UnregisterStartupShortcut();
                Log("startup_registration_disabled shortcut=" + GetStartupShortcutPath());
                SetTrayStatus("Startup registration disabled", System.Drawing.SystemIcons.Information);
            }
            else
            {
                RegisterStartupShortcut();
                Log("startup_registration_enabled shortcut=" + GetStartupShortcutPath());
                SetTrayStatus("Startup registration enabled", System.Drawing.SystemIcons.Information);
            }

            UpdateStartupRegistrationMenuCheck();
        }
        catch (Exception ex)
        {
            Log("startup_registration_toggle_error " + ex.Message);
            SetTrayStatus("Startup registration failed", System.Drawing.SystemIcons.Error);
        }
    }

    private static bool IsStartupRegistered()
    {
        try
        {
            return File.Exists(GetStartupShortcutPath());
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterStartupShortcut()
    {
        var shortcutPath = GetStartupShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("WScript.Shell is not available");
        }

        var shell = Activator.CreateInstance(shellType);
        object shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });
            var shortcutType = shortcut.GetType();
            SetComProperty(shortcutType, shortcut, "TargetPath", Application.ExecutablePath);
            SetComProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(Application.ExecutablePath));
            SetComProperty(shortcutType, shortcut, "Arguments", "");
            SetComProperty(shortcutType, shortcut, "Description", "Start Salad WSL Manager");
            SetComProperty(shortcutType, shortcut, "IconLocation", Application.ExecutablePath + ",0");
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void UnregisterStartupShortcut()
    {
        var shortcutPath = GetStartupShortcutPath();
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static string GetStartupShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupShortcutName);
    }

    private static void UpdateStartupRegistrationMenuCheck()
    {
        if (ShouldPostToUi())
        {
            PostToUi(UpdateStartupRegistrationMenuCheck);
            return;
        }

        if (startupRegistrationMenuItem != null)
        {
            startupRegistrationMenuItem.Checked = IsStartupRegistered();
        }
    }

    private static void SetComProperty(Type type, object instance, string name, object value)
    {
        type.InvokeMember(
            name,
            BindingFlags.SetProperty,
            null,
            instance,
            new[] { value });
    }

    private static void ReleaseComObject(object value)
    {
        if (value == null)
        {
            return;
        }

        try
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }
}
