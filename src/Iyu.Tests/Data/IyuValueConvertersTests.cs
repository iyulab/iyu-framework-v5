using Iyu.Core.Entities;
using Iyu.Core.ValueObjects;
using Iyu.Data;
using Iyu.Data.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Data;

public class IyuValueConvertersTests
{
    private sealed class Contact : IyuEntity
    {
        public PhoneNumber Phone { get; set; }
        public EmailAddress Email { get; set; }
        public WebUrl Homepage { get; set; }
    }

    private sealed class ContactContext(DbContextOptions<ContactContext> options) : IyuDbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var contact = modelBuilder.Entity<Contact>();
            contact.Property(c => c.Phone).HasConversion(IyuValueConverters.Phone);
            contact.Property(c => c.Email).HasConversion(IyuValueConverters.Email);
            contact.Property(c => c.Homepage).HasConversion(IyuValueConverters.Url);
        }
    }

    private static ContactContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<ContactContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ContactContext(options);
    }

    [Fact]
    public async Task VO_fields_round_trip_through_the_provider()
    {
        var id = Guid.NewGuid();
        using (var ctx = CreateContext(nameof(VO_fields_round_trip_through_the_provider)))
        {
            ctx.Contacts.Add(new Contact
            {
                Id = id,
                Phone = PhoneNumber.Parse("010-1234-5678"),
                Email = EmailAddress.Parse("User@Example.COM"),
                Homepage = WebUrl.Parse("https://example.com/")
            });
            await ctx.SaveChangesAsync();
        }

        using (var ctx = CreateContext(nameof(VO_fields_round_trip_through_the_provider)))
        {
            var loaded = await ctx.Contacts.SingleAsync(c => c.Id == id);
            Assert.Equal("010-1234-5678", loaded.Phone.Value);
            Assert.Equal("user@example.com", loaded.Email.Value);
            Assert.StartsWith("https://example.com/", loaded.Homepage.Value);
        }
    }

    [Fact]
    public void Converters_write_empty_string_for_default_struct()
    {
        var phoneWriter = IyuValueConverters.Phone.ConvertToProvider;
        Assert.Equal(string.Empty, phoneWriter(default(PhoneNumber)));

        var emailWriter = IyuValueConverters.Email.ConvertToProvider;
        Assert.Equal(string.Empty, emailWriter(default(EmailAddress)));

        var urlWriter = IyuValueConverters.Url.ConvertToProvider;
        Assert.Equal(string.Empty, urlWriter(default(WebUrl)));
    }

    [Fact]
    public void Converters_yield_default_on_invalid_legacy_data()
    {
        // ConvertFromProvider returns boxed struct; compare against default
        // typed-explicitly so inference doesn't resolve to null.
        Assert.Equal((object)default(PhoneNumber), IyuValueConverters.Phone.ConvertFromProvider("garbage"));
        Assert.Equal((object)default(EmailAddress), IyuValueConverters.Email.ConvertFromProvider("not-an-email"));
        Assert.Equal((object)default(WebUrl), IyuValueConverters.Url.ConvertFromProvider("ftp://example.com"));
    }
}
