using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClearStart;

/// <summary>A file attached to a release on GitHub.</summary>
internal sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A release of ClearStart on GitHub, as far as the updater needs it.</summary>
internal sealed record ReleaseInfo(Version Version, string Page, ReleaseAsset Installer, ReleaseAsset Checksum)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>What the updater is doing.</summary>
internal enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    /// <summary>The new version's Setup is downloaded and verified, and can start.</summary>
    Ready,
    /// <summary>Setup has started; it closes ClearStart, updates it and starts it again.</summary>
    Installing,
}

/// <summary>Something about a release or a download that stops the update, said so the user can follow it.</summary>
internal sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Keeps ClearStart up to date from its releases on GitHub. A little after the start, and then once a day,
/// it asks GitHub's public API for the latest release. Nothing is sent but that request: no account, no
/// token and nothing about the PC. When there is a newer version, the bar shows a button for it and the
/// General settings page says so. Installing downloads that version's Setup and its checksum, and runs
/// Setup only if both the checksum and the publisher's signature are right. Only the copy that Setup
/// installed updates itself.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string Repository = "C-Tech-Solutions/ClearStart";

    /// <summary>Who signs ClearStart: the name on its code signing certificate.</summary>
    public const string Publisher = "C-Tech Solutions LLC";

    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    private const string LatestAddress = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string DownloadPrefix = ReleasesPage + "/download/";

    // Where Setup records the folder it installed into. The key is named after the AppId in installer\ClearStart.iss.
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{FB0EF138-BB9B-491B-B0AB-B3004ECA5D90}_is1";

    private const long MaxInstallerBytes = 400L * 1024 * 1024;
    private const long MaxChecksumBytes = 4096;

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(10);

    private static readonly Regex TagPattern = new(@"^v(\d{1,6})\.(\d{1,6})\.(\d{1,6})$", RegexOptions.CultureInvariant);

    /// <summary>The version that is running. (Before <see cref="Http"/>, which puts it in its User-Agent.)</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version);

    private static readonly HttpClient Http = CreateClient();

    private readonly HostMode _mode;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lazy<bool> _canInstall;
    private bool? _sampleCanInstall;
    private DateTime _startedAt;
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _checkFailed;
    private bool _cleanedUp;
    private bool _installWhenReady;
    // A version whose Setup ran and stopped without updating: not tried again by itself in this session.
    private Version? _setupFailedFor;
    // Why that Setup did not update, for as long as its download is kept.
    private string? _setupProblem;
    // The verified Setup, kept open so that nothing can change it before it runs.
    private FileStream? _verified;
    private bool _disposed;

    public UpdateService(HostMode mode)
    {
        _mode = mode;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _canInstall = new Lazy<bool>(() => mode != HostMode.SelfTest && IsInstalledCopy());
        // Looks once a minute whether a check is due; this also catches up by itself after the PC has slept.
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Where downloaded installers are kept until they have run.</summary>
    public static string Folder => Path.Combine(Log.DataDirectory, "updates");

    public UpdateState State { get; private set; }

    /// <summary>A newer version than this one, once a check has found it.</summary>
    public ReleaseInfo? Latest { get; private set; }

    /// <summary>How far the download has got, in percent.</summary>
    public int Progress { get; private set; }

    /// <summary>What went wrong last, in a few words; null when nothing did.</summary>
    public string? Problem { get; private set; }

    /// <summary>When GitHub last answered a check (local time).</summary>
    public DateTime? LastChecked { get; private set; }

    /// <summary>
    /// True for the copy Setup installed, which is the only one that updates itself. A copy run from a build
    /// folder can still say that a newer version is out.
    /// </summary>
    public bool CanInstall => _sampleCanInstall ?? _canInstall.Value;

    /// <summary>Raised on the UI thread whenever anything above changed.</summary>
    public event Action? Changed;

    /// <summary>What there is to say about updates, in one line for the settings page and the bar's tooltip.</summary>
    public string Status
    {
        get
        {
            string version = Latest?.VersionText ?? "";
            switch (State)
            {
                case UpdateState.Installing:
                    return $"Installing version {version}. ClearStart closes and starts again by itself.";
                case UpdateState.Downloading:
                    return $"Downloading version {version}… {Progress}%";
                case UpdateState.Ready:
                    return _setupProblem != null
                        ? $"Version {version} is downloaded, but did not install. {_setupProblem}"
                        : $"Version {version} is downloaded and checked, ready to install";
                case UpdateState.Checking:
                    return "Checking for updates…";
            }
            if (Latest != null)
            {
                string more = Problem != null ? ". " + Problem
                    : Latest.Version == _setupFailedFor ? ". The last try to install it did not finish; Setup's log is in " + Folder
                    : "";
                return $"Version {version} is available" + more;
            }
            if (Problem != null) return Problem;
            if (LastChecked is { } when)
            {
                string at = when.Date == DateTime.Today
                    ? when.ToString("t", CultureInfo.CurrentCulture)
                    : when.ToString("g", CultureInfo.CurrentCulture);
                return "ClearStart is up to date. Last checked " + at;
            }
            return Settings.Current.Updates.CheckAutomatically
                ? "ClearStart checks for a new version a few minutes after it starts, and then once a day"
                : "Automatic checks are off";
        }
    }

    public void Start()
    {
        if (_disposed || _mode == HostMode.SelfTest || _timer.IsEnabled) return;
        _startedAt = DateTime.UtcNow;
        _setupFailedFor = LastFailedAttempt();
        _timer.Start();
    }

    /// <summary>Check now, whether or not automatic checks are on.</summary>
    public void CheckNow()
    {
        if (!Allowed || State is UpdateState.Downloading or UpdateState.Installing) return;
        _ = CheckAsync(manual: true);
    }

    /// <summary>
    /// Install the newer version: download and verify it first if that has not been done yet, and install it
    /// once it is ready. A copy that does not update itself opens the release page instead.
    /// </summary>
    public void Install()
    {
        if (!Allowed || Latest == null) return;
        if (!CanInstall)
        {
            OpenReleasePage();
            return;
        }
        switch (State)
        {
            case UpdateState.Ready:
                Launch();
                break;
            case UpdateState.Downloading:
                _installWhenReady = true;
                break;
            case UpdateState.Idle:
                _ = DownloadAsync(installWhenReady: true);
                break;
        }
    }

    /// <summary>Whether <see cref="Install"/> would do anything just now.</summary>
    public bool CanStartInstall => Latest != null && State is UpdateState.Idle or UpdateState.Ready or UpdateState.Downloading;

    /// <summary>Opens the newer version's release notes, or the list of releases.</summary>
    public void OpenReleasePage() => Ui.Safe(() => Launcher.Run(Latest?.Page ?? ReleasesPage), "Release notes");

    /// <summary>Either switch was thrown: start what the new settings call for.</summary>
    public void SettingsChanged()
    {
        if (!Allowed) return;
        Changed?.Invoke();
        if (WantsAutomaticDownload()) _ = DownloadAsync(installWhenReady: false);
    }

    /// <summary>
    /// Puts made-up news in place of a check, so the self-test can draw the bar and the settings page as an
    /// installed copy (or, with <paramref name="canInstall"/> false, a build-folder copy) would show it.
    /// Nothing can be checked, downloaded or started in the self-test whatever this says.
    /// </summary>
    internal void ShowSample(ReleaseInfo? latest, UpdateState state, int progress = 0, string? problem = null,
        DateTime? lastChecked = null, bool canInstall = true, string? setupProblem = null)
    {
        if (_mode != HostMode.SelfTest) return;
        _sampleCanInstall = canInstall;
        _setupProblem = setupProblem;
        Latest = latest;
        State = state;
        Progress = progress;
        Problem = problem;
        LastChecked = lastChecked;
        Changed?.Invoke();
    }

    private bool Allowed => !_disposed && _mode != HostMode.SelfTest;

    // With automatic checks off, nothing goes to GitHub unless the user asks: not even a download of news found earlier.
    private bool WantsAutomaticDownload() =>
        Latest != null && State == UpdateState.Idle && CanInstall && Settings.Current.Updates.InstallAutomatically
        && Settings.Current.Updates.CheckAutomatically && Latest.Version != _setupFailedFor;

    private void Tick()
    {
        if (!Allowed) return;
        var now = DateTime.UtcNow;
        // Nothing during the start: ClearStart has enough to do then.
        if (now - _startedAt < FirstCheckDelay) return;

        if (!_cleanedUp)
        {
            _cleanedUp = true;
            CleanUp();
        }

        var settings = Settings.Current.Updates;
        if (State == UpdateState.Ready && settings.InstallAutomatically && Latest?.Version != _setupFailedFor
            && IdleTime() >= IdleBeforeInstall)
        {
            Log.Info($"Installing version {Latest?.VersionText} while the PC is not in use");
            Launch();
            return;
        }

        if (!settings.CheckAutomatically || State is not (UpdateState.Idle or UpdateState.Ready)) return;
        var wait = _checkFailed ? RetryInterval : CheckInterval;
        if (now - _lastCheck >= wait) _ = CheckAsync(manual: false);
    }

    private async Task CheckAsync(bool manual)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        var before = State;
        _lastCheck = DateTime.UtcNow;
        State = UpdateState.Checking;
        Changed?.Invoke();

        try
        {
            var release = await FetchLatestAsync(_stop.Token);
            if (_disposed) return;
            if (_checkFailed) Log.Info("Update checks work again");
            _checkFailed = false;
            Problem = null;
            LastChecked = DateTime.Now;

            if (release == null || !IsNewer(release.Version, CurrentVersion))
            {
                if (Latest != null || manual) Log.Info($"ClearStart {CurrentVersion.ToString(3)} is up to date");
                Forget();
                Latest = null;
                State = UpdateState.Idle;
            }
            else if (Latest?.Version == release.Version)
            {
                // Still the same news: a download that is ready stays ready.
                Latest = release;
                State = before == UpdateState.Ready ? UpdateState.Ready : UpdateState.Idle;
            }
            else
            {
                Log.Info($"Version {release.VersionText} of ClearStart is available");
                Forget();
                Latest = release;
                State = UpdateState.Idle;
            }
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            if (!_checkFailed || manual) Log.Warn("Update check failed: " + ex.Message);
            _checkFailed = true;
            Problem = ex switch
            {
                UpdateException => ex.Message,
                JsonException => "GitHub's answer about the latest release could not be read",
                _ => "Could not reach GitHub to check for updates",
            };
            State = before == UpdateState.Ready ? UpdateState.Ready : UpdateState.Idle;
        }

        Changed?.Invoke();
        if (WantsAutomaticDownload()) _ = DownloadAsync(installWhenReady: false);
    }

    private async Task DownloadAsync(bool installWhenReady)
    {
        var release = Latest;
        if (!Allowed || release == null || !CanInstall || State != UpdateState.Idle) return;

        _installWhenReady = installWhenReady;
        State = UpdateState.Downloading;
        Progress = 0;
        Problem = null;
        Changed?.Invoke();

        string setup = Path.Combine(Folder, release.Installer.Name);
        string partial = setup + ".part";
        try
        {
            Forget();
            Directory.CreateDirectory(Folder);
            Log.Info($"Downloading version {release.VersionText}");

            byte[] listed = await DownloadAsync(release.Checksum, null, null);
            string checksum = ParseChecksum(Encoding.UTF8.GetString(listed), release.Installer.Name);

            int shown = -1;
            await DownloadAsync(release.Installer, partial, percent =>
            {
                if (percent == shown) return;
                shown = Progress = percent;
                Changed?.Invoke();
            });
            File.Move(partial, setup, overwrite: true);

            // Hashing and asking Windows about the signature take a moment, and the revocation check goes online.
            var verified = await Task.Run(() => OpenVerified(setup, checksum), _stop.Token);
            if (_disposed || !ReferenceEquals(Latest, release))
            {
                verified.Dispose();
                return;
            }
            _verified = verified;
            State = UpdateState.Ready;
            Log.Info($"Version {release.VersionText} is downloaded, and its checksum and signature are right");
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Log.Warn($"Could not get version {release.VersionText} ready: " + ex.Message);
            Problem = ex is UpdateException ? ex.Message : "The download did not work";
            State = UpdateState.Idle;
            _installWhenReady = false;
            Delete(partial);
            Delete(setup);
        }

        Changed?.Invoke();
        if (State == UpdateState.Ready && _installWhenReady) Launch();
    }

    /// <summary>
    /// Downloads one of a release's files, into a file or, when there is no path, into memory. It has to be
    /// exactly the size the release gives (which <see cref="ParseRelease"/> has already kept within limits).
    /// </summary>
    private async Task<byte[]> DownloadAsync(ReleaseAsset asset, string? path, Action<int>? progress)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var token = deadline.Token;

        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
        {
            throw new UpdateException($"{asset.Name} is not the size the release says it is");
        }

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using Stream target = path == null
            ? new MemoryStream()
            : new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > asset.Size) throw new UpdateException($"{asset.Name} is larger than the release says it is");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Invoke((int)(total * 100 / Math.Max(1, asset.Size)));
        }
        if (total != asset.Size) throw new UpdateException($"{asset.Name} did not download completely");
        return target is MemoryStream memory ? memory.ToArray() : Array.Empty<byte>();
    }

    /// <summary>
    /// Opens a downloaded Setup so that nothing else can change it, and checks it. Returns it still open,
    /// or throws if it must not run.
    /// </summary>
    private static FileStream OpenVerified(string setup, string checksum)
    {
        string? running = Environment.ProcessPath;
        var signer = running == null
            ? new SignatureInfo(false, null, null, "the program file could not be found")
            : Authenticode.Check(running, online: true);

        var file = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string? problem = Verify(file, checksum, signer, online: true);
            if (problem != null) throw new UpdateException("The download was refused: " + problem);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Why a downloaded Setup must not run, or null when it may. Its SHA-256 must be the one the release lists,
    /// and it must carry a valid signature from the same publisher as the copy of ClearStart that is running.
    /// </summary>
    /// <param name="file">The Setup, open for reading.</param>
    /// <param name="checksum">The SHA-256 the release lists for it, in hex.</param>
    /// <param name="running">The signature of the running ClearStart.</param>
    /// <param name="online">Ask whether the certificates have been revoked (the self-test stays offline).</param>
    public static string? Verify(FileStream file, string checksum, SignatureInfo running, bool online)
    {
        file.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(file));
        if (!string.Equals(hash, checksum, StringComparison.OrdinalIgnoreCase))
        {
            return "its checksum is not the one the release lists";
        }
        return Judge(Authenticode.Check(file.Name, file.SafeFileHandle, online), running);
    }

    /// <summary>
    /// The publisher check. The running copy has to be signed by <see cref="Publisher"/> itself: its certificate
    /// then says exactly who that is (the name, and where the company is registered), and the Setup must have
    /// been signed by that same company. A certificate for some other company that happens to have the same
    /// name does not pass.
    /// </summary>
    public static string? Judge(SignatureInfo setup, SignatureInfo running)
    {
        if (!running.Valid || running.Name != Publisher || string.IsNullOrEmpty(running.Subject))
        {
            return $"this copy of ClearStart is not signed by {Publisher}, so it cannot tell who made the update";
        }
        if (!setup.Valid) return "its signature is not valid: " + setup.Problem;
        if (setup.Name != Publisher) return $"it is signed by {setup.Name ?? "someone else"}, not by {Publisher}";
        if (!string.Equals(setup.Subject, running.Subject, StringComparison.Ordinal))
        {
            return $"it is signed as \"{setup.Subject}\", which is not who signed this copy of ClearStart";
        }
        return null;
    }

    /// <summary>
    /// Starts the verified Setup. It closes this ClearStart, installs the new version over it without asking
    /// anything, and starts it again in the same mode (see DeinitializeSetup in installer\ClearStart.iss).
    /// </summary>
    private void Launch()
    {
        var release = Latest;
        if (!Allowed || _verified == null || release == null || State != UpdateState.Ready) return;
        _installWhenReady = false;
        try
        {
            // Setup closes this copy before it installs, and starts the old one again if it fails. Written down,
            // the attempt tells that copy not to try this version again by itself.
            File.WriteAllText(AttemptFile, release.VersionText);
            var start = new ProcessStartInfo(_verified.Name, SetupArguments(_mode, Path.Combine(Folder, "setup.log")))
            {
                UseShellExecute = false,
                WorkingDirectory = Folder,
            };
            var setup = Process.Start(start) ?? throw new InvalidOperationException("Setup did not start");
            setup.EnableRaisingEvents = true;
            setup.Exited += (_, _) => _dispatcher.BeginInvoke(() => SetupEnded(release, setup));
            Log.Info($"Started Setup for version {release.VersionText}; it closes ClearStart and starts it again");
            State = UpdateState.Installing;
            Problem = _setupProblem = null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start Setup", ex);
            _setupProblem = "Setup could not start.";
            _setupFailedFor = release.Version;
        }
        Changed?.Invoke();
    }

    /// <summary>Setup ended and this ClearStart is still running, so it did not update.</summary>
    private void SetupEnded(ReleaseInfo release, Process setup)
    {
        int code = -1;
        try { code = setup.ExitCode; } catch { /* gone */ }
        setup.Dispose();
        if (_disposed || State != UpdateState.Installing) return;
        Log.Warn($"Setup for version {release.VersionText} ended (code {code}) without updating; " +
            "its log is " + Path.Combine(Folder, "setup.log"));
        _setupFailedFor = release.Version;
        _setupProblem = "Setup stopped without updating; its log is in " + Folder;
        State = UpdateState.Ready;
        Changed?.Invoke();
    }

    /// <summary>What Setup is started with: silently, without changing "Start with Windows", and coming back in the same mode.</summary>
    public static string SetupArguments(HostMode mode, string log) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /MERGETASKS=\"!startup\" " +
        $"/LOG=\"{log}\" /RESTART={(mode == HostMode.Preview ? "preview" : "live")}";

    /// <summary>Lets go of a download that is no longer wanted.</summary>
    private void Forget()
    {
        if (_verified == null) return;
        string path = _verified.Name;
        _verified.Dispose();
        _verified = null;
        _setupProblem = null;
        Delete(path);
        if (State == UpdateState.Ready) State = UpdateState.Idle;
    }

    /// <summary>The version a Setup was last started for (see <see cref="Launch"/>).</summary>
    private static string AttemptFile => Path.Combine(Folder, "last-attempt.txt");

    /// <summary>
    /// A version Setup was started for that is still newer than this one, so that Setup did not update it.
    /// Once this copy is that version or later, the record has done its job and goes.
    /// </summary>
    private static Version? LastFailedAttempt()
    {
        try
        {
            if (!File.Exists(AttemptFile)) return null;
            var tried = ParseTag("v" + File.ReadAllText(AttemptFile).Trim());
            if (tried != null && IsNewer(tried, CurrentVersion))
            {
                Log.Warn($"Setup for version {tried.ToString(3)} did not update ClearStart; it is not installed again by itself");
                return tried;
            }
            File.Delete(AttemptFile);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the last update attempt: " + ex.Message);
        }
        return null;
    }

    /// <summary>Removes what earlier updates left behind. Setup's last log is kept.</summary>
    private static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string file in Directory.EnumerateFiles(Folder, "ClearStart-Setup-*")) Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tidy the updates folder: " + ex.Message);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Still in use (a Setup that is just finishing): the next start tidies it.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _stop.Cancel();
        // Closing the file is all: a Setup that is running has it open itself.
        _verified?.Dispose();
        _verified = null;
    }

    // ------------------------------------------------------------------ talking to GitHub

    /// <summary>The latest release, or null when there has been none yet.</summary>
    private async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestAddress);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(deadline.Token));
    }

    /// <summary>
    /// Reads GitHub's description of a release and picks out the two files the updater uses. Throws an
    /// <see cref="UpdateException"/> when the release is not one it can install from.
    /// </summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string tag = Text(root, "tag_name") ?? "";
        var version = ParseTag(tag) ?? throw new UpdateException($"The latest release's tag \"{tag}\" is not a version");
        if (Flag(root, "draft") || Flag(root, "prerelease")) throw new UpdateException("The latest release is not a finished one");

        // The notes are opened in the browser, so only a page of this project's releases will do.
        string? page = Text(root, "html_url");
        if (page == null || !page.StartsWith(ReleasesPage + "/", StringComparison.OrdinalIgnoreCase)) page = ReleasesPage;

        string setupName = $"ClearStart-Setup-{version.ToString(3)}.exe";
        string checksumName = setupName + ".sha256.txt";
        ReleaseAsset? setup = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = Text(asset, "name");
                if (name == setupName) setup = Asset(asset, name, MaxInstallerBytes);
                else if (name == checksumName) checksum = Asset(asset, name, MaxChecksumBytes);
            }
        }
        if (setup == null || checksum == null)
        {
            throw new UpdateException($"Version {version.ToString(3)} does not have its installer and checksum yet");
        }
        return new ReleaseInfo(version, page, setup, checksum);
    }

    private static ReleaseAsset Asset(JsonElement asset, string name, long limit)
    {
        string url = Text(asset, "browser_download_url") ?? "";
        long size = asset.TryGetProperty("size", out var value) && value.TryGetInt64(out long bytes) ? bytes : 0;
        if (!url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException($"{name} is not downloaded from this project's releases");
        }
        if (size <= 0 || size > limit) throw new UpdateException($"{name} is not a size the updater accepts");
        return new ReleaseAsset(name, url, size);
    }

    /// <summary>"v1.2.3" as a version; null for any other kind of tag.</summary>
    public static Version? ParseTag(string tag)
    {
        var match = TagPattern.Match(tag);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>True only for a later version; the same one or an older one is never offered.</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>Major, minor and build; a missing build counts as 0 and the fourth part is left out.</summary>
    private static Version Normalize(Version? version) =>
        version == null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>
    /// The SHA-256 a checksum file gives for the named file, in the form "hash  name" that sha256sum writes
    /// (a line with the hash alone also does). Throws if the file does not give one.
    /// </summary>
    public static string ParseChecksum(string text, string fileName)
    {
        foreach (string raw in text.TrimStart('﻿').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            string hash = parts[0];
            string? name = parts.Length > 1 ? parts[1].Trim().TrimStart('*') : null;
            if (name != null && name != fileName) continue;
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return hash.ToLowerInvariant();
        }
        throw new UpdateException("The release's checksum file does not give one for " + fileName);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static HttpClient CreateClient()
    {
        // Headers arrive within this; a download's body has its own, longer limit.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClearStart/" + CurrentVersion.ToString(3));
        return client;
    }

    // ------------------------------------------------------------------ this PC

    /// <summary>True when this is the copy in the folder Setup installed ClearStart into.</summary>
    private static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string folder && folder.Length > 0
                && SameFolder(folder, AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tell whether this copy of ClearStart was installed: " + ex.Message);
            return false;
        }
    }

    internal static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>How long it has been since anyone touched the keyboard or the mouse.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime))
            : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
