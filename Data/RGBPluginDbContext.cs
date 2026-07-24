using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BTCPayServer.Plugins.RgbUtexo.Data;

public class RGBPluginDbContext : DbContext
{
    public RGBPluginDbContext(DbContextOptions<RGBPluginDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public DbSet<RGBWallet> RGBWallets { get; set; } = null!;
    public DbSet<RGBInvoice> RGBInvoices { get; set; } = null!;
    public DbSet<RGBAsset> RGBAssets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RGBWallet>(entity =>
        {
            entity.ToTable("RGB_Wallets");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.StoreId).IsUnique().HasFilter("\"IsActive\" = true");
            entity.Property(e => e.XpubVanilla).IsRequired();
            entity.Property(e => e.XpubColored).IsRequired();
            entity.Property(e => e.MasterFingerprint).IsRequired();
            entity.Property(e => e.MaxAllocationsPerUtxo).HasDefaultValue(10);
            entity.Property(e => e.NeedsRecovery).HasDefaultValue(false);
        });

        modelBuilder.Entity<RGBInvoice>(entity =>
        {
            entity.ToTable("RGB_Invoices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.RecipientId);
            entity.HasIndex(e => e.BtcPayInvoiceId);
            entity.HasIndex(e => e.Status);
            
            entity.HasOne(e => e.Wallet)
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RGBAsset>(entity =>
        {
            entity.ToTable("RGB_Assets");
            entity.HasKey(e => new { e.WalletId, e.AssetId });
            entity.HasIndex(e => e.WalletId);
            
            entity.HasOne(e => e.Wallet)
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}


