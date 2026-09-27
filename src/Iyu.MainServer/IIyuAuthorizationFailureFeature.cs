namespace Iyu.MainServer;

/// <summary>
/// Which policy the framework found unsatisfied when it refused a request on its own, and for which
/// entity set — read from <c>HttpContext.Features</c> by a host that words its own 401/403 answers.
/// </summary>
/// <remarks>
/// <para>
/// Set by the <c>$expand</c> authorization check, which refuses a request because of a set the
/// request reaches rather than the one it addresses. The endpoint's own authorization metadata
/// names the addressed set's policy — one the caller may already satisfy — so a host that reads the
/// policy from there tells the caller to acquire a permission it has. The failing policy is known
/// only where it was evaluated, and this is where that knowledge is handed on.
/// </para>
/// <para>
/// The refusal itself is unchanged: a <c>ForbidResult</c> for an authenticated caller and a
/// <c>ChallengeResult</c> otherwise, the same split the endpoint's own authorization produces, so
/// both paths go through the authentication scheme's handling. Absent when the framework did not
/// refuse the request this way.
/// </para>
/// </remarks>
public interface IIyuAuthorizationFailureFeature
{
    /// <summary>The name of the policy that was not satisfied.</summary>
    string Policy { get; }

    /// <summary>The entity set whose policy it is.</summary>
    string EntitySet { get; }
}

internal sealed record IyuAuthorizationFailureFeature(string Policy, string EntitySet) : IIyuAuthorizationFailureFeature;
