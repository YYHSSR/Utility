using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Utility.Core.Models;

namespace Utility.Platform.Lenovo;

/// <summary>Uses an administrator-enrolled Task Scheduler action for the owner's firmware controls.</summary>
public static class HardwareAuthorizationTask
{
    private const string LauncherName = "Utility.HardwareLauncher.exe";
    private const string TaskNamePrefix = "Utility.Hardware.";
    private const string Arguments = "--pipe $(Arg0) --token $(Arg1) --server-pid $(Arg2)";
    private const int InteractiveToken = 3;
    private const int HighestAvailable = 1;
    private const int ExecuteAction = 0;
    private const int CreateOrUpdateWithoutPrincipalAce = 0x16;
    private const int FileNotFound = unchecked((int)0x80070002);

    public static bool Allows(ElevatedOperation operation) => operation is
        ElevatedOperation.ReadThermalSnapshot or ElevatedOperation.ApplyOwnerPowerPreset or
        ElevatedOperation.ReadFanTables or ElevatedOperation.SetFanSpeedLevels or
        ElevatedOperation.ReadGraphicsMode or ElevatedOperation.SetGraphicsMode;

    public static void Register()
    {
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var workerPath = Path.Combine(directory, "Utility.ElevatedWorker.exe");
        var launcher = Path.Combine(directory, LauncherName);
        if (!LenovoWmiProvider.IsProcessElevated() || !ElevatedWorkerInstallation.IsProtected(directory) ||
            !File.Exists(launcher) || !ElevatedWorkerInstallation.HasConsistentSignature(directory, launcher) ||
            !ElevatedWorkerInstallation.HasConsistentSignature(directory, workerPath))
            throw new UnauthorizedAccessException("Hardware authorization requires the protected Utility installation and an administrator.");

        var sid = GetCurrentUserSid();
        dynamic scheduler = Connect();
        dynamic root = scheduler.GetFolder("\\");
        dynamic definition = scheduler.NewTask(0);
        definition.RegistrationInfo.Description = "On-demand Utility power, fan and graphics controls for this Windows account.";
        definition.Principal.UserId = sid;
        definition.Principal.LogonType = InteractiveToken;
        definition.Principal.RunLevel = HighestAvailable;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT2M";
        definition.Settings.MultipleInstances = 0; // TASK_INSTANCES_PARALLEL
        dynamic action = definition.Actions.Create(ExecuteAction);
        action.Path = launcher;
        action.WorkingDirectory = directory;
        action.Arguments = Arguments;

        // The current user may read and run the task. Only administrators and SYSTEM may change it.
        var security = $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})";
        root.RegisterTaskDefinition(TaskName(sid), definition, CreateOrUpdateWithoutPrincipalAce,
            sid, null, InteractiveToken, security);
    }

    public static void RemoveForCurrentInstallation()
    {
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!LenovoWmiProvider.IsProcessElevated() || !ElevatedWorkerInstallation.IsProtected(directory))
            throw new UnauthorizedAccessException("Removing hardware authorization requires the protected Utility installation and an administrator.");

        dynamic scheduler = Connect();
        dynamic root = scheduler.GetFolder("\\");
        dynamic tasks = root.GetTasks(1); // TASK_ENUM_HIDDEN
        var names = new List<string>();
        int count = tasks.Count;
        for (var index = 1; index <= count; index++)
        {
            dynamic task = tasks.Item(index);
            string name = task.Name;
            if (!name.StartsWith(TaskNamePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            dynamic definition = task.Definition;
            if ((int)definition.Actions.Count == 1 &&
                MatchesLauncher(definition.Actions.Item(1), directory) &&
                string.Equals(name, TaskName(ResolveSid((string)definition.Principal.UserId)), StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }

        foreach (var name in names)
            root.DeleteTask(name, 0);
    }

    internal static bool TryStart(string workerPath, string pipeName, string token)
    {
        var sid = GetCurrentUserSid();
        dynamic scheduler = Connect();
        dynamic root = scheduler.GetFolder("\\");
        dynamic task;
        try
        {
            task = root.GetTask(TaskName(sid));
        }
        catch (COMException ex) when (ex.HResult == FileNotFound)
        {
            return false;
        }

        dynamic definition = task.Definition;
        if ((int)definition.Actions.Count != 1 || (int)definition.Principal.RunLevel != HighestAvailable ||
            (int)definition.Principal.LogonType != InteractiveToken || ResolveSid((string)definition.Principal.UserId) != sid)
            throw new InvalidOperationException("The Utility hardware authorization task has an unexpected principal or action.");

        var directory = Path.GetDirectoryName(workerPath)!;
        var launcher = Path.Combine(directory, LauncherName);
        dynamic action = definition.Actions.Item(1);
        if (!MatchesLauncher(action, directory) || !File.Exists(launcher) ||
            !ElevatedWorkerInstallation.HasConsistentSignature(directory, launcher))
            throw new InvalidOperationException("The Utility hardware authorization task points to a different command.");

        task.Run(new[] { pipeName, token, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) });
        return true;
    }

    private static object Connect()
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic scheduler = Activator.CreateInstance(schedulerType)!;
        scheduler.Connect();
        return scheduler;
    }

    private static bool MatchesLauncher(dynamic action, string directory) =>
        (int)action.Type == ExecuteAction &&
        string.Equals((string)action.Path, Path.Combine(directory, LauncherName), StringComparison.OrdinalIgnoreCase) &&
        string.Equals((string)action.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase) &&
        (string)action.Arguments == Arguments;

    private static string TaskName(string sid) => $"{TaskNamePrefix}{sid}";

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The Windows account could not be identified.");
    }

    private static string ResolveSid(string principal) => principal.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        ? new SecurityIdentifier(principal).Value
        : ((SecurityIdentifier)new NTAccount(principal).Translate(typeof(SecurityIdentifier))).Value;
}
