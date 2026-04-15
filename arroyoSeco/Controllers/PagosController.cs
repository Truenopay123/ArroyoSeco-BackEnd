using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Domain.Entities.Pagos;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PagosController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly ILogger<PagosController> _logger;
    private readonly ICurrentUserService _current;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;
    private readonly IStorageService _storage;

    public PagosController(
        IAppDbContext db,
        ILogger<PagosController> logger,
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
        var reserva = await _db.Reservas
            .Include(r => r.Alojamiento)
            .FirstOrDefaultAsync(r => r.Id == reservaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        var oferente = await _db.Oferentes.FindAsync(reserva.Alojamiento!.OferenteId);
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

        var reserva = await _db.Reservas
            .Include(r => r.Alojamiento)
            .FirstOrDefaultAsync(r => r.Id == reservaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        if (!string.Equals(reserva.Estado, "Pendiente", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "La reserva no esta en estado pendiente de pago." });

        using var stream = comprobante.OpenReadStream();
        var relativePath = await _storage.SaveFileAsync(stream, comprobante.FileName, "pagos", default);
        var publicUrl = _storage.GetPublicUrl(relativePath);

        var pago = new Pago
        {
            ReservaId = reserva.Id,
            Monto = monto,
            MetodoPago = "Transferencia",
            ComprobanteUrl = publicUrl,
            Estado = "PendienteConfirmacion"
        };
        _db.Pagos.Add(pago);

        reserva.Estado = "PendienteConfirmacion";
        reserva.ComprobanteUrl = publicUrl;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Comprobante de pago enviado. ReservaId={ReservaId}, PagoId={PagoId}", reserva.Id, pago.Id);

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
        var pago = await _db.Pagos
            .Include(p => p.Reserva)
                .ThenInclude(r => r!.Alojamiento)
            .FirstOrDefaultAsync(p => p.Id == pagoId);

        if (pago is null) return NotFound(new { message = "Pago no encontrado." });

        if (User.IsInRole("Oferente") && pago.Reserva?.Alojamiento?.OferenteId != _current.UserId)
            return Forbid();

        if (!string.Equals(pago.Estado, "PendienteConfirmacion", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Este pago no esta pendiente de confirmacion." });

        pago.Estado = "Aprobado";
        pago.FechaActualizacion = DateTime.UtcNow;

        var reserva = pago.Reserva!;
        reserva.Estado = "Confirmada";

        await _db.SaveChangesAsync();

        _logger.LogInformation("Pago confirmado por oferente. PagoId={PagoId}, ReservaId={ReservaId}", pagoId, reserva.Id);

        await EnviarCorreoConfirmacionPagoAsync(reserva, pago);

        return Ok(new { message = "Pago confirmado. La reserva esta activa.", pagoId, estado = pago.Estado });
    }

    [Authorize(Roles = "Oferente,Admin")]
    [HttpPost("rechazar/{pagoId}")]
    public async Task<IActionResult> RechazarPago(int pagoId)
    {
        var pago = await _db.Pagos
            .Include(p => p.Reserva)
                .ThenInclude(r => r!.Alojamiento)
            .FirstOrDefaultAsync(p => p.Id == pagoId);

        if (pago is null) return NotFound(new { message = "Pago no encontrado." });

        if (User.IsInRole("Oferente") && pago.Reserva?.Alojamiento?.OferenteId != _current.UserId)
            return Forbid();

        if (!string.Equals(pago.Estado, "PendienteConfirmacion", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Este pago no esta pendiente de confirmacion." });

        pago.Estado = "Rechazado";
        pago.FechaActualizacion = DateTime.UtcNow;

        var reserva = pago.Reserva!;
        reserva.Estado = "Pendiente";

        await _db.SaveChangesAsync();

        _logger.LogInformation("Pago rechazado por oferente. PagoId={PagoId}, ReservaId={ReservaId}", pagoId, reserva.Id);

        return Ok(new { message = "Pago rechazado. La reserva vuelve a pendiente de pago.", pagoId, estado = pago.Estado });
    }

    // -- Obtener pagos de una reserva --

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("reserva/{reservaId}")]
    public async Task<IActionResult> GetPorReserva(int reservaId)
    {
        var reserva = await _db.Reservas.Include(r => r.Alojamiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });

        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Alojamiento?.OferenteId != _current.UserId)
            return Forbid();

        var pagos = await _db.Pagos
            .Where(p => p.ReservaId == reservaId)
            .OrderByDescending(p => p.FechaCreacion)
            .ToListAsync();

        return Ok(pagos);
    }

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("reserva/{reservaId}/comprobante")]
    public async Task<IActionResult> Comprobante(int reservaId)
    {
        var reserva = await _db.Reservas.Include(r => r.Alojamiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });

        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Alojamiento?.OferenteId != _current.UserId)
            return Forbid();

        var pago = await _db.Pagos
            .Where(p => p.ReservaId == reservaId)
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync();

        if (pago is null)
            return NotFound(new { message = "No existe comprobante de pago para esta reserva." });

        return Ok(new
        {
            pagoId = pago.Id,
            reservaId = pago.ReservaId,
            estado = pago.Estado,
            monto = pago.Monto,
            metodoPago = pago.MetodoPago,
            comprobanteUrl = pago.ComprobanteUrl,
            fechaCreacion = pago.FechaCreacion,
            fechaActualizacion = pago.FechaActualizacion
        });
    }

    // -- Oferente: listar reservas con comprobante pendiente --

    [Authorize(Roles = "Oferente,Admin")]
    [HttpGet("pendientes-confirmacion")]
    public async Task<IActionResult> PendientesConfirmacion()
    {
        var query = _db.Pagos
            .Include(p => p.Reserva)
                .ThenInclude(r => r!.Alojamiento)
            .Where(p => p.Estado == "PendienteConfirmacion");

        if (User.IsInRole("Oferente"))
            query = query.Where(p => p.Reserva!.Alojamiento!.OferenteId == _current.UserId);

        var pagos = await query
            .OrderByDescending(p => p.FechaCreacion)
            .Select(p => new
            {
                pagoId = p.Id,
                reservaId = p.ReservaId,
                folio = p.Reserva!.Folio,
                alojamiento = p.Reserva.Alojamiento!.Nombre,
                monto = p.Monto,
                comprobanteUrl = p.ComprobanteUrl,
                fechaCreacion = p.FechaCreacion,
                fechaEntrada = p.Reserva.FechaEntrada,
                fechaSalida = p.Reserva.FechaSalida
            })
            .ToListAsync();

        return Ok(pagos);
    }

    // -- Helpers --

    private async Task EnviarCorreoConfirmacionPagoAsync(Reserva reserva, Pago pago)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(reserva.ClienteId);
            var toEmail = user?.Email;
            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogWarning("No se encontro email del cliente para enviar confirmacion. ReservaId={ReservaId}", pago.ReservaId);
                return;
            }

            var alojamientoNombre = reserva.Alojamiento?.Nombre ?? "Alojamiento";
            var fechaEntrada = reserva.FechaEntrada.ToString("dd/MM/yyyy");
            var fechaSalida = reserva.FechaSalida.ToString("dd/MM/yyyy");

            var html = $@"<h2>Pago confirmado</h2>
<p>Tu pago por transferencia fue confirmado y tu reserva quedo activa.</p>
<ul>
  <li><strong>Folio:</strong> {reserva.Folio}</li>
  <li><strong>Alojamiento:</strong> {alojamientoNombre}</li>
  <li><strong>Check-in:</strong> {fechaEntrada}</li>
  <li><strong>Check-out:</strong> {fechaSalida}</li>
  <li><strong>Total pagado:</strong> ${pago.Monto:N2}</li>
  <li><strong>Estado:</strong> Confirmada</li>
</ul>
<p>Gracias por reservar en Arroyo Seco.</p>";

            await _email.SendEmailAsync(toEmail, $"Reserva confirmada - {reserva.Folio}", html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando correo de confirmacion de pago. ReservaId={ReservaId}", pago.ReservaId);
        }
    }
}
