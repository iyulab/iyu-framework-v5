using Iyu.Data;
using Iyu.Data.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Data;

/// <summary>
/// SQL Server's error numbers, named by their standard SQLSTATE — the class a failed write is classified by. The
/// server's own messages are in the cases for 547, which reports a foreign key and a check constraint under one number.
/// </summary>
public class SqlServerSqlStateTranslatorTests
{
    [Theory]
    [InlineData(2627, "Violation of UNIQUE KEY constraint 'UQ_x'. Cannot insert duplicate key in object 'dbo.T'.", "23505")]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.T' with unique index 'IX_x'.", "23505")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_x\".", "23503")]
    [InlineData(547, "The DELETE statement conflicted with the REFERENCE constraint \"FK_x\".", "23503")]
    [InlineData(547, "The INSERT statement conflicted with the CHECK constraint \"CK_x\".", "23514")]
    [InlineData(515, "Cannot insert the value NULL into column 'Name', table 'dbo.T'; column does not allow nulls.", "23502")]
    [InlineData(2628, "String or binary data would be truncated in table 'dbo.T', column 'Name'.", "22001")]
    [InlineData(8152, "String or binary data would be truncated.", "22001")]
    [InlineData(8115, "Arithmetic overflow error converting numeric to data type numeric.", "22003")]
    [InlineData(220, "Arithmetic overflow error for data type smallint, value = 40000.", "22003")]
    [InlineData(1205, "Transaction was deadlocked on lock resources with another process.", "40001")]
    public void A_known_error_number_is_named_by_its_sqlstate(int number, string message, string expected)
        => Assert.Equal(expected, SqlServerSqlStateTranslator.SqlStateOf(number, message));

    [Theory]
    [InlineData(547, "L'instruction INSERT est en conflit avec la contrainte \"FK_x\".")] // a server in another language
    [InlineData(4060, "Cannot open database requested by the login.")]                   // not a write's refusal
    public void What_it_cannot_name_it_leaves_unnamed(int number, string message)
        => Assert.Null(SqlServerSqlStateTranslator.SqlStateOf(number, message));

    [Fact]
    public void Registering_it_twice_registers_it_once()
    {
        var services = new ServiceCollection().AddIyuSqlServerErrors().AddIyuSqlServerErrors();
        Assert.Single(services.BuildServiceProvider().GetServices<ISqlStateTranslator>());
    }
}
