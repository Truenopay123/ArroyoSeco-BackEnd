using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Application.Features.Alojamiento.Commands.Crear;
using arroyoSeco.Hubs;
using AlojamientoEntity = arroyoSeco.Domain.Entities.Alojamientos.Alojamiento;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AlojamientosController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly CrearAlojamientoCommandHandler _crear;
    private readonly ICurrentUserService _current;
    private readonly IHubContext<PriceUpdateHub> _priceHub;

    public AlojamientosController(
        IAppDbContext db,
        CrearAlojamientoCommandHandler crear,
        ICurrentUserService current,
        IHubContext<PriceUpdateHub> priceHub)
    {
        _db = db;
        _crear = crear;
        _current = current;
        _priceHub = priceHub;
    }

    // P�blico
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AlojamientoEntity>>> List(CancellationToken ct)
        => Ok(await _db.Alojamientos
            .Include(a => a.Fotos)
            .AsNoTracking()
            .ToListAsync(ct));

    // P�blico
    [AllowAnonymous]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AlojamientoEntity>> GetById(int id, CancellationToken ct)
    {
        var a = await _db.Alojamientos
            .Include(x => x.Fotos)
            .Include(x => x.Reservas)
            .Include(x => x.Oferente)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? NotFound() : Ok(a);
    }

    // Rangos ocupados para pintar en calendario
    [AllowAnonymous]
    [HttpGet("{id:int}/calendario")]
    public async Task<IActionResult> Calendario(int id, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var rangos = await _db.Reservas
            .Where(r => r.AlojamientoId == id
                && r.Estado == "Confirmada"
                && r.FechaSalida.Date >= today)
            .Select(r => new { inicio = r.FechaEntrada, fin = r.FechaSalida })
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(rangos);
    }

    // Solo Oferente autenticado: obtiene sus alojamientos
    [Authorize(Roles = "Oferente")]
    [HttpGet("mios")]
    public async Task<ActionResult<IEnumerable<AlojamientoEntity>>> MisAlojamientos(CancellationToken ct)
    {
        var userId = _current.UserId;
        var items = await _db.Alojamientos
            .Where(a => a.OferenteId == userId)
            .Include(a => a.Fotos)
            .Include(a => a.Reservas)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(items);
    }

    // Solo Oferente autenticado
    [Authorize(Roles = "Oferente")]
    [HttpPost]
    public async Task<ActionResult<int>> Crear([FromBody] CrearAlojamientoCommand cmd, CancellationToken ct)
    {
        var id = await _crear.Handle(cmd, ct);
        return CreatedAtAction(nameof(GetById), new { id }, id);
    }

    public record ActualizarAlojamientoDto(
        string Nombre,
        string Ubicacion,
        double? Latitud,
        double? Longitud,
        string? Direccion,
        int MaxHuespedes,
        int Habitaciones,
        int Banos,
        decimal PrecioPorNoche,
        string? FotoPrincipal,
        List<string>? FotosUrls,
        List<string>? Amenidades);

    // Solo Oferente
    [Authorize(Roles = "Oferente")]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ActualizarAlojamientoDto dto, CancellationToken ct)
    {
        var a = await _db.Alojamientos
            .Include(x => x.Fotos)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();

        var precioAnterior = a.PrecioPorNoche;
        
        a.Nombre = dto.Nombre;
        a.Ubicacion = dto.Ubicacion;
        a.Latitud = dto.Latitud;
        a.Longitud = dto.Longitud;
        a.Direccion = dto.Direccion;
        a.MaxHuespedes = dto.MaxHuespedes;
        a.Habitaciones = dto.Habitaciones;
        a.Banos = dto.Banos;
        a.PrecioPorNoche = dto.PrecioPorNoche;
        a.FotoPrincipal = dto.FotoPrincipal;

        var fotosExtras = (dto.FotosUrls ?? new List<string>())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Where(url => !string.Equals(url, dto.FotoPrincipal?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        a.Fotos.Clear();
        a.Fotos.AddRange(fotosExtras.Select((url, idx) => new Domain.Entities.Alojamientos.FotoAlojamiento
        {
            Url = url,
            Orden = idx + 1
        }));

        a.Amenidades = dto.Amenidades?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        await _db.SaveChangesAsync(ct);

        if (precioAnterior != a.PrecioPorNoche)
        {
            await _priceHub.Clients
                .Group(PriceUpdateHub.GetAlojamientoGroupName(a.Id))
                .SendAsync(
                    PriceUpdateHub.PriceUpdatedEvent,
                    new
                    {
                        alojamientoId = a.Id,
                        precioNuevo = a.PrecioPorNoche
                    },
                    ct);
        }

        return NoContent();
    }

    // Solo Oferente
    [Authorize(Roles = "Oferente")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var a = await _db.Alojamientos.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        _db.Alojamientos.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}