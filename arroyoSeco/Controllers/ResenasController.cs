using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Resenas;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ResenasController : ControllerBase
{
    private readonly IAppDbContext _db;

    public ResenasController(IAppDbContext db) => _db = db;

    public record CrearResenaDto(int ReservaId, int Calificacion, string Comentario);
    public record ModerarResenaDto(string Estado); // Aprobada | Rechazada

    // ── Cliente: crear reseña ─────────────────────────────────────────────

    [Authorize(Roles = "Cliente")]
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearResenaDto dto)
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (clienteId is null) return Unauthorized();

        if (dto.Calificacion < 1 || dto.Calificacion > 5)
            return BadRequest(new { message = "La calificación debe estar entre 1 y 5." });

        if (string.IsNullOrWhiteSpace(dto.Comentario) || dto.Comentario.Length < 10)
            return BadRequest(new { message = "El comentario debe tener al menos 10 caracteres." });

        // La reserva debe existir, pertenecer al cliente y estar Completada
        var reserva = await _db.Reservas
            .FirstOrDefaultAsync(r => r.Id == dto.ReservaId && r.ClienteId == clienteId);

        if (reserva is null)
            return NotFound(new { message = "Reserva no encontrada." });

        if (reserva.Estado != "Completada")
            return BadRequest(new { message = "Solo puedes reseñar reservas completadas." });

        // Evitar duplicados
        var yaReseno = await _db.Resenas.AnyAsync(r => r.ReservaId == dto.ReservaId);
        if (yaReseno)
            return Conflict(new { message = "Ya existe una reseña para esta reserva." });

        var resena = new Resena
        {
            AlojamientoId = reserva.AlojamientoId,
            ReservaId     = reserva.Id,
            ClienteId     = clienteId,
            Calificacion  = dto.Calificacion,
            Comentario    = dto.Comentario.Trim(),
            Estado        = "Pendiente"
        };

        _db.Resenas.Add(resena);
        await _db.SaveChangesAsync();

        return Created($"/api/resenas/{resena.Id}", new { message = "Reseña enviada. Será revisada antes de publicarse.", id = resena.Id });
    }

    // ── Público: listar reseñas aprobadas de un alojamiento ──────────────

    [AllowAnonymous]
    [HttpGet("alojamiento/{alojamientoId}")]
    public async Task<IActionResult> PorAlojamiento(int alojamientoId)
    {
        var resenas = await _db.Resenas
            .Where(r => r.AlojamientoId == alojamientoId && r.Estado == "Aprobada")
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.Calificacion,
                r.Comentario,
                r.FechaCreacion,
                r.ClienteId
            })
            .ToListAsync();

        var promedio = resenas.Count > 0
            ? Math.Round(resenas.Average(r => (double)r.Calificacion), 1)
            : 0.0;

        return Ok(new { promedio, total = resenas.Count, resenas });
    }

    // ── Oferente: ver reseñas de su alojamiento ───────────────────────────

    [Authorize(Roles = "Oferente")]
    [HttpGet("mis-alojamientos")]
    public async Task<IActionResult> ResenasMisAlojamientos()
    {
        var oferenteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var resenas = await _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.Alojamiento!.OferenteId == oferenteId)
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.AlojamientoId,
                alojamientoNombre = r.Alojamiento!.Nombre,
                r.ReservaId,
                r.ClienteId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.FechaCreacion
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Cliente: ver mis reseñas ──────────────────────────────────────────

    [Authorize(Roles = "Cliente")]
    [HttpGet("mias")]
    public async Task<IActionResult> MisResenas()
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var resenas = await _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.ClienteId == clienteId)
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.AlojamientoId,
                alojamientoNombre = r.Alojamiento!.Nombre,
                r.ReservaId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.FechaCreacion
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Admin: listar todas las reseñas ───────────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> ListarAll([FromQuery] string? estado)
    {
        var query = _db.Resenas.Include(r => r.Alojamiento).AsQueryable();

        if (!string.IsNullOrEmpty(estado))
            query = query.Where(r => r.Estado == estado);

        var resenas = await query
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.AlojamientoId,
                alojamientoNombre = r.Alojamiento!.Nombre,
                r.ReservaId,
                r.ClienteId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.FechaCreacion
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Admin: moderar reseña ─────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpPatch("{id}/moderar")]
    public async Task<IActionResult> Moderar(int id, [FromBody] ModerarResenaDto dto)
    {
        var estados = new[] { "Aprobada", "Rechazada" };
        if (!estados.Contains(dto.Estado))
            return BadRequest(new { message = "Estado inválido. Usa 'Aprobada' o 'Rechazada'." });

        var resena = await _db.Resenas.FindAsync(id);
        if (resena is null) return NotFound();

        resena.Estado = dto.Estado;
        await _db.SaveChangesAsync();

        return Ok(new { message = $"Reseña {dto.Estado.ToLower()} correctamente." });
    }

    // ── Admin: eliminar reseña ────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var resena = await _db.Resenas.FindAsync(id);
        if (resena is null) return NotFound();

        _db.Resenas.Remove(resena);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Admin: conocer reservas que puedan ser reseñadas ─────────────────

    [Authorize(Roles = "Cliente")]
    [HttpGet("pendientes-de-resena")]
    public async Task<IActionResult> PendientesDeResena()
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var reservasCompletadas = await _db.Reservas
            .Include(r => r.Alojamiento)
            .Where(r => r.ClienteId == clienteId && r.Estado == "Completada")
            .ToListAsync();

        var reservasConResena = await _db.Resenas
            .Where(r => r.ClienteId == clienteId)
            .Select(r => r.ReservaId)
            .ToListAsync();

        var pendientes = reservasCompletadas
            .Where(r => !reservasConResena.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Folio,
                r.AlojamientoId,
                alojamientoNombre = r.Alojamiento?.Nombre,
                r.FechaEntrada,
                r.FechaSalida
            });

        return Ok(pendientes);
    }
}
