using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Domain.Entities.Notificaciones;
using arroyoSeco.Domain.Entities.Solicitudes;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Domain.Entities.Resenas;
using arroyoSeco.Domain.Entities.Pagos;
using arroyoSeco.Application.Common.Interfaces;
using System.Text.Json;
// Alias para desambiguar el Oferente correcto (de Usuarios)
using UsuarioOferente = arroyoSeco.Domain.Entities.Usuarios.Oferente;

namespace arroyoSeco.Infrastructure.Data;

public class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UsuarioOferente> Oferentes => Set<UsuarioOferente>();
    public DbSet<Alojamiento> Alojamientos => Set<Alojamiento>();
    public DbSet<FotoAlojamiento> FotosAlojamiento => Set<FotoAlojamiento>();
    public DbSet<Reserva> Reservas => Set<Reserva>();
    public DbSet<Establecimiento> Establecimientos => Set<Establecimiento>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Mesa> Mesas => Set<Mesa>();
    public DbSet<ReservaGastronomia> ReservasGastronomia => Set<ReservaGastronomia>();
    public DbSet<Notificacion> Notificaciones => Set<Notificacion>();
    public DbSet<SolicitudOferente> SolicitudesOferente => Set<SolicitudOferente>();
    public DbSet<Resena> Resenas => Set<Resena>();
    public DbSet<Pago> Pagos => Set<Pago>();

    public new Task<int> SaveChangesAsync(CancellationToken ct = default) => base.SaveChangesAsync(ct);

    protected override void OnModelCreating(ModelBuilder b)
    {
        var amenidadesComparer = new ValueComparer<List<string>>(
            (c1, c2) => (c1 ?? new List<string>()).SequenceEqual(c2 ?? new List<string>()),
            c => c == null ? 0 : c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c == null ? new List<string>() : c.ToList());

        b.Entity<Alojamiento>()
            .Property(a => a.Amenidades)
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<string>(), (JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(amenidadesComparer);

        b.Entity<Alojamiento>()
            .HasMany(a => a.Fotos)
            .WithOne(f => f.Alojamiento)
            .HasForeignKey(f => f.AlojamientoId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Alojamiento>()
            .HasMany(a => a.Reservas)
            .WithOne(r => r.Alojamiento)
            .HasForeignKey(r => r.AlojamientoId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Alojamiento>()
            .HasOne(a => a.Oferente)
            .WithMany(o => o.Alojamientos)
            .HasForeignKey(a => a.OferenteId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Establecimiento>()
            .HasOne(e => e.Oferente)
            .WithMany(o => o.Establecimientos)
            .HasForeignKey(e => e.OferenteId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Establecimiento>()
            .HasMany(e => e.Menus)
            .WithOne(m => m.Establecimiento)
            .HasForeignKey(m => m.EstablecimientoId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Establecimiento>()
            .HasMany(e => e.Mesas)
            .WithOne(ms => ms.Establecimiento)
            .HasForeignKey(ms => ms.EstablecimientoId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Establecimiento>()
            .HasMany(e => e.Reservas)
            .WithOne(r => r.Establecimiento)
            .HasForeignKey(r => r.EstablecimientoId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Menu>()
            .HasMany(m => m.Items)
            .WithOne(i => i.Menu)
            .HasForeignKey(i => i.MenuId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Reserva>(e =>
        {
            e.HasIndex(r => r.Folio).IsUnique();
            e.Property(r => r.Total).HasColumnType("numeric(18,2)").HasDefaultValue(0);
            e.Property(r => r.ComprobanteUrl).HasMaxLength(500);
        });

        b.Entity<ReservaGastronomia>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.UsuarioId).IsRequired();
            e.Property(r => r.EstablecimientoId).IsRequired();
            e.Property(r => r.Fecha).IsRequired();
            e.Property(r => r.Estado).IsRequired().HasDefaultValue("Pendiente");
            e.Property(r => r.NumeroPersonas).IsRequired();
            e.Property(r => r.Total).HasColumnType("numeric(18,2)").HasDefaultValue(0);

            e.HasOne(r => r.Establecimiento)
                .WithMany(est => est.Reservas)
                .HasForeignKey(r => r.EstablecimientoId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Mesa)
                .WithMany()
                .HasForeignKey(r => r.MesaId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Notificacion>().HasIndex(n => n.UsuarioId);
        b.Entity<SolicitudOferente>().HasIndex(s => s.Estatus);

        // Reseñas
        b.Entity<Resena>(e =>
        {
            e.HasIndex(r => r.ReservaId).IsUnique(); // una reseña por reserva
            e.HasIndex(r => r.AlojamientoId);
            e.Property(r => r.Estado).HasDefaultValue("Pendiente");
            e.Property(r => r.FechaCreacion).HasDefaultValueSql("now()");
            e.Property(r => r.Comentario).HasMaxLength(2000);

            e.HasOne(r => r.Alojamiento)
                .WithMany()
                .HasForeignKey(r => r.AlojamientoId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Reserva)
                .WithMany()
                .HasForeignKey(r => r.ReservaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Pagos
        b.Entity<Pago>(e =>
        {
            e.Property(p => p.Monto).HasColumnType("numeric(18,2)").HasDefaultValue(0);
            e.Property(p => p.Estado).HasDefaultValue("Pendiente");
            e.Property(p => p.FechaCreacion).HasDefaultValueSql("now()");

            e.HasOne(p => p.Reserva)
                .WithMany()
                .HasForeignKey(p => p.ReservaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(b);
    }
}
