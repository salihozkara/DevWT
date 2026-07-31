using Devwt.Core;

namespace Devwt.Service.Tests;

public sealed class HookRuntimeProductTests
{
    [Fact]
    public void Hook_runtime_planner_maps_active_contexts_to_folder_watcher()
    {
        var context = new DevwtContext(
            Id: "ctx-sample-feature",
            RepositoryId: "repo-sample",
            Name: "sample-feature",
            WorktreeRootPath: @"C:\work\sample-feature",
            GitRef: "feature/demo",
            AssignedIp: "127.0.0.1",
            RuntimeName: "DevWT-ctx-sample-feature",
            Status: DevwtContextStatus.Active,
            AssignedPortBase: 24000);

        var command = HookRuntimeCommandPlanner.PlanFolderWatcher(
            watcherPath: @"C:\Program Files\DevWT\app\hook\devwt-folder-watcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\app\hook\devwt-hook.dll",
            contexts: [context],
            mapFilePath: @"C:\ProgramData\DevWT\hook-contexts.tsv");

        Assert.Equal(@"C:\Program Files\DevWT\app\hook\devwt-folder-watcher.exe", command.ExecutablePath);
        Assert.Contains("--poll-ms", command.Arguments);
        Assert.Contains("1000", command.Arguments);
        Assert.Contains("--duration-ms", command.Arguments);
        Assert.Contains("0", command.Arguments);
        Assert.Contains("--process-events", command.Arguments);
        Assert.Contains("--map-file", command.Arguments);
        Assert.Contains(@"C:\ProgramData\DevWT\hook-contexts.tsv", command.Arguments);
        Assert.DoesNotContain("--watch-shells", command.Arguments);
        Assert.Contains("--map", command.Arguments);
        Assert.Contains(@"C:\work\sample-feature=ctx-sample-feature,127.0.0.1,127.0.0.1,24000", command.Arguments);
    }

    [Fact]
    public void Hook_runtime_planner_includes_children_only_ide_parent_images()
    {
        var context = new DevwtContext(
            Id: "ctx-sample-feature",
            RepositoryId: "repo-sample",
            Name: "sample-feature",
            WorktreeRootPath: @"C:\work\sample-feature",
            GitRef: "feature/demo",
            AssignedIp: "127.0.0.1",
            RuntimeName: "DevWT-ctx-sample-feature",
            Status: DevwtContextStatus.Active,
            AssignedPortBase: 24000);
        var ide = new DevwtIdeWatch("Rider", @"C:\Tools\Rider\bin\rider64.exe");

        var command = HookRuntimeCommandPlanner.PlanFolderWatcher(
            watcherPath: @"C:\Program Files\DevWT\app\hook\devwt-folder-watcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\app\hook\devwt-hook.dll",
            contexts: [context],
            ideWatches: [ide]);

        Assert.Contains("--children-only-image", command.Arguments);
        Assert.Contains(@"C:\Tools\Rider\bin\rider64.exe", command.Arguments);
    }

