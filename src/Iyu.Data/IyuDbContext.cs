using System.Reflection;
using System.Runtime.Serialization;
using Iyu.Data.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Iyu.Data;

/// <summary>
/// Base <see cref="DbContext"/> that all generated consumer DbContexts
/// (e.g. <c>YesungDbContext</c>) derive from. Automatically registers the
/// <see cref="IyuTimestampInterceptor"/> so that every save operation maintains
/// <c>CreatedAt</c>/<c>UpdatedAt</c> invariants.
/// </summary>
/// <remarks>
/// Consumers pass <see cref="DbContextOptions"/> through the standard EF Core
/// DI pipeline. Additional interceptors supplied via <see cref="DbContextOptionsBuilder"/>
/// are preserved — this class only ensures the timestamp interceptor is present.
/// </remarks>
public abstract class IyuDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // Idempotent registration — harmless if consumer already added one.
        optionsBuilder.AddInterceptors(new IyuTimestampInterceptor());
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
