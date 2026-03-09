using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Application.Common.Interfaces;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/admin/dashboard")]
[Authorize(Roles = "Admin")]
public class AdminDashboardController : ControllerBase
{
    private readonly IAppDbContext _db;

    public AdminDashboardController(IAppDbContext db)
    {
        _db = db;
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> GetResumen(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);

        var oferentesActivos = await _db.Oferentes
            .AsNoTracking()
            .CountAsync(o => o.Estado == "Activo", ct);

        var reservasAlojamientoMes = await _db.Reservas
            .AsNoTracking()
            .Where(r => r.FechaReserva >= monthStart && r.FechaReserva < monthEnd)
            .ToListAsync(ct);

        var reservasGastronomiaMes = await _db.ReservasGastronomia
            .AsNoTracking()
            .Where(r => r.Fecha >= monthStart && r.Fecha < monthEnd)
            .ToListAsync(ct);

        var reservasMes = reservasAlojamientoMes.Count + reservasGastronomiaMes.Count;

        var ingresosAlojamiento = reservasAlojamientoMes
            .Where(r => string.Equals(r.Estado, "Confirmada", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Estado, "Completada", StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.Total);

        var ingresosGastronomia = reservasGastronomiaMes
            .Where(r => string.Equals(r.Estado, "Confirmada", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Estado, "Completada", StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.Total);

        var ingresosMes = ingresosAlojamiento + ingresosGastronomia;

        var solicitudesPendientes = await _db.SolicitudesOferente
            .AsNoTracking()
            .CountAsync(s => s.Estatus == "Pendiente", ct);

        var ultimasSolicitudes = await _db.SolicitudesOferente
            .AsNoTracking()
            .OrderByDescending(s => s.FechaSolicitud)
            .Take(5)
            .Select(s => new
            {
                date = s.FechaSolicitud,
                text = $"Solicitud recibida: {s.NombreNegocio}",
                type = "solicitud"
            })
            .ToListAsync(ct);

        var ultimasReservasAlojamiento = await _db.Reservas
            .AsNoTracking()
            .Include(r => r.Alojamiento)
            .OrderByDescending(r => r.FechaReserva)
            .Take(5)
            .Select(r => new
            {
                date = r.FechaReserva,
                text = $"Reserva {r.Estado}: {r.Alojamiento.Nombre} ({r.Folio})",
                type = "reserva"
            })
            .ToListAsync(ct);

        var ultimasReservasGastronomia = await _db.ReservasGastronomia
            .AsNoTracking()
            .Include(r => r.Establecimiento)
            .OrderByDescending(r => r.Fecha)
            .Take(5)
            .Select(r => new
            {
                date = r.Fecha,
                text = $"Reserva gastronomía {r.Estado}: {(r.Establecimiento != null ? r.Establecimiento.Nombre : "Establecimiento")}",
                type = "reserva"
            })
            .ToListAsync(ct);

        var recentActivity = ultimasSolicitudes
            .Concat(ultimasReservasAlojamiento)
            .Concat(ultimasReservasGastronomia)
            .OrderByDescending(x => x.date)
            .Take(8)
            .ToList();

        return Ok(new
        {
            oferentesActivos,
            reservasMes,
            ingresosMes,
            solicitudesPendientes,
            recentActivity
        });
    }
}
