using System.Reflection;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace Iyu.MainServer;

/// <summary>
/// Attaches an <see cref="AuthorizeFilter"/> to a generated OData controller's actions when its
/// entity set was registered with <c>IyuEdmModelBuilder.RestrictPolicy</c> (Iyu.Server.OData) —
/// GET gets <c>ReadPolicy</c>, POST/PATCH/DELETE get <c>WritePolicy</c>. The OData counterpart of
/// <c>IyuGraphQLPolicyAuthorizationHandler</c> (Iyu.Server.GraphQL); wired automatically by
/// <see cref="MainServerExtensions.AddIyuMainServer{TContext}"/> whenever any registered set uses
/// <c>RestrictPolicy</c>, so there is no separate step for a consumer to remember or forget.
/// </summary>
internal sealed class IyuODataAuthorizationConvention(IyuEntityPairRegistry registry) : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        var readType = FindReadType(controller.ControllerType);
        if (readType is null) return;

        var pair = registry.FindByReadType(readType);
        if (pair is null) return;

        foreach (var action in controller.Actions)
        {
            // Matches IyuODataController<TRead,TWrite>'s own action method names exactly — no
            // [HttpGet]/[HttpPost] attributes to fall back on, same convention OData's own routing
            // already relies on (IyuODataController.cs).
            var policy = action.ActionMethod.Name switch
            {
                "Get" => pair.ReadPolicy,
                "Post" or "Patch" or "Delete" => pair.WritePolicy,
                _ => null,
            };
            if (policy is not null)
                action.Filters.Add(new AuthorizeFilter(policy));
        }
    }

    /// <summary>
    /// Walks <paramref name="controllerType"/>'s base-type chain to the closed
    /// <c>IyuODataController&lt;TRead,TWrite&gt;</c> and returns its <c>TRead</c> argument, or
    /// <see langword="null"/> for a controller that is not one (e.g. OData's own
    /// <c>MetadataController</c>).
    /// </summary>
    private static Type? FindReadType(TypeInfo controllerType)
    {
        for (var t = controllerType.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IyuODataController<,>))
                return t.GetGenericArguments()[0];
        }
        return null;
    }
}
