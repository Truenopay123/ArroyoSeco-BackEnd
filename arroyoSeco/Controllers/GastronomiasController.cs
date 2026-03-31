using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Application.Features.Gastronomia.Commands.Crear;
using arroyoSeco.Domain.Entities.Gastronomia;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GastronomiasController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly CrearEstablecimientoCommandHandler _crear;
    private readonly CrearMenuCommandHandler _crearMenu;
    private readonly AgregarMenuItemCommandHandler _agregarItem;
    private readonly CrearMesaCommandHandler _crearMesa;
    private readonly CrearReservaGastronomiaCommandHandler _crearReserva;
    private readonly ICurrentUserService _current;

    public GastronomiasController(IAppDbContext db, CrearEstablecimientoCommandHandler crear, CrearMenuCommandHandler crearMenu, AgregarMenuItemCommandHandler agregarItem, CrearMesaCommandHandler crearMesa, CrearReservaGastronomiaCommandHandler crearReserva, ICurrentUserService current)
    {
        _db = db;
        _crear = crear;
        _crearMenu = crearMenu;
        _agregarItem = agregarItem;
        _crearMesa = crearMesa;
        _crearReserva = crearReserva;
        _current = current;
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Establecimiento>>> List(CancellationToken ct)
        => Ok(await _db.Establecimientos
            .Include(e => e.Fotos.OrderBy(f => f.Orden))
            .Include(e => e.Menus)
            .Include(e => e.Mesas)
            .AsNoTracking()
            .ToListAsync(ct));

    // Endpoint para obtener establecimientos del oferente logueado
    [Authorize(Roles = "Oferente")]
    [HttpGet("mios")]
    public async Task<ActionResult<IEnumerable<Establecimiento>>> GetMisEstablecimientos(CancellationToken ct)
    {
        var establecimientos = await _db.Establecimientos
            .Where(e => e.OferenteId == _current.UserId)
            .Include(e => e.Fotos.OrderBy(f => f.Orden))
            .Include(e => e.Menus)
            .ThenInclude(m => m.Items)
            .Include(e => e.Mesas)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(establecimientos);
    }

    [AllowAnonymous]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Establecimiento>> GetById(int id, CancellationToken ct)
    {
        var e = await _db.Establecimientos
            .Include(x => x.Fotos.OrderBy(f => f.Orden))
            .Include(x => x.Menus)
            .ThenInclude(m => m.Items)
            .Include(x => x.Mesas)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? NotFound() : Ok(e);
    }

    // Solo Oferente (propietario)
    [Authorize(Roles = "Oferente")]
    [HttpPost]
    public async Task<ActionResult<int>> Crear([FromBody] CrearEstablecimientoCommand cmd, CancellationToken ct)
    {
        var id = await _crear.Handle(cmd, ct);
        return CreatedAtAction(nameof(GetById), new { id }, id);
    }

    // Menus
    [Authorize(Roles = "Oferente")]
    [HttpPost("{id:int}/menus")]
    public async Task<ActionResult<int>> CrearMenu(int id, [FromBody] CrearMenuCommand cmd, CancellationToken ct)
    {
        cmd.EstablecimientoId = id;
        var mid = await _crearMenu.Handle(cmd, ct);
        return CreatedAtAction(nameof(GetById), new { id }, mid);
    }

    [AllowAnonymous]
    [HttpGet("{id:int}/menus")]
    public async Task<ActionResult> ListMenus(int id, CancellationToken ct)
    {
        var menus = await _db.Menus.Where(m => m.EstablecimientoId == id).Include(m => m.Items).AsNoTracking().ToListAsync(ct);
        return Ok(menus);
    }

    [Authorize(Roles = "Oferente")]
    [HttpPost("{id:int}/menus/{menuId:int}/items")]
    public async Task<ActionResult<int>> AgregarItem(int id, int menuId, [FromBody] AgregarMenuItemCommand cmd, CancellationToken ct)
    {
        cmd.MenuId = menuId;
        var itemId = await _agregarItem.Handle(cmd, ct);
        return CreatedAtAction(nameof(ListMenus), new { id }, itemId);
    }

    // Mesas
    [Authorize(Roles = "Oferente")]
    [HttpPost("{id:int}/mesas")]
    public async Task<ActionResult<int>> CrearMesa(int id, [FromBody] CrearMesaCommand cmd, CancellationToken ct)
    {
        cmd.EstablecimientoId = id;
        var mesaId = await _crearMesa.Handle(cmd, ct);
        return CreatedAtAction(nameof(GetById), new { id }, mesaId);
    }

    [Authorize(Roles = "Oferente")]
    [HttpPut("{id:int}/mesas/{mesaId:int}/disponible")]
    public async Task<IActionResult> SetDisponibilidad(int id, int mesaId, [FromBody] bool disponible, CancellationToken ct)
    {
        var mesa = await _db.Mesas.FirstOrDefaultAsync(m => m.Id == mesaId && m.EstablecimientoId == id, ct);
        if (mesa == null) return NotFound();
        if (mesa.Establecimiento?.OferenteId != _current.UserId) return Forbid();
        mesa.Disponible = disponible;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Reservas
    [Authorize]
    [HttpPost("{id:int}/reservas")]
    public async Task<ActionResult<int>> CrearReserva(int id, [FromBody] CrearReservaGastronomiaCommand cmd, CancellationToken ct)
    {
        cmd.EstablecimientoId = id;
        var reservaId = await _crearReserva.Handle(cmd, ct);
        return CreatedAtAction(nameof(GetById), new { id }, reservaId);
    }

    [Authorize(Roles = "Oferente")]
    [HttpGet("{id:int}/reservas")]
    public async Task<ActionResult> ListReservas(int id, CancellationToken ct)
    {
        var est = await _db.Establecimientos.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound();
        if (est.OferenteId != _current.UserId) return Forbid();

        var reservas = await _db.ReservasGastronomia
            .Where(r => r.EstablecimientoId == id)
            .Include(r => r.Mesa)
            .AsNoTracking()
            .ToListAsync(ct);
        return Ok(reservas);
    }

    [AllowAnonymous]
    [HttpGet("{id:int}/disponibilidad")]
    public async Task<ActionResult> VerificarDisponibilidad(int id, [FromQuery] DateTime fecha, CancellationToken ct)
    {
        var mesas = await _db.Mesas
            .Where(m => m.EstablecimientoId == id && m.Disponible)
            .AsNoTracking()
            .ToListAsync(ct);
        return Ok(new { mesasDisponibles = mesas.Count, mesas });
    }

    // PUT /api/Gastronomias/{id}
    [Authorize(Roles = "Oferente")]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateEstablecimientoRequest request, CancellationToken ct)
    {
        var est = await _db.Establecimientos
            .Include(e => e.Fotos)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound(new { message = "Establecimiento no encontrado" });
        
        // Verificar que el oferente sea dueño
        if (est.OferenteId != _current.UserId) return Forbid();

        // Actualizar campos
        if (!string.IsNullOrWhiteSpace(request.Nombre))
            est.Nombre = request.Nombre;
        
        if (!string.IsNullOrWhiteSpace(request.Ubicacion))
            est.Ubicacion = request.Ubicacion;
        
        if (request.Latitud.HasValue)
            est.Latitud = request.Latitud;
        
        if (request.Longitud.HasValue)
            est.Longitud = request.Longitud;
        
        if (request.Direccion != null)
            est.Direccion = request.Direccion;
        
        if (request.Descripcion != null)
            est.Descripcion = request.Descripcion;
        
        if (!string.IsNullOrWhiteSpace(request.FotoPrincipal))
            est.FotoPrincipal = request.FotoPrincipal;

        // Actualizar fotos extras
        if (request.FotosUrls != null)
        {
            var fotosExtras = request.FotosUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Where(url => !string.Equals(url, request.FotoPrincipal?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            est.Fotos.Clear();
            est.Fotos.AddRange(fotosExtras.Select((url, idx) => new FotoEstablecimiento
            {
                Url = url,
                Orden = idx + 1
            }));
        }

        await _db.SaveChangesAsync(ct);
        
        return Ok(est);
    }

    // DELETE /api/Gastronomias/{id}
    [Authorize(Roles = "Oferente")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var est = await _db.Establecimientos.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound(new { message = "Establecimiento no encontrado" });
        
        // Verificar que el oferente sea dueño
        if (est.OferenteId != _current.UserId) return Forbid();

        _db.Establecimientos.Remove(est);
        await _db.SaveChangesAsync(ct);
        
        return Ok(new { message = "Establecimiento eliminado correctamente" });
    }

    // POST /api/Gastronomias/{id}/fotos
    [Authorize(Roles = "Oferente")]
    [HttpPost("{id:int}/fotos")]
    public async Task<IActionResult> AgregarFotos(int id, [FromBody] AgregarFotosRequest request, CancellationToken ct)
    {
        var est = await _db.Establecimientos
            .Include(e => e.Fotos)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound(new { message = "Establecimiento no encontrado" });
        if (est.OferenteId != _current.UserId) return Forbid();

        var maxOrden = est.Fotos.Any() ? est.Fotos.Max(f => f.Orden) : 0;

        var nuevas = (request.Urls ?? new List<string>())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((url, idx) => new FotoEstablecimiento
            {
                Url = url,
                Orden = maxOrden + idx + 1
            })
            .ToList();

        est.Fotos.AddRange(nuevas);
        await _db.SaveChangesAsync(ct);

        return Ok(est.Fotos.OrderBy(f => f.Orden));
    }

    // DELETE /api/Gastronomias/{id}/fotos/{fotoId}
    [Authorize(Roles = "Oferente")]
    [HttpDelete("{id:int}/fotos/{fotoId:int}")]
    public async Task<IActionResult> EliminarFoto(int id, int fotoId, CancellationToken ct)
    {
        var est = await _db.Establecimientos.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound(new { message = "Establecimiento no encontrado" });
        if (est.OferenteId != _current.UserId) return Forbid();

        var foto = await _db.FotosEstablecimiento.FirstOrDefaultAsync(f => f.Id == fotoId && f.EstablecimientoId == id, ct);
        if (foto == null) return NotFound(new { message = "Foto no encontrada" });

        _db.FotosEstablecimiento.Remove(foto);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Foto eliminada correctamente" });
    }

    // PUT /api/Gastronomias/{id}/fotos/reordenar
    [Authorize(Roles = "Oferente")]
    [HttpPut("{id:int}/fotos/reordenar")]
    public async Task<IActionResult> ReordenarFotos(int id, [FromBody] ReordenarFotosRequest request, CancellationToken ct)
    {
        var est = await _db.Establecimientos
            .Include(e => e.Fotos)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (est == null) return NotFound(new { message = "Establecimiento no encontrado" });
        if (est.OferenteId != _current.UserId) return Forbid();

        foreach (var item in request.FotoIds.Select((fotoId, idx) => new { fotoId, orden = idx + 1 }))
        {
            var foto = est.Fotos.FirstOrDefault(f => f.Id == item.fotoId);
            if (foto != null) foto.Orden = item.orden;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(est.Fotos.OrderBy(f => f.Orden));
    }
}

public record UpdateEstablecimientoRequest(
    string? Nombre,
    string? Ubicacion,
    double? Latitud,
    double? Longitud,
    string? Direccion,
    string? Descripcion,
    string? FotoPrincipal,
    List<string>? FotosUrls
);

public record AgregarFotosRequest(List<string> Urls);

public record ReordenarFotosRequest(List<int> FotoIds);
