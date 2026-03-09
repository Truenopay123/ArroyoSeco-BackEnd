using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Infrastructure.Auth;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class EstadisticasController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly AuthDbContext _authDb;

    public EstadisticasController(IAppDbContext db, AuthDbContext authDb)
    {
        _db = db;
        _authDb = authDb;
    }

    // ── Resumen general (dashboard) ───────────────────────────────────────

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var totalVisitantes = await _authDb.Users.CountAsync(u =>
            _authDb.UserRoles.Any(ur => ur.UserId == u.Id &&
                _authDb.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Cliente")));

        var totalReservas  = await _db.Reservas.CountAsync();
        var reservasActivas = await _db.Reservas.CountAsync(r => r.Estado == "Confirmada");
        var ingresoTotal   = await _db.Reservas
            .Where(r => r.Estado == "Confirmada" || r.Estado == "Completada")
            .SumAsync(r => (decimal?)r.Total) ?? 0;

        var totalResenas   = await _db.Resenas.CountAsync(r => r.Estado == "Aprobada");
        var promedioGlobal = totalResenas > 0
            ? await _db.Resenas.Where(r => r.Estado == "Aprobada").AverageAsync(r => (double)r.Calificacion)
            : 0;

        return Ok(new
        {
            totalVisitantes,
            totalReservas,
            reservasActivas,
            ingresoTotal,
            totalResenas,
            promedioGlobal = Math.Round(promedioGlobal, 1)
        });
    }

    // ── Distribución por sexo ─────────────────────────────────────────────

    [HttpGet("por-sexo")]
    public async Task<IActionResult> PorSexo()
    {
        var clientes = await _authDb.Users
            .Where(u => _authDb.UserRoles.Any(ur => ur.UserId == u.Id &&
                _authDb.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Cliente")))
            .Select(u => new { u.Sexo })
            .ToListAsync();

        var distribucion = clientes
            .GroupBy(u => string.IsNullOrEmpty(u.Sexo) ? "No especificado" : u.Sexo)
            .Select(g => new { categoria = g.Key, cantidad = g.Count() })
            .OrderByDescending(x => x.cantidad)
            .ToList();

        return Ok(distribucion);
    }

    // ── Distribución por grupo etario ─────────────────────────────────────

    [HttpGet("por-edad")]
    public async Task<IActionResult> PorEdad()
    {
        var hoy = DateTime.UtcNow;

        var clientes = await _authDb.Users
            .Where(u => _authDb.UserRoles.Any(ur => ur.UserId == u.Id &&
                _authDb.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Cliente"))
                && u.FechaNacimiento != null)
            .Select(u => new { u.FechaNacimiento })
            .ToListAsync();

        var grupos = clientes
            .Select(u =>
            {
                var edad = (int)((hoy - u.FechaNacimiento!.Value).TotalDays / 365.25);
                return edad switch
                {
                    < 18 => "Menor de 18",
                    < 26 => "18–25",
                    < 36 => "26–35",
                    < 46 => "36–45",
                    < 60 => "46–59",
                    _    => "60 o más"
                };
            })
            .GroupBy(g => g)
            .Select(g => new { categoria = g.Key, cantidad = g.Count() })
            .OrderBy(x => x.categoria)
            .ToList();

        return Ok(grupos);
    }

    // ── Distribución por lugar de origen ─────────────────────────────────

    [HttpGet("por-origen")]
    public async Task<IActionResult> PorOrigen()
    {
        var clientes = await _authDb.Users
            .Where(u => _authDb.UserRoles.Any(ur => ur.UserId == u.Id &&
                _authDb.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Cliente"))
                && u.LugarOrigen != null)
            .GroupBy(u => u.LugarOrigen!)
            .Select(g => new { ciudad = g.Key, cantidad = g.Count() })
            .OrderByDescending(x => x.cantidad)
            .Take(15) // Top 15 orígenes
            .ToListAsync();

        return Ok(clientes);
    }

    // ── Tendencia de reservas por mes ─────────────────────────────────────

    [HttpGet("reservas-por-mes")]
    public async Task<IActionResult> ReservasPorMes([FromQuery] int anio = 0)
    {
        if (anio == 0) anio = DateTime.UtcNow.Year;

        var reservas = await _db.Reservas
            .Where(r => r.FechaReserva.Year == anio)
            .ToListAsync();

        var porMes = reservas
            .GroupBy(r => r.FechaReserva.Month)
            .Select(g => new
            {
                mes = g.Key,
                mesNombre = new System.Globalization.CultureInfo("es-AR").DateTimeFormat.GetMonthName(g.Key),
                cantidad = g.Count(),
                ingresos = g.Sum(r => r.Total)
            })
            .OrderBy(x => x.mes)
            .ToList();

        return Ok(porMes);
    }

    // ── Rating promedio por alojamiento ──────────────────────────────────

    [HttpGet("rating-alojamientos")]
    public async Task<IActionResult> RatingAlojamientos()
    {
        var ratings = await _db.Resenas
            .Where(r => r.Estado == "Aprobada")
            .Include(r => r.Alojamiento)
            .GroupBy(r => new { r.AlojamientoId, r.Alojamiento!.Nombre })
            .Select(g => new
            {
                alojamientoId = g.Key.AlojamientoId,
                nombre        = g.Key.Nombre,
                promedio      = Math.Round(g.Average(r => (double)r.Calificacion), 1),
                totalResenas  = g.Count()
            })
            .OrderByDescending(x => x.promedio)
            .ToListAsync();

        return Ok(ratings);
    }

    // ── Visitantes nuevos por mes ─────────────────────────────────────────

    [HttpGet("visitantes-por-mes")]
    public async Task<IActionResult> VisitantesPorMes([FromQuery] int anio = 0)
    {
        if (anio == 0) anio = DateTime.UtcNow.Year;

        var clientes = await _authDb.Users
            .Where(u => _authDb.UserRoles.Any(ur => ur.UserId == u.Id &&
                _authDb.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Cliente"))
                && u.FechaPrimerLogin != null
                && u.FechaPrimerLogin.Value.Year == anio)
            .ToListAsync();

        var porMes = clientes
            .GroupBy(u => u.FechaPrimerLogin!.Value.Month)
            .Select(g => new
            {
                mes = g.Key,
                mesNombre = new System.Globalization.CultureInfo("es-AR").DateTimeFormat.GetMonthName(g.Key),
                cantidad = g.Count()
            })
            .OrderBy(x => x.mes)
            .ToList();

        return Ok(porMes);
    }
}
