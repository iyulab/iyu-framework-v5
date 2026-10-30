namespace Iyu.Data.WriteRules;

/// <summary>
/// A write rule's refusal, written for the caller: <see cref="Code"/> tells one rule from another without parsing
/// text, and the message says why in words a person can act on. Thrown from <see cref="EntityWriteRule{T}"/> or
/// <see cref="EntityDeleteRule{TEntity}"/>, the main server answers it <c>422</c> with that code and message — an application that throws its own exception type
/// instead keeps mapping it itself.
/// </summary>
public sealed class WriteRuleViolationException : Exception
{
    /// <param name="code">A stable identifier for the rule, such as <c>VatTypeLocked</c>.</param>
    /// <param name="message">Why the write was refused — sent to the caller as it is.</param>
    public WriteRuleViolationException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>The rule's stable identifier — the response's <c>error.code</c>.</summary>
    public string Code { get; }
}
