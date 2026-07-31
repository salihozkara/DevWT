namespace Devwt.Core.Tests;

public sealed class ShortcutWrappingTests
{
    [Fact]
    public void Wrap_plan_preserves_original_target_as_devwt_children_only_arguments()
    {
        var shortcut = new DevwtShortcut(
            Path: @"C:\Pinned\JetBrains Rider.lnk",
            TargetPath: @"C:\Users\developer\AppData\Local\Programs\Rider\bin\rider64.exe",
            Arguments: "--restore",
            WorkingDirectory: @"C:\Users\developer\AppData\Local\Programs\Rider\bin",
            IconLocation: "");

        var plan = DevwtShortcutPlanner.CreateWrapPlan(
            shortcut,
            @"C:\Program Files\DevWT\app\Devwt.Cli.exe",
            worktreePath: null);

        Assert.False(plan.AlreadyWrapped);
        Assert.Equal(@"C:\Pinned\JetBrains Rider.devwt.bak.lnk", plan.BackupPath);
        Assert.Equal(@"C:\Program Files\DevWT\app\Devwt.Cli.exe", plan.TargetPath);
        Assert.Equal(
            @"run --children-only -- ""C:\Users\developer\AppData\Local\Programs\Rider\bin\rider64.exe"" --restore",
            plan.Arguments);
        Assert.Equal(@"C:\Users\developer\AppData\Local\Programs\Rider\bin", plan.WorkingDirectory);
        Assert.Equal(@"C:\Users\developer\AppData\Local\Programs\Rider\bin\rider64.exe,0", plan.IconLocation);
    }

    [Fact]
    public void Wrap_plan_can_include_worktree_fallback_context()
    {
        var shortcut = new DevwtShortcut(
            Path: @"C:\Pinned\Sample IDE.lnk",
            TargetPath: @"C:\Tools\SampleIDE\sample-ide.exe",
            Arguments: @"""D:\repos\sample-app\sample-app.sln""",
            WorkingDirectory: "",
            IconLocation: @"C:\Icons\studio.ico");

        var plan = DevwtShortcutPlanner.CreateWrapPlan(
            shortcut,
            @"C:\Program Files\DevWT\app\Devwt.Cli.exe",
            worktreePath: @"D:\repos\sample-app");

        Assert.Equal(
            @"run --children-only --worktree ""D:\repos\sample-app"" -- ""C:\Tools\SampleIDE\sample-ide.exe"" ""D:\repos\sample-app\sample-app.sln""",
            plan.Arguments);
        Assert.Equal(@"C:\Icons\studio.ico", plan.IconLocation);
    }
}
