using System.Diagnostics;
using Devwt.Core;
using Devwt.Service;

internal static class DevwtRuntimeLauncher
{
    public static int Execute(IReadOnlyList<string> args, string currentDirectory)
    {
        DevwtCommand command;
        try
        {
            command = DevwtCommandParser.Parse(args, currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        return command switch
        {
            RunCommand run => RunProgram(run.WorkingDirectory, run.Program, run.Arguments, passthroughOutsideContext: false, run.ChildrenOnly),
            ExecCommand exec => RunProgram(exec.WorkingDirectory, exec.Program, exec.Arguments, passthroughOutsideContext: true, exec.ChildrenOnly),
            TerminalCommand terminal => RunUnhookedProgram(
                terminal.WorkingDirectory,
                ResolveShell(terminal.Shell),
                []),
            _ => 2
        };
    }

    private static int RunProgram(
        string workingDirectory,
        string program,
        IReadOnlyList<string> arguments,
        bool passthroughOutsideContext,
        bool childrenOnly)
    {
        try
        {
            if (!childrenOnly && IsShellOrTerminalHost(program))
            {
                return RunUnhookedProgram(workingDirectory, program, arguments);
            }

            var store = new DevwtStateStore();
            var context = ResolveContext(store.LoadContexts(), workingDirectory);
            if (context is null && !childrenOnly)
            {
                if (passthroughOutsideContext)
                {
                    return RunUnhookedProgram(workingDirectory, program, arguments);
                }

                Console.Error.WriteLine($"No active DevWT context contains: {workingDirectory}");
                Console.Error.WriteLine("Run `devwt add` in the repository first, then retry from a registered worktree.");
                return 2;
            }

            var hookRoot = ResolveHookRoot();
            var launcherPath = Path.Combine(hookRoot, "devwt-hook-launcher.exe");
            var hookDllPath = Path.Combine(hookRoot, "devwt-hook.dll");
            if (!File.Exists(launcherPath) || !File.Exists(hookDllPath))
            {
                Console.Error.WriteLine($"Hook runtime artifacts are missing under: {hookRoot}");
                Console.Error.WriteLine("Install DevWT from the installer bundle or set DEVWT_HOOK_ROOT to the hook artifact directory.");
                return 2;
            }

            var plan = DevwtRuntimeLaunchPlan.Create(
                launcherPath: launcherPath,
                hookDllPath: hookDllPath,
                contextId: context?.Id,
                bindIp: context is null ? null : DevwtPortShift.LoopbackAddress,
                connectIp: context is null ? null : DevwtPortShift.LoopbackAddress,
                childrenOnly: childrenOnly,
                program: program,
                arguments: arguments,
                portOffset: context?.AssignedPortBase,
                portBindingsPath: HookPortBindingMap.ResolvePath(store.StateRoot));

            var startInfo = new ProcessStartInfo(plan.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };
            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("Failed to start hook launcher.");
                return 1;
            }

            return WaitForStartedProcess(process);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int RunUnhookedProgram(string workingDirectory, string program, IReadOnlyList<string> arguments)
    {
        try
        {
            var resolved = DevwtWindowsCommandResolver.Resolve(program, arguments);
            var startInfo = new ProcessStartInfo(resolved.Program)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };
            foreach (var argument in resolved.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("Failed to start process.");
                return 1;
            }

            return WaitForStartedProcess(process);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static DevwtContext? ResolveContext(DevwtContextState state, string workingDirectory) =>
        state.Contexts
            .Where(context => context.Status == DevwtContextStatus.Active)
            .Where(context => DevwtPath.IsUnderRoot(workingDirectory, context.WorktreeRootPath))
            .OrderByDescending(context => context.WorktreeRootPath.Length)
            .FirstOrDefault();

    private static int WaitForStartedProcess(Process process)
    {
        using var cancelRequested = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancelRequested.Set();
            TryKillProcessTree(process);
        };

        var registered = TryRegisterCancelHandler(handler);
        try
        {
            while (true)
            {
                if (process.WaitForExit(100))
                {
                    return process.ExitCode;
                }

                if (!cancelRequested.IsSet)
                {
                    continue;
                }

                TryKillProcessTree(process);
                process.WaitForExit();
                return TryGetExitCode(process, 130);
            }
        }
        finally
        {
            if (registered)
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }

    private static bool TryRegisterCancelHandler(ConsoleCancelEventHandler handler)
    {
        try
        {
            Console.CancelKeyPress += handler;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static int TryGetExitCode(Process process, int fallback)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static string ResolveHookRoot()
        => DevwtHookRuntimePaths.ResolveHookRoot(AppContext.BaseDirectory);

    private static string ResolveShell(string shell) =>
        shell.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

    private static bool IsShellOrTerminalHost(string program)
    {
        var fileName = Path.GetFileNameWithoutExtension(program);
        return fileName.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("WindowsTerminalPreview", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("conhost", StringComparison.OrdinalIgnoreCase);
    }
}