    [Fact]
    public void Hook_runtime_planner_includes_children_only_store_package_families()
    {
        var context = new DevwtContext(
            Id: "ctx-sample-feature",
            RepositoryId: "repo-sample",
            Name: "sample-feature",
            WorktreeRootPath: @"C:\work\sample-feature",
            GitRef: "feature/demo",
            AssignedIp: "127.0.0.1",
            RuntimeName: "DevWT-ctx-sample-feature",
            Status: DevwtContextStatus.Active,
            AssignedPortBase: 24000);
        var codex = new DevwtIdeWatch(
            Name: "Codex",
            ImagePath: null,
            AppId: "OpenAI.Codex_2p2nqsd0c76g0!App",
            PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

        var command = HookRuntimeCommandPlanner.PlanFolderWatcher(
            watcherPath: @"C:\Program Files\DevWT\app\hook\devwt-folder-watcher.exe",
            hookDllPath: @"C:\Program Files\DevWT\app\hook\devwt-hook.dll",
            contexts: [context],
            ideWatches: [codex]);

        Assert.Contains("--children-only-package-family", command.Arguments);
        Assert.Contains("OpenAI.Codex_2p2nqsd0c76g0", command.Arguments);
    }

    [Fact]
    public void Hook_runtime_context_map_orders_longest_roots_before_prefix_roots()
    {
        using var temp = new TempDirectory();
        var mapPath = Path.Combine(temp.Path, "hook-contexts.tsv");
        var shorter = new DevwtContext(
            Id: "ctx-tab-a",
            RepositoryId: "repo-tab",
            Name: "tab-a",
            WorktreeRootPath: @"C:\devwt-tab-https-smoke\tab-a",
            GitRef: "main",
            AssignedIp: "127.80.92.206",
            RuntimeName: "DevWT-ctx-tab-a",
            Status: DevwtContextStatus.Active);
        var longer = shorter with
        {
            Id = "ctx-tab-a-test",
            Name = "tab-a-test",
            WorktreeRootPath = @"C:\devwt-tab-https-smoke\tab-a-test",
            AssignedIp = "127.0.0.1",
            AssignedPortBase = 25000
        };

        HookRuntimeContextMap.Write(mapPath, [shorter, longer]);

        var lines = File.ReadAllLines(mapPath);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(@"C:\devwt-tab-https-smoke\tab-a-test" + "\tctx-tab-a-test\t127.0.0.1\t127.0.0.1\t25000", lines[0]);
        Assert.StartsWith(@"C:\devwt-tab-https-smoke\tab-a" + "\tctx-tab-a\t127.0.0.1\t127.0.0.1\t", lines[1]);
    }

    [Fact]
    public void Hook_port_binding_map_reads_original_and_target_bind_endpoints()
    {
        using var temp = new TempDirectory();
        var path = HookPortBindingMap.ResolvePath(temp.Path);
        File.WriteAllLines(path, [
            "ctx-sample\t192.168.1.10\t44334\t192.168.1.10\t55297\t42\tudp"
        ]);

        var binding = Assert.Single(HookPortBindingMap.Read(path));

        Assert.Equal("ctx-sample", binding.ContextId);
        Assert.Equal("192.168.1.10", binding.OriginalIp);
        Assert.Equal(44334, binding.OriginalPort);
        Assert.Equal("192.168.1.10", binding.TargetIp);
        Assert.Equal(55297, binding.TargetPort);
        Assert.Equal(42, binding.ProcessId);
        Assert.Equal(GatewayRouteProtocol.Udp, binding.Protocol);
    }

    [Fact]
    public void Hook_port_binding_map_reads_ipv6_loopback_endpoints()
    {
        using var temp = new TempDirectory();
        var path = HookPortBindingMap.ResolvePath(temp.Path);
        File.WriteAllLines(path, [
            "ctx-sample\t::1\t44334\t::1\t55297\t42\ttcp"
        ]);

        var binding = Assert.Single(HookPortBindingMap.Read(path));

        Assert.Equal("::1", binding.OriginalIp);
        Assert.Equal(44334, binding.OriginalPort);
        Assert.Equal("::1", binding.TargetIp);
        Assert.Equal(55297, binding.TargetPort);
        Assert.Equal(GatewayRouteProtocol.Tcp, binding.Protocol);
    }

    [Fact]
    public void Port_shift_hook_is_runtime_agnostic_and_handles_ipv6_bindings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hookSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "src",
            "devwt_hook.cpp"));
        var listenerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Devwt.Service",
            "MonitorObservationSources.cs"));
        var watcherSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "src",
            "devwt_folder_watcher.cpp"));
        var cliSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Devwt.Cli",
            "Program.cs"));
        var smokeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "Run-PortShiftIpv6HookSmoke.ps1"));
        var environmentSmokeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "Run-CreateProcessEnvironmentPassThroughSmoke.ps1"));

        Assert.Contains("sockaddr_storage OriginalEndpoint", hookSource, StringComparison.Ordinal);
        Assert.Contains("name->sa_family == AF_INET6", hookSource, StringComparison.Ordinal);
        Assert.Contains("&& !g_hasPortOffset", hookSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RewriteCreateProcessEnvironment", hookSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_URLS", hookSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DOTNET_URLS", hookSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel__Endpoints", hookSource, StringComparison.Ordinal);
        Assert.DoesNotContain("node.exe", watcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet.exe", watcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("python.exe", watcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--DevWT-Proxy--", watcherSource, StringComparison.Ordinal);
        Assert.Contains("--children-only-pid", watcherSource, StringComparison.Ordinal);
        Assert.Contains("--children-only-pid", cliSource, StringComparison.Ordinal);
        Assert.Contains("ipv6", smokeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--bind-ip\", \"::1", smokeSource, StringComparison.Ordinal);
        Assert.Contains("DEVWT_TEST_ENDPOINT", environmentSmokeSource, StringComparison.Ordinal);
        Assert.Contains("Child environment changed", environmentSmokeSource, StringComparison.Ordinal);
        Assert.Contains("AfInet6 = 23", listenerSource, StringComparison.Ordinal);
        Assert.Contains("MibTcp6RowOwnerPid", listenerSource, StringComparison.Ordinal);
        Assert.Contains("MibUdp6RowOwnerPid", listenerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_bind_probe_can_exercise_application_owned_port_sharing()
    {
        var repositoryRoot = FindRepositoryRoot();
        var probeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "src",
            "devwt_bind_probe.cpp"));
        var smokeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "poc",
            "hook-win32",
            "Run-SameContextPortReuseHookSmoke.ps1"));

        Assert.Contains("SO_REUSEADDR", probeSource, StringComparison.Ordinal);
        Assert.Contains("SO_EXCLUSIVEADDRUSE", probeSource, StringComparison.Ordinal);
        Assert.Contains("--reuse-address", smokeSource, StringComparison.Ordinal);
        Assert.Contains("--exclusive-address-use", smokeSource, StringComparison.Ordinal);
        Assert.Contains("same target port", smokeSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hook_launcher_owns_target_process_tree_for_ctrl_c_cleanup()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "poc",
            "hook-win32",
            "src",
            "devwt_hook_launcher.cpp"));

        Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", source, StringComparison.Ordinal);
        Assert.Contains("AssignProcessToJobObject", source, StringComparison.Ordinal);
        Assert.Contains("CloseHandle(job)", source, StringComparison.Ordinal);
        Assert.Contains("CREATE_BREAKAWAY_FROM_JOB", source, StringComparison.Ordinal);
        Assert.Contains("TerminateProcess(process.hProcess, 101)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignProcessToJobObject failed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_hook_does_not_inherit_parent_context_when_context_map_exists_without_a_directory_match()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "poc",
                "hook-win32",
                "src",
                "devwt_hook.cpp"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "if (hasMapFile)\n    {\n        return false;\n    }\n\n    if (!g_hasBindAddress)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (BuildContextMapPath(mapPath, sizeof(mapPath)))\n    {\n        offset += _snprintf_s(content + offset, sizeof(content) - offset, _TRUNCATE, \"DEVWT_HOOK_MAP_FILE=%s\\n\", mapPath);",
            source,
            StringComparison.Ordinal);

        var watcherSource = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "poc",
                "hook-win32",
                "src",
                "devwt_folder_watcher.cpp"))
            .ReplaceLineEndings("\n");
        Assert.Contains(
            "const std::string &portOffset,\n    const std::wstring &mapFilePath,\n    const std::wstring &portBindingsFilePath)",
            watcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "match->PortOffset,\n        state.MapFilePath,\n        state.PortBindingsFilePath",
            watcherSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_launcher_kills_started_process_tree_on_console_cancel()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Devwt.Cli",
            "DevwtRuntimeLauncher.cs"));

        Assert.Contains("Console.CancelKeyPress", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_launcher_resolves_unhooked_windows_command_shims_before_starting_them()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Devwt.Cli",
            "DevwtRuntimeLauncher.cs"));

        Assert.Contains(
            "var resolved = DevwtWindowsCommandResolver.Resolve(program, arguments);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("new ProcessStartInfo(resolved.Program)", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var argument in resolved.Arguments)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_bundle_does_not_publish_managed_debug_symbols()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "installer",
            "Build-DevWTInstallerBundle.ps1"));

        Assert.Contains("-p:DebugType=None", source, StringComparison.Ordinal);
        Assert.Contains("-p:DebugSymbols=false", source, StringComparison.Ordinal);
        Assert.Contains("-p:ContinuousIntegrationBuild=true", source, StringComparison.Ordinal);
        Assert.Contains("-p:PathMap=$repoRoot=/_/", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_stops_orphan_hook_injectors_before_copying_runtime()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Install-DevWT.ps1"));

        Assert.Contains("[switch] $KillHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookInjectors", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("devwt-folder-watcher", source, StringComparison.OrdinalIgnoreCase);
        var serviceStop = source.IndexOf("Stop-DevwtServiceForUpgrade", StringComparison.Ordinal);
        var injectorStop = source.IndexOf("Stop-DevwtHookInjectors", serviceStop, StringComparison.Ordinal);
        var hookedAppStop = source.IndexOf("Stop-DevwtHookedApplications -Kill:$KillHookedApplications", injectorStop, StringComparison.Ordinal);
        var hookCopy = source.IndexOf("Copy-Item -Path (Join-Path $hookSource '*')", StringComparison.Ordinal);
        Assert.True(serviceStop >= 0);
        Assert.True(injectorStop > serviceStop);
        Assert.True(hookedAppStop > injectorStop);
        Assert.True(hookCopy > hookedAppStop);
    }

    [Fact]
    public void Installer_does_not_kill_hooked_applications_by_default()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Install-DevWT.ps1"));

        Assert.Contains("KillHookedApplications was not specified", source, StringComparison.Ordinal);
        Assert.Contains("Stop-DevwtHookedApplications -Kill:$KillHookedApplications", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_reinstaller_closes_runtime_and_hooked_applications_before_reinstalling()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Reinstall-DevWTClean.ps1"));

        Assert.Contains("Stop-DevwtRuntimeProcesses", source, StringComparison.Ordinal);
        Assert.Contains("-KillHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("-RemoveState:$RemoveState", source, StringComparison.Ordinal);
        Assert.Contains("Uninstall-DevWT.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Install-DevWT.ps1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_updater_preserves_applications_and_can_stage_a_new_hook_runtime()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Update-DevWTManaged.ps1"));

        Assert.Contains("app-versions", source, StringComparison.Ordinal);
        Assert.Contains("$installedHookPointer", source, StringComparison.Ordinal);
        Assert.Contains("[switch] $UpdateHookRuntime", source, StringComparison.Ordinal);
        Assert.Contains("$activeHookRoot", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookInjectors", source, StringComparison.Ordinal);
        Assert.Contains("Set-DevwtServiceBinary", source, StringComparison.Ordinal);
        Assert.Contains("Wait-DevwtGatewayEndpoints", source, StringComparison.Ordinal);
        Assert.Contains("$previousServicePath", source, StringComparison.Ordinal);
        Assert.Contains("[switch] $KillHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("KillHookedApplications was not specified", source, StringComparison.Ordinal);
        Assert.Contains(
            "KillHookedApplications requires -UpdateHookRuntime",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Stop-DevwtHookedApplications -Kill:$KillHookedApplications", source, StringComparison.Ordinal);
        var updateHookConditional = source.IndexOf("if ($UpdateHookRuntime)", StringComparison.Ordinal);
        var hookCopy = source.IndexOf("Copy-Item -Path (Join-Path $hookSource '*')", StringComparison.Ordinal);
        var hookedAppStop = source.IndexOf(
            "Stop-DevwtHookedApplications -Kill:$KillHookedApplications",
            hookCopy,
            StringComparison.Ordinal);
        var serviceStop = source.IndexOf("Stop-Service -Name 'DevWTService' -Force", StringComparison.Ordinal);
        var injectorStop = source.IndexOf("Stop-DevwtHookInjectors", serviceStop, StringComparison.Ordinal);
        var serviceSwitch = source.IndexOf("Set-DevwtServiceBinary $newServicePath", injectorStop, StringComparison.Ordinal);
        Assert.True(updateHookConditional >= 0);
        Assert.True(hookCopy > updateHookConditional);
        Assert.True(hookedAppStop > hookCopy);
        Assert.True(serviceStop >= 0);
        Assert.True(serviceStop > hookedAppStop);
        Assert.True(injectorStop > serviceStop);
        Assert.True(serviceSwitch > injectorStop);
    }

    [Fact]
    public void Installer_detects_hooked_applications_from_any_installed_devwt_hook_path()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Install-DevWT.ps1"));

        Assert.Contains("Test-DevwtPathUnderRoot $module.FileName $InstallRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_stops_orphan_hook_injectors_before_disconnect_only_return()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Uninstall-DevWT.ps1"));

        Assert.Contains("[switch] $KillHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookInjectors", source, StringComparison.Ordinal);
        Assert.Contains("function Stop-DevwtHookedApplications", source, StringComparison.Ordinal);
        Assert.Contains("devwt-folder-watcher", source, StringComparison.OrdinalIgnoreCase);
        var injectorStop = source.IndexOf("Stop-DevwtHookInjectors", StringComparison.Ordinal);
        var hookedAppStop = source.IndexOf("Stop-DevwtHookedApplications -Kill:$KillHookedApplications", injectorStop, StringComparison.Ordinal);
        var disconnectOnly = source.IndexOf("if ($DisconnectOnly)", StringComparison.Ordinal);
        Assert.True(injectorStop >= 0);
        Assert.True(hookedAppStop > injectorStop);
        Assert.True(disconnectOnly > hookedAppStop);
    }

    [Fact]
    public void Uninstaller_does_not_kill_hooked_applications_by_default()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Uninstall-DevWT.ps1"));

        Assert.Contains("KillHookedApplications was not specified", source, StringComparison.Ordinal);
        Assert.Contains("Stop-DevwtHookedApplications -Kill:$KillHookedApplications", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_detects_hooked_applications_from_any_installed_devwt_hook_path()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Uninstall-DevWT.ps1"));

        Assert.Contains("Test-DevwtPathUnderRoot $module.FileName $InstallRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_stops_service_even_when_installed_cli_is_missing()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "Uninstall-DevWT.ps1"));
        var installedCliAssignment = source.IndexOf("$installedCli =", StringComparison.Ordinal);
        var serviceStop = source.IndexOf("Stop-DevwtServiceForUninstall", installedCliAssignment, StringComparison.Ordinal);
        var installedCliCheck = source.IndexOf("if (Test-Path $installedCli)", StringComparison.Ordinal);

        Assert.True(installedCliAssignment >= 0);
        Assert.True(serviceStop > installedCliAssignment);
        Assert.True(installedCliCheck > serviceStop);
    }

    [Fact]
    public void Manager_assigns_localhost_and_port_shift_base_without_requiring_aliases()
    {
        using var temp = new TempDirectory();
        var repoRoot = Path.Combine(temp.Path, "sample");
        Directory.CreateDirectory(repoRoot);
        var git = new FakeGitInspector(new GitRepositoryInfo(
            repoRoot,
            Path.Combine(repoRoot, ".git"),
            [new GitWorktreeInfo(repoRoot, "main")]));
        var manager = new DevwtManager(new DevwtStateStore(temp.Path), git, new RecordingHookRuntimeConfigurator());

        var result = manager.AddRepository(new AddRepositoryRequest(repoRoot, "sample", []));

        var context = Assert.Single(result.Contexts);
        Assert.Equal("127.0.0.1", context.AssignedIp);
        Assert.InRange(context.AssignedPortBase, 1, 65535);
        Assert.StartsWith("DevWT-", context.RuntimeName, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_settings_start_without_ide_watchers()
    {
        using var temp = new TempDirectory();
        var store = new DevwtStateStore(temp.Path);

        var settings = store.LoadRuntimeSettings();

        Assert.Empty(settings.IdeWatches);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "Devwt.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find Devwt.slnx.");
    }

    private sealed class FakeGitInspector(GitRepositoryInfo info) : IGitInspector
    {
        public GitRepositoryInfo InspectRepository(string workingDirectory) => info;

        public string EnsureHooksDirectory(string workingDirectory, GitRepositoryInfo repository)
        {
            var hooksPath = Path.Combine(repository.GitCommonDir, "hooks");
            Directory.CreateDirectory(hooksPath);
            return hooksPath;
        }
    }

    private sealed class RecordingHookRuntimeConfigurator : IHookRuntimeConfigurator
    {
        public List<DevwtContext> Configured { get; } = [];

        public void Configure(DevwtRepository repository, DevwtContext context)
        {
            Configured.Add(context);
        }

        public void Remove(DevwtContext context)
        {
        }
    }
}
