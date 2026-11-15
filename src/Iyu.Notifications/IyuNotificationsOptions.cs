using Microsoft.Extensions.Options;

namespace Iyu.Notifications;

/// <summary>What <c>AddIyuNotifications</c> is configured with: the events the application declares, and how long an inbox keeps what is finished with.</summary>
public sealed class IyuNotificationsOptions
{
    /// <summary>The longest key or name an event may have; it is what a stored column is sized for.</summary>
    public const int MaxTextLength = 200;

    private readonly List<NotificationEventDefinition> _events = [];

    /// <summary>The events declared so far.</summary>
    public IReadOnlyList<NotificationEventDefinition> Events => _events;

    /// <summary>
    /// How long an inbox keeps a notice, and a handled action request, before the clean-up removes it; null (the default) keeps
    /// everything. How long is a business or legal question, so the application says — the package never picks a number. An
    /// action request that is still open is never removed, whatever this is.
    /// </summary>
    public TimeSpan? InboxRetention { get; set; }

    /// <summary>How often the clean-up runs when <see cref="InboxRetention"/> is set. One hour by default.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Declares an event. Code raises it by <paramref name="key"/>; a key that was not declared here is refused.</summary>
    /// <param name="key">The identifier, for example <c>work-order.delayed</c>. Compared ignoring case.</param>
    /// <param name="kind">Whether the event informs or asks for action.</param>
    /// <param name="name">What a person sees; the key when omitted.</param>
    /// <param name="description">A sentence about when it happens, for administration screens.</param>
    /// <exception cref="ArgumentException">An event with that key (ignoring case) is already declared.</exception>
    public IyuNotificationsOptions Event(string key, NotificationKind kind, string? name = null, string? description = null)
    {
        if (!string.IsNullOrWhiteSpace(key)
            && _events.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"An event with the key '{key}' is already declared (keys are compared ignoring case).", nameof(key));

        // A blank key or name is kept as given and reported by the start-up validation with every other problem, rather than
        // thrown here one at a time.
        _events.Add(new NotificationEventDefinition(key, name ?? key, kind, description));
        return this;
    }
}

internal sealed class IyuNotificationsOptionsValidator : IValidateOptions<IyuNotificationsOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuNotificationsOptions options)
    {
        var problems = new List<string>();

        if (options.Events.Count == 0)
            problems.Add("declare at least one event with IyuNotificationsOptions.Event");

        foreach (var declared in options.Events)
        {
            if (string.IsNullOrWhiteSpace(declared.Key))
                problems.Add("an event has an empty key");
            else if (declared.Key != declared.Key.Trim())
                problems.Add($"the key '{declared.Key}' has leading or trailing spaces");
            else if (declared.Key.Length > IyuNotificationsOptions.MaxTextLength)
                problems.Add($"the key '{declared.Key}' is longer than {IyuNotificationsOptions.MaxTextLength} characters");

            if (string.IsNullOrWhiteSpace(declared.Name))
                problems.Add($"the event '{declared.Key}' has an empty name");
            else if (declared.Name.Length > IyuNotificationsOptions.MaxTextLength)
                problems.Add($"the name of the event '{declared.Key}' is longer than {IyuNotificationsOptions.MaxTextLength} characters");
        }

        if (options.InboxRetention is { } retention && retention <= TimeSpan.Zero)
            problems.Add("InboxRetention must be longer than zero, or null to keep everything");
        if (options.CleanupInterval <= TimeSpan.Zero)
            problems.Add("CleanupInterval must be longer than zero");

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("AddIyuNotifications: " + string.Join("; ", problems) + ".");
    }
}
