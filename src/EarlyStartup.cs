using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClearStart;

/// <summary>Explicit opt-in to the experimental per-user shell loader. Never enabled by installation or migration.</summary>
internal static class EarlyStartup
{
    private const string ShellKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private static string Loader => Path.Combine(AppContext.BaseDirectory, "ClearStart.Loader.exe");
    private static string Command => $"\"{Loader}\" --shell";
    private static string? ReadShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ShellKey);
        return key?.GetValue("Shell", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    public static bool IsEnabled => string.Equals(ReadShell(), Command, StringComparison.OrdinalIgnoreCase);

    public static string? Target
    {
        get
        {
            string? shell = ReadShell();
            const string suffix = "\\ClearStart.Loader.exe\" --shell";
            if (shell == null || !shell.StartsWith('"') || !shell.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(shell[1..^suffix.Length], "ClearStart.exe");
        }
    }

    public static int RunCommand(string? value)
    {
        Log.Init();
        try
        {
            AutoStart.ChangeStartup(() =>
            {
                switch (value?.ToLowerInvariant())
                {
                    case "on": Enable(); break;
                    case "off": Disable(); break;
                    default: throw new ArgumentException("--early-startup takes on or off");
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Could not change early startup", ex);
            return 1;
        }
    }

    private static void Enable()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !Environment.Is64BitProcess)
            throw new NotSupportedException("Early startup currently requires native x64 Windows");
        foreach (string name in new[] { "ClearStart.Loader.exe", "ClearStart.ExplorerGuard.dll" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, name)))
                throw new FileNotFoundException("Build and stage the optional startup components first", name);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var policy = hive.OpenSubKey(PolicyKey);
            if (policy?.GetValue("Shell") != null)
                throw new InvalidOperationException("A shell policy is already configured; early startup leaves it alone");
        }
        string? current = ReadShell();
        // Only an absent per-user override is accepted. No backup/replacement of somebody else's shell.
        if (current != null && !IsEnabled)
            throw new InvalidOperationException("A per-user shell is already configured; early startup leaves it alone");
        if (AutoStart.Target is { } target && !string.Equals(target, AutoStart.ThisProgram, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Another ClearStart copy starts at sign-in; disable its startup entry first");
        using var machine = Registry.LocalMachine.OpenSubKey(ShellKey);
        if (!string.Equals(machine?.GetValue("Shell") as string, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The machine uses a custom shell; early startup leaves it alone");

        // Remove the old competing startup entry before the loader can become the sign-in path.
        AutoStart.RemoveOwnEntries();
        using var key = Registry.CurrentUser.CreateSubKey(ShellKey, writable: true);
        key.SetValue("Shell", Command, RegistryValueKind.String);
        Log.Info("Early startup enabled for the next sign-in. The current desktop was not changed.");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ShellKey, writable: true);
        if (string.Equals(key?.GetValue("Shell") as string, Command, StringComparison.OrdinalIgnoreCase))
            key!.DeleteValue("Shell", throwOnMissingValue: false);
        Log.Info("Early startup disabled for this copy. Windows will start its normal shell next time.");
    }
}
