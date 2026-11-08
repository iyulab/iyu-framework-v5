using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.Ml.EntityFrameworkCore;

/// <summary>
/// What is wrong with a declared dataset, said once and asked twice: when the host starts, so a mistake stops it, and again at
/// every read, so a source that was built without the host ever starting — a service provider assembled by hand, a test — cannot
/// serve a column the guard refuses. Neither question is cached: a guard's answer is read when it is needed.
/// </summary>
internal static class EfMlDeclarations
{
    /// <summary>
    /// What is missing from the host's turning the layer on: <c>AddIyuMl</c> not called at all, or no word about read permissions.
    /// The neutral layer checks these when the host starts; asked here as well, because a source that works without being told
    /// how read permissions apply serves everything to anyone signed in, which is the one thing that must never be a default.
    /// </summary>
    public static List<string> LayerProblems(IServiceProvider scoped)
    {
        var problems = new List<string>();
        if (scoped.GetService<IyuMlRegistration>() is null)
        {
            problems.Add("the ML layer is not turned on: call AddIyuMl, which is where the host says how read permissions apply");
            return problems;
        }

        var guards = scoped.GetServices<IMlReadGuard>().Count();
        var noPolicies = scoped.GetRequiredService<IOptions<IyuMlOptions>>().Value.ReadPoliciesNotUsed;
        if (guards == 0 && !noPolicies)
            problems.Add("the host has neither registered an IMlReadGuard nor said NoReadPolicies()");
        if (guards > 1)
            problems.Add("more than one IMlReadGuard is registered");
        return problems;
    }

    /// <summary>The columns and filter properties the guard refuses. Needs no database and no model.</summary>
    public static List<string> Refusals(IEfDatasetPlan plan, IMlReadGuard? guard)
    {
        var problems = new List<string>();
        if (guard is null) return problems;

        foreach (var read in plan.Reads)
            if (guard.RefusalFor(plan.EntityType, read.Property) is { } refusal)
                problems.Add($"dataset '{plan.Definition.Name}': {plan.EntityType.Name}.{read.Property} cannot be read as a {read.Use} — {refusal}");
        return problems;
    }

    /// <summary>The entity or properties that are not part of the context's model.</summary>
    public static List<string> ModelProblems(IEfDatasetPlan plan, IModel model, Type contextType)
    {
        var problems = new List<string>();
        var entity = model.FindEntityType(plan.EntityType);
        if (entity is null)
        {
            problems.Add($"dataset '{plan.Definition.Name}': {plan.EntityType.Name} is not part of the {contextType.Name} model");
            return problems;
        }

        foreach (var read in plan.Reads.Where(r => entity.FindProperty(r.Property) is null))
            problems.Add($"dataset '{plan.Definition.Name}': {plan.EntityType.Name}.{read.Property} is not a mapped column ({read.Use})");
        return problems;
    }
}
