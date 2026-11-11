using Microsoft.EntityFrameworkCore;

namespace Pragma.Accounts.Infrastructure.Persistence;

/// <summary>Entidad de persistencia: no se expone fuera del adapter.</summary>
internal sealed class AccountEntity
{
    public string Id { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string AccountType { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<AccountEntity>();
        account.ToTable("accounts");
        account.HasKey(a => a.Id);
        account.Property(a => a.Id).HasColumnName("id").HasMaxLength(64);
        account.Property(a => a.HolderName).HasColumnName("holder_name").HasMaxLength(120).IsRequired();
        account.Property(a => a.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
        account.Property(a => a.AccountType).HasColumnName("account_type").HasMaxLength(30).IsRequired();
        account.Property(a => a.Balance).HasColumnName("balance").HasPrecision(19, 4);
        account.Property(a => a.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        account.Property(a => a.CreatedAt).HasColumnName("created_at");
        account.HasIndex(a => a.Status);
    }
}
