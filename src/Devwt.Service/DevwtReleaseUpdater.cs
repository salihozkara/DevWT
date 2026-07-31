using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devwt.Core;

namespace Devwt.Service;

public interface IDevwtElevatedScriptRunner
{
    Task<int> RunAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class WindowsDevwtElevatedScriptRunner : IDevwtElevatedScriptRunner
{
    public async Task<int> RunAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the elevated DevWT updater.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}

public sealed partial class DevwtReleaseUpdater(
    HttpClient httpClient,
    IDevwtElevatedScriptRunner scriptRunner,
    Uri? releaseApiUri = null)
{
    public static readonly Uri DefaultReleaseApiUri =
        new("https://api.github.com/repos/salihozkara/DevWT/releases?per_page=20");

    private readonly Uri _releaseApiUri = releaseApiUri ?? DefaultReleaseApiUri;

    public async Task<DevwtCommandResult> ExecuteAsync(
        UpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        var release = await ResolveLatestReleaseAsync(cancellationToken);
        var archiveAsset = SingleAsset(
            release.Assets,
            asset => InstallerArchiveName().IsMatch(asset.Name),
            "installer ZIP");
        var checksumAsset = SingleAsset(
            release.Assets,
            asset => asset.Name.Equals($"{archiveAsset.Name}.sha256", StringComparison.Ordinal),
            "SHA-256 checksum");

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"devwt-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var archivePath = Path.Combine(tempRoot, archiveAsset.Name);
            var checksumPath = Path.Combine(tempRoot, checksumAsset.Name);
            await DownloadAsync(archiveAsset.DownloadUri, archivePath, cancellationToken);
            await DownloadAsync(checksumAsset.DownloadUri, checksumPath, cancellationToken);
            await VerifyChecksumAsync(archivePath, checksumPath, cancellationToken);

            var packageRoot = Path.Combine(tempRoot, "package");
            ZipFile.ExtractToDirectory(archivePath, packageRoot);
            var updaterScripts = Directory.GetFiles(
                packageRoot,
                "Update-DevWTManaged.ps1",
                SearchOption.AllDirectories);
            if (updaterScripts.Length != 1)
            {
                throw new InvalidDataException(
                    $"Expected exactly one Update-DevWTManaged.ps1 in the verified release, but found {updaterScripts.Length}.");
            }

            var arguments = new List<string> { "-UpdateHookRuntime" };
            if (command.StopRunningApplications)
            {
                arguments.Add("-KillHookedApplications");
            }

            var exitCode = await scriptRunner.RunAsync(
                updaterScripts[0],
                arguments,
                cancellationToken);
            var mode = command.StopRunningApplications
                ? "Hooked applications were stopped and are not restarted automatically."
                : "Existing hooked applications were left running on their loaded runtime.";
            if (exitCode != 0)
            {
                return new DevwtCommandResult(
                    $"DevWT {release.TagName} managed update failed with exit code {exitCode}.{Environment.NewLine}",
                    exitCode);
            }

            return new DevwtCommandResult(
                $"DevWT {release.TagName} managed update finished. {mode}{Environment.NewLine}",
                exitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<ReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _releaseApiUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("DevWT-Updater");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub release response was not an array.");
        }

        var releases = new List<ReleaseInfo>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()
                || !release.TryGetProperty("published_at", out var publishedElement)
                || !DateTimeOffset.TryParse(publishedElement.GetString(), out var publishedAt))
            {
                continue;
            }

            var assets = release.GetProperty("assets")
                .EnumerateArray()
                .Select(asset => new ReleaseAsset(
                    asset.GetProperty("name").GetString() ?? "",
                    ParseHttpsUri(asset.GetProperty("browser_download_url").GetString())))
                .ToArray();
            releases.Add(new ReleaseInfo(
                release.GetProperty("tag_name").GetString() ?? "",
                publishedAt,
                assets));
        }

        return releases
            .OrderByDescending(release => release.PublishedAt)
            .FirstOrDefault()
            ?? throw new InvalidDataException("No published DevWT release was found.");
    }

    private async Task DownloadAsync(
        Uri uri,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task VerifyChecksumAsync(
        string archivePath,
        string checksumPath,
        CancellationToken cancellationToken)
    {
        var checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken);
        var match = ChecksumPattern().Match(checksumText);
        if (!match.Success)
        {
            throw new InvalidDataException("The checksum asset does not contain a SHA-256 value.");
        }

        await using var archive = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken));
        if (!actualHash.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("DevWT update checksum verification failed.");
        }
    }

    private static ReleaseAsset SingleAsset(
        IReadOnlyList<ReleaseAsset> assets,
        Func<ReleaseAsset, bool> predicate,
        string description)
    {
        var matches = assets.Where(predicate).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Expected exactly one {description} asset, but found {matches.Length}.");
    }

    private static Uri ParseHttpsUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Release asset URL must use HTTPS.");
        }

        return uri;
    }

    [GeneratedRegex("^DevWT-v.+-installer\\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallerArchiveName();

    [GeneratedRegex("\\b([a-fA-F0-9]{64})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();

    private sealed record ReleaseInfo(
        string TagName,
        DateTimeOffset PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, Uri DownloadUri);
}
