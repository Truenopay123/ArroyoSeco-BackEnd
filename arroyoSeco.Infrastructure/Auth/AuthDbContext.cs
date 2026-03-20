using arroyoSeco.Domain.Entities.Usuarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace arroyoSeco.Infrastructure.Auth;

// DbContext para ASP.NET Core Identity (usuarios/roles)
public class AuthDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Mapear FaceDescriptor como columna JSONB en PostgreSQL
        builder.Entity<ApplicationUser>()
            .Property(u => u.FaceDescriptor)
            .HasColumnType("jsonb");
    }
}