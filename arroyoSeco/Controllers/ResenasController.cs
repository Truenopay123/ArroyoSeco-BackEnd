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
    public record ReportarResenaDto(string Motivo); // Motivo del reporte

    // ── Cliente: crear reseña (se publica automáticamente) ──────────────────

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

        // Considerar completada si: Estado == "Completada" O si está confirmada y la fecha de salida ya pasó
        var estaCompletada = reserva.Estado == "Completada"
            || (reserva.Estado == "Confirmada" && reserva.FechaSalida <= DateTime.UtcNow);

        if (!estaCompletada)
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
            Estado        = "publicada"  // ← Publicada automáticamente, sin aprobación previa
        };

        _db.Resenas.Add(resena);
        await _db.SaveChangesAsync();

        return Created($"/api/resenas/{resena.Id}", new { message = "Reseña publicada exitosamente.", id = resena.Id });
    }

    // ── Público: listar reseñas publicadas de un alojamiento ──────────────

    [AllowAnonymous]
    [HttpGet("alojamiento/{alojamientoId}")]
    public async Task<IActionResult> PorAlojamiento(int alojamientoId)
    {
        var resenas = await _db.Resenas
            .Where(r => r.AlojamientoId == alojamientoId && r.Estado == "publicada")
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

    // ── Oferente: ver reseñas de su alojamiento (incluyendo reportadas) ───

    [Authorize(Roles = "Oferente")]
    [HttpGet("mis-alojamientos")]
    public async Task<IActionResult> ResenasMisAlojamientos()
    {
        var oferenteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var resenas = await _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.Alojamiento!.OferenteId == oferenteId && r.Estado != "eliminada")
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

        var resenas = await _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.ClienteId == clienteId && r.Estado != "eliminada")
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

    // ── Admin: listar todas las reseñas (excepto eliminadas) ──────────────

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> ListarAll([FromQuery] string? estado)
    {
        var query = _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.Estado != "eliminada")
            .AsQueryable();

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
    public async Task<IActionResult> ReportarResena(int id, [FromBody] ReportarResenaDto dto)
    {
        var oferenteId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (oferenteId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.Motivo) || dto.Motivo.Length < 10)
            return BadRequest(new { message = "El motivo debe tener al menos 10 caracteres." });

        var resena = await _db.Resenas
            .Include(r => r.Alojamiento)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        // Verificar que la reseña pertenece a un alojamiento del Oferente
        if (resena.Alojamiento?.OferenteId != oferenteId)
            return StatusCode(403, new { message = "No puedes reportar reseñas de otros oferentes." });

        if (resena.Estado == "eliminada")
            return BadRequest(new { message = "No puedes reportar una reseña que ya fue eliminada." });

        if (resena.Estado == "reportada")
            return BadRequest(new { message = "Esta reseña ya fue reportada anteriormente." });

        // Cambiar estado a reportada
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
        var resenas = await _db.Resenas
            .Include(r => r.Alojamiento)
            .Where(r => r.Estado == "reportada")
            .OrderByDescending(r => r.FechaReporte)
            .Select(r => new
            {
                r.Id,
                r.AlojamientoId,
                alojamientoNombre = r.Alojamiento!.Nombre,
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
        var resena = await _db.Resenas.FindAsync(id);
        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        if (resena.Estado != "reportada")
            return BadRequest(new { message = "Solo puedes eliminar reseñas que están reportadas." });

        // Cambiar estado a eliminada en lugar de borrar físicamente
        resena.Estado = "eliminada";
        await _db.SaveChangesAsync();

        return Ok(new { message = "Reseña eliminada correctamente." });
    }

    // ── Admin: desestimar un reporte (ignorar y dejar visible) ────────────

    [Authorize(Roles = "Admin")]
    [HttpPatch("{id}/desestimar-reporte")]
    public async Task<IActionResult> DestimarReporte(int id)
    {
        var resena = await _db.Resenas.FindAsync(id);
        if (resena is null)
            return NotFound(new { message = "Reseña no encontrada." });

        if (resena.Estado != "reportada")
            return BadRequest(new { message = "Solo puedes desestimar reportes de reseñas reportadas." });

        // Volver a publicada y limpiar datos de reporte
        resena.Estado = "publicada";
        resena.MotivoReporte = null;
        resena.FechaReporte = null;
        resena.OfferenteIdQueReporto = null;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Reporte desestimado. La reseña vuelve a su estado publicado." });
    }

    // ── Admin: conocer reservas que puedan ser reseñadas ─────────────────

    [Authorize(Roles = "Cliente")]
    [HttpGet("pendientes-de-resena")]
    public async Task<IActionResult> PendientesDeResena()
    {
        var clienteId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var now = DateTime.UtcNow;
        var reservasCompletadas = await _db.Reservas
            .Include(r => r.Alojamiento)
            .Where(r => r.ClienteId == clienteId
                && (r.Estado == "Completada"
                    || (r.Estado == "Confirmada" && r.FechaSalida <= now)))
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
