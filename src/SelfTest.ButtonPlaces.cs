using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ManagedShell.AppBar;

namespace ClearStart;

internal static partial class SelfTest
{
    /// <summary>
    /// Where a window's button is, which Windows asks before it minimizes or restores the window. A bottom bar and
    /// a side bar are put in a window far off-screen and asked for the windows they list; then the question is put
    /// to ClearStart's answering window the way Windows puts it, for made-up windows. That window is not registered
    /// with Windows here, so no window on the desktop minimizes any differently.
    /// </summary>
    private static async Task CheckButtonPlacesAsync(ShellHost host, AppBarScreen screen,
        Dictionary<string, object?> report, List<string> errors)
    {
        var found = new List<object>();
        foreach (var edge in new[] { AppBarEdge.Bottom, AppBarEdge.Left })
        {
            bool side = edge == AppBarEdge.Left;
            double thickness = side ? Settings.Current.Taskbar.Width : Settings.Current.Taskbar.Height;
            var view = new TaskbarView(host, screen, edge, thickness);
            Stage stage;
            if (side)
            {
                view.Width = thickness;
                view.HorizontalAlignment = HorizontalAlignment.Left;
                stage = new Stage(view, thickness + 80, 900);
            }
            else
            {
                view.Height = thickness;
                view.VerticalAlignment = VerticalAlignment.Bottom;
                stage = new Stage(view, 1600, thickness + 36);
            }

            try
            {
                await Task.Delay(400);
                string name = side ? "side bar" : "bottom bar";
                if (PresentationSource.FromVisual(view) is not HwndSource source
                    || !Native.GetWindowRect(source.Handle, out var window))
                {
                    errors.Add($"Button places ({name}): the bar is in no window");
                    continue;
                }

                var running = view.AppButtons.Where(b => b.IsRunning).ToList();
                var places = new List<Native.RECT>();
                foreach (var button in running)
                {
                    if (view.ButtonPlace(button.First!.Handle) is not { } place)
                    {
                        errors.Add($"Button places ({name}): no place for the button of \"{button.Title}\"");
                        continue;
                    }
                    places.Add(place);
                    if (place.Width <= 0 || place.Height <= 0
                        || place.Left < window.Left || place.Top < window.Top
                        || place.Right > window.Right || place.Bottom > window.Bottom)
                    {
                        errors.Add($"Button places ({name}): \"{button.Title}\" is at {Text(place)}, outside the bar's window at {Text(window)}");
                    }
                    // Every window behind one button shrinks into that button.
                    foreach (var other in button.Windows.Skip(1))
                    {
                        if (view.ButtonPlace(other.Handle) is not { } same || Text(same) != Text(place))
                            errors.Add($"Button places ({name}): two windows of \"{button.Title}\" have different places");
                    }
                }

                // Along the bar the buttons follow one another: left to right on a bottom bar, downwards on a side
                // bar. Buttons scrolled out of the row all count as at its end, so "not before" is all that holds
                // for those.
                for (int i = 1; i < places.Count; i++)
                {
                    var before = places[i - 1];
                    var after = places[i];
                    bool whole = Math.Min(before.Width, before.Height) >= 8 && Math.Min(after.Width, after.Height) >= 8;
                    int step = side ? after.Top - before.Top : after.Left - before.Left;
                    int across = side ? after.Left - before.Left : after.Top - before.Top;
                    if (step < 0 || (whole && step == 0) || (whole && Math.Abs(across) > 1))
                    {
                        errors.Add($"Button places ({name}): button {i + 1} is at {Text(after)}, after one at {Text(before)}");
                        break;
                    }
                }

                // A window that has no button here gets no place.
                if (view.ButtonPlace(source.Handle) != null)
                    errors.Add($"Button places ({name}): a window without a button was given a place");

                found.Add(new
                {
                    bar = name,
                    buttonsWithWindows = running.Count,
                    first = places.Count > 0 ? Text(places[0]) : null,
                    last = places.Count > 0 ? Text(places[^1]) : null,
                    barWindow = Text(window),
                });
            }
            finally
            {
                stage.Close();
                view.Detach();
            }
        }

        // The question does not depend on what is open on the desktop: two made-up windows, one with a button on a
        // display left of the main one (so its place has numbers below zero) and one without a button.
        var listed = new IntPtr(0x5101);
        var listedPlace = new Native.RECT { Left = -1900, Top = 1040, Right = -1856, Bottom = 1080 };
        CheckQuestion(asked => asked == listed ? listedPlace : null, listed, new IntPtr(0x5102), errors);

        report["buttonPlaces"] = found;
    }

    /// <summary>Puts the question the way Windows does: to the answering window, with the window's handle and room for the place.</summary>
    private static void CheckQuestion(Func<IntPtr, Native.RECT?> placeOf, IntPtr listed, IntPtr unlisted, List<string> errors)
    {
        (bool Answered, Native.RECT Place) Ask(MinimizeTarget target, IntPtr window)
        {
            IntPtr block = Marshal.AllocHGlobal(IntPtr.Size + 8);
            try
            {
                Marshal.WriteIntPtr(block, window);
                for (int i = 0; i < 4; i++) Marshal.WriteInt16(block, IntPtr.Size + i * 2, 7777);
                IntPtr result = Native.SendMessage(target.Window, (uint)MinimizeTarget.ShellMessage,
                    new IntPtr(MinimizeTarget.Question), block);
                return (result != IntPtr.Zero, new Native.RECT
                {
                    Left = Marshal.ReadInt16(block, IntPtr.Size),
                    Top = Marshal.ReadInt16(block, IntPtr.Size + 2),
                    Right = Marshal.ReadInt16(block, IntPtr.Size + 4),
                    Bottom = Marshal.ReadInt16(block, IntPtr.Size + 6),
                });
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }

        using (var target = new MinimizeTarget(placeOf))
        {
            var expected = placeOf(listed);
            var (answered, place) = Ask(target, listed);
            if (!answered || expected == null || Text(place) != Text(expected.Value))
                errors.Add($"Button places: asked as Windows asks, the answer was {(answered ? Text(place) : "none")}, not {(expected == null ? "none" : Text(expected.Value))}");

            (answered, place) = Ask(target, unlisted);
            if (answered || Text(place) != "7777,7777,7777,7777")
                errors.Add($"Button places: a window without a button was answered for ({Text(place)})");

            // Other news for the same windows is not the question.
            IntPtr other = Native.SendMessage(target.Window, (uint)MinimizeTarget.ShellMessage, new IntPtr(1), listed);
            if (other != IntPtr.Zero) errors.Add("Button places: news other than the question was answered");
        }

        // A place the 16-bit numbers cannot hold is not answered at all.
        using var far = new MinimizeTarget(_ => new Native.RECT { Left = 40000, Top = 0, Right = 40040, Bottom = 40 });
        if (Ask(far, listed).Answered) errors.Add("Button places: a place too far out for the answer was answered");
    }

    private static string Text(Native.RECT place) => $"{place.Left},{place.Top},{place.Right},{place.Bottom}";
}
