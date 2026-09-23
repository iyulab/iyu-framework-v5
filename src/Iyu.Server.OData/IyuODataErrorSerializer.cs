using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OData;

namespace Iyu.Server.OData;

/// <summary>
/// Writes OData error payloads without the exception diagnostics — <c>innererror</c>, carrying
/// the exception type and stack trace — unless detailed errors are enabled.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnableQueryAttribute</c> turns every query-option failure (an unknown property in
/// <c>$select</c>/<c>$filter</c>, a malformed literal, a limit exceeded) into a 400 whose body is
/// built from the exception itself, stack trace included, with no environment check. The top-level
/// <c>message</c> is what a caller needs to fix the query; the <c>innererror</c> beneath it only
/// tells them which framework types and CLR entity types sit behind the service. This serializer
/// keeps the former and drops the latter.
/// </para>
/// <para>
/// Replacing the error serializer is the extension point Microsoft.AspNetCore.OData documents for
/// this (its <c>docs/customize_odataerror.md</c>) — the attribute's error construction is private,
/// and the serializer is the one place every error on the route passes through, whichever action
/// or filter produced it.
/// </para>
/// </remarks>
public sealed class IyuODataErrorSerializer : ODataErrorSerializer
{
    private readonly bool? _includeDetails;

    /// <param name="includeDetails">
    /// <see langword="true"/> to always write <c>innererror</c>, <see langword="false"/> to never
    /// write it, <see langword="null"/> to write it only when the host environment is Development.
    /// </param>
    public IyuODataErrorSerializer(bool? includeDetails) => _includeDetails = includeDetails;

    /// <inheritdoc />
    public override Task WriteObjectAsync(
        object graph, Type type, ODataMessageWriter messageWriter, ODataSerializerContext writeContext)
    {
        if (IncludeDetails(writeContext?.Request)) return base.WriteObjectAsync(graph, type, messageWriter, writeContext);

        var error = graph switch
        {
            ODataError odataError => odataError,
            SerializableError serializable => serializable.CreateODataError(),
            _ => null,
        };
        if (error is null) return base.WriteObjectAsync(graph, type, messageWriter, writeContext);

        error.InnerError = null;
        return base.WriteObjectAsync(error, typeof(ODataError), messageWriter, writeContext);
    }

    private bool IncludeDetails(HttpRequest? request)
    {
        if (_includeDetails is { } explicitChoice) return explicitChoice;
        var environment = request?.HttpContext.RequestServices.GetService<IHostEnvironment>();
        return environment?.IsDevelopment() == true;
    }
}

/// <summary>
/// The Iyu runtime's <see cref="IODataSerializerProvider"/>: the stock provider, with errors written
/// by <see cref="IyuODataErrorSerializer"/>.
/// </summary>
public sealed class IyuODataSerializerProvider : ODataSerializerProvider
{
    private readonly IyuODataErrorSerializer _errorSerializer;

    /// <param name="services">The route's service provider, as the stock provider takes it.</param>
    /// <param name="includeErrorDetails">See <see cref="IyuODataErrorSerializer(bool?)"/>.</param>
    public IyuODataSerializerProvider(IServiceProvider services, bool? includeErrorDetails)
        : base(services)
        => _errorSerializer = new IyuODataErrorSerializer(includeErrorDetails);

    /// <inheritdoc />
    public override IODataSerializer GetODataPayloadSerializer(Type type, HttpRequest request)
        => type == typeof(ODataError) || type == typeof(SerializableError)
            ? _errorSerializer
            : base.GetODataPayloadSerializer(type, request);
}
