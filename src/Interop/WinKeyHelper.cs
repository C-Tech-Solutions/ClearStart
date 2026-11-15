using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>
/// Hands the Windows key to ClearStart.WinKey.exe (native/WinKey.cpp). While the keyboard belongs to a program
/// running as administrator, Windows does not call ClearStart's own hook (<see cref="WinKeyHook"/>); it does call the
/// helper's, which has "UI Access". Windows gives that only to a signed program in a folder under Program Files, so
/// the helper is used only from there. With no helper, one that does not start or one that stops, the key stays
/// with, or goes back to, ClearStart's own hook.
/// </summary>
internal sealed class WinKeyHelper : IDisposable
{
    public const string FileName = "ClearStart.WinKey.exe";

    // What the two say to each other: this message, with Hello or Tap, and the version of these rules. The helper's
    // side is in native/WinKey.cpp.
    internal const int Protocol = 1;
    internal const int Hello = 1, Tap = 2;
    internal static readonly int Message = (int)Native.RegisterWindowMessage("ClearStart.WinKey");

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Action _onTap, _onWatching, _onGone;
    private HwndSource? _window;
    private EventWaitHandle? _stop;
    private Process? _process;
    private bool _ended;

    /// <summary>All three are called on the thread that calls <see cref="Start"/>.</summary>
    /// <param name="onTap">The Windows key was tapped, or Ctrl+Esc pressed.</param>
    /// <param name="onWatching">The helper has the key from now on.</param>
    /// <param name="onGone">The helper did not start, or it has stopped. Called once at the most.</param>
    public WinKeyHelper(Action onTap, Action onWatching, Action onGone)
    {
        _onTap = onTap;
        _onWatching = onWatching;
        _onGone = onGone;
    }

    /// <summary>True from the helper saying that it watches the key until it stops.</summary>
    public bool Watching { get; private set; }

    /// <summary>The window the helper talks to.</summary>
    internal IntPtr Window => _window?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// The helper in a place where Windows gives it UI Access, or null. That is beside ClearStart if ClearStart is
    /// itself under Program Files, and otherwise Program Files\ClearStart, where for now it is put by hand.
    /// </summary>
    public static string? Find()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return programFiles.Length == 0 ? null : Find(AppContext.BaseDirectory, programFiles, File.Exists);
    }

    internal static string? Find(string appFolder, string programFiles, Func<string, bool> exists)
    {
        string root = Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar;
        var folders = new List<string>();
        if (appFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) folders.Add(appFolder);
        folders.Add(Path.Combine(root, "ClearStart"));
        return folders.Select(folder => Path.Combine(folder, FileName)).FirstOrDefault(exists);
    }

    /// <summary>Makes the window the helper talks to. Its messages arrive on the calling thread.</summary>
    internal void Listen()
    {
        _window = new HwndSource(new HwndSourceParameters("ClearStart Windows key")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>
    /// Starts the helper. What comes of it is told through the three callbacks: it says that it is watching, or it
    /// ends. There is no time limit on the first: right after sign-in a program can take a long time to start,
    /// and until it answers the key is with ClearStart's own hook.
    /// </summary>
    public void Start(string path)
    {
        Listen();
        var dispatcher = _window!.Dispatcher;
        // ClearStart cannot stop a program with UI Access, so it tells it to stop: the helper waits for this event.
        string stopName = $@"Local\ClearStart.WinKey.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        var start = new ProcessStartInfo(path)
        {
            // Through the shell: Windows starts a program with UI Access in no other way.
            UseShellExecute = true,
            Arguments = $"--watch {Environment.ProcessId} {Window.ToInt64()} {stopName}",
            WorkingDirectory = Path.GetDirectoryName(path),
        };
        // Not on this thread, which is drawing the bars: starting it can take a moment.
        Task.Run(() =>
        {
            try
            {
                var process = Process.Start(start);
                dispatcher.BeginInvoke(() => Started(process));
            }
            catch (Exception ex)
            {
                dispatcher.BeginInvoke(() => End("it did not start: " + ex.Message));
            }
        });
    }

    private void Started(Process? process)
    {
        if (_ended || process == null)
        {
            // Told to stop before it had started: it finds the stop event set, or gone, and ends. Or Windows gave
            // nothing to watch it by: then it can still say that it is watching, but not be seen to end.
            process?.Dispose();
            return;
        }
        _process = process;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            process.Exited += (_, _) => dispatcher.BeginInvoke(() => End(ExitReason(process)));
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn("The Windows-key helper cannot be watched for its end: " + ex.Message);
        }
    }

    private static string ExitReason(Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (Exception)
        {
            return "it stopped";
        }
        // The helper's exit codes (native/WinKey.cpp).
        return code switch
        {
            2 => "Windows did not give it UI Access, for which it has to be signed and in a folder under Program Files",
            3 => "it could not find this ClearStart",
            4 => "it could not install its hook",
            _ => $"it stopped (exit code {code})",
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Message) return IntPtr.Zero;
        handled = true;
        if (_ended) return IntPtr.Zero;

        long what = wParam.ToInt64(), protocol = lParam.ToInt64();
        if (protocol != Protocol)
        {
            End($"it is of another version (its messages are version {protocol}, this ClearStart's are version {Protocol})");
        }
        else if (what == Hello && !Watching)
        {
            Watching = true;
            _onWatching();
        }
        else if (what == Tap && Watching)
        {
            _onTap();
        }
        return IntPtr.Zero;
    }

    private void End(string why)
    {
        if (_ended) return;
        _ended = true;
        Watching = false;
        _stop?.Set();
        Log.Warn("The Windows-key helper is not used: " + why);
        _onGone();
    }

    public void Dispose()
    {
        _ended = true;
        Watching = false;
        _stop?.Set();
        try
        {
            // Its hook has to be gone before the key is ClearStart's own again, or nobody's.
            _process?.WaitForExit(1000);
        }
        catch (Exception)
        {
            // It ends with ClearStart in any case.
        }
        _process?.Dispose();
        _process = null;
        _stop?.Dispose();
        _stop = null;
        _window?.Dispose();
        _window = null;
    }
}
