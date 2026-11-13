using System.Security;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ClearStart;

/// <summary>
/// "Start with Windows".
///
/// ClearStart is started by a task in Task Scheduler that runs as the user when they sign in. Windows runs it as the
/// session starts, without waiting for Explorer, so ClearStart can hide the Windows taskbar as it first appears.
/// Earlier versions used an entry under the user's Run key instead. Windows starts those only once Explorer is up,
/// and then holds them back (by more than a minute and a half, on the machine this was written on), so the Windows
/// taskbar showed until ClearStart arrived. A live run moves such an entry to the task (<see cref="MoveToTask"/>).
///
/// A user can make a task that runs as themselves without administrator rights. If Task Scheduler refuses anyway,
/// the Run key entry is the fallback. Both start ClearStart with no arguments, which is the real thing (in place of
/// the Windows taskbar), not a preview. Removing the task and the entry undoes it; nothing else is touched.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager and Settings > Apps > Startup keep their own on/off switch for each Run entry here.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ClearStart";

    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    private const int ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);
    private static readonly XNamespace TaskXml = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly Lazy<string?> UserSid = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    });

    /// <summary>
    /// The task's name. Every user's tasks share one list, so it carries the user's ID, as other per-user tasks do.
    /// Null if Windows does not say who the user is.
    /// </summary>
    private static string? TaskName => UserSid.Value is { } sid ? $"ClearStart at sign-in ({sid})" : null;

    /// <summary>The program that is running now.</summary>
    public static string? ThisProgram => Environment.ProcessPath;

    /// <summary>The program Windows is set to start, or null when it starts none.</summary>
    public static string? Target => EarlyStartup.Target is { } early ? early
        : ReadTask() is { } task ? task.Program : RunEntryTarget();

    /// <summary>True when Windows will start this very copy at sign-in.</summary>
    public static bool IsEnabled
    {
        get
        {
            return EarlyStartup.IsEnabled || (ReadTask() is { Enabled: true } task && SamePath(task.Program, ThisProgram))
                || (SamePath(RunEntryTarget(), ThisProgram) && !SwitchedOffInWindows());
        }
    }

    /// <summary>Sets Windows to start this copy at sign-in, or to start no copy.</summary>
    public static void Set(bool enabled) => ChangeStartup(() => SetCore(enabled));

    private static void SetCore(bool enabled)
    {
        if (!enabled)
        {
            EarlyStartup.Disable();
            // The Run key first: it is what is used when Task Scheduler cannot be reached, and then so is deleting the task.
            DeleteRunEntry();
            DeleteTask();
            Log.Info("Start with Windows: off");
            return;
        }

        if (EarlyStartup.IsEnabled) return;
        if (EarlyStartup.Target != null)
            throw new InvalidOperationException("Another copy uses early startup. Disable it from that copy before changing startup here.");

        string program = ThisProgram ?? throw new InvalidOperationException("Cannot tell where ClearStart is running from");
        if (RegisterTask(program))
        {
            DeleteRunEntry();
            Log.Info("Start with Windows: on, by a task in Task Scheduler");
        }
        else
        {
            try { DeleteTask(); }
            catch (Exception ex) { Log.Warn("Could not remove the old sign-in task: " + ex.Message); }
            WriteRunEntry(program);
            Log.Info("Start with Windows: on, by the Run key (Task Scheduler would not take the task)");
        }
    }

    /// <summary>
    /// Moves "Start with Windows" from the Run key entry an earlier version wrote to the task, which starts ClearStart
    /// sooner. Only when the entry starts this copy and is not switched off in Windows; otherwise nothing changes.
    /// </summary>
    public static void MoveToTask()
    {
        try
        {
            ChangeStartup(() =>
            {
                if (EarlyStartup.IsEnabled) return;
                string? program = ThisProgram;
                if (program == null || !SamePath(RunEntryTarget(), program) || SwitchedOffInWindows()) return;
                if (!RegisterTask(program)) return;
                DeleteRunEntry();
                Log.Info("Start with Windows moved from the Run key to a task in Task Scheduler, which starts ClearStart sooner");
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not move Start with Windows to Task Scheduler: " + ex.Message);
        }
    }

    /// <summary>
    /// "ClearStart.exe --start-with-windows on|off", which Setup runs. "on" is the settings switch. "off" takes away
    /// only what starts this copy, so uninstalling one copy leaves another copy's start alone. Returns the exit code.
    /// </summary>
    public static int RunCommand(string? value)
    {
        Log.Init();
        try
        {
            switch (value?.ToLowerInvariant())
            {
                case "on":
                    Set(true);
                    return 0;
                case "off":
                    ChangeStartup(() =>
                    {
                        EarlyStartup.Disable();
                        RemoveOwnEntries();
                    });
                    Log.Info("Start with Windows: off for this copy");
                    return 0;
                default:
                    Log.Warn($"--start-with-windows takes \"on\" or \"off\", not \"{value}\"");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change Start with Windows", ex);
            return 1;
        }
    }

    /// <summary>What the settings window says under the switch.</summary>
    public static string Describe()
    {
        if (EarlyStartup.IsEnabled)
            return "On: the experimental early-startup loader starts Explorer and ClearStart at sign-in. Switch this off to return to normal Windows startup.";
        if (EarlyStartup.Target is { } early)
            return $"Early startup is set for another copy, {early}. Disable early startup from that copy first.";
        var task = ReadTask();
        string? runTarget = RunEntryTarget();
        if (SamePath(runTarget, ThisProgram) && !SwitchedOffInWindows()
            && !(task is { Enabled: true } activeTask && SamePath(activeTask.Program, ThisProgram)))
        {
            return $"On: Windows starts {runTarget} from its Startup list."
                + (task is { Enabled: true } ? $" A sign-in task also starts {task.Value.Program}." : "");
        }
        string? target = task?.Program ?? runTarget;
        if (target == null) return "Off: Windows does not start ClearStart.";
        if (!SamePath(target, ThisProgram))
        {
            return $"Windows is set to start another copy, {target}. Switch this on to start this copy instead.";
        }
        if (task is { Enabled: false })
        {
            return "It is switched off in Task Scheduler. Switch it on here to turn it back on.";
        }
        if (task == null && SwitchedOffInWindows())
        {
            return "Windows has it switched off in its own list of startup apps. Switch it on here to turn it back on.";
        }
        return task != null
            ? $"On: Windows starts {target} as soon as you sign in. If that folder is moved or renamed, " +
              "switch this off and on again."
            : $"On: Windows starts {target} once you have signed in and the desktop is up. If that folder is moved " +
              "or renamed, switch this off and on again.";
    }

    // ------------------------------------------------------------------ the task

    /// <summary>Serializes migration and explicit changes, including Setup's separate process.</summary>
    internal static void ChangeStartup(Action change)
    {
        string sid = UserSid.Value ?? throw new InvalidOperationException("Cannot identify the startup task's user");
        using var mutex = new Mutex(false, @"Global\ClearStart.Startup." + sid);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Another ClearStart copy is changing Start with Windows; try again");
        }
        catch (AbandonedMutexException)
        {
            // We own it now; re-read the entries inside the operation.
        }
        try { change(); }
        finally { mutex.ReleaseMutex(); }
    }

    // Called while ChangeStartup holds the per-user lock, including early-startup opt-in and uninstall.
    internal static void RemoveOwnEntries()
    {
        if (SamePath(RunEntryTarget(), ThisProgram)) DeleteRunEntry();
        if (SamePath(ReadTask(throwOnError: true)?.Program, ThisProgram)) DeleteTask();
    }

    internal static void RequireOwnEntries()
    {
        foreach (string? target in new[] { ReadTask(throwOnError: true)?.Program, RunEntryTarget() })
            if (target != null && !SamePath(target, ThisProgram))
                throw new InvalidOperationException("Another ClearStart copy starts at sign-in; disable its startup entry first");
    }

    private readonly record struct TaskInfo(bool Enabled, string? Program);

    /// <summary>The task, or null when there is none (or Task Scheduler cannot be asked).</summary>
    private static TaskInfo? ReadTask(bool throwOnError = false)
    {
        if (TaskName is not { } name) return null;
        try
        {
            dynamic folder = TaskFolder();
            dynamic task;
            try
            {
                task = folder.GetTask(name);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                return null;
            }
            bool enabled = task.Enabled;
            string xml = task.Xml;
            string? command = XDocument.Parse(xml).Descendants(TaskXml + "Exec")
                .Elements(TaskXml + "Command").Select(c => c.Value).FirstOrDefault();
            return new TaskInfo(enabled, ProgramIn(command));
        }
        catch (Exception ex) when (!throwOnError)
        {
            Log.Warn("Could not read the sign-in task: " + ex.Message);
            return null;
        }
    }

    /// <summary>Makes or replaces the task so that it starts this program. False if Task Scheduler would not take it.</summary>
    private static bool RegisterTask(string program)
    {
        if (TaskName is not { } name || UserSid.Value is not { } sid) return false;
        try
        {
            string folder = Path.GetDirectoryName(program) ?? "";
            string xml = $"""
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>Starts ClearStart in place of the Windows taskbar when you sign in. Switch it off in ClearStart's settings, under Start with Windows.</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{sid}</UserId>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{sid}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>LeastPrivilege</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <Enabled>true</Enabled>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>"{SecurityElement.Escape(program)}"</Command>
                      <WorkingDirectory>{SecurityElement.Escape(folder)}</WorkingDirectory>
                    </Exec>
                  </Actions>
                </Task>
                """;
            // Runs as the user, only while they are signed in, with no stored password and no raised rights.
            // No time limit and no battery rules (Task Scheduler's own defaults would stop a taskbar), and normal
            // priority (its default is below normal).
            TaskFolder().RegisterTask(name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Task Scheduler would not take the sign-in task: " + ex.Message);
            return false;
        }
    }

    private static void DeleteTask()
    {
        if (TaskName is not { } name) return;
        try
        {
            TaskFolder().DeleteTask(name, 0);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            // Already gone.
        }
    }

    /// <summary>Task Scheduler's top folder, where tasks that are not part of Windows go.</summary>
    private static dynamic TaskFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    private static bool IsNotFound(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.HResult == ERROR_FILE_NOT_FOUND) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the Run key entry

    /// <summary>The program the Run key entry starts, or null when there is no entry.</summary>
    private static string? RunEntryTarget()
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

    private static void WriteRunEntry(string program)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            key.SetValue(ValueName, "\"" + program + "\"", RegistryValueKind.String);
        }
        DeleteApproval();
    }

    private static void DeleteRunEntry()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        DeleteApproval();
    }

    /// <summary>
    /// Windows' own switch for the entry goes with any change: left behind as "off" it would keep a new entry from
    /// starting, and without an entry it is clutter.
    /// </summary>
    private static void DeleteApproval()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
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

    // ------------------------------------------------------------------ paths

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
