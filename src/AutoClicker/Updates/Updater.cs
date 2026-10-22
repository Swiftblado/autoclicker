using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoClicker.Updates;

/// <summary>A newer release, and the download for this computer within it.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long Size, string PageUrl);

/// <summary>Why <see cref="Updater.InstallAsync"/> couldn't swap itself in; the caller sends the user to <see cref="PageUrl"/>.</summary>
public sealed class UpdateBlockedException(string message, string pageUrl) : Exception(message)
{
    public string PageUrl { get; } = pageUrl;
}

/// <summary>
/// Updates the app from its GitHub releases: finds the newest release, downloads the file for
/// this OS and processor, puts it where the running app lives, and relaunches. Each OS needs a
/// different swap; see <see cref="InstallAsync"/>.
/// </summary>
public static class Updater
{
    const string Repo = "Swiftblado/autoclicker";
    const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>Passed to the new copy so it waits for this one to exit before starting.</summary>
    public const string AfterUpdateArg = "--after-update";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoClicker/" + AppVersion.Format(AppVersion.Current));
        return client;
    }

    /// <summary>The release file that runs on this computer, or null if none is published for it.</summary>
    public static string? AssetNameForThisComputer()
    {
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        // Windows on Arm runs the x64 build under emulation; there's no Arm64 Windows or Linux build.
        if (OperatingSystem.IsWindows()) return "AutoClicker-windows-x64.exe";
        if (OperatingSystem.IsMacOS()) return arm ? "AutoClicker-macos-arm64.zip" : "AutoClicker-macos-x64.zip";
        if (OperatingSystem.IsLinux() && !arm) return "AutoClicker-linux-x64.tar.gz";
        return null;
    }

    /// <summary>The newest release if it's newer than this build; null if this is up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApi, cancel);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
        return ParseRelease(json.RootElement, AppVersion.Current, AssetNameForThisComputer());
    }

    /// <summary>Picks the newer-than-<paramref name="current"/> release apart. Public for tests.</summary>
    public static UpdateInfo? ParseRelease(JsonElement release, Version current, string? assetName)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= current || assetName == null) return null;

        string page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;
            return new UpdateInfo(latest, tag, assetName,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                page);
        }
        return null;
    }

    /// <summary>
    /// Downloads <paramref name="update"/> and swaps it in. On success the new copy is already
    /// on disk and launched (it waits for this process to exit); the caller should quit.
    /// Throws <see cref="UpdateBlockedException"/> when this copy can't be replaced.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        string exe = Environment.ProcessPath ?? throw new UpdateBlockedException("Couldn't tell where the app is running from.", update.PageUrl);

        if (OperatingSystem.IsMacOS()) await InstallMacAsync(update, exe, progress, cancel);
        else if (OperatingSystem.IsWindows()) await InstallWindowsAsync(update, exe, progress, cancel);
        else if (OperatingSystem.IsLinux()) await InstallLinuxAsync(update, exe, progress, cancel);
        else throw new UpdateBlockedException("This system can't update itself.", update.PageUrl);
    }

    // ------------------------------------------------------------------ Windows

    /// <summary>
    /// Windows won't let a running .exe be overwritten or deleted, but it can be renamed. So the
    /// download goes in beside it, the running one becomes .old, and the new one takes its name.
    /// The new copy deletes the .old once this one has exited (<see cref="CleanUpAfterUpdate"/>).
    /// </summary>
    static async Task InstallWindowsAsync(UpdateInfo update, string exe, IProgress<double>? progress, CancellationToken cancel)
    {
        string dir = Path.GetDirectoryName(exe)!;
        EnsureWritable(dir, update, "Auto Clicker's folder can't be written to, so it can't update itself there.");

        string fresh = exe + ".new";
        string old = exe + ".old";
        await DownloadAsync(update, fresh, progress, cancel);

        try
        {
            File.Delete(old);
            File.Move(exe, old);
            File.Move(fresh, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put things back if we got halfway.
            if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
            TryDelete(fresh);
            throw new UpdateBlockedException("Couldn't replace the app file: " + ex.Message, update.PageUrl);
        }

        Relaunch(exe);
    }

    // ------------------------------------------------------------------ Linux

    /// <summary>
    /// The download is a .tar.gz; only the AutoClicker binary inside it is needed. Unix lets a
    /// running file be replaced by renaming over it, so the swap is one atomic move.
    /// </summary>
    static async Task InstallLinuxAsync(UpdateInfo update, string exe, IProgress<double>? progress, CancellationToken cancel)
    {
        string dir = Path.GetDirectoryName(exe)!;
        EnsureWritable(dir, update, "Auto Clicker's folder (" + dir + ") can't be written to, so it can't update itself there.");

        string archive = Path.Combine(Path.GetTempPath(), "autoclicker-update-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        string fresh = exe + ".new";
        try
        {
            await DownloadAsync(update, archive, progress, cancel);
            ExtractFromTarGz(archive, "AutoClicker", fresh);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fresh, (UnixFileMode)0b111_101_101);   // rwxr-xr-x
            File.Move(fresh, exe, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(fresh);
            throw new UpdateBlockedException("Couldn't install the update: " + ex.Message, update.PageUrl);
        }
        finally
        {
            TryDelete(archive);
        }

        Relaunch(exe);
    }

    static void ExtractFromTarGz(string archive, string entryName, string destination)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (Path.GetFileName(entry.Name.TrimEnd('/')) != entryName) continue;
            entry.ExtractToFile(destination, overwrite: true);
            return;
        }
        throw new InvalidDataException("The download didn't contain " + entryName + ".");
    }

    // ------------------------------------------------------------------ macOS

    /// <summary>
    /// The download is a zipped .app. It's unpacked with <c>ditto</c> (keeps permissions and the
    /// code signature intact), then the old bundle is moved aside, the new one moved into its
    /// place, and the old one deleted. Files fetched by the app itself aren't quarantined, so
    /// Gatekeeper doesn't ask again — but macOS does forget the Accessibility permission, since
    /// the new copy's ad-hoc signature is different.
    /// </summary>
    static async Task InstallMacAsync(UpdateInfo update, string exe, IProgress<double>? progress, CancellationToken cancel)
    {
        // exe is …/AutoClicker.app/Contents/MacOS/AutoClicker
        string bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
        if (!bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new UpdateBlockedException("Auto Clicker isn't running from its .app, so it can't update itself.", update.PageUrl);

        // An app opened straight from Downloads (still quarantined) runs from a read-only copy.
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
            throw new UpdateBlockedException(
                "macOS is running Auto Clicker from a temporary copy. Move it to your Applications folder, open it from there, then update.",
                update.PageUrl);

        string parent = Path.GetDirectoryName(bundle)!;
        EnsureWritable(parent, update, "The folder Auto Clicker is in (" + parent + ") can't be written to, so it can't update itself there.");

        string work = Path.Combine(Path.GetTempPath(), "autoclicker-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, update.AssetName);
        string aside = Path.Combine(parent, "." + Path.GetFileName(bundle) + ".old-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await DownloadAsync(update, zip, progress, cancel);
            Run("/usr/bin/ditto", "-x", "-k", zip, work);
            string freshBundle = Path.Combine(work, "AutoClicker.app");
            if (!Directory.Exists(freshBundle))
                throw new InvalidDataException("The download didn't contain AutoClicker.app.");

            // Unpacked on the startup disk; the bundle may be elsewhere, so copy rather than move.
            string staged = Path.Combine(parent, "." + Path.GetFileName(bundle) + ".new-" + Guid.NewGuid().ToString("N")[..8]);
            Run("/usr/bin/ditto", freshBundle, staged);
            Directory.Move(bundle, aside);
            try
            {
                Directory.Move(staged, bundle);
            }
            catch
            {
                Directory.Move(aside, bundle);
                TryDeleteDirectory(staged);
                throw;
            }
            TryDeleteDirectory(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            throw new UpdateBlockedException("Couldn't install the update: " + ex.Message, update.PageUrl);
        }
        finally
        {
            TryDeleteDirectory(work);
        }

        // `open -n` starts a new instance of the bundle even though this one is still running.
        var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        foreach (var arg in new[] { "-n", bundle, "--args", AfterUpdateArg, Environment.ProcessId.ToString() })
            info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
    }

    static void Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't run " + file + ".");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(file) + " failed: " + error.Trim());
    }

    // ------------------------------------------------------------------ shared

    static async Task DownloadAsync(UpdateInfo update, string destination, IProgress<double>? progress, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(destination);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }

            if (total > 0 && done != total)
                throw new IOException("The download was cut short.");
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    static void EnsureWritable(string dir, UpdateInfo update, string message)
    {
        string probe = Path.Combine(dir, ".autoclicker-write-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateBlockedException(message, update.PageUrl);
        }
    }

    static void Relaunch(string exe)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add(AfterUpdateArg);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// Run by the new copy at startup when launched with <see cref="AfterUpdateArg"/>: waits for the
    /// old copy to exit (it holds the single-instance lock and is saving settings), then clears up
    /// what the swap left behind. Returns true when this launch is the result of an update.
    /// </summary>
    public static bool CleanUpAfterUpdate(string[] args)
    {
        int at = Array.IndexOf(args, AfterUpdateArg);
        if (at < 0) return false;

        if (at + 1 < args.Length && int.TryParse(args[at + 1], out int oldPid))
        {
            try
            {
                using var old = Process.GetProcessById(oldPid);
                old.WaitForExit(15000);
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { }
        }

        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
            TryDelete(exe + ".old");
        return true;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception) { }
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", url)?.Dispose();
            else Process.Start("xdg-open", url)?.Dispose();
        }
        catch (Exception) { /* no browser to open; the status line still has the explanation */ }
    }
}
