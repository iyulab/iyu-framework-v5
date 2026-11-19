using System.Linq.Expressions;
using System.Reflection;
using HotChocolate;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Sorting;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;

namespace Iyu.Server.GraphQL;

/// <summary>
/// The columns a query field's <c>where</c> and <c>order</c> arguments may name: the same ones its output type shows, and
/// only those whose value is a plain scalar. An input type is part of the schema like an output type is, so it has to
/// leave out what the output type leaves out — a column hidden from the selection must not be one a caller can filter
/// or sort by, which is a way to read it a bit at a time.
/// </summary>
internal static class IyuGraphQLQueryArguments
{
    /// <summary>The public properties of <paramref name="type"/> whose value a condition or an ordering can compare.</summary>
    internal static IReadOnlyList<PropertyInfo> Columns(
        Type type, Func<Type, string, bool> isWithheld)
    {
        var columns = new List<PropertyInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true }) continue;
            // What the output type leaves out by attribute stays out of the input: a column the caller cannot select must
            // not be one they can narrow a condition on.
            if (property.IsDefined(typeof(GraphQLIgnoreAttribute), inherit: true)) continue;
            if (!IsScalar(property.PropertyType)) continue;
            if (isWithheld(type, property.Name)) continue;
            columns.Add(property);
        }
        return columns;
    }

    /// <summary>
    /// The single-valued references of <paramref name="type"/> a condition may cross: a property whose type is itself a served
    /// model, shown by the output type (not removed, not ignored by attribute, a public getter). A collection is not one - it
    /// has no filter here, and the output type serves it as a page of its own.
    /// </summary>
    internal static IReadOnlyList<PropertyInfo> Navigations(
        Type type, Func<Type, bool> isServed, Func<Type, string, bool> isWithheld, bool collections = false)
    {
        var navigations = new List<PropertyInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true }) continue;
            if (property.IsDefined(typeof(GraphQLIgnoreAttribute), inherit: true)) continue;
            if (IsScalar(property.PropertyType)) continue;
            var target = collections ? ElementTypeOf(property.PropertyType) : property.PropertyType;
            if (target is null || !isServed(target)) continue;
            // Asked for single references a collection is not one, and asked for collections a single reference is not.
            if (!collections && ElementTypeOf(property.PropertyType) is not null) continue;
            if (isWithheld(type, property.Name)) continue;
            navigations.Add(property);
        }
        return navigations;
    }

    /// <summary>The element type of a collection property (an array or an <c>IEnumerable&lt;T&gt;</c> other than <c>string</c>), or <see langword="null"/>.</summary>
    internal static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
            || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan);
    }

    /// <summary>A property access as the <c>Func&lt;T, object&gt;</c> the descriptors take (value types boxed by a conversion they see through).</summary>
    private static Expression<Func<T, object>> Access<T>(PropertyInfo property)
    {
        var row = Expression.Parameter(typeof(T), "e");
        Expression body = Expression.Property(row, property);
        if (body.Type != typeof(object)) body = Expression.Convert(body, typeof(object));
        return Expression.Lambda<Func<T, object>>(body, row);
    }

    /// <summary>What a <c>where</c> or <c>order</c> value reads: the columns it names, and the models it crosses into to name them.</summary>
    internal sealed record Reads(HashSet<(Type Model, string Column)> Columns, HashSet<Type> Entered);

    /// <summary>
    /// What a request reads in <paramref name="argument"/> (<c>where</c> or <c>order</c>), taken from the argument's value before
    /// anything runs - through variables, <c>and</c> / <c>or</c>, lists and the references a condition crosses. A construct that is
    /// not recognised is refused rather than skipped: what cannot be read as naming nothing cannot be allowed to.
    /// </summary>
    /// <param name="context">The field's resolver context - its arguments and the request's variables.</param>
    /// <param name="argument">The argument to read: <c>where</c> or <c>order</c>.</param>
    /// <param name="rootModel">The model the query field serves.</param>
    /// <param name="maxDepth">The deepest nesting followed before the value is refused.</param>
    /// <param name="maxNodes">The most parts counted before the value is refused.</param>
    /// <param name="refusal">Why the value is refused, or <see langword="null"/> when it was read in full.</param>
    internal static Reads Read(
        IMiddlewareContext context, string argument, Type rootModel, int maxDepth, int maxNodes, out string? refusal)
    {
        var reads = new Reads(new HashSet<(Type, string)>(), new HashSet<Type>());
        refusal = null;
        // A field that takes the argument always has it as an input object (this runs only when the arguments are turned on);
        // one that does not is something this check was not written for, and what it cannot check it does not pass.
        if (!context.Selection.Field.Arguments.TryGetField(argument, out var definition)
            || definition.Type.NamedType() is not IInputObjectTypeDefinition rootInput)
        {
            refusal = $"This field's {argument} argument cannot be checked.";
            return reads;
        }

        var nodes = 0;
        string? why = null;

        void Visit(IValueNode? node, int depth, IInputObjectTypeDefinition inputType, Type model, bool isList = false)
        {
            if (why is not null || node is null) return;
            if (++nodes > maxNodes) { why = $"The {argument} condition is larger than {maxNodes} parts."; return; }
            if (depth > maxDepth) { why = $"The {argument} condition is nested deeper than {maxDepth} levels."; return; }

            switch (node)
            {
                case NullValueNode:
                    return;
                case VariableNode variable:
                    // The executor puts a variable's value into the argument before a field step sees it, so a variable
                    // is not expected here at all. One that does turn up is read as what it holds, and one that cannot
                    // be read is refused - it is not something to wave through.
                    if (context.Variables.TryGetValue<IValueNode>(variable.Name.Value, out var value))
                        Visit(value, depth + 1, inputType, model, isList);
                    else
                        why = $"The {argument} condition uses a variable that cannot be read.";
                    return;
                case ListValueNode list:
                    foreach (var item in list.Items) Visit(item, depth + 1, inputType, model, isList);
                    return;
                case ObjectValueNode obj when isList:
                    // The wrapper a collection's condition is written in: some / all / none each hold a condition on the
                    // element model, any is a plain yes-or-no on whether the collection has a row.
                    foreach (var field in obj.Fields)
                    {
                        if (why is not null) return;
                        var name = field.Name.Value;
                        if (name is "some" or "all" or "none"
                            && inputType.Fields.TryGetField(name, out var quantified)
                            && quantified.Type.NamedType() is IInputObjectTypeDefinition elementInput)
                            Visit(field.Value, depth + 1, elementInput, model);
                        else if (name == "any" && inputType.Fields.TryGetField(name, out _))
                            nodes += PartsOf(field.Value);
                        else
                        {
                            why = $"The {argument} condition has a part this field cannot check ('{name}').";
                            return;
                        }
                    }
                    return;
                case ObjectValueNode obj:
                    foreach (var field in obj.Fields)
                    {
                        if (why is not null) return;
                        var name = field.Name.Value;
                        if (!inputType.Fields.TryGetField(name, out var inputField))
                        {
                            why = $"The {argument} condition names '{name}', which this field does not take.";
                            return;
                        }
                        var member = inputField switch
                        {
                            IFilterField filter => filter.Member,
                            ISortField sort => sort.Member,
                            _ => null,
                        };
                        if (member is PropertyInfo column && IsScalar(column.PropertyType))
                        {
                            reads.Columns.Add((model, column.Name));
                            // What follows is the operation (`eq: ...`, `in: [...]`) or the direction - values, not columns. They
                            // are not read for a column name, but every part of them counts: a list of ten thousand ids is
                            // ten thousand parts of the database's work.
                            nodes += PartsOf(field.Value);
                            if (nodes > maxNodes) { why = $"The {argument} condition is larger than {maxNodes} parts."; return; }
                        }
                        else if (member is PropertyInfo collection && ElementTypeOf(collection.PropertyType) is { } element
                                 && inputField.Type.NamedType() is IInputObjectTypeDefinition listInput)
                        {
                            // Crossing into a collection reads the model of its rows, whichever of some / all / none / any is asked.
                            reads.Entered.Add(element);
                            Visit(field.Value, depth + 1, listInput, element, isList: true);
                        }
                        else if (member is PropertyInfo reference && inputField.Type.NamedType() is IInputObjectTypeDefinition nested)
                        {
                            // Crossing into another model reads that model: its own policy comes first, then the columns named in it.
                            reads.Entered.Add(reference.PropertyType);
                            Visit(field.Value, depth + 1, nested, reference.PropertyType);
                        }
                        else if (name is "and" or "or")
                            Visit(field.Value, depth + 1, inputType, model);
                        else
                        {
                            why = $"The {argument} condition has a part this field cannot check ('{name}').";
                            return;
                        }
                    }
                    return;
                default:
                    why = $"The {argument} condition has a part this field cannot check.";
                    return;
            }
        }

        Visit(context.ArgumentLiteral<IValueNode>(argument), depth: 0, rootInput, rootModel);
        refusal = why;
        return reads;
    }

    /// <summary>How many parts a value has — itself and everything inside it (object fields, list items), without reading names.</summary>
    private static int PartsOf(IValueNode? node) => node switch
    {
        ObjectValueNode obj => 1 + obj.Fields.Sum(f => PartsOf(f.Value)),
        ListValueNode list => 1 + list.Items.Sum(PartsOf),
        null => 0,
        _ => 1,
    };

    internal static void ConfigureFilter<T>(
        IFilterInputTypeDescriptor<T> descriptor, IReadOnlyList<PropertyInfo> columns, IReadOnlyList<PropertyInfo> navigations,
        IReadOnlyList<PropertyInfo>? collections = null)
        where T : class
    {
        descriptor.BindFieldsExplicitly();
        foreach (var column in columns) descriptor.Field(Access<T>(column));
        foreach (var navigation in navigations) descriptor.Field(Access<T>(navigation));
        foreach (var collection in collections ?? []) descriptor.Field(Access<T>(collection));
    }

    internal static void ConfigureSort<T>(
        ISortInputTypeDescriptor<T> descriptor, IReadOnlyList<PropertyInfo> columns, IReadOnlyList<PropertyInfo> navigations)
        where T : class
    {
        descriptor.BindFieldsExplicitly();
        foreach (var column in columns) descriptor.Field(Access<T>(column));
        foreach (var navigation in navigations) descriptor.Field(Access<T>(navigation));
    }
}
