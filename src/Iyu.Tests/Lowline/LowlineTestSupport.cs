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

    public Task<LowlineFormContext?> OpenAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.FromResult<LowlineFormContext?>(new LowlineFormContext("plant-a"));

    public Task<LowlineSavedRecord?> ReadSavedAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(
            _rows.TryGetValue(recordId, out var row) && row.Owner == user.Identity?.Name ? row.Row : null);
    }
}
