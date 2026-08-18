using System.Runtime.Serialization;
using Iyu.Core.Entities;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;
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
}
