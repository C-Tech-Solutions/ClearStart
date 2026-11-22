using System.Windows;
using ManagedShell.AppBar;

namespace ClearStart;

internal static partial class SelfTest
{
    /// <summary>
    /// Pinned apps dragged along a bar to new places, as a press and a move would drag them: on a bottom bar, on a side
    /// bar and on a bar that leaves one pin out. The bars are in a window far off-screen. The pins move in memory only,
    /// nothing is saved, and they are put back in their order at the end.
    /// </summary>
    private static async Task CheckPinDragAsync(ShellHost host, AppBarScreen screen, string directory,
        Dictionary<string, object?> report, List<string> errors)
    {
        var pins = host.Pins.Taskbar;
        if (pins.Count < 3)
        {
            report["pinDrag"] = "not tried: it needs three pinned apps";
            return;
        }

        var taskbar = Settings.Current.Taskbar;
        var original = pins.ToList();
        var pinnedBefore = taskbar.Pinned;
        var monitorsBefore = taskbar.Monitors;
        bool everyBarBefore = taskbar.PinsOnAllMonitors;
        var found = new Dictionary<string, object?>();

        string Order(IEnumerable<PinnedItem> list) => string.Join(" ", list.Select(pin => original.IndexOf(pin)));
        static List<PinnedItem> Shown(TaskbarView view) => view.AppButtons.Where(b => b.Pin != null).Select(b => b.Pin!).ToList();
        static List<T> Moved<T>(List<T> list, int from, int to)
        {
            var copy = list.ToList();
            var item = copy[from];
            copy.RemoveAt(from);
            copy.Insert(to, item);
            return copy;
        }

        // Where the pin goes among all of them: beside the shown pin it now comes before, or after the last shown one.
        static List<PinnedItem> Placed(List<PinnedItem> all, List<PinnedItem> shown, int from, int to)
        {
            var copy = all.ToList();
            var pin = shown[from];
            var others = shown.Where(p => !ReferenceEquals(p, pin)).ToList();
            copy.Remove(pin);
            if (to < others.Count) copy.Insert(copy.IndexOf(others[to]), pin);
            else copy.Insert(copy.IndexOf(others[^1]) + 1, pin);
            return copy;
        }

        void Check(string what, TaskbarView view, List<PinnedItem> shown, List<PinnedItem> all)
        {
            if (!Shown(view).SequenceEqual(shown))
                errors.Add($"Pin drag ({what}): the bar shows them as {Order(Shown(view))}, not {Order(shown)}");
            if (!pins.SequenceEqual(all))
                errors.Add($"Pin drag ({what}): the pins are {Order(pins)}, not {Order(all)}");
            if (taskbar.Pinned is not { } saved || !saved.SequenceEqual(pins.Select(pin => pin.Path)))
                errors.Add($"Pin drag ({what}): the settings do not list the pins in their new order");
        }

        // Once a drop has settled, every button is drawn where it is laid out.
        void Settled(string what, TaskbarView view)
        {
            var laid = view.PinMiddles();
            var drawn = view.PinMiddles(drawn: true);
            for (int i = 0; i < laid.Count; i++)
            {
                if (Math.Abs(laid[i] - drawn[i]) > 0.5)
                {
                    errors.Add($"Pin drag ({what}): pin {i + 1} is still drawn at {drawn[i]:0.#}, laid out at {laid[i]:0.#}");
                    return;
                }
            }
        }

        // Takes hold of a pin by its middle, moves the mouse to a place along the bar, and lets go or gives up.
        int Drag(string what, TaskbarView view, int index, Func<List<double>, double> to, bool drop)
        {
            if (!view.StartPinDragForTest(index))
            {
                errors.Add($"Pin drag ({what}): the drag did not start");
                return -1;
            }
            view.MovePinDragForTest(to(view.PinMiddles()));
            int place = view.PinDropPlace;
            view.EndPinDragForTest(drop);
            return place;
        }

        (TaskbarView View, Stage Stage) Open(AppBarEdge edge)
        {
            bool side = edge == AppBarEdge.Left;
            double thickness = side ? taskbar.Width : taskbar.Height;
            var view = new TaskbarView(host, screen, edge, thickness);
            if (side)
            {
                view.Width = thickness;
                view.HorizontalAlignment = HorizontalAlignment.Left;
                return (view, new Stage(view, thickness + 80, 900));
            }
            view.Height = thickness;
            view.VerticalAlignment = VerticalAlignment.Bottom;
            return (view, new Stage(view, 1600, thickness + 36));
        }

        try
        {
            // Every bar shows every pin, whatever the settings say; the choices are put back at the end.
            taskbar.PinsOnAllMonitors = true;
            taskbar.Monitors = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, monitor) in monitorsBefore)
            {
                taskbar.Monitors[name] = new MonitorSettings
                {
                    Edge = monitor.Edge,
                    Size = monitor.Size,
                    StartButton = monitor.StartButton,
                    Hidden = monitor.Hidden,
                };
            }

