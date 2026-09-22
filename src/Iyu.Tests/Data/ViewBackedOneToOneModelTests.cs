using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Data;

/// <summary>
/// Pins what EF Core accepts for a one-to-one relationship between two <em>view-backed</em> read
/// types — the shape a generated read model would need before <c>$expand</c> or <c>Include</c> can
/// reach across it.
/// <para>
/// The generated read types are mapped with <c>ToView</c>, and a view carries no foreign key for EF
/// to infer a relationship from, so whether a relationship can be configured between two of them at
/// all is a question about EF rather than about any generator. It is answered here, in the
/// repository that owns the EF usage, so that a generator emitting navigation properties is not the
/// thing that finds out.
/// </para>
/// <para>
/// Scope is deliberate: this is the <b>model</b>, not a query. <c>ToView</c> is a model-level
/// configuration and the in-memory provider records it without pretending to be relational, so the
/// question "does EF build this model" is answerable here without a relational provider — none is
/// referenced by any test project in this repository. What a real view returns for such an
/// <c>Include</c> is a separate question, and one for the generator's own SQL/model cross-check.
/// </para>
/// </summary>
public class ViewBackedOneToOneModelTests
{
    private sealed class ProfileExt : IyuEntity
    {
        public Guid OwnerId { get; set; }
        public OwnerExt? Owner { get; set; }
    }

    private sealed class OwnerExt : IyuEntity
    {
        public string Name { get; set; } = "";
        public ProfileExt? Profile { get; set; }
    }

    private sealed class ViewPairContext(DbContextOptions<ViewPairContext> options) : IyuDbContext(options)
    {
        public DbSet<OwnerExt> Owners => Set<OwnerExt>();
        public DbSet<ProfileExt> Profiles => Set<ProfileExt>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Exactly the shape a generated DbContext uses for a read model: no table, a view.
            modelBuilder.Entity<OwnerExt>().ToTable((string?)null).ToView("v_owner");
            modelBuilder.Entity<ProfileExt>().ToTable((string?)null).ToView("v_profile");

            // And the relationship the generator would have to add, since a view gives EF nothing
            // to infer it from.
            modelBuilder.Entity<ProfileExt>()
                .HasOne(p => p.Owner)
                .WithOne(o => o!.Profile)
                .HasForeignKey<ProfileExt>(p => p.OwnerId);
        }
    }

    private static ViewPairContext Build(string name)
        => new(new DbContextOptionsBuilder<ViewPairContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public void A_one_to_one_between_two_view_backed_types_is_accepted_and_is_one_to_one()
    {
        using var ctx = Build(nameof(A_one_to_one_between_two_view_backed_types_is_accepted_and_is_one_to_one));

        // Building the model at all is half the answer: a rejected configuration throws here.
        var model = ctx.Model;

        var profile = model.FindEntityType(typeof(ProfileExt));
        var owner = model.FindEntityType(typeof(OwnerExt));
        Assert.NotNull(profile);
        Assert.NotNull(owner);

        // Both are views, not tables — the premise of the question.
        Assert.Equal("v_profile", profile!.GetViewName());
        Assert.Equal("v_owner", owner!.GetViewName());
        Assert.Null(profile.GetTableName());
        Assert.Null(owner.GetTableName());

        // The navigation exists in both directions and the relationship is one-to-one, not
        // one-to-many — the distinction the generator has to get right, since a unique FK alone
        // does not tell EF which it is.
        var toOwner = profile.FindNavigation(nameof(ProfileExt.Owner));
        var toProfile = owner.FindNavigation(nameof(OwnerExt.Profile));
        Assert.NotNull(toOwner);
        Assert.NotNull(toProfile);
        Assert.True(toOwner!.ForeignKey.IsUnique, "the relationship must be one-to-one");
        Assert.Same(toOwner.ForeignKey, toProfile!.ForeignKey);
        Assert.Equal(
            [nameof(ProfileExt.OwnerId)],
            toOwner.ForeignKey.Properties.Select(p => p.Name));
    }

    /// <summary>The model without the explicit configuration, so convention is what is measured.</summary>
    private sealed class UnconfiguredContext(DbContextOptions<UnconfiguredContext> options)
        : IyuDbContext(options)
    {
        public DbSet<OwnerExt> Owners => Set<OwnerExt>();
        public DbSet<ProfileExt> Profiles => Set<ProfileExt>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<OwnerExt>().ToTable((string?)null).ToView("v_owner");
            modelBuilder.Entity<ProfileExt>().ToTable((string?)null).ToView("v_profile");
            // No HasOne/WithOne on purpose.
        }
    }

    /// <summary>
    /// The negative control, and it failed — which is the more useful result. EF's own conventions
    /// pair two reciprocal reference navigations into a <b>one-to-one</b> and take the matching
    /// <c>{Navigation}Id</c> property as its foreign key, with no configuration at all. So the
    /// generator's job for this shape is narrower than the design assumed: emitting the pair of
    /// navigation properties is what creates the relationship, and explicit
    /// <c>HasOne/WithOne/HasForeignKey</c> is only needed where convention cannot reach the answer —
    /// a foreign key whose name does not match, or two references to the same target, where
    /// convention has no way to choose.
    /// <para>
    /// Pinned as an assertion rather than left as a note, because it is a property of the EF version
    /// this framework pins. If a later EF stops inferring it, this goes red and the generator's
    /// scope grows — which is exactly when that needs to be noticed.
    /// </para>
    /// </summary>
    [Fact]
    public void Convention_alone_already_infers_the_one_to_one_from_reciprocal_navigations()
    {
        using var ctx = new UnconfiguredContext(
            new DbContextOptionsBuilder<UnconfiguredContext>()
                .UseInMemoryDatabase(nameof(Convention_alone_already_infers_the_one_to_one_from_reciprocal_navigations))
                .Options);

        var profile = ctx.Model.FindEntityType(typeof(ProfileExt));
        Assert.NotNull(profile);

        var toOwner = profile!.FindNavigation(nameof(ProfileExt.Owner));
        Assert.NotNull(toOwner);
        Assert.True(toOwner!.ForeignKey.IsUnique, "convention already infers the one-to-one");

        // And it uses the declared property, not a shadow one — so the generated FK column is the
        // one the model declares rather than something EF invented.
        Assert.Equal(
            [nameof(ProfileExt.OwnerId)],
            toOwner.ForeignKey.Properties.Select(p => p.Name));
        Assert.False(toOwner.ForeignKey.Properties[0].IsShadowProperty());

        // The inverse is paired too, which is what makes it usable from either side.
        var owner = ctx.Model.FindEntityType(typeof(OwnerExt));
        Assert.NotNull(owner);
        var toProfile = owner!.FindNavigation(nameof(OwnerExt.Profile));
        Assert.NotNull(toProfile);
        Assert.Same(toOwner.ForeignKey, toProfile!.ForeignKey);
    }
}
