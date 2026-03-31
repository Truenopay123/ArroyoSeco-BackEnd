using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Application.Features.Gastronomia.Commands.Crear;
using arroyoSeco.Domain.Entities.Usuarios;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Infrastructure.Storage;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReservasGastronomiaController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly CrearReservaGastronomiaCommandHandler _crear;
    private readonly IEmailService _email;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly string _comprobantesPath;

    public ReservasGastronomiaController(
        IAppDbContext db,
        ICurrentUserService current,
        CrearReservaGastronomiaCommandHandler crear,
        IEmailService email,
        UserManager<ApplicationUser> userManager,
        IOptions<StorageOptions> storage)
    {
        _db = db;
        _current = current;
        _crear = crear;
        _email = email;
        _userManager = userManager;
        _comprobantesPath = storage.Value.ComprobantesPath;
    }

    // POST /api/ReservasGastronomia
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearReservaGastronomiaCommand cmd, CancellationToken ct)
    {
        // Validación explícita de autenticación
        if (!User.Identity?.IsAuthenticated ?? true)
            return Unauthorized(new { message = "Debes iniciar sesión para crear una reserva" });

        if (string.IsNullOrWhiteSpace(_current.UserId))
            return Unauthorized(new { message = "Usuario no identificado" });

        try
        {
            var id = await _crear.Handle(cmd, ct);
            var reserva = await _db.ReservasGastronomia
                .AsNoTracking()
                .Include(r => r.Establecimiento)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            
            if (reserva is null) 
                return Created(nameof(Crear), new { Id = id });
            
            return CreatedAtAction(nameof(GetByIdGastronomia), new { id = reserva.Id }, new 
            { 
                reserva.Id,
                reserva.Folio,
                reserva.EstablecimientoId, 
                reserva.Fecha, 
                reserva.NumeroPersonas, 
                reserva.Estado 
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "Error creando reserva", detalle = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "Datos inválidos", detalle = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Error interno", detalle = ex.Message });
        }
    }

    // GET /api/ReservasGastronomia/{id}
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetByIdGastronomia(int id, CancellationToken ct)
    {
        var reserva = await _db.ReservasGastronomia
            .AsNoTracking()
            .Include(r => r.Establecimiento)
            .Include(r => r.Mesa)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        
        if (reserva is null) 
            return NotFound(new { message = "Reserva no encontrada" });
        
        return Ok(new
        {
            reserva.Id,
            reserva.Folio,
            reserva.EstablecimientoId,
            EstablecimientoNombre = reserva.Establecimiento?.Nombre,
            reserva.MesaId,
            MesaNumero = reserva.Mesa?.Numero,
            reserva.UsuarioId,
            reserva.Fecha,
            reserva.NumeroPersonas,
            reserva.Estado,
            reserva.Total,
            reserva.FechaReserva,
            reserva.ComprobanteUrl
        });
    }

    // GET /api/ReservasGastronomia/folio/{folio}
    [HttpGet("folio/{folio}")]
    public async Task<IActionResult> GetByFolio(string folio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folio)) return BadRequest("Folio requerido.");
        var r = await _db.ReservasGastronomia.AsNoTracking()
            .Include(x => x.Establecimiento)
            .Include(x => x.Mesa)
            .FirstOrDefaultAsync(x => x.Folio == folio, ct);
        return r is null ? NotFound() : Ok(r);
    }

    // POST /api/ReservasGastronomia/{id}/comprobante
    [HttpPost("{id:int}/comprobante")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> SubirComprobante(int id, IFormFile archivo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0) return BadRequest("Archivo requerido.");
        var permitidos = new[] { "application/pdf", "image/jpeg", "image/png" };
        if (!permitidos.Contains(archivo.ContentType))
            return BadRequest("Formato no permitido (PDF/JPG/PNG).");

        var r = await _db.ReservasGastronomia.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();

        var esCliente = r.UsuarioId == _current.UserId;
        var esOferente = User.IsInRole("Oferente");
        var esAdmin = User.IsInRole("Admin");
        if (!(esCliente || esOferente || esAdmin))
            return Forbid();

        Directory.CreateDirectory(_comprobantesPath);
        var ext = Path.GetExtension(archivo.FileName);
        var safeFolio = string.Join("_", (r.Folio ?? "folio").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var fileName = $"{safeFolio}_{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(_comprobantesPath, fileName);

        try
        {
            await using var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await archivo.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error guardando archivo: {ex.Message}");
        }

        r.ComprobanteUrl = $"/comprobantes/{fileName}";
        if (esCliente && r.Estado == "Pendiente")
            r.Estado = "PagoEnRevision";

        await _db.SaveChangesAsync(ct);
        return Ok(new { r.Id, r.Folio, r.ComprobanteUrl, r.Estado });
    }

    // GET /api/ReservasGastronomia/{id}/comprobante
    [HttpGet("{id:int}/comprobante")]
    public async Task<IActionResult> DescargarComprobante(int id, CancellationToken ct)
    {
        var r = await _db.ReservasGastronomia.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "Reserva no encontrada" });

        if (string.IsNullOrWhiteSpace(r.ComprobanteUrl))
            return NotFound(new { message = "La reserva no tiene comprobante" });

        var fileName = r.ComprobanteUrl.Split('/').Last();
        var filePath = Path.Combine(_comprobantesPath, fileName);

        if (!System.IO.File.Exists(filePath))
            return NotFound(new { message = "Archivo de comprobante no encontrado" });

        var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath, ct);
        var contentType = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) 
            ? "application/pdf" 
            : "image/jpeg";

        return File(fileBytes, contentType, fileName);
    }

    // GET /api/ReservasGastronomia/establecimiento/{establecimientoId}
    [Authorize(Roles = "Admin,Oferente")]
    [HttpGet("establecimiento/{establecimientoId:int}")]
    public async Task<IActionResult> PorEstablecimiento(int establecimientoId, [FromQuery] string? estado, CancellationToken ct)
    {
        if (establecimientoId <= 0) return BadRequest("establecimientoId inválido");

        if (User.IsInRole("Oferente"))
        {
            var esMio = await _db.Establecimientos
                .AsNoTracking()
                .AnyAsync(e => e.Id == establecimientoId && e.OferenteId == _current.UserId, ct);
            if (!esMio) return Forbid();
        }

        var q = _db.ReservasGastronomia
            .AsNoTracking()
            .Where(r => r.EstablecimientoId == establecimientoId);

        if (!string.IsNullOrWhiteSpace(estado))
            q = q.Where(r => r.Estado == estado);

        var items = await q
            .Include(r => r.Establecimiento)
            .Include(r => r.Mesa)
            .OrderByDescending(r => r.FechaReserva)
            .ToListAsync(ct);

        var ids = items.Select(i => i.UsuarioId).Distinct().ToList();
        var nombres = await MapearNombres(ids);

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.EstablecimientoId,
            EstablecimientoNombre = r.Establecimiento?.Nombre,
            r.UsuarioId,
            Huesped = nombres.TryGetValue(r.UsuarioId, out var nom) ? nom : r.UsuarioId,
            r.MesaId,
            MesaNumero = r.Mesa?.Numero,
            r.Estado,
            r.Fecha,
            r.NumeroPersonas,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl
        });

        return Ok(result);
    }

    // GET /api/ReservasGastronomia/activas
    [HttpGet("activas")]
    public async Task<ActionResult> GetReservasActivas(CancellationToken ct)
    {
        var userId = _current.UserId;
        var now = DateTime.UtcNow;

        // Si es cliente, obtener sus reservas activas
        if (User.IsInRole("Cliente"))
        {
            var reservas = await _db.ReservasGastronomia
                .Where(r => r.UsuarioId == userId && 
                           (r.Estado == "Pendiente" || r.Estado == "Confirmada") &&
                           r.Fecha >= now)
                .Include(r => r.Establecimiento)
                .Include(r => r.Mesa)
                .AsNoTracking()
                .OrderBy(r => r.Fecha)
                .ToListAsync(ct);

            return Ok(reservas.Select(r => new
            {
                r.Id,
                r.Folio,
                r.EstablecimientoId,
                EstablecimientoNombre = r.Establecimiento?.Nombre,
                r.MesaId,
                MesaNumero = r.Mesa?.Numero,
                r.UsuarioId,
                r.Fecha,
                r.NumeroPersonas,
                r.Estado,
                r.Total,
                r.FechaReserva,
                r.ComprobanteUrl,
                EnCurso = true
            }));
        }

        // Si es oferente, obtener reservas activas de sus establecimientos
        if (User.IsInRole("Oferente"))
        {
            var reservas = await _db.ReservasGastronomia
                .Where(r => r.Establecimiento!.OferenteId == userId &&
                           (r.Estado == "Pendiente" || r.Estado == "Confirmada") &&
                           r.Fecha >= now)
                .Include(r => r.Establecimiento)
                .Include(r => r.Mesa)
                .AsNoTracking()
                .OrderBy(r => r.Fecha)
                .ToListAsync(ct);

            return Ok(reservas.Select(r => new
            {
                r.Id,
                r.Folio,
                r.EstablecimientoId,
                EstablecimientoNombre = r.Establecimiento?.Nombre,
                r.MesaId,
                MesaNumero = r.Mesa?.Numero,
                r.UsuarioId,
                r.Fecha,
                r.NumeroPersonas,
                r.Estado,
                r.Total,
                r.FechaReserva,
                r.ComprobanteUrl,
                EnCurso = true
            }));
        }

        return Ok(Array.Empty<object>());
    }

    // GET /api/ReservasGastronomia/historial
    [HttpGet("historial")]
    public async Task<ActionResult> GetHistorial(CancellationToken ct)
    {
        var userId = _current.UserId;
        var now = DateTime.UtcNow;

        if (User.IsInRole("Cliente"))
        {
            var reservas = await _db.ReservasGastronomia
                .Where(r => r.UsuarioId == userId && 
                           (r.Fecha < now || r.Estado == "Cancelada" || r.Estado == "Completada"))
                .Include(r => r.Establecimiento)
                .Include(r => r.Mesa)
                .AsNoTracking()
                .OrderByDescending(r => r.Fecha)
                .ToListAsync(ct);

            return Ok(reservas.Select(r => new
            {
                r.Id,
                r.Folio,
                r.EstablecimientoId,
                EstablecimientoNombre = r.Establecimiento?.Nombre,
                r.MesaId,
                MesaNumero = r.Mesa?.Numero,
                r.UsuarioId,
                r.Fecha,
                r.NumeroPersonas,
                r.Estado,
                r.Total,
                r.FechaReserva,
                r.ComprobanteUrl,
                EnCurso = false
            }));
        }

        if (User.IsInRole("Oferente"))
        {
            var reservas = await _db.ReservasGastronomia
                .Where(r => r.Establecimiento!.OferenteId == userId &&
                           (r.Fecha < now || r.Estado == "Cancelada" || r.Estado == "Completada"))
                .Include(r => r.Establecimiento)
                .Include(r => r.Mesa)
                .AsNoTracking()
                .OrderByDescending(r => r.Fecha)
                .ToListAsync(ct);

            return Ok(reservas.Select(r => new
            {
                r.Id,
                r.Folio,
                r.EstablecimientoId,
                EstablecimientoNombre = r.Establecimiento?.Nombre,
                r.MesaId,
                MesaNumero = r.Mesa?.Numero,
                r.UsuarioId,
                r.Fecha,
                r.NumeroPersonas,
                r.Estado,
                r.Total,
                r.FechaReserva,
                r.ComprobanteUrl,
                EnCurso = false
            }));
        }

        return Ok(Array.Empty<object>());
    }

    // PATCH /api/ReservasGastronomia/{id}/estado
    [Authorize(Roles = "Admin,Oferente")]
    [HttpPatch("{id:int}/estado")]
    public async Task<IActionResult> CambiarEstado(int id, [FromBody] CambiarEstadoReservaGastronomiaDto dto, CancellationToken ct)
    {
        var reserva = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (reserva == null) return NotFound(new { message = "Reserva no encontrada" });

        // Verificar que el oferente sea dueño del establecimiento
        if (User.IsInRole("Oferente") && reserva.Establecimiento?.OferenteId != _current.UserId)
        {
            return Forbid();
        }

        reserva.Estado = dto.Estado;
        await _db.SaveChangesAsync(ct);

        // Enviar correo al cliente
        var cliente = await _userManager.FindByIdAsync(reserva.UsuarioId);
        if (cliente?.Email != null)
        {
            var asunto = "";
            var mensaje = "";
            var color = "";

            if (dto.Estado == "Confirmada")
            {
                asunto = "Tu reserva en gastronomía ha sido confirmada";
                mensaje = $"Tu reserva en {reserva.Establecimiento?.Nombre} para {reserva.NumeroPersonas} personas el {reserva.Fecha:dd/MM/yyyy HH:mm} ha sido confirmada.";
                color = "#27ae60";
            }
            else if (dto.Estado == "Cancelada")
            {
                asunto = "Tu reserva en gastronomía ha sido cancelada";
                mensaje = $"Tu reserva en {reserva.Establecimiento?.Nombre} ha sido cancelada.";
                color = "#e74c3c";
            }
            else if (dto.Estado == "Completada")
            {
                asunto = "Tu reserva en gastronomía ha sido completada";
                mensaje = $"¡Gracias por visitarnos en {reserva.Establecimiento?.Nombre}! Esperamos verte pronto.";
                color = "#3498db";
            }

            if (!string.IsNullOrEmpty(mensaje))
            {
                var correoHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: {color}; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #ecf0f1; padding: 20px; border-radius: 0 0 5px 5px; }}
        .details {{ background-color: #fff; padding: 15px; border-left: 4px solid {color}; margin: 15px 0; }}
        .details p {{ margin: 5px 0; }}
        .auto-email {{ background-color: #fff3cd; padding: 12px; border-left: 4px solid #ffc107; margin: 15px 0; font-size: 12px; color: #856404; }}
        .footer {{ margin-top: 20px; font-size: 12px; color: #7f8c8d; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>{asunto}</h1>
        </div>
        <div class='content'>
            <p>Hola {cliente.UserName},</p>
            <p>{mensaje}</p>
            
            <div class='details'>
                <p><strong>Establecimiento:</strong> {reserva.Establecimiento?.Nombre}</p>
                <p><strong>Fecha:</strong> {reserva.Fecha:dd/MM/yyyy HH:mm}</p>
                <p><strong>Personas:</strong> {reserva.NumeroPersonas}</p>
                <p><strong>Total:</strong> ${reserva.Total:F2}</p>
            </div>
            
            <p>Si tienes dudas, contáctanos a través de nuestro sitio web.</p>
            
            <div class='auto-email'>
                <strong>⚠️ Nota:</strong> Este es un correo automático, por favor no contestes a este mensaje. No recibiremos tu respuesta. Si necesitas ayuda, contáctanos a través de nuestro sitio web.
            </div>
        </div>
        <div class='footer'>
            <p>© 2025 Arroyo Seco. Todos los derechos reservados.</p>
        </div>
    </div>
</body>
</html>";

                await _email.SendEmailAsync(cliente.Email, asunto, correoHtml, ct);
            }
        }

        return Ok(new { reserva.Id, reserva.Folio, reserva.Estado });
    }

    // GET /api/ReservasGastronomia/cliente/{clienteId}/historial
    [HttpGet("cliente/{clienteId}/historial")]
    public async Task<IActionResult> HistorialCliente(string clienteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clienteId)) return BadRequest("clienteId requerido");
        var esMismoCliente = _current.UserId == clienteId;
        var esAdmin = User.IsInRole("Admin");
        if (!(esMismoCliente || esAdmin)) return Forbid();

        var items = await _db.ReservasGastronomia
            .AsNoTracking()
            .Include(r => r.Establecimiento)
            .Include(r => r.Mesa)
            .Where(r => r.UsuarioId == clienteId)
            .OrderByDescending(r => r.Fecha)
            .ThenByDescending(r => r.FechaReserva)
            .ToListAsync(ct);

        var nombres = await MapearNombres(new[] { clienteId });
        var nombre = nombres.TryGetValue(clienteId, out var n) ? n : clienteId;

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.EstablecimientoId,
            EstablecimientoNombre = r.Establecimiento?.Nombre,
            r.UsuarioId,
            Huesped = nombre,
            r.MesaId,
            MesaNumero = r.Mesa?.Numero,
            r.Estado,
            r.Fecha,
            r.NumeroPersonas,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl
        });
        return Ok(result);
    }

    private async Task<Dictionary<string,string>> MapearNombres(IEnumerable<string> ids)
    {
        var dic = new Dictionary<string,string>();
        foreach (var id in ids)
        {
            var u = await _userManager.FindByIdAsync(id);
            dic[id] = u?.Email ?? u?.UserName ?? id;
        }
        return dic;
    }

    public record CambiarEstadoReservaGastronomiaDto(string Estado);
}
