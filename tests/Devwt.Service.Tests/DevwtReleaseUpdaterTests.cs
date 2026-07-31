using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Devwt.Core;

namespace Devwt.Service.Tests;

public sealed class DevwtReleaseUpdaterTests
{
    [Fact]
    public async Task Update_downloads_a_verified_bundle_and_preserves_running_applications_by_default()
    {
        var fixture = CreateReleaseFixture();
        var runner = new RecordingElevatedScriptRunner();
        using var client = new HttpClient(fixture.Handler);
        var updater = new DevwtReleaseUpdater(client, runner, fixture.ReleaseApiUri);

        var result = await updater.ExecuteAsync(new UpdateCommand(StopRunningApplications: false));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("v0.1.0-preview.test", result.Output, StringComparison.Ordinal);
        Assert.Contains("-UpdateHookRuntime", runner.Arguments);
        Assert.DoesNotContain("-KillHookedApplications", runner.Arguments);
        Assert.Contains("managed updater", runner.ScriptContents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_can_explicitly_stop_running_applications()
    {
        var fixture = CreateReleaseFixture();
        var runner = new RecordingElevatedScriptRunner();
        using var client = new HttpClient(fixture.Handler);
        var updater = new DevwtReleaseUpdater(client, runner, fixture.ReleaseApiUri);

        var result = await updater.ExecuteAsync(new UpdateCommand(StopRunningApplications: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-UpdateHookRuntime", runner.Arguments);
        Assert.Contains("-KillHookedApplications", runner.Arguments);
    }

    [Fact]
    public async Task Update_rejects_a_checksum_mismatch_before_elevation()
    {
        var fixture = CreateReleaseFixture(checksumOverride: new string('0', 64));
        var runner = new RecordingElevatedScriptRunner();
        using var client = new HttpClient(fixture.Handler);
        var updater = new DevwtReleaseUpdater(client, runner, fixture.ReleaseApiUri);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => updater.ExecuteAsync(new UpdateCommand(StopRunningApplications: false)));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task Update_reports_an_elevated_updater_failure()
    {
        var fixture = CreateReleaseFixture();
        var runner = new RecordingElevatedScriptRunner { ExitCode = 7 };
        using var client = new HttpClient(fixture.Handler);
        var updater = new DevwtReleaseUpdater(client, runner, fixture.ReleaseApiUri);

        var result = await updater.ExecuteAsync(new UpdateCommand(StopRunningApplications: false));

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("failed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static ReleaseFixture CreateReleaseFixture(string? checksumOverride = null)
    {
        var zipBytes = CreateBundle();
        var hash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        var checksum = checksumOverride ?? hash;
        var releaseApiUri = new Uri("https://api.example.test/releases");
        const string archiveName = "DevWT-v0.1.0-preview.test-installer.zip";
        var archiveUri = new Uri($"https://downloads.example.test/{archiveName}");
        var checksumUri = new Uri($"https://downloads.example.test/{archiveName}.sha256");
        var json = $$"""
            [
              {
                "tag_name": "v0.1.0-preview.test",
                "draft": false,
                "published_at": "2026-07-29T20:00:00Z",
                "assets": [
                  {
                    "name": "{{archiveName}}",
                    "browser_download_url": "{{archiveUri}}"
                  },
                  {
                    "name": "{{archiveName}}.sha256",
                    "browser_download_url": "{{checksumUri}}"
                  }
                ]
              }
            ]
            """;

        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri == releaseApiUri)
            {
                return JsonResponse(json);
            }

            if (request.RequestUri == archiveUri)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                };
            }

            if (request.RequestUri == checksumUri)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{checksum}  {archiveName}", Encoding.ASCII)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return new ReleaseFixture(releaseApiUri, handler);
    }

    private static byte[] CreateBundle()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var updater = archive.CreateEntry("Update-DevWTManaged.ps1");
            using var writer = new StreamWriter(updater.Open(), Encoding.UTF8);
            writer.Write("# managed updater");
        }

        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed record ReleaseFixture(Uri ReleaseApiUri, StubHttpMessageHandler Handler);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class RecordingElevatedScriptRunner : IDevwtElevatedScriptRunner
    {
        public int CallCount { get; private set; }
        public int ExitCode { get; init; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string ScriptContents { get; private set; } = "";

        public Task<int> RunAsync(
            string scriptPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Arguments = arguments;
            ScriptContents = File.ReadAllText(scriptPath);
            return Task.FromResult(ExitCode);
        }
    }
}
