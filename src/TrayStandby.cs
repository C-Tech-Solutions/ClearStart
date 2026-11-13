using System.Reflection;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// Moves ClearStart's tray behind Explorer's for a while, and back in front.
///
/// Windows hands the tray icons to the first window of the taskbar's class, so ManagedShell keeps ClearStart's tray in
/// front of Explorer's and checks ten times a second. An Explorer that starts while ClearStart's tray is in front does
/// not work fully until it starts again: windows no longer snap with the Win+arrow keys, and it no longer closes when
/// Windows asks it to. So while Explorer starts, ClearStart's tray goes behind Explorer's and stops pushing forward
/// (see <see cref="ShellHost"/>).
///
/// ManagedShell can do this but does not make it public, so it is reached by name. If a later ManagedShell renames it,
/// this says so in the log and leaves the tray as it is.
/// </summary>
internal static class TrayStandby
{
    private static readonly FieldInfo? ServiceField =
        typeof(NotificationArea).GetField("_trayService", BindingFlags.Instance | BindingFlags.NonPublic);
    private static bool _missingLogged;

    /// <summary>Puts ClearStart's tray behind Explorer's and keeps it there. False if that could not be done.</summary>
    public static bool StepBack(NotificationArea area) => Call(area, "Suspend");

    /// <summary>Puts ClearStart's tray in front of Explorer's again, and keeps it there. False if that could not be done.</summary>
    public static bool StepForward(NotificationArea area) => Call(area, "Resume");

    private static bool Call(NotificationArea area, string name)
    {
        try
        {
            object? service = ServiceField?.GetValue(area);
            MethodInfo? method = service?.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic,
                Type.EmptyTypes);
            if (method != null)
            {
                method.Invoke(service, null);
                return true;
            }
            if (!_missingLogged)
            {
                _missingLogged = true;
                Log.Warn($"ManagedShell has no tray step \"{name}\" in this version; ClearStart's tray stays in front of Explorer's");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not move ClearStart's tray ({name})", ex);
        }
        return false;
    }
}
