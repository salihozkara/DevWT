using System.Runtime.InteropServices;
using Devwt.Core;

internal static class DevwtShortcutCommand
{
    public static int Execute(IReadOnlyList<string> args, string currentDirectory, string devwtExecutablePath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("shortcut commands are only supported on Windows.");
                return 2;
            }

            var parsed = DevwtCommandParser.Parse(args, currentDirectory);
            if (parsed is not ShortcutCommand command)
            {
                Console.Error.WriteLine("shortcut command could not be parsed.");
                return 2;
            }

            var shortcuts = ResolveShortcutPaths(command).ToArray();
            if (shortcuts.Length == 0)
            {
                Console.Error.WriteLine("No matching shortcuts found.");
                return 1;
            }

            return command.Action switch
            {
                ShortcutAction.List => List(shortcuts),
                ShortcutAction.Wrap => Wrap(shortcuts, command, devwtExecutablePath),
                ShortcutAction.Restore => Restore(shortcuts, command.DryRun),
                _ => 2
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or COMException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int List(IReadOnlyList<string> shortcutPaths)
    {
        foreach (var path in shortcutPaths)
        {
            var shortcut = WindowsShortcutStore.Read(path);
            Console.WriteLine($"{System.IO.Path.GetFileNameWithoutExtension(path)}");
            Console.WriteLine($"  path: {path}");
            Console.WriteLine($"  target: {shortcut.TargetPath}");
            if (!string.IsNullOrWhiteSpace(shortcut.Arguments))
            {
                Console.WriteLine($"  args: {shortcut.Arguments}");
            }
        }

        return 0;
    }

    private static int Wrap(IReadOnlyList<string> shortcutPaths, ShortcutCommand command, string devwtExecutablePath)
    {
        var exitCode = 0;
        foreach (var path in shortcutPaths)
        {
            var shortcut = WindowsShortcutStore.Read(path);
            var plan = DevwtShortcutPlanner.CreateWrapPlan(shortcut, devwtExecutablePath, command.WorktreePath);
            if (plan.AlreadyWrapped)
            {
                Console.WriteLine($"already wrapped: {path}");
                continue;
            }

            if (command.DryRun)
            {
                Console.WriteLine($"would wrap: {path}");
                Console.WriteLine($"  backup: {plan.BackupPath}");
                Console.WriteLine($"  target: {plan.TargetPath}");
                Console.WriteLine($"  args: {plan.Arguments}");
                continue;
            }

            if (!File.Exists(plan.BackupPath))
            {
                File.Copy(path, plan.BackupPath);
            }

            WindowsShortcutStore.Write(plan);
            Console.WriteLine($"wrapped: {path}");
        }

        return exitCode;
    }

    private static int Restore(IReadOnlyList<string> shortcutPaths, bool dryRun)
    {
        var exitCode = 0;
        foreach (var path in shortcutPaths)
        {
            var backupPath = DevwtShortcutPlanner.BuildBackupPath(path);
            if (!File.Exists(backupPath))
            {
                Console.Error.WriteLine($"backup not found: {backupPath}");
                exitCode = 1;
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"would restore: {path}");
                Console.WriteLine($"  backup: {backupPath}");
                continue;
            }

            File.Copy(backupPath, path, overwrite: true);
            Console.WriteLine($"restored: {path}");
        }

        return exitCode;
    }

    private static IEnumerable<string> ResolveShortcutPaths(ShortcutCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.ShortcutPath))
        {
            if (!File.Exists(command.ShortcutPath))
            {
                throw new FileNotFoundException($"Shortcut not found: {command.ShortcutPath}", command.ShortcutPath);
            }

            yield return command.ShortcutPath;
            yield break;
        }

        var folder = ResolveTaskbarShortcutFolder();
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Taskbar shortcut folder not found: {folder}");
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.lnk").Order(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = System.IO.Path.GetFileName(path);
            if (fileName.Contains(".devwt.bak.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (command.Action == ShortcutAction.List ||
                command.All ||
                (!string.IsNullOrWhiteSpace(command.Name) &&
                    System.IO.Path.GetFileNameWithoutExtension(path).Contains(command.Name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return path;
            }
        }
    }

    private static string ResolveTaskbarShortcutFolder() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

    private static class WindowsShortcutStore
    {
        public static DevwtShortcut Read(string path)
        {
            using var shell = ComObject.Create("WScript.Shell");
            using var shortcut = shell.InvokeCreateShortcut(path);
            dynamic value = shortcut.Value;
            return new DevwtShortcut(
                Path: path,
                TargetPath: value.TargetPath ?? "",
                Arguments: value.Arguments ?? "",
                WorkingDirectory: value.WorkingDirectory ?? "",
                IconLocation: value.IconLocation ?? "");
        }

        public static void Write(DevwtShortcutWrapPlan plan)
        {
            using var shell = ComObject.Create("WScript.Shell");
            using var shortcut = shell.InvokeCreateShortcut(plan.ShortcutPath);
            dynamic value = shortcut.Value;
            value.TargetPath = plan.TargetPath;
            value.Arguments = plan.Arguments;
            value.WorkingDirectory = plan.WorkingDirectory;
            value.IconLocation = plan.IconLocation;
            value.Save();
        }
    }

    private sealed class ComObject : IDisposable
    {
        private ComObject(object value)
        {
            Value = value;
        }

        public object Value { get; }

        public static ComObject Create(string progId)
        {
#pragma warning disable CA1416
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"{progId} is not available on this machine.");
#pragma warning restore CA1416
            return new ComObject(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Failed to create {progId}."));
        }

        public ComObject InvokeCreateShortcut(string path)
        {
            dynamic shell = Value;
            object shortcut = shell.CreateShortcut(path);
            return new ComObject(shortcut);
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(Value))
            {
#pragma warning disable CA1416
                Marshal.FinalReleaseComObject(Value);
#pragma warning restore CA1416
            }
        }
    }
}
