namespace Devwt.Core.Tests;

public sealed class HookRuntimeCommandTests
{
    [Fact]
    public void Run_command_is_parsed_after_double_dash()
    {
        var command = Assert.IsType<RunCommand>(DevwtCommandParser.Parse(
            ["run", "--", "dotnet", "run", "--project", "src/App"],
            @"C:\repos\app"));

        Assert.Equal(@"C:\repos\app", command.WorkingDirectory);
        Assert.Equal("dotnet", command.Program);
        Assert.Equal(["run", "--project", "src/App"], command.Arguments);
    }

    [Fact]
    public void Terminal_command_defaults_to_current_worktree()
    {
        var command = Assert.IsType<TerminalCommand>(DevwtCommandParser.Parse(
            ["terminal"],
            @"C:\repos\app"));

        Assert.Equal(@"C:\repos\app", command.WorkingDirectory);
        Assert.Equal("powershell", command.Shell);
    }
}
