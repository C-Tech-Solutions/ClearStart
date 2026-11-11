namespace ClearStart;

/// <summary>The right-click menus of the network and volume glyphs next to the clock.</summary>
internal static class TrayMenus
{
    // The page with the output in use: its volume, format and spatial sound.
    private const string OutputSettings = "ms-settings:sound-defaultoutputproperties";

    public static List<MenuEntry> Sound()
    {
        var outputs = Audio.Outputs()
            .Select(o => new MenuEntry(o.Name, () => SwitchOutput(o), Checked: o.IsDefault))
            .ToList();

        var spatial = Audio.Spatial();
        var formats = new List<MenuEntry>();
        if (spatial is { Supported: true })
        {
            formats.AddRange(spatial.Formats.Select(f => new MenuEntry(f.Name, () => SwitchSpatial(f), Checked: f.IsActive)));
            formats.Add(new MenuEntry(null, null));
        }
        formats.Add(new MenuEntry("More spatial sound options…", () => Launcher.Run(OutputSettings)));

        return new List<MenuEntry>
        {
            new("Open Sound settings", () => Launcher.Run("ms-settings:sound")),
            new("Open Volume mixer", OpenVolumeMixer),
            new("Output device", null, Children: outputs),
            new(spatial == null ? "Spatial sound" : $"Spatial sound ({spatial.ActiveName})", null, Children: formats),
            new("Sounds", () => Launcher.Run("rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,2")),
            new("Troubleshoot sound problems", Troubleshoot),
        };
    }

    public static List<MenuEntry> Network() => new()
    {
        new("Open Network & Internet settings", () => Launcher.Run("ms-settings:network")),
        new("Network connections", () => Launcher.Run("ncpa.cpl")),
    };

    private static void SwitchOutput(AudioOutput output)
    {
        if (output.IsDefault) return;
        if (Audio.SetDefaultOutput(output.Id))
        {
            Log.Info("Sound output switched to: " + output.Name);
        }
        else
        {
            // Windows said no; its own page can still do it.
            Launcher.Run("ms-settings:sound");
        }
    }

    private static async void SwitchSpatial(SpatialFormat format)
    {
        if (format.IsActive) return;
        try
        {
            if (await Audio.SetSpatialAsync(format.Id))
            {
                Log.Info("Spatial sound set to: " + format.Name);
            }
            else
            {
                // Windows keeps this switch to itself for some formats; its own page has it.
                Launcher.Run(OutputSettings);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change spatial sound", ex);
        }
    }

    /// <summary>The classic mixer with one slider per app; the Settings page if Windows no longer has it.</summary>
    private static void OpenVolumeMixer()
    {
        if (InSystemFolder("SndVol.exe") is { } mixer) Launcher.Run(mixer);
        else Launcher.Run("ms-settings:apps-volume");
    }

    /// <summary>
    /// Windows moved its troubleshooters into the Get Help app; the old command still starts the sound one
    /// (or hands over to Get Help) where it exists.
    /// </summary>
    private static void Troubleshoot()
    {
        if (InSystemFolder("msdt.exe") is { } tool) Launcher.Run(tool, "/id AudioPlaybackDiagnostic");
        else Launcher.Run("ms-settings:troubleshoot");
    }

    private static string? InSystemFolder(string program)
    {
        string path = Path.Combine(Environment.SystemDirectory, program);
        return File.Exists(path) ? path : null;
    }
}
