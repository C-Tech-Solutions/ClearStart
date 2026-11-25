namespace ClearStart;

// Keeping the bars' room on the desktop after the displays change, worked out on made-up bars: no window is docked
// and nothing is read from Windows.
internal static partial class SelfTest
{
    private sealed class RoomTrialBar(string name) : IRoomBar
    {
        public string Name { get; } = "self-test " + name;
        public bool HasRoom { get; set; } = true;
        // The redock that wins the bar its room back; 0 for never.
        public int RoomAtRedock { get; set; }
        public bool RoomOnDockAgain { get; set; }
        public int Redocks { get; private set; }
        public int DocksAgain { get; private set; }

        public void Redock()
        {
            Redocks++;
            if (RoomAtRedock > 0 && Redocks >= RoomAtRedock) HasRoom = true;
        }

        public void DockAgain()
        {
            DocksAgain++;
            if (RoomOnDockAgain) HasRoom = true;
        }
    }

    private static List<string> CheckRoomWatch(List<string> errors)
    {
        var steps = new List<string>();

        // Every bar has its room: one look, nothing docked, and the watch stops.
        var one = new RoomTrialBar("one");
        var two = new RoomTrialBar("two");
        var bars = new List<IRoomBar> { one, two };
        var watch = new RoomWatch(() => bars, null);
        watch.Watch(lookNow: true);
        if (watch.Running || one.Redocks + two.Redocks + one.DocksAgain + two.DocksAgain != 0)
        {
            errors.Add("Room watch: with every bar in its room, a bar was docked or the watch went on");
        }
        steps.Add("all bars in their room: stops at once");

        // A bar that wins its room back on the third redock: only that bar is docked, and the watch stops then.
        two.HasRoom = false;
        two.RoomAtRedock = 3;
        watch.Watch(lookNow: false);
        for (int look = 0; look < 3; look++) watch.Look();
        if (watch.Running || two.Redocks != 3 || two.DocksAgain != 0 || one.Redocks != 0 || !two.HasRoom)
        {
            errors.Add($"Room watch: a bar back in its room on the third redock gave {two.Redocks} redocks, " +
                $"{two.DocksAgain} docked anew, the other bar {one.Redocks} redocks, running={watch.Running}");
        }
        steps.Add("a bar back in its room after three looks: stops then");

        // A bar that redocking does not help: docked anew through Explorer at its fifth look, which wins it back.
        var three = new RoomTrialBar("three") { HasRoom = false, RoomOnDockAgain = true };
        bars = new List<IRoomBar> { one, three };
        watch.Watch(lookNow: false);
        for (int look = 0; look < RoomWatch.DockAnewAt; look++) watch.Look();
        if (watch.Running || three.DocksAgain != 1 || three.Redocks != RoomWatch.DockAnewAt - 1)
        {
            errors.Add($"Room watch: a bar only docking anew helps gave {three.Redocks} redocks and {three.DocksAgain} " +
                $"docked anew, running={watch.Running}");
        }
        steps.Add($"docked anew through Explorer at look {RoomWatch.DockAnewAt}");

        // A bar that never gets its room: tried for the whole watch, then left until the displays change.
        var four = new RoomTrialBar("four") { HasRoom = false };
        bars = new List<IRoomBar> { one, four };
        watch.Watch(lookNow: false);
        for (int look = 0; look < RoomWatch.Looks + 3; look++) watch.Look();
        if (watch.Running || !watch.GaveUp || four.Redocks + four.DocksAgain != RoomWatch.Looks || four.DocksAgain != 1)
        {
            errors.Add($"Room watch: a bar that never gets its room was tried {four.Redocks} + {four.DocksAgain} times, " +
                $"running={watch.Running}, gave up={watch.GaveUp}");
        }
        steps.Add($"a bar that never gets its room: tried {RoomWatch.Looks} times, then left");

        // Windows saying a monitor's area changed does not start the watch again after it gave up...
        watch.WorkAreaChanged();
        if (watch.Running) errors.Add("Room watch: a change to a monitor's area started a watch after it gave up");
        // ...but the displays changing does.
        watch.Watch(lookNow: false);
        if (!watch.Running || watch.GaveUp) errors.Add("Room watch: a change to the displays did not start a watch after it gave up");
        watch.Stop();
        steps.Add("after giving up, only a change to the displays watches again");

        // Windows saying a monitor's area changed: no watch while every bar has its room, one when a bar lost it.
        four.HasRoom = true;
        watch.WorkAreaChanged();
        if (watch.Running) errors.Add("Room watch: a change to a monitor's area started a watch with every bar in its room");
        one.HasRoom = false;
        one.RoomAtRedock = one.Redocks + 1;
        watch.WorkAreaChanged();
        if (!watch.Running) errors.Add("Room watch: a change to a monitor's area did not start a watch for a bar without its room");
        watch.Look();
        if (watch.Running || !one.HasRoom) errors.Add("Room watch: the watch a change to a monitor's area started did not dock the bar");
        steps.Add("a change to a monitor's area: watches only when a bar lost its room");

        // A bar that goes away during a watch, closed for a new layout, is no longer looked for.
        var five = new RoomTrialBar("five") { HasRoom = false };
        bars = new List<IRoomBar> { one, five };
        watch.Watch(lookNow: true);
        bars = new List<IRoomBar> { one };
        int redocks = five.Redocks;
        watch.Look();
        if (watch.Running || five.Redocks != redocks) errors.Add("Room watch: a bar that went away was still docked, or kept the watch going");
        steps.Add("a bar that goes away is no longer looked for");

        return steps;
    }
}
