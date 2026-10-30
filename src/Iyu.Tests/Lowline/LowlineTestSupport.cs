using System.Security.Claims;
using Iyu.Lowline;
using Lowline;

namespace Iyu.Tests.Lowline;

/// <summary>An application's one form hook: rows by id, each owned by a person and kept in a space.</summary>
public sealed class OrderHook : ILowlineFormHook
{
    private readonly Dictionary<string, (string Owner, LowlineSavedRecord Row)> _rows = [];

    public int Reads { get; private set; }

    public string Form => "order";

    public void Save(string id, string owner, string space, string customer, string colour) =>
        _rows[id] = (owner, new LowlineSavedRecord(space, new Dictionary<string, FieldValue>
        {
            ["customer"] = customer,
            ["colour"] = colour,
        }));

    /// <summary>Whether this hook lists its sources (a form that does not leaves its records to be forgotten some other way).</summary>
    public bool CanList { get; set; }

    /// <summary>Runs inside the source listing, after it was taken — a save that lands while the recovery pass is working.</summary>
    public Func<Task>? DuringList { get; set; }

    /// <summary>Spaces whose source listing throws, as a database that is down for one tenant would.</summary>
    public HashSet<string> ListingFailsFor { get; } = [];

    public void Delete(string id) => _rows.Remove(id);

    public async Task<IReadOnlySet<string>?> ListSourceKeysAsync(string space, CancellationToken cancellationToken)
    {
        if (!CanList) return null;
        if (ListingFailsFor.Contains(space)) throw new InvalidOperationException("the source for this space is unreachable");
        IReadOnlySet<string> keys = _rows.Where(r => r.Value.Row.Space == space).Select(r => r.Key).ToHashSet();
        if (DuringList is not null) await DuringList();
        return keys;
    }

    public Task<LowlineFormContext?> OpenAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.FromResult<LowlineFormContext?>(new LowlineFormContext("plant-a"));

    public Task<LowlineSavedRecord?> ReadSavedAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(
            _rows.TryGetValue(recordId, out var row) && row.Owner == user.Identity?.Name ? row.Row : null);
    }
}
