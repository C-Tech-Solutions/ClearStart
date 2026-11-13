// Test doubles keep the actual PinStore code away from COM, disk, user settings and shell integration.
namespace ManagedShell.ShellFolders
{
    internal sealed class ShellItem : IDisposable
    {
        public static readonly List<string> Created = new();
        public static int LargeReads, ExtraLargeReads;
        public ShellItem(string path) { DisplayName = path; Created.Add(path); }
        public bool Loaded => true;
        public string DisplayName { get; }
        public object? LargeIcon { get { LargeReads++; return null; } }
        public object? ExtraLargeIcon { get { ExtraLargeReads++; return null; } }
        public void Dispose() { }
    }
}

namespace ClearStart
{
    internal static class Settings
    {
        public static readonly Config Current = new();
        public static void Save() { }
    }
    internal sealed class Config
    {
        public PinSettings Taskbar { get; } = new();
        public PinSettings StartMenu { get; } = new();
    }
    internal sealed class PinSettings
    {
        public List<string>? Pinned { get; set; }
        public List<PinFolderEntry>? PinFolders { get; set; }
    }
    internal sealed class PinFolderEntry
    {
        public string Name { get; set; } = "";
        public List<string> Items { get; set; } = new();
    }
    internal static class ShellLink { public static string? TargetOf(string path) => path; }
    internal static class AppCatalog
    {
        public const string AppsFolderPrefix = "apps:";
        public static string? FileOf(string path) => path;
    }
    internal static class AppKey
    {
        public static string ForFile(string path) => "file:" + path;
        public static string ForAppId(string id) => "id:" + id;
    }
    internal static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
    }
}
