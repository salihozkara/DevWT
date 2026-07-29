using System.Diagnostics;

namespace Devwt.Core;

public interface ICommandRunner
{
    CommandResult Run(IReadOnlyList<string> arguments);
}

public sealed record CommandResult(int ExitCode, string Output = "", string Error = "");

public sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException("Command runner requires at least one argument.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = arguments[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        for (var index = 1; index < arguments.Count; index++)
        {
            startInfo.ArgumentList.Add(arguments[index]);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start command: {arguments[0]}");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        return new CommandResult(process.ExitCode, output, error);
    }
}
