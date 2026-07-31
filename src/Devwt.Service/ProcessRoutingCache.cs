using Devwt.Core;
using System.Globalization;

namespace Devwt.Service;

public sealed record CachedProcessIdentity(
    string? ProcessImagePath,
    string? ApplicationKey,
    string? SessionId);

public sealed class ProcessRoutingCache
{
    private sealed record Entry<T>(T Value, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt);

    private readonly object _gate = new();
    private readonly Dictionary<int, Entry<CachedProcessIdentity>> _identities = [];
    private readonly Dictionary<int, Entry<string>> _lastContexts = [];
    private readonly int _capacity;
    private readonly TimeSpan _identityLifetime;
    private readonly TimeSpan _lastContextLifetime;

    public ProcessRoutingCache(
        int capacity = 512,
        TimeSpan? identityLifetime = null,
        TimeSpan? lastContextLifetime = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be positive.");
        }

        _capacity = capacity;
        _identityLifetime = identityLifetime ?? TimeSpan.FromSeconds(5);
        _lastContextLifetime = lastContextLifetime ?? TimeSpan.FromMinutes(5);
    }

    public CachedProcessIdentity? TryGetIdentity(int processId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            if (!_identities.TryGetValue(processId, out var entry))
            {
                return null;
            }

            _identities[processId] = entry with { LastSeenAt = now };
            return entry.Value;
        }
    }

    public void SetIdentity(int processId, CachedProcessIdentity value, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            _identities[processId] = new Entry<CachedProcessIdentity>(value, now, now + _identityLifetime);
            Trim(_identities);
        }
    }

    public string? TryGetLastContext(int processId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            if (!_lastContexts.TryGetValue(processId, out var entry))
            {
                return null;
            }

            _lastContexts[processId] = entry with { LastSeenAt = now };
            return entry.Value;
        }
    }

    public void SetLastContext(int processId, string contextId, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            _lastContexts[processId] = new Entry<string>(contextId, now, now + _lastContextLifetime);
            Trim(_lastContexts);
        }
    }

    public void Prune(IReadOnlySet<int> activeProcessIds, DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneExpired(now);
            RemoveWhere(_identities, (_, processId) => !activeProcessIds.Contains(processId));
            RemoveWhere(_lastContexts, (_, processId) => !activeProcessIds.Contains(processId));
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        RemoveWhere(_identities, (entry, _) => entry.ExpiresAt <= now);
        RemoveWhere(_lastContexts, (entry, _) => entry.ExpiresAt <= now);
    }

    private void Trim<T>(Dictionary<int, Entry<T>> entries)
    {
        foreach (var processId in entries
                     .OrderBy(item => item.Value.LastSeenAt)
                     .Take(Math.Max(0, entries.Count - _capacity))
                     .Select(item => item.Key)
                     .ToArray())
        {
            entries.Remove(processId);
        }
    }

    private static void RemoveWhere<T>(
        Dictionary<int, Entry<T>> entries,
        Func<Entry<T>, int, bool> predicate)
    {
        foreach (var processId in entries
                     .Where(item => predicate(item.Value, item.Key))
                     .Select(item => item.Key)
                     .ToArray())
        {
            entries.Remove(processId);
        }
    }
}

public sealed class ProcessSnapshotCache
{
    private sealed record Snapshot(
        IReadOnlyList<ProcessObservation> Processes,
        IReadOnlySet<int> ProcessIds,
        IReadOnlyDictionary<int, DateTimeOffset> ProcessStartTimes,
        string SessionRulesFingerprint);

    private readonly object _gate = new();
    private Snapshot? _snapshot;

    public IReadOnlyList<ProcessObservation> GetOrRefresh(
        IReadOnlySet<int> requiredProcessIds,
        string sessionRulesFingerprint,
        Func<IReadOnlyList<ProcessObservation>> loader,
        IReadOnlyDictionary<int, DateTimeOffset>? requiredProcessStartTimes = null)
    {
        ArgumentNullException.ThrowIfNull(requiredProcessIds);
        ArgumentNullException.ThrowIfNull(sessionRulesFingerprint);
        ArgumentNullException.ThrowIfNull(loader);

        var snapshot = Volatile.Read(ref _snapshot);
        if (CanReuse(snapshot, requiredProcessIds, requiredProcessStartTimes, sessionRulesFingerprint))
        {
            return snapshot!.Processes;
        }

        lock (_gate)
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (CanReuse(snapshot, requiredProcessIds, requiredProcessStartTimes, sessionRulesFingerprint))
            {
                return snapshot!.Processes;
            }

            var processes = loader().ToArray();
            var replacement = new Snapshot(
                processes,
                processes.Select(process => process.ProcessId).ToHashSet(),
                processes
                    .Select(process => (
                        process.ProcessId,
                        StartTime: ParseStartTime(process.StartTime)))
                    .Where(item => item.StartTime is not null)
                    .GroupBy(item => item.ProcessId)
                    .ToDictionary(group => group.Key, group => group.Last().StartTime!.Value),
                sessionRulesFingerprint);
            Volatile.Write(ref _snapshot, replacement);
            return replacement.Processes;
        }
    }

    private static bool CanReuse(
        Snapshot? snapshot,
        IReadOnlySet<int> requiredProcessIds,
        IReadOnlyDictionary<int, DateTimeOffset>? requiredProcessStartTimes,
        string sessionRulesFingerprint) =>
        snapshot is not null
        && snapshot.SessionRulesFingerprint.Equals(sessionRulesFingerprint, StringComparison.Ordinal)
        && requiredProcessIds.All(snapshot.ProcessIds.Contains)
        && (requiredProcessStartTimes is null
            || requiredProcessStartTimes.All(item =>
                !snapshot.ProcessStartTimes.TryGetValue(item.Key, out var cachedStartTime)
                || cachedStartTime.ToUnixTimeMilliseconds() == item.Value.ToUnixTimeMilliseconds()));

    private static DateTimeOffset? ParseStartTime(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
