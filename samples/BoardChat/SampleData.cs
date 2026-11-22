using BoardChat.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardChat;

/// <summary>Creates the database and fills it with dummy sales for the twelve months before the sample was written.</summary>
public static class SampleData
{
    public static void Create(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesDb>();
        if (!db.Database.EnsureCreated()) return;
        // The read type of an order is a view over its lookups (the generated SQL creates it; EnsureCreated creates tables only).
        db.Database.ExecuteSqlRaw("""
            CREATE VIEW OrderFullView AS SELECT o.*, r.Name AS RegionName, c.Name AS CustomerName, p.Name AS ProductName
            FROM "Order" o JOIN Region r ON r.Id = o.RegionId JOIN Customer c ON c.Id = o.CustomerId JOIN Product p ON p.Id = o.ProductId
            """);

        var random = new Random(2026);
        var regions = new[] { "North", "South", "East", "West", "Central" }.Select(n => new Region { Name = n }).ToList();
        var products = Enumerable.Range(0, 12).Select(i => new Product
        {
            Name = $"{new[] { "Atlas", "Beacon", "Cobalt", "Delta" }[i % 4]} {new[] { "Basic", "Plus", "Max" }[i / 4]}",
            Category = new[] { "Hardware", "Software", "Services" }[i / 4], Price = 40 + 35 * (i % 4) + 60 * (i / 4),
        }).ToList();
        var customers = Enumerable.Range(1, 60).Select(i => new Customer
        {
            Name = $"Customer {i:00}", Email = $"customer{i:00}@example.test", SignupDate = new DateOnly(2024, 1, 1).AddDays(i * 11),
            Segment = (CustomerSegment)(i % 3),
        }).ToList();
        var orders = Enumerable.Range(0, 640).Select(i =>
        {
            var month = i * 12 / 640;
            var product = products[random.Next(products.Count)];
            var total = Math.Round(product.Price * random.Next(1, 6) * (1 + month * 0.03m), 2);
            return new Order
            {
                OrderDate = new DateOnly(2025, 11, 1).AddMonths(month).AddDays(random.Next(28)),
                Status = random.Next(100) switch { < 87 => OrderStatus.Paid, < 94 => OrderStatus.Pending, _ => OrderStatus.Refunded },
                Total = total, Margin = Math.Round(total * (0.18m + random.Next(20) / 100m), 2),
                Region = regions[random.Next(regions.Count)], Customer = customers[random.Next(customers.Count)], Product = product,
            };
        }).ToList();
        db.AddRange(regions); db.AddRange(products); db.AddRange(customers); db.AddRange(orders);
        db.SaveChanges();
    }
}
