using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/pagos-gastronomia")]
public class PagosGastronomiaController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly ILogger<PagosGastronomiaController> _logger;
    private readonly ICurrentUserService _current;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;
    private readonly IStorageService _storage;

    public PagosGastronomiaController(
        IAppDbContext db,
        ILogger<PagosGastronomiaController> logger,
        ICurrentUserService current,
        UserManager<ApplicationUser> userManager,
        IEmailService email,
        IStorageService storage)
    {
        _db = db;
        _logger = logger;
        _current = current;
        _userManager = userManager;
        _email = email;
        _storage = storage;
    }

    // -- Obtener datos bancarios del oferente para una reserva --

    [Authorize(Roles = "Cliente,Admin")]
    [HttpGet("datos-bancarios/{reservaId}")]
    public async Task<IActionResult> ObtenerDatosBancarios(int reservaId)
    {
        var reserva = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == reservaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        var oferente = await _db.Oferentes.FindAsync(reserva.Establecimiento!.OferenteId);
        if (oferente is null) return NotFound(new { message = "Oferente no encontrado." });

        return Ok(new
        {
            titularCuenta = oferente.TitularCuenta,
            banco = oferente.Banco,
            numeroCuenta = oferente.NumeroCuenta,
            clabe = oferente.CLABE,
            monto = reserva.Total
        });
    }

    // -- Cliente envia comprobante de transferencia --

    [Authorize(Roles = "Cliente,Admin")]
    [HttpPost("enviar-comprobante")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> EnviarComprobante(
        [FromForm] int reservaId,
        [FromForm] decimal monto,
        IFormFile comprobante)
    {
        if (comprobante is null || comprobante.Length == 0)
            return BadRequest(new { message = "Debe adjuntar un comprobante." });

        var ext = Path.GetExtension(comprobante.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".pdf"))
            return BadRequest(new { message = "Solo se aceptan archivos JPG, PNG o PDF." });

        var reserva = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == reservaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        if (!string.Equals(reserva.Estado, "Pendiente", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "La reserva no esta en estado pendiente de pago." });

        using var stream = comprobante.OpenReadStream();
        var relativePath = await _storage.SaveFileAsync(stream, comprobante.FileName, "pagos-gastronomia", default);
        var publicUrl = _storage.GetPublicUrl(relativePath);

        var pago = new PagoGastronomia
        {
            ReservaGastronomiaId = reserva.Id,
            Monto = monto,
            MetodoPago = "Transferencia",
            ComprobanteUrl = publicUrl,
            Estado = "PendienteConfirmacion"
        };
        _db.PagosGastronomia.Add(pago);

        reserva.Estado = "PendienteConfirmacion";
        reserva.ComprobanteUrl = publicUrl;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Comprobante de pago gastronomia enviado. ReservaId={ReservaId}, PagoId={PagoId}", reserva.Id, pago.Id);

        return Ok(new
        {
            message = "Comprobante enviado. El oferente revisara tu pago.",
            pagoId = pago.Id,
            estado = pago.Estado
        });
    }

    // -- Oferente confirma o rechaza el pago --

    [Authorize(Roles = "Oferente,Admin")]
    [HttpPost("confirmar/{pagoId}")]
    public async Task<IActionResult> ConfirmarPago(int pagoId)
    {
        var pago = await _db.PagosGastronomia
            .Include(p => p.ReservaGastronomia)
                .ThenInclude(r => r!.Establecimiento)
            .FirstOrDefaultAsync(p => p.Id == pagoId);

        if (pago is null) return NotFound(new { message = "Pago no encontrado." });

        if (User.IsInRole("Oferente") && pago.ReservaGastronomia?.Establecimiento?.OferenteId != _current.UserId)
            return Forbid();

        if (!string.Equals(pago.Estado, "PendienteConfirmacion", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Este pago no esta pendiente de confirmacion." });

        pago.Estado = "Aprobado";
        pago.FechaActualizacion = DateTime.UtcNow;

        var reserva = pago.ReservaGastronomia!;
        reserva.Estado = "Confirmada";

        await _db.SaveChangesAsync();

        _logger.LogInformation("Pago gastronomia confirmado por oferente. PagoId={PagoId}, ReservaId={ReservaId}", pagoId, reserva.Id);

        await EnviarCorreoConfirmacionPagoAsync(reserva, pago);

        return Ok(new { message = "Pago confirmado. La reserva esta activa.", pagoId, estado = pago.Estado });
    }

    [Authorize(Roles = "Oferente,Admin")]
    [HttpPost("rechazar/{pagoId}")]
    public async Task<IActionResult> RechazarPago(int pagoId)
    {
        var pago = await _db.PagosGastronomia
            .Include(p => p.ReservaGastronomia)
                .ThenInclude(r => r!.Establecimiento)
            .FirstOrDefaultAsync(p => p.Id == pagoId);

        if (pago is null) return NotFound(new { message = "Pago no encontrado." });

        if (User.IsInRole("Oferente") && pago.ReservaGastronomia?.Establecimiento?.OferenteId != _current.UserId)
            return Forbid();

        if (!string.Equals(pago.Estado, "PendienteConfirmacion", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Este pago no esta pendiente de confirmacion." });

        pago.Estado = "Rechazado";
        pago.FechaActualizacion = DateTime.UtcNow;

        var reserva = pago.ReservaGastronomia!;
        reserva.Estado = "Pendiente";

        await _db.SaveChangesAsync();

        _logger.LogInformation("Pago gastronomia rechazado por oferente. PagoId={PagoId}, ReservaId={ReservaId}", pagoId, reserva.Id);

        return Ok(new { message = "Pago rechazado. La reserva vuelve a pendiente de pago.", pagoId, estado = pago.Estado });
    }

    // -- Obtener pagos de una reserva --

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("reserva/{reservaId}")]
    public async Task<IActionResult> GetPorReserva(int reservaId)
    {
        var reserva = await _db.ReservasGastronomia.Include(r => r.Establecimiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });

        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Establecimiento?.OferenteId != _current.UserId)
            return Forbid();

        var pagos = await _db.PagosGastronomia
            .Where(p => p.ReservaGastronomiaId == reservaId)
            .OrderByDescending(p => p.FechaCreacion)
            .ToListAsync();

        return Ok(pagos);
    }

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("reserva/{reservaId}/comprobante")]
    public async Task<IActionResult> Comprobante(int reservaId)
    {
        var reserva = await _db.ReservasGastronomia.Include(r => r.Establecimiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });

        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Establecimiento?.OferenteId != _current.UserId)
            return Forbid();

        var pago = await _db.PagosGastronomia
            .Where(p => p.ReservaGastronomiaId == reservaId)
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync();

        if (pago is null)
            return NotFound(new { message = "No existe comprobante de pago para esta reserva." });

        return Ok(new
        {
            pagoId = pago.Id,
            reservaId = pago.ReservaGastronomiaId,
            estado = pago.Estado,
            monto = pago.Monto,
            metodoPago = pago.MetodoPago,
            comprobanteUrl = pago.ComprobanteUrl,
            fechaCreacion = pago.FechaCreacion,
            fechaActualizacion = pago.FechaActualizacion
        });
    }

    // -- Oferente: listar reservas gastronomia con comprobante pendiente --

    [Authorize(Roles = "Oferente,Admin")]
    [HttpGet("pendientes-confirmacion")]
    public async Task<IActionResult> PendientesConfirmacion()
    {
        var query = _db.PagosGastronomia
            .Include(p => p.ReservaGastronomia)
                .ThenInclude(r => r!.Establecimiento)
            .Where(p => p.Estado == "PendienteConfirmacion");

        if (User.IsInRole("Oferente"))
            query = query.Where(p => p.ReservaGastronomia!.Establecimiento!.OferenteId == _current.UserId);

        var pagos = await query
            .OrderByDescending(p => p.FechaCreacion)
            .Select(p => new
            {
                pagoId = p.Id,
                reservaId = p.ReservaGastronomiaId,
                folio = p.ReservaGastronomia!.Folio,
                establecimiento = p.ReservaGastronomia.Establecimiento!.Nombre,
                monto = p.Monto,
                comprobanteUrl = p.ComprobanteUrl,
                fechaCreacion = p.FechaCreacion,
                fecha = p.ReservaGastronomia.Fecha,
                numeroPersonas = p.ReservaGastronomia.NumeroPersonas
            })
            .ToListAsync();

        return Ok(pagos);
    }

    // -- Helpers --

    private async Task EnviarCorreoConfirmacionPagoAsync(ReservaGastronomia reserva, PagoGastronomia pago)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(reserva.UsuarioId);
            var toEmail = user?.Email;
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var establecimientoNombre = reserva.Establecimiento?.Nombre ?? "Establecimiento";
            var fecha = reserva.Fecha.ToString("dd/MM/yyyy HH:mm");

            var html = $@"<h2>Pago confirmado - Gastronomia</h2>
<p>Tu pago por transferencia fue confirmado y tu reserva quedo activa.</p>
<ul>
  <li><strong>Folio:</strong> {reserva.Folio}</li>
  <li><strong>Establecimiento:</strong> {establecimientoNombre}</li>
  <li><strong>Fecha:</strong> {fecha}</li>
  <li><strong>Personas:</strong> {reserva.NumeroPersonas}</li>
  <li><strong>Total pagado:</strong> ${pago.Monto:N2}</li>
  <li><strong>Estado:</strong> Confirmada</li>
</ul>
<p>Gracias por reservar en Arroyo Seco.</p>";

            await _email.SendEmailAsync(toEmail, $"Reserva gastronomia confirmada - {reserva.Folio}", html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando correo confirmacion pago gastronomia. ReservaId={Id}", pago.ReservaGastronomiaId);
        }
    }
}
