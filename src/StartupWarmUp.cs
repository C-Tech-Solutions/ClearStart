using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>
/// First-time work done on threads of their own while a live run waits for Explorer at sign-in, so the UI thread
/// finds it done when it builds the bars: the settings reader, the shell's look-up of the taskbar pins, the fonts
/// and the date and time formats the bars draw with, and the libraries they load.
///
/// Only what the whole process shares is warmed this way: loaded libraries, file and shell caches, fonts, the JSON
/// reader. Nothing made here is kept. Anything that belongs to the thread that makes it (a window, a WPF control or
/// style, a shell object kept for later, Windows Forms screens, system events) stays on the UI thread. A warm-up that
/// fails or is still running when the UI thread gets there only means the UI thread does that work itself.
/// </summary>
internal static class StartupWarmUp
{
    // How the names of the shell library, Windows Forms and what Windows Forms uses begin. The UI thread first needs
    // them while it builds the first bar.
    private static readonly string[] Libraries = { "ManagedShell", "System.Windows.Forms", "System.Drawing", "System.Private.Windows" };

    public static void Start()
    {
        Run("settings and pins", SettingsAndPins);
        Run("text", Text);
        Run("libraries", LoadLibraries);
    }

    private static void Run(string name, Action work)
    {
        try
        {
            var thread = new Thread(() =>
            {
                long started = StartTiming.BeginStep();
                try
                {
                    work();
                    Log.Info($"Start-up warm-up: {name}, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Start-up warm-up of {name} stopped: {ex.Message}");
                }
                finally
                {
                    // Measuring text gives the thread a dispatcher, and with it a hidden window; close both with the thread.
                    try
                    {
                        Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
                    }
                    catch
                    {
                        // The thread ends either way.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ClearStart warm-up: " + name,
            };
            // The shell and WPF expect a single-threaded apartment, as on the UI thread.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start the start-up warm-up of {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the settings, and asks the shell for each taskbar pin the way the pin list will. The shell items are let
    /// go at once: what is kept is the shell's own caches and the file system's, which the UI thread then finds warm.
    /// </summary>
    private static void SettingsAndPins()
    {
        var settings = Settings.ParseForWarmUp();
        Guid iid = Native.IID_IShellItem;
        foreach (string path in PinStore.TaskbarPinPaths(settings?.Taskbar.Pinned))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IntPtr item);
            if (item != IntPtr.Zero) Marshal.Release(item);
        }
    }

    /// <summary>
    /// Measures the kind of text the bars show, in the fonts they show it in (UiFont and IconFont in
    /// Themes/Styles.xaml): this loads the fonts and the text engine, and the culture's date and time formats.
    /// </summary>
    private static void Text()
    {
        var culture = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        string clock = string.Join(" ", now.ToString("t", culture), now.ToString("T", culture), now.ToString("d", culture),
            now.ToString("D", culture), string.Join(" ", culture.DateTimeFormat.DayNames), "0123456789");
        Measure("Segoe UI Variable Text, Segoe UI", clock, culture);
        // A few glyphs from the icon font the bars' buttons are drawn with.
        Measure("Segoe Fluent Icons, Segoe MDL2 Assets", "\uE700\uE721\uEC15\uE8BB\uE713", culture);
    }

    private static void Measure(string family, string text, CultureInfo culture)
    {
        var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _ = new FormattedText(text, culture, FlowDirection.LeftToRight, typeface, 12, Brushes.White, 1.0).Width;
    }

    /// <summary>Loads the shell library and Windows Forms, and what they use in turn. Loading runs none of their code.</summary>
    private static void LoadLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        pending.Enqueue(typeof(StartupWarmUp).Assembly);
        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                string name = reference.Name ?? "";
                if (!Libraries.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || !seen.Add(name))
                {
                    continue;
                }
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch
                {
                    // Loaded when it is first used instead.
                }
            }
        }
    }
}
