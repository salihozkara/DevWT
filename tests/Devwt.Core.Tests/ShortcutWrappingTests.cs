namespace Devwt.Core.Tests;

public sealed class ShortcutWrappingTests
{
    [Fact]
    public void Wrap_plan_preserves_original_target_as_devwt_children_only_arguments()
    {
        var shortcut = new DevwtShortcut(
            Path: @"C:\Pinned\JetBrains Rider.lnk",
            TargetPath: @"C:\Users\salih\AppData\Local\Programs\Rider\bin\rider64.exe",
            Arguments: "--restore",
            WorkingDirectory: @"C:\Users\salih\AppData\Local\Programs\Rider\bin",
            IconLocation: "");

        var plan = DevwtShortcutPlanner.CreateWrapPlan(
            shortcut,
            @"C:\Program Files\DevWT\app\Devwt.Cli.exe",
            worktreePath: null);

        Assert.False(plan.AlreadyWrapped);
        Assert.Equal(@"C:\Pinned\JetBrains Rider.devwt.bak.lnk", plan.BackupPath);
        Assert.Equal(@"C:\Program Files\DevWT\app\Devwt.Cli.exe", plan.TargetPath);
        Assert.Equal(
            @"run --children-only -- ""C:\Users\salih\AppData\Local\Programs\Rider\bin\rider64.exe"" --restore",
            plan.Arguments);
        Assert.Equal(@"C:\Users\salih\AppData\Local\Programs\Rider\bin", plan.WorkingDirectory);
        Assert.Equal(@"C:\Users\salih\AppData\Local\Programs\Rider\bin\rider64.exe,0", plan.IconLocation);
    }

    [Fact]
    public void Wrap_plan_can_include_worktree_fallback_context()
    {
        var shortcut = new DevwtShortcut(
            Path: @"C:\Pinned\ABP Studio.lnk",
            TargetPath: @"C:\Users\salih\AppData\Local\abp-studio\current\Volo.Abp.Studio.UI.Host.exe",
            Arguments: @"""D:\GitHub\volo\abp\low-code\Volo.Abp.LowCode.abpsln""",
            WorkingDirectory: "",
            IconLocation: @"C:\Icons\studio.ico");

        var plan = DevwtShortcutPlanner.CreateWrapPlan(
            shortcut,
            @"C:\Program Files\DevWT\app\Devwt.Cli.exe",
            worktreePath: @"D:\GitHub\volo");

        Assert.Equal(
            @"run --children-only --worktree ""D:\GitHub\volo"" -- ""C:\Users\salih\AppData\Local\abp-studio\current\Volo.Abp.Studio.UI.Host.exe"" ""D:\GitHub\volo\abp\low-code\Volo.Abp.LowCode.abpsln""",
            plan.Arguments);
        Assert.Equal(@"C:\Icons\studio.ico", plan.IconLocation);
    }
}
