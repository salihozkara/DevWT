namespace Devwt.Core.Tests;

public sealed class RuntimeLaunchPlanTests
{
    [Fact]
    public void Children_only_launch_can_run_without_a_fixed_context_ip()
    {
        var plan = DevwtRuntimeLaunchPlan.Create(
            launcherPath: @"C:\Program Files\DevWT\hooks\devwt-hook-launcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
            bindIp: null,
            connectIp: null,
            childrenOnly: true,
            program: @"C:\Tools\IDE\ide.exe",
            arguments: ["--restore"]);

        Assert.Equal(@"C:\Program Files\DevWT\hooks\devwt-hook-launcher.exe", plan.ExecutablePath);
        Assert.Equal(
            [
                "--children-only",
                "--dll",
                @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
                "--",
                @"C:\Tools\IDE\ide.exe",
                "--restore"
            ],
            plan.Arguments);
    }

    [Fact]
    public void Normal_launch_uses_port_shift_context_without_a_private_loopback_ip()
    {
        var plan = DevwtRuntimeLaunchPlan.Create(
            launcherPath: @"C:\Program Files\DevWT\hooks\devwt-hook-launcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
            contextId: "ctx-sample",
            bindIp: "127.0.0.1",
            connectIp: "127.0.0.1",
            portOffset: 24000,
            portBindingsPath: @"C:\Users\developer\AppData\Local\DevWT\hook-port-bindings.tsv",
            childrenOnly: false,
            program: "dotnet",
            arguments: ["run"],
            commandPath: "");

        Assert.Equal(
            [
                "--context-id",
                "ctx-sample",
                "--bind-ip",
                "127.0.0.1",
                "--connect-ip",
                "127.0.0.1",
                "--port-offset",
                "24000",
                "--port-bindings-file",
                @"C:\Users\developer\AppData\Local\DevWT\hook-port-bindings.tsv",
                "--dll",
                @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
                "--",
                "dotnet",
                "run"
            ],
            plan.Arguments);
    }

    [Fact]
    public void Normal_launch_wraps_cmd_shims_so_native_createprocess_can_start_npm()
    {
        using var temp = new TempDirectory();
        var npm = Path.Combine(temp.Path, "npm.CMD");
        File.WriteAllText(Path.Combine(temp.Path, "npm.cmd"), "@echo off\r\n");

        var plan = DevwtRuntimeLaunchPlan.Create(
            launcherPath: @"C:\Program Files\DevWT\hooks\devwt-hook-launcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
            contextId: "ctx-sample",
            bindIp: "127.0.0.1",
            connectIp: "127.0.0.1",
            portOffset: 24000,
            portBindingsPath: @"C:\Users\developer\AppData\Local\DevWT\hook-port-bindings.tsv",
            childrenOnly: false,
            program: "npm",
            arguments: ["run", "dev"],
            commandPath: temp.Path,
            commandPathExt: ".COM;.EXE;.BAT;.CMD",
            comSpec: @"C:\Windows\System32\cmd.exe");

        Assert.Equal(
            [
                "--context-id",
                "ctx-sample",
                "--bind-ip",
                "127.0.0.1",
                "--connect-ip",
                "127.0.0.1",
                "--port-offset",
                "24000",
                "--port-bindings-file",
                @"C:\Users\developer\AppData\Local\DevWT\hook-port-bindings.tsv",
                "--dll",
                @"C:\Program Files\DevWT\hooks\devwt-hook.dll",
                "--",
                @"C:\Windows\System32\cmd.exe",
                "/d",
                "/s",
                "/c",
                "call",
                npm,
                "run",
                "dev"
            ],
            plan.Arguments);
    }
}
