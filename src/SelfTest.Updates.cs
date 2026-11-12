using System.Security.Cryptography;
using ManagedShell.AppBar;

namespace ClearStart;

// The updater's part of the self-test. Nothing here goes online: the release is a saved answer, and the
// signature checks ask Windows only about what this PC already knows.
internal static partial class SelfTest
{
    // GitHub's answer about a release, cut down to what the updater reads, with files around the two it wants.
    private const string SampleRelease = """
        {"tag_name":"v9.8.7","html_url":"https://github.com/C-Tech-Solutions/ClearStart/releases/tag/v9.8.7",
        "draft":false,"prerelease":false,"assets":[
        {"name":"ClearStart-Setup-9.8.7.exe.sig","size":100,
         "browser_download_url":"https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe.sig"},
        {"name":"ClearStart-Setup-9.8.6.exe","size":5000000,
         "browser_download_url":"https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.6/ClearStart-Setup-9.8.6.exe"},
        {"name":"ClearStart-Setup-9.8.7.exe","size":61234567,
         "browser_download_url":"https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe"},
        {"name":"ClearStart-Setup-9.8.7.exe.sha256.txt","size":95,
         "browser_download_url":"https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe.sha256.txt"}]}
        """;

    // Who signs ClearStart, as its certificate puts it. Stands in for the running copy, which a build is not signed as.
    private static readonly SignatureInfo SampleSigner = new(true,
        "CN=C-Tech Solutions LLC, O=C-Tech Solutions LLC, L=Conway, S=South Carolina, C=US", UpdateService.Publisher, null);

