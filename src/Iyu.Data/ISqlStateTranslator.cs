using System.Data.Common;

namespace Iyu.Data;

/// <summary>
/// Names the standard SQLSTATE of a data store error whose provider does not report one through
/// <see cref="DbException.SqlState"/> — the code a write failure is classified by.
/// </summary>
/// <remarks>
/// ADO.NET carries the standard class on <see cref="DbException.SqlState"/>, and a provider that sets it needs nothing
/// here. One that does not (SQL Server's client reports its own error numbers instead) is translated by an
/// implementation registered for it, so the classification itself stays in one place and provider-neutral. Every
/// registered translator is asked in registration order; the first answer wins.
/// </remarks>
public interface ISqlStateTranslator
{
    /// <summary>The SQLSTATE <paramref name="exception"/> stands for, or <see langword="null"/> when this translator does not know it.</summary>
    string? SqlStateOf(DbException exception);
}
