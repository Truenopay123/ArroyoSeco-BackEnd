using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Domain.Entities.Notificaciones;
using arroyoSeco.Domain.Entities.Solicitudes;
// Alias para desambiguar 'Oferente'
using UsuarioOferente = arroyoSeco.Domain.Entities.Usuarios.Oferente;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Domain.Entities.Resenas;
using arroyoSeco.Domain.Entities.Pagos;

namespace arroyoSeco.Application.Common.Interfaces;

public interface IAppDbContext
{
    DbSet<UsuarioOferente> Oferentes { get; }
    DbSet<Alojamiento> Alojamientos { get; }
    DbSet<FotoAlojamiento> FotosAlojamiento { get; }
    DbSet<Reserva> Reservas { get; }
    DbSet<Notificacion> Notificaciones { get; }
    DbSet<SolicitudOferente> SolicitudesOferente { get; }
    DbSet<Establecimiento> Establecimientos { get; }
    DbSet<Menu> Menus { get; }
    DbSet<MenuItem> MenuItems { get; }
    DbSet<Mesa> Mesas { get; }
    DbSet<ReservaGastronomia> ReservasGastronomia { get; }
    DbSet<FotoEstablecimiento> FotosEstablecimiento { get; }
    DbSet<ResenaGastronomia> ResenasGastronomia { get; }
    DbSet<PagoGastronomia> PagosGastronomia { get; }
    DbSet<Resena> Resenas { get; }
    DbSet<Pago> Pagos { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
