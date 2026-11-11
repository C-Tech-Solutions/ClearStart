using Microsoft.Win32;

namespace ClearStart;

/// <summary>
/// "Start with Windows": an entry under the signed-in user's Run key, which Windows starts at sign-in.
/// It needs no administrator rights and touches nothing else; removing the entry undoes it. The entry
/// starts ClearStart with no arguments, which is the real thing (in place of the Windows taskbar), not a preview.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager and Settings > Apps > Startup keep their own on/off switch for each Run entry here.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ClearStart";

    /// <summary>The program that is running now.</summary>
    public static string? ThisProgram => Environment.ProcessPath;

    /// <summary>The program Windows is set to start, or null when there is no entry.</summary>
    public static string? Target
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return ProgramIn(key?.GetValue(ValueName) as string);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the startup entry: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>True when Windows will start this very copy at sign-in.</summary>
    public static bool IsEnabled => SamePath(Target, ThisProgram) && !SwitchedOffInWindows();

    /// <summary>Adds the entry for this copy, or removes the entry.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string program = ThisProgram ?? throw new InvalidOperationException("Cannot tell where ClearStart is running from");
            key.SetValue(ValueName, "\"" + program + "\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // Either way Windows' own switch for the entry goes: left behind as "off" it would keep a new entry
        // from starting, and without an entry it is clutter.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);

        Log.Info(enabled ? "Start with Windows: on" : "Start with Windows: off");
    }

    /// <summary>What the settings window says under the switch.</summary>
    public static string Describe()
    {
        string? target = Target;
        if (target == null) return "Off: Windows does not start ClearStart.";
        if (!SamePath(target, ThisProgram))
        {
            return $"Windows is set to start another copy, {target}. Switch this on to start this copy instead.";
        }
        if (SwitchedOffInWindows())
        {
            return "Windows has it switched off in its own list of startup apps. Switch it on here to turn it back on.";
        }
        return $"On: Windows starts {target} when you sign in. If that folder is moved or renamed, " +
               "switch this off and on again.";
    }

    /// <summary>An odd first byte in Windows' own switch means the user turned the entry off there.</summary>
    private static bool SwitchedOffInWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The program in a command line: the quoted part, or everything up to ".exe".</summary>
    internal static string? ProgramIn(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
