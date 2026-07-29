namespace Devwt.Core.Tests;

public sealed class PowerShellShellIntegrationTests
{
    [Fact]
    public void Install_adds_idempotent_generic_child_injection_block_and_uninstall_removes_it()
    {
        using var temp = new TempDirectory();
        var profilePath = Path.Combine(temp.Path, "Microsoft.PowerShell_profile.ps1");
        File.WriteAllText(profilePath, "# user content\n");

        var first = PowerShellShellIntegration.Install(profilePath);
        var second = PowerShellShellIntegration.Install(profilePath);
        var profile = File.ReadAllText(profilePath);

        Assert.True(first.Modified);
        Assert.False(second.Modified);
        Assert.Equal(1, CountOccurrences(profile, "DEVWT SHELL INTEGRATION BEGIN"));
        Assert.Contains("devwt shell attach --pid $PID", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("function global:node", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("function global:dotnet", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeCommands", profile, StringComparison.Ordinal);

        var removed = PowerShellShellIntegration.Uninstall(profilePath);
        profile = File.ReadAllText(profilePath);

        Assert.True(removed.Modified);
        Assert.DoesNotContain("DEVWT SHELL INTEGRATION BEGIN", profile, StringComparison.Ordinal);
        Assert.Contains("# user content", profile, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
