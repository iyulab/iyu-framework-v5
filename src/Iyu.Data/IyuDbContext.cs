using System.Reflection;
using System.Runtime.Serialization;
using Iyu.Data.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Iyu.Data;

/// <summary>
/// Base <see cref="DbContext"/> that a generated application DbContext derives
/// from. Automatically registers the
/// <see cref="IyuTimestampInterceptor"/> so that every save operation maintains
/// <c>CreatedAt</c>/<c>UpdatedAt</c> invariants, and the
/// <see cref="IyuDateTimeOffsetNormalizationInterceptor"/> so that every
/// <see cref="DateTimeOffset"/>-typed property reaches the provider at UTC.
/// </summary>
/// <remarks>
/// Consumers pass <see cref="DbContextOptions"/> through the standard EF Core
/// DI pipeline. Additional interceptors supplied via <see cref="DbContextOptionsBuilder"/>
/// are preserved — this class only ensures its own interceptors are present.
/// <para>
/// Supplying an instance of either interceptor type on those same options <em>replaces</em> the
/// default: pass <c>new IyuTimestampInterceptor(clock)</c> to put a test clock or an injected
/// <see cref="TimeProvider"/> behind the audit timestamps, and this class will not add a
/// system-clock instance on top of it.
/// </para>
/// </remarks>
public abstract class IyuDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // These two are defaults, not overrides. EF keeps every registered interceptor and runs
        // application interceptors in the order they were added, so appending unconditionally did
        // not "do no harm when one is already there" — it let this system-clock instance write
        // last and silently beat an instance the consumer had supplied on the same options. That
        // closed the seam IyuTimestampInterceptor's own documentation points at (replace the clock
        // by passing a TimeProvider) for everyone using a IyuDbContext, and left a derived context
        // that re-registers after base.OnConfiguring as the only way through.
        //
        // The default stays here rather than moving to composition, because OnConfiguring runs
        // however the context was built — AddDbContext, a passed DbContextOptions, or `new` in a
        // test — and a default that only arrives through one of those paths is no default at all.
        var supplied = (optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors
                        ?? []).ToList();

        if (!supplied.OfType<IyuTimestampInterceptor>().Any())
            optionsBuilder.AddInterceptors(new IyuTimestampInterceptor());

        if (!supplied.OfType<IyuDateTimeOffsetNormalizationInterceptor>().Any())
            optionsBuilder.AddInterceptors(new IyuDateTimeOffsetNormalizationInterceptor());
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        IyuValueConverters.RegisterAll(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ApplyEnumMemberConverters(modelBuilder);
    }

    /// <summary>
    /// Scans all entity properties for enum types with [EnumMember] attributes
    /// and applies a value converter that stores the EnumMember value (lowercase)
    /// instead of the CLR name (PascalCase). This conversion is the primary
    /// enum-value guard; mdd-booster emits matching SQL CHECK constraints only
    /// when its opt-in <c>emitEnumCheckConstraints</c> knob is enabled.
    /// </summary>
    private static void ApplyEnumMemberConverters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = property.ClrType;
                var underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;
                if (!underlyingType.IsEnum) continue;
                if (!HasEnumMemberAttributes(underlyingType)) continue;

                var converterType = typeof(EnumMemberConverter<>).MakeGenericType(underlyingType);
                var converter = (ValueConverter)Activator.CreateInstance(converterType)!;
                property.SetValueConverter(converter);
            }
        }
    }

    private static bool HasEnumMemberAttributes(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Any(f => f.GetCustomAttribute<EnumMemberAttribute>() != null);
}

/// <summary>
/// Converts an enum to/from its [EnumMember(Value)] string representation.
/// Falls back to the CLR name if no [EnumMember] attribute is present.
/// </summary>
public class EnumMemberConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public EnumMemberConverter()
        : base(v => EnumWireNames<TEnum>.ToWire.GetValueOrDefault(v, v.ToString()),
               v => EnumWireNames<TEnum>.FromWire.GetValueOrDefault(v, default))
    { }
}