            var (bar, stage) = Open(AppBarEdge.Bottom);
            try
            {
                await Task.Delay(400);
                var shown = Shown(bar);
                int count = shown.Count;
                found["pins"] = count;

                // The first one past the middle of the third: the second and third slide back to make room.
                if (!bar.StartPinDragForTest(0)) throw new InvalidOperationException("the first drag did not start");
                var middles = bar.PinMiddles();
                bar.MovePinDragForTest(middles[2] + 1);
                if (bar.PinDropPlace != 2) errors.Add($"Pin drag (past the third): it would land in place {bar.PinDropPlace + 1}, not 3");
                await Task.Delay(300);
                var drawn = bar.PinMiddles(drawn: true);
                if (!(drawn[1] < middles[1] - 1 && drawn[2] < middles[2] - 1 && drawn[0] > middles[1]))
                    errors.Add("Pin drag (past the third): the buttons were not drawn where the drag puts them");
                found["midDrag"] = drawn.Take(4).Select(at => Math.Round(at, 1)).ToList();
                stage.Save(Path.Combine(directory, "taskbar-pin-drag.png"));
                bar.EndPinDragForTest(drop: true);
                var afterFirst = Moved(shown, 0, 2);
                Check("past the third", bar, afterFirst, Placed(original, shown, 0, 2));
                await Task.Delay(350);
                Settled("past the third", bar);
                stage.Save(Path.Combine(directory, "taskbar-pin-dropped.png"));
                found["afterFirstDrop"] = Order(pins);

                // A nudge smaller than the middle of the next button changes nothing.
                var all = pins.ToList();
                int place = Drag("a nudge", bar, 1, m => m[1] + 3, drop: true);
                if (place != 1) errors.Add($"Pin drag (a nudge): it would land in place {place + 1}, not 2");
                Check("a nudge", bar, afterFirst, all);

                // Far past the end: it goes no further than the last place.
                shown = Shown(bar);
                all = pins.ToList();
                place = Drag("past the end", bar, 0, m => m[^1] + 100000, drop: true);
                if (place != count - 1) errors.Add($"Pin drag (past the end): it would land in place {place + 1}, not {count}");
                Check("past the end", bar, Moved(shown, 0, count - 1), Placed(all, shown, 0, count - 1));

                // And back to the front from the last place.
                shown = Shown(bar);
                all = pins.ToList();
                place = Drag("to the front", bar, count - 1, m => m[0] - 100000, drop: true);
                if (place != 0) errors.Add($"Pin drag (to the front): it would land in place {place + 1}, not 1");
                Check("to the front", bar, afterFirst, Placed(all, shown, count - 1, 0));

                // Let go of with the mouse taken away: nothing moves, and the buttons slide back.
                all = pins.ToList();
                place = Drag("given up", bar, 0, m => m[2] + 1, drop: false);
                Check("given up", bar, afterFirst, all);
                await Task.Delay(350);
                Settled("given up", bar);
            }
            finally
            {
                stage.Close();
                bar.Detach();
            }

            // Down a side bar.
            (bar, stage) = Open(AppBarEdge.Left);
            try
            {
                await Task.Delay(400);
                var shown = Shown(bar);
                var all = pins.ToList();
                int place = Drag("side bar", bar, 0, m => m[1] + 1, drop: true);
                if (place != 1) errors.Add($"Pin drag (side bar): it would land in place {place + 1}, not 2");
                Check("side bar", bar, Moved(shown, 0, 1), Placed(all, shown, 0, 1));
                await Task.Delay(350);
                Settled("side bar", bar);
            }
            finally
            {
                stage.Close();
                bar.Detach();
            }

            // A bar that leaves the second pin out: the third goes before the first, and the second keeps its place
            // after the first among them all.
            var before = pins.ToList();
            host.ShowPinOn(screen.DeviceName, host.IsMainBar(screen), before[1].Path, false);
            (bar, stage) = Open(AppBarEdge.Bottom);
            try
            {
                await Task.Delay(400);
                var shown = Shown(bar);
                if (shown.Contains(before[1])) errors.Add("Pin drag (one left out): the bar still shows the pin left out");
                int place = Drag("one left out", bar, 1, m => m[0] - 1, drop: true);
                if (place != 0) errors.Add($"Pin drag (one left out): it would land in place {place + 1}, not 1");
                var expected = before.ToList();
                expected.Remove(before[2]);
                expected.Insert(0, before[2]);
                Check("one left out", bar, Moved(shown, 1, 0), expected);
                found["oneLeftOut"] = Order(pins);
            }
            finally
            {
                stage.Close();
                bar.Detach();
            }

            report["pinDrag"] = found;
        }
        finally
        {
            taskbar.PinsOnAllMonitors = everyBarBefore;
            taskbar.Monitors = monitorsBefore;
            for (int i = 0; i < original.Count; i++)
            {
                int at = pins.IndexOf(original[i]);
                if (at >= 0 && at != i) pins.Move(at, i);
            }
            taskbar.Pinned = pinnedBefore;
        }
    }
}
