using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B2B.Ordering.Api.Shared.Persistence.Configurations;

public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("Companies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
    }
}

public sealed class CompanyMembershipConfiguration : IEntityTypeConfiguration<CompanyMembership>
{
    public void Configure(EntityTypeBuilder<CompanyMembership> builder)
    {
        builder.ToTable("CompanyMemberships");
        builder.HasKey(x => new { x.CompanyId, x.UserId });
        builder.Property(x => x.Role).HasMaxLength(20).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        builder.HasOne(x => x.Company)
            .WithMany()
            .HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_CompanyMemberships_Role",
            $"[Role] IN ('{CompanyRoles.Buyer}', '{CompanyRoles.Viewer}')"));
    }
}
