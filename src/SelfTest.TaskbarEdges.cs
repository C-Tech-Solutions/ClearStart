using Rectangle = System.Drawing.Rectangle;

namespace ClearStart;

// The Windows taskbar on each edge, worked out on made-up monitors and taskbar windows: whether ClearStart takes it for
// one on the bottom, which edge its own bars go on, and the record it keeps of the edges the taskbar was moved from.
// Nothing is read from Windows or written to it, and Explorer is not touched.
internal static partial class SelfTest
{
    private static List<string> CheckTaskbarEdges(List<string> errors)
    {
        var steps = new List<string>();
        static Native.RECT Rect(int left, int top, int right, int bottom) =>
            new() { Left = left, Top = top, Right = right, Bottom = bottom };
        var one = new List<Rectangle> { new(0, 0, 1920, 1080) };
        // Side by side: a taskbar on the right of the first monitor is on the edge between them.
        var pair = new List<Rectangle> { new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080) };
        // One above the other: an auto-hidden taskbar on the bottom of the upper one reaches into the lower one.
        var stacked = new List<Rectangle> { new(0, 0, 1920, 1080), new(0, 1080, 1920, 1080) };

        // Only a taskbar on the bottom is left where it is; one on any other edge is moved.
        void ExpectBottom(string what, List<Rectangle> monitors, bool bottom, params Native.RECT[] bars)
        {
            bool now = ExplorerTaskbar.AllOnBottom(bars, monitors);
            steps.Add($"{what}: {(now ? "on the bottom" : "moved")}");
            if (now != bottom) errors.Add($"Taskbar edges ({what}): taken for {(now ? "one on the bottom" : "one to move")}");
        }
        ExpectBottom("bottom", one, true, Rect(0, 1032, 1920, 1080));
        ExpectBottom("bottom, auto-hidden over the monitor below", stacked, true, Rect(0, 1078, 1920, 1126));
        ExpectBottom("top", one, false, Rect(0, 0, 1920, 48));
        ExpectBottom("left", one, false, Rect(0, 0, 62, 1080));
        ExpectBottom("right", one, false, Rect(1858, 0, 1920, 1080));
        ExpectBottom("left, auto-hidden", one, false, Rect(-60, 0, 2, 1080));
        ExpectBottom("right, auto-hidden", one, false, Rect(1918, 0, 1980, 1080));
        ExpectBottom("left on both of two monitors", pair, false, Rect(0, 0, 62, 1080), Rect(1920, 0, 1982, 1080));
        ExpectBottom("right on both of two monitors", pair, false, Rect(1858, 0, 1920, 1080), Rect(3778, 0, 3840, 1080));
        ExpectBottom("bottom on one monitor, right on the other", pair, false,
            Rect(0, 1032, 1920, 1080), Rect(3778, 0, 3840, 1080));

        // ClearStart's bars go on the edge the taskbar was on, from where its windows were before it was moved. Windows
        // 11 does not auto-hide a taskbar on the top or a side, so they were in full view.
        void ExpectEdge(string what, Rectangle monitor, BarEdge edge, params Native.RECT[] bars)
        {
            var now = ExplorerTaskbar.EdgeOn(Rect(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom), bars.ToList());
            steps.Add($"{what}: bar on the {now?.ToString() ?? "none"}");
            if (now != edge) errors.Add($"Taskbar edges ({what}): ClearStart's bar goes on {now?.ToString() ?? "no edge"}, not {edge}");
        }
        var left = new[] { Rect(0, 0, 62, 1080), Rect(1920, 0, 1982, 1080) };
        var right = new[] { Rect(1858, 0, 1920, 1080), Rect(3778, 0, 3840, 1080) };
        ExpectEdge("left, first monitor", pair[0], BarEdge.Left, left);
        ExpectEdge("left, second monitor", pair[1], BarEdge.Left, left);
        ExpectEdge("right, first monitor", pair[0], BarEdge.Right, right);
        ExpectEdge("right, second monitor", pair[1], BarEdge.Right, right);
        ExpectEdge("top", one[0], BarEdge.Top, Rect(0, 0, 1920, 48));
        ExpectEdge("bottom", one[0], BarEdge.Bottom, Rect(0, 1032, 1920, 1080));

        // The record of the edges (0 left, 1 top, 2 right), written down before the move and read back to put them back.
        foreach (var (main, other, named) in new (byte, byte, string)[]
                 { (0, 0, "left edge"), (2, 2, "right edge"), (0, 2, "left and right edge"), (1, 0, "top and left edge") })
        {
            var entries = new List<TaskbarEdge.Entry>
            {
                new(TaskbarEdge.MainKey, TaskbarEdge.MainValue, main),
                new(TaskbarEdge.OtherMonitorsKey, "MONITOR2", other),
            };
            var lines = entries.Select(TaskbarEdge.RecordLine).Append("bar\t0\t0\t62\t1080");
            var read = TaskbarEdge.ParseRecord(lines);
            string described = TaskbarEdge.Describe(read);
            steps.Add($"record {main}/{other}: {described}");
            if (!read.SequenceEqual(entries)) errors.Add($"Taskbar edges: the record of {named} does not read back as written");
            if (described != named) errors.Add($"Taskbar edges: the record of {named} is described as \"{described}\"");
        }
        return steps;
    }
}
