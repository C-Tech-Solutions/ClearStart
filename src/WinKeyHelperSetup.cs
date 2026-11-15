using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ClearStart;

/// <summary>
/// Puts ClearStart.WinKey.exe in Program Files\ClearStart, the place Windows gives it UI Access from (see
/// <see cref="WinKeyHelper"/>), and takes it away again: "ClearStart.exe --win-key-helper install" and "remove".
/// Setup runs the first when its box is ticked, and the second when ClearStart is uninstalled. Program Files takes
/// administrator rights. Setup asks Windows for them; started without them, the command starts itself again with
/// them, which Windows asks the user about, and waits for that copy.
/// </summary>
internal static class WinKeyHelperSetup
{
    // What "ClearStart.exe --win-key-helper" ends with.
    internal const int Done = 0, Failed = 1, BadArgument = 2, Refused = 3;

    private const int ERROR_CANCELLED = 1223;

    public static int RunCommand(string? value)
    {
        Log.Init();
        try
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (programFiles.Length == 0) throw new InvalidOperationException("Windows did not say where Program Files is");
            string folder = Path.Combine(programFiles, "ClearStart");
            switch (value?.ToLowerInvariant())
            {
                case "install":
                    return Install(programFiles, folder);
                case "remove":
                    if (!File.Exists(Path.Combine(folder, WinKeyHelper.FileName)))
                    {
                        Log.Info($"The Windows-key helper is not in {folder}; there is nothing to remove");
                        return Done;
                    }
                    if (!IsAdministrator()) return RunAsAdministrator("remove");
                    Remove(folder);
                    Log.Info($"The Windows-key helper was removed from {folder}");
                    return Done;
                default:
                    Log.Warn($"--win-key-helper takes \"install\" or \"remove\", not \"{value}\"");
                    return BadArgument;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the Windows-key helper in Program Files", ex);
            return Failed;
        }
    }

    private static int Install(string programFiles, string folder)
    {
        string source = Path.Combine(AppContext.BaseDirectory, WinKeyHelper.FileName);
        if (!File.Exists(source))
        {
            Log.Warn($"The Windows-key helper was not installed: this copy of ClearStart has no {WinKeyHelper.FileName}");
            return Failed;
        }
        if (WinKeyHelper.Find(AppContext.BaseDirectory, programFiles, File.Exists) == source)
        {
            Log.Info("The Windows-key helper works from where it is: this copy of ClearStart is itself under Program Files");
            return Done;
        }
        // Before Windows asks anything: a helper that would be refused is not worth the question.
        if (Judge(source) is { } problem)
        {
            Log.Warn("The Windows-key helper was not installed: " + problem);
            return Failed;
        }
        if (SameFile(source, Path.Combine(folder, WinKeyHelper.FileName)))
        {
            Log.Info($"The Windows-key helper in {folder} is already this copy's");
            return Done;
        }
        if (!IsAdministrator()) return RunAsAdministrator("install");

        if (Install(source, folder, Judge) is { } refused)
        {
            Log.Warn("The Windows-key helper was not installed: " + refused);
            return Failed;
        }
        Log.Info($"The Windows-key helper is in {folder}; ClearStart uses it from its next start");
        return Done;
    }

    /// <summary>
    /// Copies the helper into <paramref name="folder"/> and only then judges it, where nothing without administrator
    /// rights can change it any more. One that is refused does not stay, and the helper that was there does.
    /// Returns why it was refused, or null.
    /// </summary>
    internal static string? Install(string source, string folder, Func<string, string?> judge)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, WinKeyHelper.FileName), copy = target + ".new";
        File.Copy(source, copy, overwrite: true);
        string? problem = judge(copy);
        if (problem == null)
        {
            try
            {
                File.Move(copy, target, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = "the helper that is there is in use; close ClearStart and try again (" + ex.Message + ")";
            }
        }
        File.Delete(copy);
        RemoveIfEmpty(folder);
        return problem;
    }

    /// <summary>Takes the helper out of <paramref name="folder"/>, and the folder with it if nothing else is in it.</summary>
    internal static void Remove(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string target = Path.Combine(folder, WinKeyHelper.FileName);
        File.Delete(target + ".new");
        File.Delete(target);
        RemoveIfEmpty(folder);
    }

    private static void RemoveIfEmpty(string folder)
    {
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    /// <summary>
    /// Why this helper must not go into Program Files, or null. Windows gives UI Access to any program there that
    /// carries a signature it trusts, so only one from who signed this copy of ClearStart is put there.
    /// </summary>
    private static string? Judge(string helper)
    {
        string? running = Environment.ProcessPath;
        var signer = running == null
            ? new SignatureInfo(false, null, null, "the program file could not be found")
            : Authenticode.Check(running);
        if (!UpdateService.SignedByPublisher(signer))
        {
            return $"this copy of ClearStart is not signed by {UpdateService.Publisher}, and Windows runs the helper only signed";
        }
        return UpdateService.Judge(Authenticode.Check(helper), signer);
    }

    internal static bool SameFile(string one, string other)
    {
        if (!File.Exists(one) || !File.Exists(other)) return false;
        return File.ReadAllBytes(one).AsSpan().SequenceEqual(File.ReadAllBytes(other));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RunAsAdministrator(string what)
    {
        if (Environment.ProcessPath is not { } exe) throw new InvalidOperationException("The program file could not be found");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--win-key-helper " + what)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null) throw new InvalidOperationException("Windows gave nothing to wait for");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            Log.Info("The Windows-key helper in Program Files was left as it is: Windows' question about administrator rights was answered with No");
            return Refused;
        }
    }
}
