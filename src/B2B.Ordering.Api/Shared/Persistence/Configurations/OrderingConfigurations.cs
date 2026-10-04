using B2B.Ordering.Api.Modules.Access.Data;
using B2B.Ordering.Api.Modules.Ordering.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B2B.Ordering.Api.Shared.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Sku).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ListPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.HasIndex(x => x.Sku).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_Products_ListPrice", "[ListPrice] >= 0"));
    }
}

public sealed class CompanyProductPriceConfiguration : IEntityTypeConfiguration<CompanyProductPrice>
{
    public void Configure(EntityTypeBuilder<CompanyProductPrice> builder)
    {
        builder.ToTable("CompanyProductPrices");
        builder.HasKey(x => new { x.CompanyId, x.ProductId });
        builder.Property(x => x.UnitPrice).HasPrecision(18, 2).IsRequired();

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // A negotiated price for a company that does not exist is not a tenant leak the write
        // guard can see: it only compares against the caller's company. The database has to say no.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_CompanyProductPrices_UnitPrice", "[UnitPrice] >= 0"));
    }
}

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TotalAmount).HasPrecision(18, 2).IsRequired();
        // SQL Server datetime2 has no time zone, so a value read back would have Kind
        // Unspecified and serialise without the trailing Z. The converter keeps the stored
        // response and a later GET byte-identical.
        builder.Property(x => x.CreatedAtUtc)
            .IsRequired()
            .HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        // Final (V3) mapping. The HTTP field has been called externalReference from day one; only
        // the column moved, across S1 (CustomerReference), S2 (both) and S3 (ExternalReference).
        builder.Property(x => x.ExternalReference)
            .HasMaxLength(80);

        // Alternate key targeted by OrderLine's composite FK, so the database itself refuses a
        // line that points at another company's order.
        builder.HasAlternateKey(x => new { x.CompanyId, x.Id });

        // Ownership is a real relationship, not just a column the application fills in. The tenant
        // write guard checks that the company matches the caller; only these constraints can rule
        // out an order owned by a company or created by a user that does not exist at all.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CompanyId, x.CreatedAtUtc, x.Id });
    }
}

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.UnitPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.LineTotal).HasPrecision(18, 2).IsRequired();

        builder.HasOne(x => x.Order)
            .WithMany(x => x.Lines)
            .HasForeignKey(x => new { x.CompanyId, x.OrderId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line per position within an order. It also serves the composite foreign key, whose
        // columns lead this index.
        builder.HasIndex(x => new { x.CompanyId, x.OrderId, x.LineNumber })
            .IsUnique();

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OrderLines_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_OrderLines_LineNumber", "[LineNumber] > 0");
        });
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(x => x.Id);

        // Binary collation: keys are compared case sensitively, so "abc" and "ABC" are two keys.
        builder.Property(x => x.Key)
            .HasColumnType("varchar(128)")
            .UseCollation("Latin1_General_BIN2")
            .IsRequired();

        builder.Property(x => x.RequestHash)
            .HasColumnType("char(64)")
            .IsRequired();

        builder.Property(x => x.ResponseBody).HasColumnType("nvarchar(max)");
        builder.Property(x => x.Location).HasMaxLength(200);
        // SQL Server datetime2 has no time zone, so a value read back would have Kind
        // Unspecified and serialise without the trailing Z. The converter keeps the stored
        // response and a later GET byte-identical.
        builder.Property(x => x.CreatedAtUtc)
            .IsRequired()
            .HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        // The unique key of a record is (CompanyId, UserId, Key), so both of those must name rows
        // that actually exist, or a replay could be scoped to nothing.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CompanyId, x.UserId, x.Key })
            .IsUnique()
            .HasDatabaseName("UX_IdempotencyRecords_Company_User_Key");
    }
}
