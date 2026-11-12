using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ClearStart;
using ManagedShell.ShellFolders;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // These controls never acquire a window handle. No ClearStart host, Explorer hooks, settings files or screenshots.
        LazyPins();
        Together();
        FallbackAndReuse();
        AnimationAndDetach();
        Console.WriteLine("PASS: lazy pins, taskbar icon priority, grouped reveal, fallback/rebinding, animation and disposal");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static ImageSource Icon()
    {
        var icon = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        icon.Freeze();
        return icon;
    }

    private static void LazyPins()
    {
        Settings.Current.Taskbar.Pinned = ["task-a", "task-b", "task-a", ""];
        Settings.Current.StartMenu.Pinned = ["menu-old"];
        var pins = new PinStore();
        pins.Reload();
        Check(pins.Taskbar.Count == 2 && ShellItem.Created.Count == 2, "Startup must not construct Start-menu shell items");
        Check(ShellItem.LargeReads == 2 && ShellItem.ExtraLargeReads == 0, "Only taskbar-sized icons should be prefetched");
        pins.PinToTaskbar("task-c");
        pins.UnpinFromTaskbar("task-a");
        Check(!ShellItem.Created.Contains("menu-old"), "Saving taskbar pins must not load Start pins");
        Settings.Current.StartMenu.Pinned = ["menu-new"];
        pins.Reload();
        var start = pins.Start;
        Check(start.Single().Path == "menu-new", "First access must use the current settings");
        pins.PinToStart("menu-added");
        Check(Settings.Current.StartMenu.Pinned.SequenceEqual(["menu-new", "menu-added"]), "Pinning must preserve deferred pins");
        Settings.Current.StartMenu.Pinned = ["menu-reloaded"];
        pins.Reload();
        Check(ReferenceEquals(start, pins.Start) && start.Single().Path == "menu-reloaded", "Reload must retain the bound collection");
        Check(ShellItem.ExtraLargeReads == 0, "Start icons must remain demand-loaded");
    }

    private static void Together()
    {
        using var reveal = new PinnedIconReveal(false, false);
        var first = new Image { Source = Icon() };
        var second = new Image();
        reveal.Add(first, 0);
        reveal.Add(second, 1);
        Pump(120);
        Check(!reveal.Finished && first.Opacity == 0 && second.Opacity == 0, "An early icon must wait for its group");
        second.Source = Icon();
        Pump(120);
        Check(reveal.Finished && first.Opacity == 1 && second.Opacity == 1, "Ready icons must be revealed together");
        Check(!first.HasAnimatedProperties && !second.HasAnimatedProperties, "Off must not animate");
    }

    private static void FallbackAndReuse()
    {
        // Exercise the same template source precedence as the real app, including reuse for an unpinned window.
        var template = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Image x:Name="Icon" Source="{Binding First.Icon}"/>
              <DataTemplate.Triggers><DataTrigger Binding="{Binding IsPinned}" Value="True">
                <Setter TargetName="Icon" Property="Source" Value="{Binding Pin.Item.LargeIcon}"/>
              </DataTrigger></DataTemplate.Triggers>
            </DataTemplate>
            """);
        var presenter = new ContentPresenter { ContentTemplate = template, Content = new ButtonData(true, null) };
        presenter.Measure(new Size(100, 100));
        presenter.Arrange(new Rect(0, 0, 100, 100));
        var image = (Image)template.FindName("Icon", presenter);
        using var reveal = new PinnedIconReveal(false, false);
        reveal.Add(image, 0);
        Pump(2200);
        Check(reveal.Finished && image.Source != null && image.Opacity == 1, "A missing icon must not leave the group hidden");
        reveal.Remove(image);
        var runningIcon = Icon();
        presenter.Content = new ButtonData(false, runningIcon);
        Pump(120);
        presenter.UpdateLayout();
        var reused = (Image)template.FindName("Icon", presenter);
        Check(ReferenceEquals(reused.Source, runningIcon), "Fallback must not override a reused template's running-window icon");
    }

    private static void AnimationAndDetach()
    {
        var reveal = new PinnedIconReveal(true, true);
        var first = new Image { Source = Icon() };
        var second = new Image { Source = Icon() };
        reveal.Add(second, 1);
        reveal.Add(first, 0);
        Pump(90);
        Check(reveal.Finished && first.HasAnimatedProperties && second.HasAnimatedProperties, "On must attach reveal animations");
        Check(first.RenderTransform is TranslateTransform, "Reveal must use a transform, not change layout");
        reveal.Remove(first);
        Check(first.Opacity == 1 && !first.HasAnimatedProperties, "Unloading must cancel the image animation");
        Pump(1000);
        Check(second.Opacity == 1 && !second.HasAnimatedProperties, "The animation must finish and release its opacity clock");
        reveal.Dispose();
        using var pending = new PinnedIconReveal(false, false);
        var absent = new Image();
        pending.Add(absent, 0);
        pending.Dispose();
        Pump(120);
        Check(absent.Opacity == 1 && !pending.Finished, "Disposal must cancel pending publication and restore visibility");
    }
}

public sealed record IconData(ImageSource? Icon);
public sealed record PinData(ItemData Item);
public sealed record ItemData(ImageSource? LargeIcon);
public sealed class ButtonData(bool pinned, ImageSource? icon)
{
    public bool IsPinned => pinned;
    public IconData First { get; } = new(icon);
    public PinData Pin { get; } = new(new(icon));
}
