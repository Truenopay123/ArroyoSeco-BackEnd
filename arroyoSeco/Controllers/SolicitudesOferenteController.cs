using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Solicitudes;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SolicitudesOferenteController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly INotificationService _noti;
    private readonly UserManager<ApplicationUser> _userManager;
    
    public SolicitudesOferenteController(
        IAppDbContext db,
        INotificationService noti,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _noti = noti;
        _userManager = userManager;
    }

    // GET /api/solicitudesoferente?estatus=Pendiente
    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? estatus, CancellationToken ct)
    {
        var q = _db.SolicitudesOferente.AsQueryable();
        if (!string.IsNullOrWhiteSpace(estatus)) q = q.Where(s => s.Estatus == estatus);
        return Ok(await q.AsNoTracking().ToListAsync(ct));
    }

    // POST /api/solicitudesoferente
    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] SolicitudOferente s, CancellationToken ct)
    {
        s.Id = 0;
        s.FechaSolicitud = DateTime.UtcNow;
        _db.SolicitudesOferente.Add(s);
        await _db.SaveChangesAsync(ct);
        
        // Notificar a todos los admins
        var admins = await _userManager.GetUsersInRoleAsync("Admin");
        foreach (var admin in admins)
        {
            await _noti.PushAsync(
                admin.Id,
                "Nueva solicitud de oferente",
                $"Solicitud de {s.NombreSolicitante} ({s.NombreNegocio}) - {s.TipoSolicitado}",
                "SolicitudOferente",
                $"/admin/solicitudes/{s.Id}",
                ct);
        }
        
        return CreatedAtAction(nameof(GetById), new { id = s.Id }, s.Id);
    }

    // GET /api/solicitudesoferente/{id}
    [Authorize(Roles = "Admin")]
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var s = await _db.SolicitudesOferente.FindAsync(new object[] { id }, ct);
        return s is null ? NotFound() : Ok(s);
    }
    
    // GET /api/solicitudesoferente/pendientes/count
    [Authorize(Roles = "Admin")]
    [HttpGet("pendientes/count")]
    public async Task<IActionResult> CountPendientes(CancellationToken ct)
    {
        var count = await _db.SolicitudesOferente
            .Where(s => s.Estatus == "Pendiente")
            .CountAsync(ct);
        return Ok(new { count });
    }
}