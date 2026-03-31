using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Gastronomia;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ResenasGastronomiaController : ControllerBase
{
    private readonly IAppDbContext _db;

    public ResenasGastronomiaController(IAppDbContext db) => _db = db;

    public record CrearResenaGastronomiaDto(int ReservaGastronomiaId, int Calificacion, string Comentario);
    public record ReportarResenaGastronomiaDto(string Motivo);

    // ── Cliente: crear reseña (se publica automáticamente) ──────────────────

    [Authorize(Roles = "Cliente")]
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearResenaGastronomiaDto dto)
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (clienteId is null) return Unauthorized();

        if (dto.Calificacion < 1 || dto.Calificacion > 5)
            return BadRequest(new { message = "La calificación debe estar entre 1 y 5." });

        if (string.IsNullOrWhiteSpace(dto.Comentario) || dto.Comentario.Length < 10)
            return BadRequest(new { message = "El comentario debe tener al menos 10 caracteres." });

        var reserva = await _db.ReservasGastronomia
            .FirstOrDefaultAsync(r => r.Id == dto.ReservaGastronomiaId && r.UsuarioId == clienteId);

        if (reserva is null)
            return NotFound(new { message = "Reserva no encontrada." });

        var estaCompletada = reserva.Estado == "Completada"
            || (reserva.Estado == "Confirmada" && reserva.Fecha <= DateTime.UtcNow);

        if (!estaCompletada)
            return BadRequest(new { message = "Solo puedes reseñar reservas completadas." });

        var yaReseno = await _db.ResenasGastronomia.AnyAsync(r => r.ReservaGastronomiaId == dto.ReservaGastronomiaId);
        if (yaReseno)
            return Conflict(new { message = "Ya existe una reseña para esta reserva." });

        var resena = new ResenaGastronomia
        {
            EstablecimientoId = reserva.EstablecimientoId,
            ReservaGastronomiaId = reserva.Id,
            ClienteId = clienteId,
            Calificacion = dto.Calificacion,
            Comentario = dto.Comentario.Trim(),
            Estado = "publicada"
        };

        _db.ResenasGastronomia.Add(resena);
        await _db.SaveChangesAsync();

        return Created($"/api/resenasgastronomia/{resena.Id}", new { message = "Reseña publicada exitosamente.", id = resena.Id });
    }

    // ── Público: listar reseñas publicadas de un establecimiento ──────────

    [AllowAnonymous]
    [HttpGet("establecimiento/{establecimientoId}")]
    public async Task<IActionResult> PorEstablecimiento(int establecimientoId)
    {
        var resenas = await _db.ResenasGastronomia
            .Where(r => r.EstablecimientoId == establecimientoId && r.Estado == "publicada")
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

    // ── Oferente: ver reseñas de sus establecimientos (incluyendo reportadas) ───

    [Authorize(Roles = "Oferente")]
    [HttpGet("mis-establecimientos")]
    public async Task<IActionResult> ResenasMisEstablecimientos()
    {
        var oferenteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var resenas = await _db.ResenasGastronomia
            .Include(r => r.Establecimiento)
            .Where(r => r.Establecimiento!.OferenteId == oferenteId && r.Estado != "eliminada")
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.EstablecimientoId,
                establecimientoNombre = r.Establecimiento!.Nombre,
                r.ReservaGastronomiaId,
                r.ClienteId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.MotivoReporte,
                r.FechaReporte,
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

        var resenas = await _db.ResenasGastronomia
            .Include(r => r.Establecimiento)
            .Where(r => r.ClienteId == clienteId && r.Estado != "eliminada")
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.EstablecimientoId,
                establecimientoNombre = r.Establecimiento!.Nombre,
                r.ReservaGastronomiaId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.FechaCreacion
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Admin: listar todas las reseñas (excepto eliminadas) ──────────────

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> ListarAll([FromQuery] string? estado)
    {
        var query = _db.ResenasGastronomia
            .Include(r => r.Establecimiento)
            .Where(r => r.Estado != "eliminada")
            .AsQueryable();

        if (!string.IsNullOrEmpty(estado))
            query = query.Where(r => r.Estado == estado);

        var resenas = await query
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.EstablecimientoId,
                establecimientoNombre = r.Establecimiento!.Nombre,
                r.ReservaGastronomiaId,
                r.ClienteId,
                r.Calificacion,
                r.Comentario,
                r.Estado,
                r.MotivoReporte,
                r.FechaReporte,
                r.FechaCreacion
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Oferente: reportar una reseña injusta o falsa ──────────────────────

    [Authorize(Roles = "Oferente")]
    [HttpPost("{id}/reportar")]
    public async Task<IActionResult> ReportarResena(int id, [FromBody] ReportarResenaGastronomiaDto dto)
    {
        var oferenteId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (oferenteId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.Motivo) || dto.Motivo.Length < 10)
            return BadRequest(new { message = "El motivo debe tener al menos 10 caracteres." });

        var resena = await _db.ResenasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        if (resena.Establecimiento?.OferenteId != oferenteId)
            return StatusCode(403, new { message = "No puedes reportar reseñas de otros oferentes." });

        if (resena.Estado == "eliminada")
            return BadRequest(new { message = "No puedes reportar una reseña que ya fue eliminada." });

        if (resena.Estado == "reportada")
            return BadRequest(new { message = "Esta reseña ya fue reportada anteriormente." });

        resena.Estado = "reportada";
        resena.MotivoReporte = dto.Motivo.Trim();
        resena.FechaReporte = DateTime.UtcNow;
        resena.OfferenteIdQueReporto = oferenteId;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Reseña reportada exitosamente. El Admin la revisará próximamente.", id = resena.Id });
    }

    // ── Admin: listar todas las reseñas reportadas ────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpGet("reportadas")]
    public async Task<IActionResult> ResenasReportadas()
    {
        var resenas = await _db.ResenasGastronomia
            .Include(r => r.Establecimiento)
            .Where(r => r.Estado == "reportada")
            .OrderByDescending(r => r.FechaReporte)
            .Select(r => new
            {
                r.Id,
                r.EstablecimientoId,
                establecimientoNombre = r.Establecimiento!.Nombre,
                r.ClienteId,
                r.Calificacion,
                r.Comentario,
                r.MotivoReporte,
                r.OfferenteIdQueReporto,
                r.FechaCreacion,
                r.FechaReporte
            })
            .ToListAsync();

        return Ok(resenas);
    }

    // ── Admin: eliminar una reseña reportada ──────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> EliminarResena(int id)
    {
        var resena = await _db.ResenasGastronomia.FindAsync(id);
        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        if (resena.Estado != "reportada")
            return BadRequest(new { message = "Solo puedes eliminar reseñas que están reportadas." });

        resena.Estado = "eliminada";
        await _db.SaveChangesAsync();

        return Ok(new { message = "Reseña eliminada correctamente." });
    }

    // ── Admin: desestimar un reporte (ignorar y dejar visible) ────────────

    [Authorize(Roles = "Admin")]
    [HttpPatch("{id}/desestimar-reporte")]
    public async Task<IActionResult> DestimarReporte(int id)
    {
        var resena = await _db.ResenasGastronomia.FindAsync(id);
        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        if (resena.Estado != "reportada")
            return BadRequest(new { message = "Solo puedes desestimar reportes de reseñas reportadas." });

        resena.Estado = "publicada";
        resena.MotivoReporte = null;
        resena.FechaReporte = null;
        resena.OfferenteIdQueReporto = null;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Reporte desestimado. La reseña vuelve a su estado publicado." });
    }

    // ── Cliente: reservas pendientes de reseña ───────────────────────────

    [Authorize(Roles = "Cliente")]
    [HttpGet("pendientes-de-resena")]
    public async Task<IActionResult> PendientesDeResena()
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var now = DateTime.UtcNow;
        var reservasCompletadas = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .Where(r => r.UsuarioId == clienteId
                && (r.Estado == "Completada"
                    || (r.Estado == "Confirmada" && r.Fecha <= now)))
            .ToListAsync();

        var reservasConResena = await _db.ResenasGastronomia
            .Where(r => r.ClienteId == clienteId)
            .Select(r => r.ReservaGastronomiaId)
            .ToListAsync();

        var pendientes = reservasCompletadas
            .Where(r => !reservasConResena.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Folio,
                r.EstablecimientoId,
                establecimientoNombre = r.Establecimiento?.Nombre,
                r.Fecha,
                r.NumeroPersonas
            });

        return Ok(pendientes);
    }
}
