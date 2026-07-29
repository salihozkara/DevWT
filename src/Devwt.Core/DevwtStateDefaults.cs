namespace Devwt.Core;

public static class DevwtStateDefaults
{
    public const string StateRootEnvironmentVariable = "DEVWT_STATE_ROOT";

    public static string ResolveStateRoot(string? stateRoot)
    {
        if (!string.IsNullOrWhiteSpace(stateRoot))
        {
            return stateRoot;
        }

        var configured = GetConfiguredStateRoot();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevWT");
    }

    private static string? GetConfiguredStateRoot()
    {
        var processValue = Environment.GetEnvironmentVariable(StateRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue;
        }

        var userValue = GetEnvironmentVariable(StateRootEnvironmentVariable, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userValue))
        {
            return userValue;
        }

        return GetEnvironmentVariable(StateRootEnvironmentVariable, EnvironmentVariableTarget.Machine);
    }

    private static string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(variable, target);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
