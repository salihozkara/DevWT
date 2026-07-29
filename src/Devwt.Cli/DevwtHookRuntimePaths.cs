internal static class DevwtHookRuntimePaths
{
    private const string HookRootEnvironmentVariable = "DEVWT_HOOK_ROOT";
    private const string HookRootPointerFileName = "hook-root.txt";

    public static string ResolveHookRoot(string appBaseDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(HookRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var pointerPath = Path.Combine(appBaseDirectory, HookRootPointerFileName);
        if (File.Exists(pointerPath))
        {
            var pointedRoot = File.ReadAllText(pointerPath).Trim();
            if (!string.IsNullOrWhiteSpace(pointedRoot) && Directory.Exists(pointedRoot))
            {
                return Path.GetFullPath(pointedRoot);
            }
        }

        var installedHookRoot = Path.Combine(appBaseDirectory, "hook");
        if (Directory.Exists(installedHookRoot))
        {
            return installedHookRoot;
        }

        return appBaseDirectory;
    }
}
