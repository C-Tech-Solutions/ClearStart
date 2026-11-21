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

    // This copy's shell command, written down while early startup is on for it. Turning early startup off takes the
    // record away with the entry, and so does turning ordinary startup on. An entry that goes while the record stays
    // was not turned off by the user: the loader removed it (see PutBackIfDropped).
    private static string RecordFile => Path.Combine(Log.DataDirectory, "early-startup-on.txt");
    // True once the record has been brought in line with the registry, as this copy's shell started (see NoteAtStart).
    private static volatile bool _noted;

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
        AutoStart.RequireOwnEntries();
        using var machine = Registry.LocalMachine.OpenSubKey(ShellKey);
        if (!string.Equals(machine?.GetValue("Shell") as string, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The machine uses a custom shell; early startup leaves it alone");

        // Remove the old competing startup entry before the loader can become the sign-in path.
        AutoStart.RemoveOwnEntries();
        using var key = Registry.CurrentUser.CreateSubKey(ShellKey, writable: true);
        key.SetValue("Shell", Command, RegistryValueKind.String);
        try
        {
            Remember(RecordFile, Command);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write down that early startup is on, so ClearStart does not turn it back on if Explorer " +
                "starting again turns it off: " + ex.Message);
        }
        Log.Info("Early startup enabled for the next sign-in. The current desktop was not changed.");
        // A taskbar on the top or a side goes on the bottom before the loader starts Explorer (see TaskbarEdge).
        TaskbarEdge.RecordForEarlyStartup();
    }

    public static void Disable()
    {
        // The record goes first, and a failure stops here: with the entry gone and the record left, a running ClearStart
        // would turn early startup back on the next time Explorer starts again.
        Forget(RecordFile, Command);
        using var key = Registry.CurrentUser.OpenSubKey(ShellKey, writable: true);
        if (string.Equals(key?.GetValue("Shell") as string, Command, StringComparison.OrdinalIgnoreCase))
            key!.DeleteValue("Shell", throwOnMissingValue: false);
        Log.Info("Early startup disabled for this copy. Windows will start its normal shell next time.");
    }

    /// <summary>
    /// Ordinary startup has been turned on for this copy, so early startup is no longer what the user asked of it: an
    /// entry that went missing before is not put back over it. Only a warning if the record cannot be removed, since
    /// ordinary startup is in place by then. Called under the startup lock.
    /// </summary>
    public static void ForgetRecord()
    {
        try
        {
            Forget(RecordFile, Command);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove the record that early startup is on, so a running ClearStart may turn it back on " +
                "when Explorer starts again: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ staying on

    // Windows starts the user's shell command again when Explorer has been ended or has crashed, which is the loader's
    // with early startup on. Seen with 0.8.0, after Explorer was restarted by hand: within a second the entry was gone,
    // in a session where ClearStart had not been started by the loader and stayed up. The loader removes its entry when
    // it takes a start for a failed one.
    // Nothing then starts ClearStart at the next sign-in: turning early startup on took the ordinary startup entry
    // away. A ClearStart that is up and running beside the new Explorer puts the entry back.

    internal enum Step { None, Remember, Forget, PutBack }

    /// <summary>
    /// What the shell entry and the record call for. As this copy's shell starts, the registry is taken as it is and
    /// the record follows it: an entry that went while ClearStart was not running stays off, which keeps the loader's
    /// answer to a failed sign-in. Later, once Explorer has settled beside a running ClearStart, an entry that has gone
    /// with the record still there is put back. Never over another shell, and never by the ClearStart the loader
    /// started, which the loader itself is watching.
    /// </summary>
    /// <param name="shell">The user's shell command in the registry, or null when there is none.</param>
    /// <param name="recorded">True if the record says early startup is on for this copy.</param>
    /// <param name="command">This copy's shell command.</param>
    /// <param name="starting">True as this copy's shell starts; false once Explorer has settled beside it.</param>
    /// <param name="underLoader">True if the loader started this ClearStart.</param>
    internal static Step Next(string? shell, bool recorded, string command, bool starting, bool underLoader)
    {
        bool on = string.Equals(shell, command, StringComparison.OrdinalIgnoreCase);
        if (starting) return on == recorded ? Step.None : on ? Step.Remember : Step.Forget;
        return shell == null && recorded && !underLoader ? Step.PutBack : Step.None;
    }

    /// <summary>True if <paramref name="file"/> says early startup is on for the copy with this shell command.</summary>
    internal static bool Recorded(string file, string command) =>
        File.Exists(file) && string.Equals(File.ReadAllText(file).Trim(), command, StringComparison.OrdinalIgnoreCase);

    internal static void Remember(string file, string command)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, command);
    }

    /// <summary>Removes the record, unless it is another copy's.</summary>
    internal static void Forget(string file, string command)
    {
        if (Recorded(file, command)) File.Delete(file);
    }

    /// <summary>
    /// As this copy's shell starts in live mode: brings the record in line with the registry. Under the startup lock,
    /// as every change to the record is, so not for the window thread. Nothing is put back until this has run.
    /// </summary>
    public static void NoteAtStart()
    {
        try
        {
            AutoStart.ChangeStartup(() =>
            {
                switch (Next(ReadShell(), Recorded(RecordFile, Command), Command, starting: true, EarlySession.Active))
                {
                    case Step.Remember:
                        Remember(RecordFile, Command);
                        break;
                    case Step.Forget:
                        Forget(RecordFile, Command);
                        Log.Info("Early startup was turned off while ClearStart was not running. It stays off until it " +
                            "is turned on again, in Setup or with --early-startup on");
                        break;
                }
                _noted = true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check the record that early startup is on, so ClearStart does not turn it back on in " +
                "this session if Explorer starting again turns it off: " + ex.Message);
        }
    }

    /// <summary>
    /// Explorer has settled beside this running ClearStart: turns early startup back on if its entry went missing
    /// meanwhile, the same way and with the same checks as turning it on. Asks Task Scheduler, so not for the window
    /// thread.
    /// </summary>
    public static void PutBackIfDropped()
    {
        if (!_noted) return;
        try
        {
            if (!Dropped()) return;
            AutoStart.ChangeStartup(() =>
            {
                // Looked at again under the lock: early startup may have been turned off, or on, meanwhile.
                if (!Dropped()) return;
                Log.Info("Early startup was turned off while ClearStart was running, as Windows starting Explorer again " +
                    "does. Turning it back on");
                Enable();
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not check that early startup is still on, or turn it back on: " + ex.Message);
        }
    }

    private static bool Dropped() =>
        Next(ReadShell(), Recorded(RecordFile, Command), Command, starting: false, EarlySession.Active) == Step.PutBack;
}
