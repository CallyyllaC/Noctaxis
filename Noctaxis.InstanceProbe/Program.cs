using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

// A separate process that claims Noctaxis ownership as the application does and reports each step on stdout, one
// word per line: ACQUIRED, HELD-BY-ANOTHER, DATA-FOLDER-IN-USE, PREVIOUS-STILL-RUNNING, ACTIVATION-SENT,
// ACTIVATION-UNAVAILABLE, ACTIVATED, SAVED, SAVE-REFUSED, RELEASED.
//   --data <dir> --scope <s> --lockdir <dir>     required: isolated data folder, user scope and user lock folder
//   --restart-after <pid> [--wait-ms <n>]         wait for a previous instance, as a restarted Noctaxis does
//   --add-location <name>                         after acquiring, load state, add a location and save
//   --hold                                        keep ownership until a line "exit" (or end of input) arrives
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var data = Option("--data") ?? throw new ArgumentException("--data is required");
var scope = Option("--scope") ?? throw new ArgumentException("--scope is required");
var lockDirectory = Option("--lockdir") ?? throw new ArgumentException("--lockdir is required");
int? previous = int.TryParse(Option("--restart-after"), out var pid) ? pid : null;
var wait = int.TryParse(Option("--wait-ms"), out var waitMs) ? TimeSpan.FromMilliseconds(waitMs) : (TimeSpan?)null;

var (outcome, ownership) = ApplicationInstanceOwnership.Acquire(
    new InstanceOwnershipRequest(data, scope, lockDirectory, previous, wait));
switch (outcome)
{
    case InstanceOwnershipOutcome.HeldByAnotherInstance:
        Console.WriteLine("HELD-BY-ANOTHER");
        Console.WriteLine(ApplicationInstanceOwnership.RequestActivation(scope) ? "ACTIVATION-SENT" : "ACTIVATION-UNAVAILABLE");
        return 2;
    case InstanceOwnershipOutcome.DataFolderInUse:
        Console.WriteLine("DATA-FOLDER-IN-USE");
        return 3;
    case InstanceOwnershipOutcome.PreviousInstanceStillRunning:
        Console.WriteLine("PREVIOUS-STILL-RUNNING");
        return 4;
}

using (ownership)
{
    ownership!.ActivationRequested += () => Console.WriteLine("ACTIVATED");
    Console.WriteLine("ACQUIRED");
    if (Option("--add-location") is { } name)
    {
        var store = new JsonUserDataStore(new FixedPaths(data), NullLogger<JsonUserDataStore>.Instance,
            () => SystemClock.Instance.GetCurrentInstant(), ownership: new InstanceDataDirectoryOwnership(ownership));
        var state = await store.LoadAsync(CancellationToken.None);
        var added = state with
        {
            Locations = [.. state.Locations, new SavedLocation(Guid.NewGuid(), name, new GeoCoordinate(51, -1), "UTC")]
        };
        try
        {
            await store.SaveAsync(added, CancellationToken.None);
            Console.WriteLine("SAVED");
        }
        catch (UserStateReadOnlyException)
        {
            Console.WriteLine("SAVE-REFUSED");
        }
    }
    if (args.Contains("--hold"))
    {
        while (Console.ReadLine() is { } line && line != "exit") { }
    }
}
Console.WriteLine("RELEASED");
return 0;

internal sealed record FixedPaths(string Root) : IUserDataPathProvider
{
    public string GetApplicationDataDirectory() => Root;
}
