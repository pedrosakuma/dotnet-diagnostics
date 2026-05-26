using Microsoft.EntityFrameworkCore;

sealed class BadCodeDbContext(DbContextOptions<BadCodeDbContext> options) : DbContext(options)
{
    public DbSet<BadCodeWidget> Widgets => Set<BadCodeWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BadCodeWidget>(entity =>
        {
            entity.ToTable("widgets");
            entity.HasKey(widget => widget.Id);
            entity.Property(widget => widget.Name).HasColumnName("name");
        });
    }
}

sealed class BadCodeWidget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
