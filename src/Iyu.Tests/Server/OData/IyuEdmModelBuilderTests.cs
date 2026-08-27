using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Iyu.Core.Entities;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.V1;
using Iyu.Server.OData;
using Xunit;

namespace Iyu.Tests.Server.OData;

public class IyuEdmModelBuilderTests
{
    public class BankAccount : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
    }

    public class BankAccountExt : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
    }

    public class Customer : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    public class CustomerExt : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void AddEntityPair_registers_and_exposes_set_in_edm_model()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");

        var model = builder.GetEdmModel();
        var container = model.EntityContainer;
        Assert.NotNull(container);
        Assert.NotNull(container!.FindEntitySet("BankAccounts"));

        var pair = builder.Registry.Find("BankAccounts");
        Assert.NotNull(pair);
        Assert.Equal(typeof(BankAccountExt), pair!.ReadType);
        Assert.Equal(typeof(BankAccount), pair.WriteType);
    }


    public class Secretive : IyuEntity
    {
        public string Name { get; set; } = "";
        public string SecretHash { get; set; } = "";
    }

    /// <summary>
    /// The excluded property must be gone from the EDM, not blanked: absence is what
    /// makes $select/$filter naming it a 400, so the value can neither be read nor
    /// probed one character at a time.
    /// </summary>
    [Fact]
    public void Exclude_removes_the_property_from_the_edm()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Secretive, Secretive>("Secretives");
        builder.Exclude<Secretive>(x => x.SecretHash);

        var model = builder.GetEdmModel();
        var type = model.SchemaElements.OfType<IEdmEntityType>()
            .Single(t => t.Name == nameof(Secretive));

        Assert.Null(type.FindProperty(nameof(Secretive.SecretHash)));
        Assert.NotNull(type.FindProperty(nameof(Secretive.Name)));   // 나머지는 그대로
    }

    /// <summary>Exclusion is callable after registration — consumers whose registration is generated cannot reorder it.</summary>
    [Fact]
    public void Exclude_applies_even_though_the_pair_was_registered_first()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Secretive, Secretive>("Secretives");
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Exclude<Secretive>(x => x.SecretHash);

        var model = builder.GetEdmModel();
        Assert.NotNull(model.EntityContainer!.FindEntitySet("Secretives"));
        Assert.NotNull(model.EntityContainer!.FindEntitySet("BankAccounts"));
        var type = model.SchemaElements.OfType<IEdmEntityType>()
            .Single(t => t.Name == nameof(Secretive));
        Assert.Null(type.FindProperty(nameof(Secretive.SecretHash)));
    }

    /// <summary>A typo must not silently expose the very field it meant to hide.</summary>
    [Fact]
    public void Exclude_rejects_a_non_property_expression()
    {
        var builder = new IyuEdmModelBuilder();
        Assert.Throws<ArgumentException>(() => builder.Exclude<Secretive>(x => x.Name.Length.ToString()));
    }

    /// <summary>Order-independence: nothing is applied until the model is finalized.</summary>
    [Fact]
    public void Exclude_applies_even_though_it_was_called_before_the_pair_was_registered()
    {
        var builder = new IyuEdmModelBuilder();
        builder.Exclude<Secretive>(x => x.SecretHash);
        builder.AddEntityPair<Secretive, Secretive>("Secretives");

        var type = builder.GetEdmModel().SchemaElements.OfType<IEdmEntityType>()
            .Single(t => t.Name == nameof(Secretive));
        Assert.Null(type.FindProperty(nameof(Secretive.SecretHash)));
    }

    /// <summary>
    /// Naming the write type excludes nothing — request bodies bind to the read type — so it
    /// must fail rather than leave the caller believing the value is hidden.
    /// </summary>
    /// <remarks>
    /// Refusing is not merely stricter than ignoring. The exclusion is applied by declaring
    /// the named type on the underlying convention builder, so accepting a type the model does
    /// not expose would <i>add</i> it: an attempt to hide one property would publish the rest
    /// of that type's shape. The pinned assertion below is that no such type appears.
    /// </remarks>
    [Fact]
    public void Exclude_rejects_the_write_type_and_says_which_type_to_pass()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Exclude<BankAccount>(x => x.AccountNumber);   // the write type

        var error = Assert.Throws<InvalidOperationException>(() => builder.GetEdmModel());
        Assert.Contains(nameof(BankAccountExt), error.Message, StringComparison.Ordinal);   // names the fix
        Assert.Contains("BankAccounts", error.Message, StringComparison.Ordinal);           // and where it applies
    }

    /// <summary>An exclusion on an entirely unregistered type is a configuration bug, not a no-op.</summary>
    [Fact]
    public void Exclude_rejects_a_type_the_model_does_not_expose()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Exclude<CustomerExt>(x => x.Name);

        var error = Assert.Throws<InvalidOperationException>(() => builder.GetEdmModel());
        Assert.Contains(nameof(CustomerExt), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(BankAccountExt), error.Message, StringComparison.Ordinal);   // what *is* exposed
    }

    /// <summary>
    /// The write type stays out of the model. It is the runtime's internal target, and
    /// <c>AddEntityPair</c> promises as much.
    /// </summary>
    [Fact]
    public void Excluding_on_the_read_type_leaves_the_write_type_out_of_the_model()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Exclude<BankAccountExt>(x => x.AccountNumber);

        var declared = builder.GetEdmModel().SchemaElements.OfType<IEdmEntityType>()
            .Select(t => t.Name).ToList();

        Assert.Contains(nameof(BankAccountExt), declared);
        Assert.DoesNotContain(nameof(BankAccount), declared);
    }

    [Fact]
    public void AddEntityPair_rejects_duplicate_set_name()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        Assert.Throws<InvalidOperationException>(
            () => builder.AddEntityPair<CustomerExt, Customer>("BankAccounts"));
    }

    [Fact]
    public void Registry_All_returns_all_registered_pairs()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.AddEntityPair<CustomerExt, Customer>("Customers");

        var all = builder.Registry.All;
        Assert.Equal(2, all.Count);
    }

    public enum InputType
    {
        [EnumMember(Value = "verdict")]
        Verdict,

        // No [EnumMember] — the CLR name is the declared wire name too.
        Numeric,
    }

    public class InspectionItem : IyuEntity
    {
        public InputType InputType { get; set; }
    }

    /// <summary>
    /// The EDM must advertise the same enum spelling every other layer of the wire
    /// already uses (generated C# declares <c>[EnumMember(Value = "verdict")]</c>, and
    /// deserialization only accepts that spelling) — otherwise a client built from
    /// <c>$metadata</c> sends the CLR name and gets an unexplained 400.
    /// </summary>
    [Fact]
    public void Enum_member_names_follow_EnumMemberAttribute_not_the_clr_member_name()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<InspectionItem, InspectionItem>("InspectionItems");

        var model = builder.GetEdmModel();
        var enumType = model.SchemaElements.OfType<IEdmEnumType>()
            .Single(e => e.Name == nameof(InputType));
        var names = enumType.Members.Select(m => m.Name).ToList();

        Assert.Contains("verdict", names);
        Assert.DoesNotContain("Verdict", names);
        // No attribute on this member — falls back to the CLR name, same as before.
        Assert.Contains("Numeric", names);
    }

    private static bool RestrictionValue(IEdmModel model, IEdmEntitySet entitySet, string termName, string propertyName)
    {
        var annotation = model.VocabularyAnnotations.Single(
            a => a.Target == entitySet && a.Term.Name == termName);
        var record = Assert.IsAssignableFrom<IEdmRecordExpression>(annotation.Value);
        var property = record.Properties.Single(p => p.Name == propertyName);
        return Assert.IsAssignableFrom<IEdmBooleanConstantExpression>(property.Value).Value;
    }

    [Fact]
    public void AddEntityPair_with_no_readOnlyVerbs_registers_no_capability_annotations()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");

        var model = builder.GetEdmModel();
        Assert.Empty(model.VocabularyAnnotations);
    }

    /// <summary>
    /// A set registered read-only for POST/DELETE (but not PATCH) advertises exactly
    /// those two restrictions on <c>$metadata</c> via the real OData Capabilities
    /// vocabulary — not an <c>Iyu.*</c> vendor term — so any standard OData client
    /// reads the same restriction the generic controller enforces.
    /// </summary>
    [Fact]
    public void AddEntityPair_with_readOnlyVerbs_annotates_the_entity_set_with_the_standard_capabilities_vocabulary()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts", ODataVerb.Post, ODataVerb.Delete);

        var model = builder.GetEdmModel();
        var entitySet = model.EntityContainer.FindEntitySet("BankAccounts")!;

        Assert.False(RestrictionValue(model, entitySet, "InsertRestrictions", "Insertable"));
        Assert.False(RestrictionValue(model, entitySet, "DeleteRestrictions", "Deletable"));
        Assert.DoesNotContain(model.VocabularyAnnotations, a => a.Target == entitySet && a.Term.Name == "UpdateRestrictions");

        var term = model.VocabularyAnnotations.Single(a => a.Target == entitySet && a.Term.Name == "InsertRestrictions").Term;
        Assert.Equal("Org.OData.Capabilities.V1", term.Namespace);
    }

    [Fact]
    public void AddEntityPair_stores_readOnlyVerbs_on_the_registered_pair()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts", ODataVerb.Patch);

        var pair = builder.Registry.Find("BankAccounts");
        Assert.NotNull(pair);
        Assert.Equal(new[] { ODataVerb.Patch }, pair!.ReadOnlyVerbs);
    }

    /// <summary>
    /// A consumer whose registration is code-generated (a single generated call site, no
    /// per-call <c>readOnlyVerbs</c>) can restrict a set after the fact from a location it does
    /// own. Because <see cref="IyuEdmModelBuilder.GetEdmModel"/> reads <c>Registry.All</c> lazily
    /// — the same deferred-apply property <see cref="IyuEdmModelBuilder.Exclude{T}"/> already relies on — the
    /// restriction still reaches <c>$metadata</c> even though it was applied after registration.
    /// </summary>
    [Fact]
    public void Restrict_updates_an_already_registered_set_and_it_still_reaches_metadata()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Restrict("BankAccounts", ODataVerb.Post, ODataVerb.Delete);

        Assert.Equal(
            new[] { ODataVerb.Post, ODataVerb.Delete },
            builder.Registry.Find("BankAccounts")!.ReadOnlyVerbs.OrderBy(v => v));

        var model = builder.GetEdmModel();
        var entitySet = model.EntityContainer.FindEntitySet("BankAccounts")!;
        Assert.False(RestrictionValue(model, entitySet, "InsertRestrictions", "Insertable"));
        Assert.False(RestrictionValue(model, entitySet, "DeleteRestrictions", "Deletable"));
    }

    [Fact]
    public void Restrict_on_a_set_that_was_never_registered_throws()
    {
        var builder = new IyuEdmModelBuilder();
        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.Restrict("NeverRegistered", ODataVerb.Post));
        Assert.Contains("NeverRegistered", ex.Message);
    }

    /// <summary>
    /// <see cref="IyuEntityPairRegistry.Register{TRead,TWrite}"/>'s loud-failure guard against a
    /// genuine duplicate registration is unaffected by <c>Restrict</c> existing — re-registering
    /// the same set name still throws, it is only the verb set of an already-known set that
    /// <c>Restrict</c> can update.
    /// </summary>
    [Fact]
    public void Restrict_does_not_weaken_Register_s_duplicate_registration_guard()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.Restrict("BankAccounts", ODataVerb.Post);

        Assert.Throws<InvalidOperationException>(
            () => builder.AddEntityPair<CustomerExt, Customer>("BankAccounts"));
    }

    public class Annotated : IyuEntity
    {
        [Display(Description = "The bank's public display name")]
        public string BankName { get; set; } = "";

        // No [Display] — must not gain a Description annotation.
        public string AccountNumber { get; set; } = "";
    }

    /// <summary>
    /// A generated entity's <c>[Display(Description = "...")]</c> reaches OData clients as the
    /// standard <c>Org.OData.Core.V1.Description</c> term on <c>$metadata</c> — the same text a
    /// generated form already shows, now visible to any OData-aware client too.
    /// </summary>
    [Fact]
    public void Display_description_becomes_the_standard_odata_core_description_term()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Annotated, Annotated>("Annotateds");

        var model = builder.GetEdmModel();
        var entityType = model.SchemaElements.OfType<IEdmEntityType>()
            .Single(t => t.Name == nameof(Annotated));
        var bankNameProperty = entityType.FindProperty(nameof(Annotated.BankName));

        var annotation = model.VocabularyAnnotations.Single(
            a => a.Target == bankNameProperty && a.Term.Name == "Description");
        Assert.Equal("Org.OData.Core.V1", annotation.Term.Namespace);
        var value = Assert.IsAssignableFrom<IEdmStringConstantExpression>(annotation.Value);
        Assert.Equal("The bank's public display name", value.Value);

        var accountNumberProperty = entityType.FindProperty(nameof(Annotated.AccountNumber));
        Assert.DoesNotContain(model.VocabularyAnnotations, a => a.Target == accountNumberProperty);
    }

    public class Stateful : IyuEntity
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// Unlike <see cref="IyuEdmModelBuilder.Exclude{T}"/>, the property must stay in the EDM — a
    /// caller can still read/select/filter it — and instead pick up the standard
    /// <c>Org.OData.Core.V1.Computed</c> term, the built-in vocabulary's own
    /// spelling for "server-supplied, do not send on insert/update".
    /// </summary>
    [Fact]
    public void ExcludeFromWrite_keeps_the_property_in_the_edm_and_marks_it_computed()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Stateful, Stateful>("Statefuls");
        builder.ExcludeFromWrite<Stateful>(x => x.Status);

        var model = builder.GetEdmModel();
        var entityType = model.SchemaElements.OfType<IEdmEntityType>().Single(t => t.Name == nameof(Stateful));
        var statusProperty = entityType.FindProperty(nameof(Stateful.Status));

        Assert.NotNull(statusProperty);   // still in the model — this is not Exclude<T>

        var annotation = model.VocabularyAnnotations.Single(a => a.Target == statusProperty);
        Assert.Equal("Org.OData.Core.V1", annotation.Term.Namespace);
        Assert.Equal("Computed", annotation.Term.Name);
        var value = Assert.IsAssignableFrom<IEdmBooleanConstantExpression>(annotation.Value);
        Assert.True(value.Value);

        var nameProperty = entityType.FindProperty(nameof(Stateful.Name));
        Assert.DoesNotContain(model.VocabularyAnnotations, a => a.Target == nameProperty);
    }

    /// <summary>The registry is what the generic controller actually consults at request time.</summary>
    [Fact]
    public void ExcludeFromWrite_records_the_property_name_on_the_registered_pair()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<Stateful, Stateful>("Statefuls");
        builder.ExcludeFromWrite<Stateful>(x => x.Status);

        builder.GetEdmModel();

        var pair = builder.Registry.Find("Statefuls");
        Assert.NotNull(pair);
        Assert.Contains(nameof(Stateful.Status), pair!.WriteExcludedProperties);
        Assert.DoesNotContain(nameof(Stateful.Name), pair.WriteExcludedProperties);
    }

    /// <summary>Same guard as <see cref="IyuEdmModelBuilder.Exclude{T}"/>, for the same reason — request bodies bind to the read type.</summary>
    [Fact]
    public void ExcludeFromWrite_rejects_the_write_type_and_says_which_type_to_pass()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.ExcludeFromWrite<BankAccount>(x => x.AccountNumber);

        var error = Assert.Throws<InvalidOperationException>(() => builder.GetEdmModel());
        Assert.Contains(nameof(BankAccountExt), error.Message, StringComparison.Ordinal);
        Assert.Contains("BankAccounts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludeFromWrite_rejects_a_type_the_model_does_not_expose()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<BankAccountExt, BankAccount>("BankAccounts");
        builder.ExcludeFromWrite<CustomerExt>(x => x.Name);

        var error = Assert.Throws<InvalidOperationException>(() => builder.GetEdmModel());
        Assert.Contains(nameof(CustomerExt), error.Message, StringComparison.Ordinal);
    }

    /// <summary>A typo must not silently leave the field writable.</summary>
    [Fact]
    public void ExcludeFromWrite_rejects_a_non_property_expression()
    {
        var builder = new IyuEdmModelBuilder();
        Assert.Throws<ArgumentException>(() => builder.ExcludeFromWrite<Stateful>(x => x.Name.Length.ToString()));
    }

    /// <summary>Order-independence, same as <see cref="IyuEdmModelBuilder.Exclude{T}"/>: nothing applies until <see cref="IyuEdmModelBuilder.GetEdmModel"/>.</summary>
    [Fact]
    public void ExcludeFromWrite_applies_even_though_it_was_called_before_the_pair_was_registered()
    {
        var builder = new IyuEdmModelBuilder();
        builder.ExcludeFromWrite<Stateful>(x => x.Status);
        builder.AddEntityPair<Stateful, Stateful>("Statefuls");

        builder.GetEdmModel();
        Assert.Contains(nameof(Stateful.Status), builder.Registry.Find("Statefuls")!.WriteExcludedProperties);
    }
}
