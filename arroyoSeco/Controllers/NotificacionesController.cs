using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using arroyoSeco.Application.Common.Interfaces;
using System.Text.RegularExpressions;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // requiere token
public class NotificacionesController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly INotificationService _noti;

    public NotificacionesController(IAppDbContext db, ICurrentUserService current, INotificationService noti)
    {
        _db = db;
        _current = current;
        _noti = noti;
    }

    public record NotificacionPaginadaDto(
        int Id,
        string Titulo,
        string Mensaje,
        string Tipo,
        bool Leida,
        DateTime Fecha,
        string? UrlAccion,
        string? ReservaFolio,
        string? AlojamientoNombre
    );

    public record NotificacionesPaginadasResponse(
        int Page,
        int PageSize,
        int Total,
        int TotalPages,
        IEnumerable<NotificacionPaginadaDto> Items
    );

    // GET /api/notificaciones?soloNoLeidas=true
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool soloNoLeidas = false, CancellationToken ct = default)
    {
        IQueryable<arroyoSeco.Domain.Entities.Notificaciones.Notificacion> q =
            _db.Notificaciones.AsNoTracking()
               .Where(n => n.UsuarioId == _current.UserId);

        if (soloNoLeidas)
            q = q.Where(n => !n.Leida);

        var items = await q.OrderByDescending(n => n.Fecha).ToListAsync(ct);
        return Ok(items);
    }

    // GET /api/notificaciones/paged?page=1&pageSize=10&soloNoLeidas=false&from=2026-03-01&to=2026-03-31
    [HttpGet("paged")]
    public async Task<IActionResult> ListPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] bool soloNoLeidas = false,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        IQueryable<arroyoSeco.Domain.Entities.Notificaciones.Notificacion> q =
            _db.Notificaciones.AsNoTracking()
               .Where(n => n.UsuarioId == _current.UserId);

        if (soloNoLeidas)
            q = q.Where(n => !n.Leida);

        if (from.HasValue)
        {
            var f = from.Value.Date;
            q = q.Where(n => n.Fecha >= f);
        }

        if (to.HasValue)
        {
            var t = to.Value.Date.AddDays(1).AddTicks(-1);
            q = q.Where(n => n.Fecha <= t);
        }

        var total = await q.CountAsync(ct);

        var baseItems = await q
            .OrderByDescending(n => n.Fecha)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var folios = baseItems
            .Select(n => ExtractFolio(n.Mensaje))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alojByFolio = folios.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await _db.Reservas
                .AsNoTracking()
                .Include(r => r.Alojamiento)
                .Where(r => folios.Contains(r.Folio))
                .ToDictionaryAsync(
                    r => r.Folio,
                    r => r.Alojamiento != null ? r.Alojamiento.Nombre : string.Empty,
                    StringComparer.OrdinalIgnoreCase,
                    ct);

        var items = baseItems.Select(n =>
        {
            var folio = ExtractFolio(n.Mensaje);
            alojByFolio.TryGetValue(folio ?? string.Empty, out var alojamientoNombre);

            return new NotificacionPaginadaDto(
                n.Id,
                n.Titulo,
                n.Mensaje,
                n.Tipo,
                n.Leida,
                n.Fecha,
                n.UrlAccion,
                folio,
                string.IsNullOrWhiteSpace(alojamientoNombre) ? null : alojamientoNombre
            );
        });

        var response = new NotificacionesPaginadasResponse(
            page,
            pageSize,
            total,
            (int)Math.Ceiling(total / (double)pageSize),
            items);

        return Ok(response);
    }

    private static string? ExtractFolio(string? mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje)) return null;
        var match = Regex.Match(mensaje, @"RES-\d{4}-[A-Z0-9]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    // PATCH /api/notificaciones/{id}/leer
    [HttpPatch("{id:int}/leer")]
    public async Task<IActionResult> MarcarLeida(int id, CancellationToken ct)
    {
        await _noti.MarkAsReadAsync(id, _current.UserId, ct);
        return NoContent();
    }

    // DELETE /api/notificaciones/{id}
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var n = await _db.Notificaciones.FirstOrDefaultAsync(x => x.Id == id && x.UsuarioId == _current.UserId, ct);
        if (n is null) return NotFound();
        _db.Notificaciones.Remove(n);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // GET /api/notificaciones/noLeidas/count
    [HttpGet("noLeidas/count")]
    public async Task<IActionResult> CountNoLeidas(CancellationToken ct)
    {
        var count = await _db.Notificaciones
            .Where(n => n.UsuarioId == _current.UserId && !n.Leida)
            .CountAsync(ct);
        return Ok(new { count });
    }
}