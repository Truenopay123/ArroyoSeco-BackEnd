using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Text.Json;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Application.Features.Reservas.Commands.Crear;
using arroyoSeco.Application.Features.Reservas.Commands.CambiarEstado;
using arroyoSeco.Infrastructure.Storage;
using arroyoSeco.Domain.Entities;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Domain.Entities.Usuarios;
using System.Data;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReservasController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly CrearReservaCommandHandler _crear;
    private readonly CambiarEstadoReservaCommandHandler _cambiarEstado;
    private readonly ICurrentUserService _current;
    private readonly string _comprobantesPath;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;

    public ReservasController(
        IAppDbContext db,
        CrearReservaCommandHandler crear,
        CambiarEstadoReservaCommandHandler cambiarEstado,
        ICurrentUserService current,
        IOptions<StorageOptions> storage,
        UserManager<ApplicationUser> userManager,
        IEmailService email)
    {
        _db = db;
        _crear = crear;
        _cambiarEstado = cambiarEstado;
        _current = current;
        _comprobantesPath = storage.Value.ComprobantesPath;
        _userManager = userManager;
        _email = email;
    }

    // GET /api/reservas/alojamiento/{alojamientoId}?estado=Pendiente
    // Devuelve reservas de un alojamiento. Oferente s�lo si es due�o; Admin cualquiera.
    [Authorize(Roles = "Admin,Oferente")]
    [HttpGet("alojamiento/{alojamientoId:int}")]
    public async Task<IActionResult> PorAlojamiento(int alojamientoId, [FromQuery] string? estado, CancellationToken ct)
    {
        if (alojamientoId <= 0) return BadRequest("alojamientoId inv�lido");

        if (User.IsInRole("Oferente"))
        {
            var esMio = await _db.Alojamientos
                .AsNoTracking()
                .AnyAsync(a => a.Id == alojamientoId && a.OferenteId == _current.UserId, ct);
            if (!esMio) return Forbid();
        }

        var q = _db.Reservas
            .AsNoTracking()
            .Where(r => r.AlojamientoId == alojamientoId);

        if (!string.IsNullOrWhiteSpace(estado))
            q = q.Where(r => r.Estado == estado);

        var items = await q
            .Include(r => r.Alojamiento) // mover Include despu�s de los Where
            .OrderByDescending(r => r.FechaReserva)
            .ToListAsync(ct);

        // Obtener nombres de clientes (Identity) en bloque
        var ids = items.Select(i => i.ClienteId).Distinct().ToList();
        var nombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var u = await _userManager.FindByIdAsync(id);
            nombres[id] = u?.Email ?? u?.UserName ?? id;
        }

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.AlojamientoId,
            AlojamientoNombre = r.Alojamiento?.Nombre,
            r.ClienteId,
            Huesped = nombres.TryGetValue(r.ClienteId, out var nom) ? nom : r.ClienteId,
            r.Estado,
            r.FechaEntrada,
            r.FechaSalida,
            r.NumeroHuespedes,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl
        });

        return Ok(result);
    }

    // POST JSON simple (sin comprobante)
    // POST JSON simple (sin comprobante) con manejo de errores de disponibilidad
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearReservaCommand cmd, CancellationToken ct)
    {
        // Validación explícita de autenticación
        if (!User.Identity?.IsAuthenticated ?? true)
            return Unauthorized(new { message = "Debes iniciar sesión para crear una reserva" });

        if (string.IsNullOrWhiteSpace(_current.UserId))
            return Unauthorized(new { message = "Usuario no identificado" });

        if (!cmd.AceptaPoliticaDatos && (!string.IsNullOrWhiteSpace(cmd.Sexo) || cmd.FechaNacimiento.HasValue || !string.IsNullOrWhiteSpace(cmd.LugarOrigen)))
            return BadRequest(new { message = "Debes aceptar la política de privacidad para guardar datos demográficos." });

        await ActualizarDemografiaDesdeReservaAsync(cmd);

        try
        {
            Console.WriteLine($"[ReservasController.Crear] Iniciando creación de reserva. AlojamientoId={cmd.AlojamientoId}, Usuario={_current.UserId}");
            var id = await _crear.Handle(cmd, ct);
            Console.WriteLine($"[ReservasController.Crear] Reserva creada exitosamente. ID={id}");
            var r = await _db.Reservas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Created(nameof(Crear), new { Id = id });
            return CreatedAtAction(nameof(GetByFolio), new { folio = r.Folio }, new { r.Id, r.Folio, r.Estado });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Fechas no disponibles", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = "Fechas no disponibles", detalle = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "Error en la solicitud", detalle = ex.Message });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReservasController.Crear] ERROR: {ex.GetType().Name} - {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[ReservasController.Crear] INNER: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                if (ex.InnerException.InnerException != null)
                    Console.WriteLine($"[ReservasController.Crear] INNER2: {ex.InnerException.InnerException.GetType().Name} - {ex.InnerException.InnerException.Message}");
            }
            Console.WriteLine($"[ReservasController.Crear] STACK: {ex.StackTrace}");
            
            var errorMessage = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { error = "Error interno", detalle = errorMessage, tipo = ex.GetType().Name });
        }
    }

    // POST multipart: reserva + comprobante
    // FormData: reserva (JSON string), comprobante (File PDF/JPG/PNG)
    [HttpPost("crear-con-comprobante")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> CrearConComprobante([FromForm] string reserva, [FromForm] IFormFile comprobante, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reserva)) return BadRequest("Campo 'reserva' requerido.");
        if (comprobante is null || comprobante.Length == 0) return BadRequest("Archivo 'comprobante' requerido.");

        var permitidos = new[] { "application/pdf", "image/jpeg", "image/png" };
        if (!permitidos.Contains(comprobante.ContentType))
            return BadRequest("Formato no permitido (PDF/JPG/PNG).");

        CrearReservaCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<CrearReservaCommand>(reserva, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cmd is null) return BadRequest("JSON inv�lido.");
        }
        catch (Exception ex)
        {
            return BadRequest($"Error JSON: {ex.Message}");
        }

        if (!cmd.AceptaPoliticaDatos && (!string.IsNullOrWhiteSpace(cmd.Sexo) || cmd.FechaNacimiento.HasValue || !string.IsNullOrWhiteSpace(cmd.LugarOrigen)))
            return BadRequest(new { message = "Debes aceptar la política de privacidad para guardar datos demográficos." });

        await ActualizarDemografiaDesdeReservaAsync(cmd);

        int id;
        try
        {
            id = await _crear.Handle(cmd!, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Fechas no disponibles", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = "Fechas no disponibles", detalle = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest($"Error creando reserva: {ex.Message}");
        }

        var entidad = await _db.Reservas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entidad is null) return StatusCode(500, "Reserva no disponible.");

        Directory.CreateDirectory(_comprobantesPath);
        var ext = Path.GetExtension(comprobante.FileName);
        var safeFolio = string.Join("_", (entidad.Folio ?? "folio").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var fileName = $"{safeFolio}_{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(_comprobantesPath, fileName);

        try
        {
            await using var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await comprobante.CopyToAsync(fs, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error guardando archivo: {ex.Message}");
        }

        entidad.ComprobanteUrl = $"/comprobantes/{fileName}";
        if (entidad.Estado == "Pendiente")
            entidad.Estado = "PagoEnRevision";

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetByFolio),
            new { folio = entidad.Folio },
            new { entidad.Id, entidad.Folio, entidad.ComprobanteUrl, entidad.Estado });
    }

    // Subir/actualizar comprobante despu�s (opcional)
    [HttpPost("{id:int}/comprobante")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> SubirComprobante(int id, IFormFile archivo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0) return BadRequest("Archivo requerido.");
        var permitidos = new[] { "application/pdf", "image/jpeg", "image/png" };
        if (!permitidos.Contains(archivo.ContentType))
            return BadRequest("Formato no permitido.");

        var r = await _db.Reservas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();

        // Solo cliente due�o, oferente propietario o admin (simplificado)
        var esCliente = r.ClienteId == _current.UserId;
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

    // GET folio
    [HttpGet("folio/{folio}")]
    public async Task<IActionResult> GetByFolio(string folio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folio)) return BadRequest("Folio requerido.");
        var r = await _db.Reservas.AsNoTracking()
            .Include(x => x.Alojamiento)
            .FirstOrDefaultAsync(x => x.Folio == folio, ct);
        return r is null ? NotFound() : Ok(r);
    }

    // GET /api/reservas/{id}/comprobante - Descargar comprobante por ID de reserva
    [HttpGet("{id:int}/comprobante")]
    public async Task<IActionResult> DescargarComprobante(int id, CancellationToken ct)
    {
        var r = await _db.Reservas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { message = "Reserva no encontrada" });

        if (string.IsNullOrWhiteSpace(r.ComprobanteUrl))
            return NotFound(new { message = "La reserva no tiene comprobante" });

        // Extraer nombre del archivo de la URL (/comprobantes/nombre.pdf → nombre.pdf)
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

    public record CambiarEstadoReservaDto(string Estado);

    [Authorize(Roles = "Admin,Oferente")]
    [HttpPatch("{id:int}/estado")]
    public async Task<IActionResult> CambiarEstado(int id, [FromBody] CambiarEstadoReservaDto dto, CancellationToken ct)
    {
        var r = await _db.Reservas
            .Include(x => x.Alojamiento)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        
        if (r is null) return NotFound(new { message = "Reserva no encontrada" });

        var estadoAnterior = r.Estado;
        var cambio = $"{estadoAnterior} → {dto.Estado}";

        await _cambiarEstado.Handle(new CambiarEstadoReservaCommand { ReservaId = id, NuevoEstado = dto.Estado }, ct);

        // Obtener datos actualizados
        r = await _db.Reservas
            .Include(x => x.Alojamiento)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        // Enviar correo al cliente notificando cambio de estado
        var cliente = await _userManager.FindByIdAsync(r.ClienteId);
        if (cliente?.Email != null)
        {
            var asunto = "";
            var mensaje = "";
            var color = "";

            if (dto.Estado == "Confirmada")
            {
                asunto = "Tu reserva ha sido confirmada";
                mensaje = $"La reserva en {r.Alojamiento?.Nombre} desde {r.FechaEntrada:dd/MM/yyyy} hasta {r.FechaSalida:dd/MM/yyyy} ha sido confirmada.";
                color = "#27ae60";
            }
            else if (dto.Estado == "Cancelada")
            {
                asunto = "Tu reserva ha sido cancelada";
                mensaje = $"La reserva en {r.Alojamiento?.Nombre} ha sido cancelada.";
                color = "#e74c3c";
            }
            else if (dto.Estado == "Completada")
            {
                asunto = "Tu reserva ha sido completada";
                mensaje = $"Tu estadía en {r.Alojamiento?.Nombre} ha finalizado. ¡Gracias por visitarnos!";
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
                <p><strong>Folio:</strong> {r.Folio}</p>
                <p><strong>Alojamiento:</strong> {r.Alojamiento?.Nombre}</p>
                <p><strong>Entrada:</strong> {r.FechaEntrada:dd/MM/yyyy}</p>
                <p><strong>Salida:</strong> {r.FechaSalida:dd/MM/yyyy}</p>
                <p><strong>Total:</strong> ${r.Total:F2}</p>
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

        return NoContent();
    }

    // GET /api/reservas/activas?clienteId=GUID&alojamientoId=123
    // Activas: FechaEntrada <= now && FechaSalida > now && Estado <> Cancelada
    [HttpGet("activas")]
    public async Task<IActionResult> Activas([FromQuery] string? clienteId, [FromQuery] int? alojamientoId, CancellationToken ct)
    {
        var now = DateTime.UtcNow; // comparaci�n directa, evita CONVERT(date)

        var clienteObjetivo = !string.IsNullOrWhiteSpace(clienteId)
            ? clienteId
            : (User.IsInRole("Cliente") ? _current.UserId : null);
        await ExpirarReservasPendientesDelClienteAsync(clienteObjetivo, ct);

        IQueryable<Reserva> q = _db.Reservas
            .AsNoTracking()
            .Include(r => r.Alojamiento)
            .Where(r => r.Estado != "Cancelada" && r.FechaEntrada <= now && r.FechaSalida > now);

        // Filtrado por cliente: si se pasa clienteId lo usamos, sino si el usuario tiene rol Cliente usamos su propio Id.
        if (!string.IsNullOrWhiteSpace(clienteId))
        {
            q = q.Where(r => r.ClienteId == clienteId);
        }
        else if (User.IsInRole("Cliente"))
        {
            q = q.Where(r => r.ClienteId == _current.UserId);
        }

        // Filtrar por alojamiento si se env�a
        if (alojamientoId.HasValue && alojamientoId > 0)
            q = q.Where(r => r.AlojamientoId == alojamientoId.Value);

        // Si es oferente restringir a sus alojamientos
        if (User.IsInRole("Oferente"))
            q = q.Where(r => r.Alojamiento!.OferenteId == _current.UserId);

        var items = await q.OrderBy(r => r.FechaEntrada).ToListAsync(ct);
        var nombres = await MapearNombres(items.Select(r => r.ClienteId).Distinct());

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.AlojamientoId,
            AlojamientoNombre = r.Alojamiento?.Nombre,
            r.ClienteId,
            Huesped = nombres.TryGetValue(r.ClienteId, out var nom) ? nom : r.ClienteId,
            r.Estado,
            r.FechaEntrada,
            r.FechaSalida,
            r.NumeroHuespedes,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl,
            EnCurso = true
        });
        return Ok(result);
    }

    // GET /api/reservas/historial?clienteId=GUID&alojamientoId=123
    // Historial: FechaSalida <= now OR estado en (Cancelada, Completada)
    [HttpGet("historial")]
    public async Task<IActionResult> Historial([FromQuery] string? clienteId, [FromQuery] int? alojamientoId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var estadosFin = new[] { "Cancelada", "Completada" };

        IQueryable<Reserva> q = _db.Reservas
            .AsNoTracking()
            .Include(r => r.Alojamiento)
            .Where(r => r.FechaSalida <= now || estadosFin.Contains(r.Estado));

        if (!string.IsNullOrWhiteSpace(clienteId))
        {
            q = q.Where(r => r.ClienteId == clienteId);
        }
        else if (User.IsInRole("Cliente"))
        {
            q = q.Where(r => r.ClienteId == _current.UserId);
        }

        if (alojamientoId.HasValue && alojamientoId > 0)
            q = q.Where(r => r.AlojamientoId == alojamientoId.Value);

        if (User.IsInRole("Oferente"))
            q = q.Where(r => r.Alojamiento!.OferenteId == _current.UserId);

        var items = await q
            .OrderByDescending(r => r.FechaSalida)
            .ThenByDescending(r => r.FechaReserva)
            .ToListAsync(ct);
        var nombres = await MapearNombres(items.Select(r => r.ClienteId).Distinct());

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.AlojamientoId,
            AlojamientoNombre = r.Alojamiento?.Nombre,
            r.ClienteId,
            Huesped = nombres.TryGetValue(r.ClienteId, out var nom) ? nom : r.ClienteId,
            r.Estado,
            r.FechaEntrada,
            r.FechaSalida,
            r.NumeroHuespedes,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl,
            EnCurso = false
        });
        return Ok(result);
    }

    // Nuevo: historial completo de un cliente (todas sus reservas, m�s recientes primero)
    [HttpGet("cliente/{clienteId}/historial")]
    public async Task<IActionResult> HistorialCliente(string clienteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clienteId)) return BadRequest("clienteId requerido");
        var esMismoCliente = _current.UserId == clienteId;
        var esAdmin = User.IsInRole("Admin");
        if (!(esMismoCliente || esAdmin)) return Forbid();

        await ExpirarReservasPendientesDelClienteAsync(clienteId, ct);

        var items = await _db.Reservas
            .AsNoTracking()
            .Include(r => r.Alojamiento)
            .Where(r => r.ClienteId == clienteId)
            .OrderByDescending(r => r.FechaEntrada) // m�s actuales primero
            .ThenByDescending(r => r.FechaReserva)
            .ToListAsync(ct);

        var nombres = await MapearNombres(new[] { clienteId });
        var nombre = nombres.TryGetValue(clienteId, out var n) ? n : clienteId;

        var result = items.Select(r => new
        {
            r.Id,
            r.Folio,
            r.AlojamientoId,
            AlojamientoNombre = r.Alojamiento?.Nombre,
            r.ClienteId,
            Huesped = nombre,
            r.Estado,
            r.FechaEntrada,
            r.FechaSalida,
            r.NumeroHuespedes,
            r.Total,
            r.FechaReserva,
            r.ComprobanteUrl
        });
        return Ok(result);
    }

    private async Task ExpirarReservasPendientesDelClienteAsync(string? clienteId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clienteId)) return;

        const int minutosExpiracion = 30;
        var limite = DateTime.UtcNow.AddMinutes(-minutosExpiracion);

        var candidatas = await _db.Reservas
            .Where(r => r.ClienteId == clienteId && r.Estado == "Pendiente" && r.FechaReserva <= limite)
            .ToListAsync(ct);

        if (!candidatas.Any()) return;

        var idsReserva = candidatas.Select(r => r.Id).ToArray();
        var pagos = await _db.Pagos
            .Where(p => idsReserva.Contains(p.ReservaId))
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .ToListAsync(ct);

        var ultimoPagoPorReserva = pagos
            .GroupBy(p => p.ReservaId)
            .ToDictionary(g => g.Key, g => g.First());

        var cambios = false;
        foreach (var reserva in candidatas)
        {
            ultimoPagoPorReserva.TryGetValue(reserva.Id, out var pago);
            if (string.Equals(pago?.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase))
                continue;

            reserva.Estado = "Cancelada";
            cambios = true;

            if (pago is not null && !string.Equals(pago.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase))
            {
                pago.Estado = "Cancelado";
                pago.FechaActualizacion = DateTime.UtcNow;
            }
        }

        if (cambios)
            await _db.SaveChangesAsync(ct);
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

    private async Task ActualizarDemografiaDesdeReservaAsync(CrearReservaCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(_current.UserId)) return;

        var shouldUpdate = !string.IsNullOrWhiteSpace(cmd.Sexo)
            || cmd.FechaNacimiento.HasValue
            || !string.IsNullOrWhiteSpace(cmd.LugarOrigen);

        if (!shouldUpdate) return;

        var user = await _userManager.FindByIdAsync(_current.UserId);
        if (user is null) return;

        user.Sexo = cmd.Sexo ?? user.Sexo;
        user.FechaNacimiento = NormalizeUtc(cmd.FechaNacimiento) ?? user.FechaNacimiento;
        user.LugarOrigen = cmd.LugarOrigen ?? user.LugarOrigen;
        await _userManager.UpdateAsync(user);
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue) return null;

        var dt = value.Value;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }
}