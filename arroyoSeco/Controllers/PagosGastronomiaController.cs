using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Domain.Entities.Usuarios;
using MercadoPago.Config;
using MercadoPago.Client.Preference;
using MercadoPago.Resource.Preference;
using MercadoPago.Client.Payment;
using MercadoPago.Error;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/pagos-gastronomia")]
public class PagosGastronomiaController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PagosGastronomiaController> _logger;
    private readonly ICurrentUserService _current;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;

    public PagosGastronomiaController(
        IAppDbContext db,
        IConfiguration config,
        ILogger<PagosGastronomiaController> logger,
        ICurrentUserService current,
        UserManager<ApplicationUser> userManager,
        IEmailService email)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _current = current;
        _userManager = userManager;
        _email = email;
    }

    public record CrearPreferenciaGastronomiaDto(int ReservaGastronomiaId);

    // ── Crear preferencia de Mercado Pago ─────────────────────────────────

    [Authorize(Roles = "Cliente,Admin")]
    [HttpPost("crear-preferencia")]
    public async Task<IActionResult> CrearPreferencia([FromBody] CrearPreferenciaGastronomiaDto dto)
    {
        var reserva = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == dto.ReservaGastronomiaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        if (await CancelarReservaSiPendienteExpiradaAsync(reserva))
        {
            return Conflict(new
            {
                message = "La reserva pendiente expiró por falta de pago. Crea una nueva reserva para continuar."
            });
        }

        var accessToken = _config["MercadoPago:AccessToken"]
            ?? Environment.GetEnvironmentVariable("MP_ACCESS_TOKEN");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await CancelarReservaPendienteAsync(reserva, "No se pudo crear preferencia: AccessToken faltante.");
            _logger.LogError("MercadoPago AccessToken no configurado. ReservaGastronomiaId={Id}", dto.ReservaGastronomiaId);
            return StatusCode(500, new
            {
                message = "No se pudo iniciar el pago porque Mercado Pago no esta configurado (AccessToken faltante)."
            });
        }

        if (!accessToken.StartsWith("TEST-", StringComparison.OrdinalIgnoreCase)
            && !accessToken.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
        {
            await CancelarReservaPendienteAsync(reserva, "No se pudo crear preferencia: formato de AccessToken inválido.");
            _logger.LogError("MercadoPago AccessToken con formato invalido. ReservaGastronomiaId={Id}", dto.ReservaGastronomiaId);
            return StatusCode(500, new
            {
                message = "No se pudo iniciar el pago porque el AccessToken de Mercado Pago tiene formato invalido."
            });
        }

        try
        {
            MercadoPagoConfig.AccessToken = accessToken;

            var frontendBaseRaw = _config["AppUrls:FrontendBaseUrl"];
            if (string.IsNullOrWhiteSpace(frontendBaseRaw))
                frontendBaseRaw = Environment.GetEnvironmentVariable("APP_FRONTEND_BASE_URL");

            var frontendBase = string.IsNullOrWhiteSpace(frontendBaseRaw)
                ? "http://localhost:4200"
                : frontendBaseRaw.Trim().TrimEnd('/');

            if (!Uri.TryCreate(frontendBase, UriKind.Absolute, out var frontendUri)
                || (frontendUri.Scheme != Uri.UriSchemeHttp && frontendUri.Scheme != Uri.UriSchemeHttps))
            {
                await CancelarReservaPendienteAsync(reserva, "No se pudo crear preferencia: FrontendBaseUrl inválida.");
                return StatusCode(500, new { message = "FrontendBaseUrl no es una URL válida." });
            }

            var backendBaseRaw = _config["AppUrls:BackendBaseUrl"];
            var backendBase = string.IsNullOrWhiteSpace(backendBaseRaw)
                ? $"{Request.Scheme}://{Request.Host}"
                : backendBaseRaw.Trim().TrimEnd('/');

            if (!Uri.TryCreate(backendBase, UriKind.Absolute, out var backendUri)
                || (backendUri.Scheme != Uri.UriSchemeHttp && backendUri.Scheme != Uri.UriSchemeHttps))
            {
                await CancelarReservaPendienteAsync(reserva, "No se pudo crear preferencia: BackendBaseUrl inválida.");
                return StatusCode(500, new { message = "BackendBaseUrl no es una URL válida." });
            }

            var successUrl = $"{frontendBase}/cliente/pagos-gastronomia/resultado?estado=aprobado&reservaId={reserva.Id}";
            var failureUrl = $"{frontendBase}/cliente/pagos-gastronomia/resultado?estado=rechazado&reservaId={reserva.Id}";
            var pendingUrl = $"{frontendBase}/cliente/pagos-gastronomia/resultado?estado=pendiente&reservaId={reserva.Id}";

            var client = new PreferenceClient();
            var unitPrice = Math.Round(reserva.Total, 2);

            var item = new PreferenceItemRequest
            {
                Title = $"Reserva Gastronomía {reserva.Establecimiento?.Nombre ?? "Establecimiento"} - {reserva.Folio}",
                Quantity = 1,
                UnitPrice = unitPrice
            };

            var currencyIdRaw = _config["MercadoPago:CurrencyId"];
            var currencyId = string.IsNullOrWhiteSpace(currencyIdRaw) ? null : currencyIdRaw.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(currencyId))
                item.CurrencyId = currencyId;

            var comprador = await _userManager.FindByIdAsync(reserva.UsuarioId);
            var payerEmail = comprador?.Email;

            var request = new PreferenceRequest
            {
                Items = new List<PreferenceItemRequest> { item },
                ExternalReference = $"G-{reserva.Id}",
                BackUrls = new PreferenceBackUrlsRequest
                {
                    Success = successUrl,
                    Failure = failureUrl,
                    Pending = pendingUrl
                },
                NotificationUrl = $"{backendBase}/api/pagos-gastronomia/webhook"
            };

            if (!string.IsNullOrWhiteSpace(payerEmail))
                request.Payer = new PreferencePayerRequest { Email = payerEmail };

            var canUseAutoReturn = frontendUri.Scheme == Uri.UriSchemeHttps && !frontendUri.IsLoopback;
            if (canUseAutoReturn)
                request.AutoReturn = "approved";

            Preference preference = await client.CreateAsync(request);

            var pago = new PagoGastronomia
            {
                ReservaGastronomiaId = reserva.Id,
                MercadoPagoPreferenceId = preference.Id,
                Monto = reserva.Total,
                Estado = "Pendiente"
            };
            _db.PagosGastronomia.Add(pago);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                preferenceId = preference.Id,
                initPoint = preference.InitPoint,
                sandboxInitPoint = preference.SandboxInitPoint
            });
        }
        catch (MercadoPagoApiException ex)
        {
            await CancelarReservaPendienteAsync(reserva, "Mercado Pago rechazó la preferencia.");
            var apiMessage = ex.ApiError?.Message ?? ex.Message;
            _logger.LogError(ex, "Error API Mercado Pago. ReservaGastronomiaId={Id}", dto.ReservaGastronomiaId);
            return StatusCode(502, new { message = $"Mercado Pago rechazó la preferencia: {apiMessage}" });
        }
        catch (Exception ex)
        {
            await CancelarReservaPendienteAsync(reserva, "Excepción al crear preferencia de pago.");
            _logger.LogError(ex, "Error creando preferencia MP. ReservaGastronomiaId={Id}", dto.ReservaGastronomiaId);
            return StatusCode(502, new { message = "No se pudo iniciar el pago con Mercado Pago.", detail = ex.Message });
        }
    }

    // ── Webhook de Mercado Pago ───────────────────────────────────────────

    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            _logger.LogInformation("MP Gastronomía Webhook recibido: {body}", body);

            if (!EsWebhookValido(body))
            {
                _logger.LogWarning("Webhook MP gastronomía rechazado por firma inválida.");
                return Unauthorized();
            }

            var tipo = Request.Query["type"].ToString();
            var topicLegacy = Request.Query["topic"].ToString();

            if (tipo == "payment" || topicLegacy == "payment")
            {
                string? paymentIdStr = null;

                if (tipo == "payment")
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    paymentIdStr = doc.RootElement.GetProperty("data").GetProperty("id").GetString();
                }
                else
                {
                    paymentIdStr = Request.Query["id"].ToString();
                }

                if (!string.IsNullOrEmpty(paymentIdStr))
                    await ProcesarPago(paymentIdStr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando webhook MP gastronomía");
        }

        return Ok();
    }

    // ── Obtener pagos de una reserva ──────────────────────────────────────

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
            mercadoPagoPaymentId = pago.MercadoPagoPaymentId,
            mercadoPagoPreferenceId = pago.MercadoPagoPreferenceId,
            fechaCreacion = pago.FechaCreacion,
            fechaActualizacion = pago.FechaActualizacion
        });
    }

    // ── Resultado del pago ────────────────────────────────────────────────

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("resultado")]
    public async Task<IActionResult> Resultado([FromQuery] int reservaId, [FromQuery] string estado)
    {
        var reserva = await _db.ReservasGastronomia.Include(r => r.Establecimiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound();

        if (User.IsInRole("Cliente") && reserva.UsuarioId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Establecimiento?.OferenteId != _current.UserId)
            return Forbid();

        if (await CancelarReservaSiPendienteExpiradaAsync(reserva))
            estado = "expirado";

        var estadoNorm = (estado ?? string.Empty).Trim().ToLowerInvariant();
        var retornoRechazado = estadoNorm.Contains("rechaz") || estadoNorm.Contains("rejected") || estadoNorm.Contains("cancel");

        var pago = await _db.PagosGastronomia
            .Where(p => p.ReservaGastronomiaId == reservaId)
            .OrderByDescending(p => p.FechaCreacion)
            .FirstOrDefaultAsync();

        if (retornoRechazado && pago is not null && !string.Equals(pago.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase))
        {
            pago.Estado = estadoNorm.Contains("cancel") ? "Cancelado" : "Rechazado";
            pago.FechaActualizacion = DateTime.UtcNow;

            if (string.Equals(reserva.Estado, "Pendiente", StringComparison.OrdinalIgnoreCase))
                reserva.Estado = "Cancelada";

            await _db.SaveChangesAsync();
        }

        return Ok(new
        {
            reservaId,
            estado,
            folio = reserva.Folio,
            total = reserva.Total,
            pagoEstado = pago?.Estado
        });
    }

    // ── Helper: consultar y actualizar pago en MP ─────────────────────────

    private async Task ProcesarPago(string mpPaymentId)
    {
        var accessToken = _config["MercadoPago:AccessToken"]
            ?? Environment.GetEnvironmentVariable("MP_ACCESS_TOKEN")
            ?? string.Empty;

        if (string.IsNullOrEmpty(accessToken)) return;

        MercadoPagoConfig.AccessToken = accessToken;

        var paymentClient = new PaymentClient();
        var mpPayment = await paymentClient.GetAsync(long.Parse(mpPaymentId));
        if (mpPayment is null) return;

        PagoGastronomia? pago = await _db.PagosGastronomia
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync(p => p.MercadoPagoPaymentId == mpPaymentId);

        if (pago is null && mpPayment.ExternalReference is not null
            && mpPayment.ExternalReference.StartsWith("G-")
            && int.TryParse(mpPayment.ExternalReference[2..], out var rId))
        {
            pago = await _db.PagosGastronomia
                .Where(p => p.ReservaGastronomiaId == rId)
                .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
                .FirstOrDefaultAsync();
        }

        if (pago is null) return;

        var estadoAnteriorPago = pago.Estado;

        pago.MercadoPagoPaymentId = mpPaymentId;
        pago.MetodoPago = mpPayment.PaymentMethodId;
        pago.FechaActualizacion = DateTime.UtcNow;

        pago.Estado = mpPayment.Status switch
        {
            "approved" => "Aprobado",
            "rejected" => "Rechazado",
            "cancelled" => "Cancelado",
            _ => "Pendiente"
        };

        var reserva = await _db.ReservasGastronomia
            .Include(r => r.Establecimiento)
            .FirstOrDefaultAsync(r => r.Id == pago.ReservaGastronomiaId);

        if (reserva is not null)
        {
            reserva.Estado = pago.Estado switch
            {
                "Aprobado" => "Confirmada",
                "Rechazado" => "Cancelada",
                "Cancelado" => "Cancelada",
                _ => reserva.Estado
            };
        }

        await _db.SaveChangesAsync();

        var pasoAAprobado = !string.Equals(estadoAnteriorPago, "Aprobado", StringComparison.OrdinalIgnoreCase)
            && string.Equals(pago.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase);

        if (pasoAAprobado && reserva is not null)
            await EnviarCorreoConfirmacionPagoAsync(reserva, pago);
    }

    private async Task EnviarCorreoConfirmacionPagoAsync(ReservaGastronomia reserva, PagoGastronomia pago)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(reserva.UsuarioId);
            var toEmail = user?.Email;
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var establecimientoNombre = reserva.Establecimiento?.Nombre ?? "Establecimiento";
            var fecha = reserva.Fecha.ToString("dd/MM/yyyy HH:mm");

            var html = $@"<h2>Pago confirmado - Gastronomía</h2>
<p>Tu pago fue aprobado y tu reserva quedó confirmada.</p>
<ul>
  <li><strong>Folio:</strong> {reserva.Folio}</li>
  <li><strong>Establecimiento:</strong> {establecimientoNombre}</li>
  <li><strong>Fecha:</strong> {fecha}</li>
  <li><strong>Personas:</strong> {reserva.NumeroPersonas}</li>
  <li><strong>Total pagado:</strong> ${pago.Monto:N2}</li>
  <li><strong>Estado:</strong> Confirmada</li>
</ul>
<p>Gracias por reservar en Arroyo Seco.</p>";

            await _email.SendEmailAsync(toEmail, $"Reserva gastronomía confirmada - {reserva.Folio}", html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando correo confirmación pago gastronomía. ReservaId={Id}", pago.ReservaGastronomiaId);
        }
    }

    private bool EsWebhookValido(string body)
    {
        var secret = _config["MercadoPago:WebhookSecret"]
            ?? Environment.GetEnvironmentVariable("MP_WEBHOOK_SECRET");

        if (string.IsNullOrWhiteSpace(secret)) return true;

        var signature = Request.Headers["x-signature"].ToString();
        if (string.IsNullOrWhiteSpace(signature)) return false;

        var v1 = signature.Split(',')
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.StartsWith("v1=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=')[1];

        if (string.IsNullOrWhiteSpace(v1)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        return string.Equals(computed, v1, StringComparison.OrdinalIgnoreCase);
    }

    private int GetReservaPendienteExpiracionMinutos()
    {
        var raw = _config["MercadoPago:PendingReservationExpirationMinutes"];
        return int.TryParse(raw, out var minutes) && minutes > 0 ? minutes : 30;
    }

    private async Task<bool> CancelarReservaSiPendienteExpiradaAsync(ReservaGastronomia reserva)
    {
        if (!string.Equals(reserva.Estado, "Pendiente", StringComparison.OrdinalIgnoreCase))
            return false;

        var minutos = GetReservaPendienteExpiracionMinutos();
        if (DateTime.UtcNow < reserva.FechaReserva.AddMinutes(minutos))
            return false;

        var ultimoPago = await _db.PagosGastronomia
            .Where(p => p.ReservaGastronomiaId == reserva.Id)
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync();

        if (string.Equals(ultimoPago?.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase))
            return false;

        await CancelarReservaPendienteAsync(reserva, $"Reserva pendiente expirada tras {minutos} minutos sin pago aprobado.");
        return true;
    }

    private async Task CancelarReservaPendienteAsync(ReservaGastronomia reserva, string motivo)
    {
        if (!string.Equals(reserva.Estado, "Pendiente", StringComparison.OrdinalIgnoreCase))
            return;

        reserva.Estado = "Cancelada";

        var ultimoPago = await _db.PagosGastronomia
            .Where(p => p.ReservaGastronomiaId == reserva.Id)
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync();

        if (ultimoPago is not null && !string.Equals(ultimoPago.Estado, "Aprobado", StringComparison.OrdinalIgnoreCase))
        {
            ultimoPago.Estado = "Cancelado";
            ultimoPago.FechaActualizacion = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("Reserva gastronomía cancelada automáticamente. ReservaId={Id}. Motivo={Motivo}", reserva.Id, motivo);
    }
}
