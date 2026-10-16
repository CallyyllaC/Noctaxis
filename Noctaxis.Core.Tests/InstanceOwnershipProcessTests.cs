using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// NX-31 (Opus R-02 / Astra R03): one Noctaxis per operating-system user, and one owner per data folder, checked with
/// real separate processes (Noctaxis.InstanceProbe), each taking ownership exactly as the application's Program does.
/// Every test uses its own temporary data folder and its own user scope, so it never meets a running Noctaxis or
/// touches the user's state.
/// </summary>
public sealed class InstanceOwnershipProcessTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctaxis-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly string _scope = "test:" + Guid.NewGuid().ToString("N");
    private readonly List<Probe> _probes = [];

    private string Data => Path.Combine(_root, "data");
    private string Locks => Path.Combine(_root, "locks");

    private Probe Start(string? data = null, string? scope = null, params string[] extra)
    {
        var probe = new Probe(data ?? Data, scope ?? _scope, Locks, extra);
        _probes.Add(probe);
        return probe;
    }

    [Fact]
    public async Task FirstLaunchOwnsAndSavesItsState()
    {
        var first = Start(extra: ["--add-location", "First", "--hold"]);
        await first.ExpectAsync("ACQUIRED");
        await first.ExpectAsync("SAVED");
        Assert.True(File.Exists(Path.Combine(Data, ApplicationInstanceOwnership.DataLockFileName)));
        await first.ExitAsync();
        Assert.Equal(["First"], await LocationNamesAsync());
    }

    [Fact]
    public async Task SecondLaunchIsRefusedAsksTheFirstToShowItselfAndWritesNothing()
    {
        var first = Start(extra: ["--hold"]);
        await first.ExpectAsync("ACQUIRED");

        var second = Start(extra: ["--add-location", "Second"]);
        await second.ExpectAsync("HELD-BY-ANOTHER");
        Assert.Equal(2, await second.ExitCodeAsync());
        if (OperatingSystem.IsWindows())
        {
            await second.ExpectAsync("ACTIVATION-SENT");
            await first.ExpectAsync("ACTIVATED");
        }
        Assert.DoesNotContain("SAVED", second.Lines);
        Assert.False(File.Exists(Path.Combine(Data, "state.json")));
        await first.ExitAsync();
    }

    [Fact]
    public async Task TheDataFolderOfARunningInstanceCannotBeOpenedByAnotherWriter()
    {
        var first = Start(extra: ["--hold"]);
        await first.ExpectAsync("ACQUIRED");

        // Another user (another scope) pointed at the same folder: the folder lock refuses it.
        var other = Start(scope: "test:" + Guid.NewGuid().ToString("N"), extra: ["--add-location", "Intruder"]);
        await other.ExpectAsync("DATA-FOLDER-IN-USE");
        Assert.Equal(3, await other.ExitCodeAsync());

        // A store in this test process that does not own the folder refuses to save.
        var store = new JsonUserDataStore(new Paths(Data), NullLogger<JsonUserDataStore>.Instance, () => Instant.FromUtc(2026, 10, 16, 12, 0),
            ownership: new NotOwner());
        var state = await store.LoadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Data, "state.json")));
        await first.ExitAsync();
    }

    [Fact]
    public async Task OwnershipPassesOnAfterANormalExit()
    {
        var first = Start(extra: ["--add-location", "First", "--hold"]);
        await first.ExpectAsync("SAVED");
        await first.ExitAsync();
        Assert.Equal(0, await first.ExitCodeAsync());

        var second = Start(extra: ["--add-location", "Second"]);
        await second.ExpectAsync("SAVED");
        Assert.Equal(0, await second.ExitCodeAsync());
        Assert.Equal(["First", "Second"], await LocationNamesAsync());
    }

    [Fact]
    public async Task OwnershipPassesOnAfterTheOwnerIsKilled()
    {
        var first = Start(extra: ["--hold"]);
        await first.ExpectAsync("ACQUIRED");
        first.Kill();

        var second = Start(extra: ["--add-location", "AfterCrash"]);
        await second.ExpectAsync("ACQUIRED");
        await second.ExpectAsync("SAVED");
        Assert.Equal(["AfterCrash"], await LocationNamesAsync());
    }

    [Fact]
    public async Task ARestartWaitsForThePreviousInstanceAndThenOwns()
    {
        var first = Start(extra: ["--add-location", "BeforeRestart", "--hold"]);
        await first.ExpectAsync("SAVED");

        var replacement = Start(extra: ["--restart-after", first.Id.ToString(), "--wait-ms", "20000", "--add-location", "AfterRestart"]);
        await Task.Delay(500);
        Assert.DoesNotContain("ACQUIRED", replacement.Lines); // Still waiting: the previous instance owns everything.
        await first.ExitAsync();

        await replacement.ExpectAsync("ACQUIRED");
        await replacement.ExpectAsync("SAVED");
        Assert.Equal(["BeforeRestart", "AfterRestart"], await LocationNamesAsync());
    }

    [Fact]
    public async Task ARestartWhosePreviousInstanceNeverExitsGivesUpWithoutWriting()
    {
        var first = Start(extra: ["--hold"]);
        await first.ExpectAsync("ACQUIRED");

        var timer = Stopwatch.StartNew();
        var replacement = Start(extra: ["--restart-after", first.Id.ToString(), "--wait-ms", "1500", "--add-location", "Replacement"]);
        await replacement.ExpectAsync("PREVIOUS-STILL-RUNNING");
        Assert.Equal(4, await replacement.ExitCodeAsync());
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(1.2), Step);
        Assert.False(File.Exists(Path.Combine(Data, "state.json")));
        await first.ExitAsync();
    }

    [Fact]
    public async Task SimultaneousLaunchesProduceExactlyOneOwner()
    {
        var launches = Enumerable.Range(0, 6).Select(_ => Start(extra: ["--add-location", "Racer", "--hold"])).ToArray();
        await Task.WhenAll(launches.Select(probe => probe.ExpectAnyAsync("ACQUIRED", "HELD-BY-ANOTHER")));
        var owners = launches.Where(probe => probe.Lines.Contains("ACQUIRED")).ToArray();
        Assert.Single(owners);
        await owners[0].ExpectAsync("SAVED");
        foreach (var probe in launches.Except(owners)) Assert.Equal(2, await probe.ExitCodeAsync());
        await owners[0].ExitAsync();
        Assert.Equal(["Racer"], await LocationNamesAsync());
    }

    [Fact]
    public async Task DifferentUsersWithTheirOwnDataFoldersDoNotInterfere()
    {
        var first = Start(extra: ["--hold"]);
        await first.ExpectAsync("ACQUIRED");
        var otherData = Path.Combine(_root, "other-user-data");
        var otherUser = Start(data: otherData, scope: "test:" + Guid.NewGuid().ToString("N"), extra: ["--hold"]);
        await otherUser.ExpectAsync("ACQUIRED");
        await otherUser.ExitAsync();
        await first.ExitAsync();
    }

    [Fact]
    public async Task ThePreviousLostUpdateCannotHappenAtTheOwnershipBoundary()
    {
        // Before NX-31, two instances that had both loaded the file each added a location and saved; one addition
        // was silently lost. Now the second instance never reaches its state.
        var first = Start(extra: ["--add-location", "From the first", "--hold"]);
        await first.ExpectAsync("SAVED");
        var second = Start(extra: ["--add-location", "From the second"]);
        await second.ExpectAsync("HELD-BY-ANOTHER");
        await first.ExitAsync();
        Assert.Equal(["From the first"], await LocationNamesAsync());
    }

    [Fact]
    public void TheProductionScopeIsTheOperatingSystemUser()
    {
        var scope = ApplicationInstanceOwnership.CurrentUserScope();
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            Assert.Equal("sid:" + identity.User!.Value, scope);
        }
        else Assert.StartsWith("user:" + System.Environment.UserName + "|", scope);
        Assert.NotEqual(ApplicationInstanceOwnership.ScopeKey(scope), ApplicationInstanceOwnership.ScopeKey(scope + "x"));
    }

    private async Task<string[]> LocationNamesAsync()
    {
        var state = await new JsonUserDataStore(new Paths(Data), NullLogger<JsonUserDataStore>.Instance,
            () => Instant.FromUtc(2026, 10, 16, 12, 0)).LoadAsync(CancellationToken.None);
        return state.Locations.Select(location => location.Name).ToArray();
    }

    public void Dispose()
    {
        foreach (var probe in _probes) probe.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Paths(string Root) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Root; }

    private sealed class NotOwner : IDataDirectoryOwnership { public bool Owns(string directory) => false; }

    /// <summary>One Noctaxis.InstanceProbe process, with its stdout lines collected as they arrive.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly Process _process;
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public Probe(string data, string scope, string locks, string[] extra)
        {
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Noctaxis.InstanceProbe.dll"));
            foreach (var argument in new[] { "--data", data, "--scope", scope, "--lockdir", locks }.Concat(extra))
                info.ArgumentList.Add(argument);
            _process = new Process { StartInfo = info };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_lines) _lines.Add(e.Data.Trim());
                _arrived.Release();
            };
            _process.Start();
            _process.BeginOutputReadLine();
        }

        public int Id => _process.Id;
        public string[] Lines { get { lock (_lines) return [.. _lines]; } }

        public Task ExpectAsync(string line) => ExpectAnyAsync(line);

        public async Task ExpectAnyAsync(params string[] any)
        {
            var deadline = DateTime.UtcNow + Step;
            while (!Lines.Any(any.Contains))
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left))
                    Assert.Fail($"Expected {string.Join(" or ", any)}; the probe wrote: {string.Join(", ", Lines)}");
            }
        }

        public async Task ExitAsync()
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("exit"); await _process.StandardInput.FlushAsync(); }
                catch (IOException) { }
            }
            await _process.WaitForExitAsync().WaitAsync(Step);
        }

        public async Task<int> ExitCodeAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(Step);
            return _process.ExitCode;
        }

        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit((int)Step.TotalMilliseconds);
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) Kill(); }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}