    private static async Task CheckUpdatesAsync(ShellHost host, string directory, Dictionary<string, object?> report,
        List<string> errors)
    {
        // Reading a release, and the releases that must be turned down.
        ReleaseInfo? release = null;
        try
        {
            release = UpdateService.ParseRelease(SampleRelease);
            if (release.Version != new Version(9, 8, 7) || release.Installer.Name != "ClearStart-Setup-9.8.7.exe"
                || release.Installer.Size != 61234567 || !release.Installer.Url.EndsWith("/v9.8.7/ClearStart-Setup-9.8.7.exe", StringComparison.Ordinal)
                || release.Checksum.Name != "ClearStart-Setup-9.8.7.exe.sha256.txt"
                || release.Page != UpdateService.ReleasesPage + "/tag/v9.8.7")
            {
                errors.Add("Updates: the sample release was not read right");
            }

            var refused = new Dictionary<string, string>
            {
                ["a tag that is not a version"] = SampleRelease.Replace("\"v9.8.7\"", "\"latest\""),
                ["a tag without the v"] = SampleRelease.Replace("\"v9.8.7\"", "\"9.8.7\""),
                ["a tag with four parts"] = SampleRelease.Replace("\"v9.8.7\"", "\"v9.8.7.1\""),
                ["a pre-release tag"] = SampleRelease.Replace("\"v9.8.7\"", "\"v9.8.7-beta\""),
                ["a draft"] = SampleRelease.Replace("\"draft\":false", "\"draft\":true"),
                ["a pre-release"] = SampleRelease.Replace("\"prerelease\":false", "\"prerelease\":true"),
                ["no checksum file"] = SampleRelease.Replace("exe.sha256.txt\"", "exe.sha512.txt\""),
                ["an installer from another site"] = SampleRelease.Replace(
                    "https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe\"",
                    "https://example.com/ClearStart-Setup-9.8.7.exe\""),
                ["an installer from another project"] = SampleRelease.Replace(
                    "https://github.com/C-Tech-Solutions/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe\"",
                    "https://github.com/someone-else/ClearStart/releases/download/v9.8.7/ClearStart-Setup-9.8.7.exe\""),
                ["an installer of no size"] = SampleRelease.Replace("\"size\":61234567", "\"size\":0"),
                ["an installer far too big"] = SampleRelease.Replace("\"size\":61234567", "\"size\":900000000000"),
            };
            var reasons = new Dictionary<string, string>();
            foreach (var (what, json) in refused)
            {
                if (json == SampleRelease)
                {
                    errors.Add($"Updates: the test for {what} did not change the sample");
                    continue;
                }
                try
                {
                    var wrong = UpdateService.ParseRelease(json);
                    errors.Add($"Updates: {what} was accepted as version {wrong.VersionText}");
                }
                catch (UpdateException ex)
                {
                    reasons[what] = ex.Message;
                }
            }
            report["updateRefusedReleases"] = reasons;

            // Release notes open in the browser: only a page of this project's releases is used.
            var elsewhere = UpdateService.ParseRelease(SampleRelease.Replace(
                "\"https://github.com/C-Tech-Solutions/ClearStart/releases/tag/v9.8.7\"", "\"https://example.com/notes\""));
            if (elsewhere.Page != UpdateService.ReleasesPage) errors.Add("Updates: release notes elsewhere than GitHub were not replaced");
        }
        catch (Exception ex)
        {
            errors.Add("Updates (reading releases): " + ex);
        }

        // The two switches: checks on, installing by itself off, for new settings and for a file without them.
        try
        {
            var fresh = new Settings().Updates;
            var missing = Settings.Parse("""{"updates":null}""").Updates;
            var written = new Settings();
            written.Updates.CheckAutomatically = false;
            written.Updates.InstallAutomatically = true;
            var read = Settings.Parse(Settings.Write(written)).Updates;
            if (!fresh.CheckAutomatically || fresh.InstallAutomatically || missing is not { CheckAutomatically: true, InstallAutomatically: false }
                || read.CheckAutomatically || !read.InstallAutomatically)
            {
                errors.Add("Updates: the update settings do not start as they should or did not read back as written");
            }
        }
        catch (Exception ex)
        {
            errors.Add("Updates (settings): " + ex);
        }

        // Which versions count as newer.
        try
        {
            var cases = new (string Candidate, string Current, bool Newer)[]
            {
                ("0.1.0", "0.1.0", false),
                ("0.1", "0.1.0", false),
                ("0.1.0.9", "0.1.0", false),
                ("0.0.9", "0.1.0", false),
                ("0.1.1", "0.1.0", true),
                ("0.10.0", "0.9.0", true),
                ("1.0.0", "0.99.99", true),
                ("1.0.0", "1.0.1", false),
            };
            foreach (var (candidate, current, newer) in cases)
            {
                if (UpdateService.IsNewer(Version.Parse(candidate), Version.Parse(current)) != newer)
                {
                    errors.Add($"Updates: {candidate} {(newer ? "should" : "should not")} count as newer than {current}");
                }
            }
            if (UpdateService.ParseTag("v0.10.2") != new Version(0, 10, 2)) errors.Add("Updates: the tag v0.10.2 was not read right");
            report["updateRunningVersion"] = UpdateService.CurrentVersion.ToString(3);
        }
        catch (Exception ex)
        {
            errors.Add("Updates (versions): " + ex);
        }

        // Checksum files.
        try
        {
            string hash = new string('a', 32) + new string('B', 32);
            string name = "ClearStart-Setup-9.8.7.exe";
            var good = new[]
            {
                $"{hash}  {name}\r\n",
                $"\uFEFF{hash} *{name}\n",
                hash,
                $"{new string('c', 64)}  ClearStart-Setup-9.8.6.exe\n{hash}  {name}\n",
            };
            foreach (string text in good)
            {
                if (UpdateService.ParseChecksum(text, name) != hash.ToLowerInvariant())
                {
                    errors.Add("Updates: a checksum file was not read right: " + text.Trim());
                }
            }
            foreach (string text in new[] { $"{hash}  ClearStart-Setup-9.8.6.exe", hash[..60] + "  " + name, "", $"{hash[..63]}g  {name}" })
            {
                try
                {
                    UpdateService.ParseChecksum(text, name);
                    errors.Add("Updates: a checksum file that does not fit was accepted: " + text);
                }
                catch (UpdateException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("Updates (checksum files): " + ex);
        }

        // Checking a downloaded Setup: its checksum, then its signature and who made it.
        var checks = new Dictionary<string, string?>();
        string scratch = Path.Combine(Path.GetTempPath(), "ClearStart-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);

            // Not a program at all, with the right checksum and with a wrong one.
            string plain = Path.Combine(scratch, "plain.exe");
            File.WriteAllText(plain, "not a program");
            checks["wrong checksum"] = Refused("wrong checksum", plain, new string('0', 64), SampleSigner, errors);
            checks["not signed"] = Refused("not signed", plain, null, SampleSigner, errors);

            // A signed file by someone else: the .NET runtime's own, signed by Microsoft.
            string other = typeof(object).Assembly.Location;
            var otherSignature = Authenticode.Check(other);
            report["updateOtherSigner"] = otherSignature;
            if (otherSignature.Valid)
            {
                checks["someone else's"] = Refused("someone else's", other, null, SampleSigner, errors);
            }

            // The installed ClearStart, if there is one on this PC and it is signed: it should pass against its own
            // signature, and fail once one byte of it has changed.
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ClearStart", "ClearStart.exe");
            var installedSignature = File.Exists(installed) ? Authenticode.Check(installed) : null;
            report["updateInstalledSigner"] = installedSignature;
            string signed = installedSignature is { Valid: true, Name: UpdateService.Publisher } ? installed
                : otherSignature.Valid ? other : "";
            if (installedSignature is { Valid: true, Name: UpdateService.Publisher })
            {
                checks["the installed copy"] = Accepted(installed, installedSignature, errors);
                // Same name, but registered somewhere else: a different company.
                var namesake = installedSignature with { Subject = installedSignature.Subject + ", OU=Somewhere else" };
                checks["a namesake"] = Refused("a namesake", installed, null, namesake, errors);
            }
            if (signed.Length > 0)
            {
                string changed = Path.Combine(scratch, "changed.exe");
                File.Copy(signed, changed);
                using (var file = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite))
                {
                    file.Position = file.Length / 2;
                    int b = file.ReadByte();
                    file.Position = file.Length / 2;
                    file.WriteByte((byte)(b ^ 0xFF));
                }
                checks["changed after signing"] = Refused("changed after signing", changed, null,
                    signed == installed ? installedSignature! : SampleSigner, errors);
            }

            // The running copy has to be signed by the publisher, or no Setup is trusted.
            var unsignedRunning = new SignatureInfo(false, null, null, "it is not signed");
            checks["running copy not signed"] = UpdateService.Judge(SampleSigner, unsignedRunning);
            if (checks["running copy not signed"] == null) errors.Add("Updates: a Setup was trusted though ClearStart itself is not signed");
            var wrongRunning = SampleSigner with { Name = "Someone Else", Subject = "CN=Someone Else" };
            checks["running copy by someone else"] = UpdateService.Judge(wrongRunning, wrongRunning);
            if (checks["running copy by someone else"] == null) errors.Add("Updates: a copy signed by someone else trusted its own kind");
            if (UpdateService.Judge(SampleSigner, SampleSigner) != null) errors.Add("Updates: the publisher's own signature was turned down");
        }
        catch (Exception ex)
        {
            errors.Add("Updates (checking a download): " + ex);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { /* the temp folder tidies itself */ }
        }
        report["updateChecks"] = checks;

        // How Setup is started.
        string live = UpdateService.SetupArguments(HostMode.Live, @"C:\x\setup.log");
        string preview = UpdateService.SetupArguments(HostMode.Preview, @"C:\x\setup.log");
        report["updateSetupArguments"] = new[] { live, preview };
        if (!live.Contains("/VERYSILENT") || !live.EndsWith("/RESTART=live", StringComparison.Ordinal)
            || !preview.EndsWith("/RESTART=preview", StringComparison.Ordinal)
            || !live.Contains("/MERGETASKS=\"!startup\""))
        {
            errors.Add("Updates: Setup would not be started as it should: " + live);
        }

        // What the bar and the settings show. Nothing here can check or download: the self-test's updater does neither.
        if (release == null) return;
        var updates = host.Updates;
        try
        {
            var statuses = new List<string>();
            updates.ShowSample(null, UpdateState.Idle, lastChecked: DateTime.Today.AddHours(9));
            statuses.Add(updates.Status);
            updates.ShowSample(null, UpdateState.Idle, problem: "Could not reach GitHub to check for updates");
            statuses.Add(updates.Status);
            foreach (var state in new[] { UpdateState.Idle, UpdateState.Checking, UpdateState.Downloading, UpdateState.Ready, UpdateState.Installing })
            {
                updates.ShowSample(release, state, progress: 42);
                statuses.Add(updates.Status);
            }
            // A Setup that stopped without updating says so, and a failed check meanwhile does not hide it.
            updates.ShowSample(release, UpdateState.Ready, problem: "Could not reach GitHub to check for updates",
                setupProblem: "Setup could not start.");
            statuses.Add(updates.Status);
            if (!updates.Status.Contains("did not install. Setup could not start.", StringComparison.Ordinal))
            {
                errors.Add("updates: a Setup that did not start is not in the status: " + updates.Status);
            }
            report["updateStatus"] = statuses;

            var screens = AppBarScreen.FromAllScreens();
            var primary = screens.FirstOrDefault(s => s.Primary) ?? screens.First();
            double flat = Settings.Current.Taskbar.Height;
            updates.ShowSample(release, UpdateState.Idle);
            var view = new TaskbarView(host, primary, AppBarEdge.Bottom, flat);
            report["updateMenu"] = MenuLines(view.UpdateMenu(), "");
            view.Detach();
            updates.ShowSample(release, UpdateState.Idle, canInstall: false);
            view = new TaskbarView(host, primary, AppBarEdge.Bottom, flat);
            report["updateMenuBuildCopy"] = MenuLines(view.UpdateMenu(), "");
            view.Detach();

            updates.ShowSample(release, UpdateState.Idle);
            await DrawBarAsync(host, primary, Path.Combine(directory, "taskbar-update.png"), AppBarEdge.Bottom, flat, 1600, null, 1200);
            updates.ShowSample(release, UpdateState.Downloading, progress: 42);
            await DrawBarAsync(host, primary, Path.Combine(directory, "sidebar-update.png"), AppBarEdge.Left,
                Settings.Current.Taskbar.Width, 900, null, 1200);

            updates.ShowSample(release, UpdateState.Ready);
            await DrawAsync(new SettingsView(host, SettingsPage.About) { Width = 880, Height = 960 }, 880, 960,
                Path.Combine(directory, "settings-about-update.png"));
            updates.ShowSample(release, UpdateState.Idle, canInstall: false);
            await DrawAsync(new SettingsView(host, SettingsPage.About) { Width = 880, Height = 960 }, 880, 960,
                Path.Combine(directory, "settings-about-update-build.png"));
        }
        catch (Exception ex)
        {
            errors.Add("Updates (on screen): " + ex);
        }
        finally
        {
            updates.ShowSample(null, UpdateState.Idle);
        }
    }

    /// <summary>Checks a file as a downloaded Setup and expects it to be turned down; returns why it was.</summary>
    private static string? Refused(string what, string path, string? checksum, SignatureInfo running, List<string> errors)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        checksum ??= Convert.ToHexString(SHA256.HashData(file));
        string? problem = UpdateService.Verify(file, checksum, running, online: false);
        if (problem == null) errors.Add($"Updates: a Setup was accepted that should not be ({what})");
        return problem;
    }

    /// <summary>Checks a file as a downloaded Setup and expects it to pass.</summary>
    private static string? Accepted(string path, SignatureInfo running, List<string> errors)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string checksum = Convert.ToHexString(SHA256.HashData(file));
        string? problem = UpdateService.Verify(file, checksum, running, online: false);
        if (problem != null) errors.Add("Updates: the installed ClearStart was turned down: " + problem);
        return problem ?? "accepted";
    }
}
