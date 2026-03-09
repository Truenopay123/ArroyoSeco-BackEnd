using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Pagos;
using MercadoPago.Config;
using MercadoPago.Client.Preference;
using MercadoPago.Resource.Preference;
using MercadoPago.Client.Payment;
using MercadoPago.Error;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PagosController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PagosController> _logger;
    private readonly ICurrentUserService _current;

    public PagosController(IAppDbContext db, IConfiguration config, ILogger<PagosController> logger, ICurrentUserService current)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _current = current;
    }

    public record CrearPreferenciaDto(int ReservaId);

    // ── Crear preferencia de Mercado Pago ─────────────────────────────────

    [Authorize(Roles = "Cliente,Admin")]
    [HttpPost("crear-preferencia")]
    public async Task<IActionResult> CrearPreferencia([FromBody] CrearPreferenciaDto dto)
    {
        var reserva = await _db.Reservas
            .Include(r => r.Alojamiento)
            .FirstOrDefaultAsync(r => r.Id == dto.ReservaId);

        if (reserva is null) return NotFound(new { message = "Reserva no encontrada." });
        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        var accessToken = _config["MercadoPago:AccessToken"]
            ?? Environment.GetEnvironmentVariable("MP_ACCESS_TOKEN");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogError("MercadoPago AccessToken no configurado. ReservaId={ReservaId}", dto.ReservaId);
            return StatusCode(500, new
            {
                message = "No se pudo iniciar el pago porque Mercado Pago no esta configurado (AccessToken faltante)."
            });
        }

        if (!accessToken.StartsWith("TEST-", StringComparison.OrdinalIgnoreCase)
            && !accessToken.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("MercadoPago AccessToken con formato invalido. ReservaId={ReservaId}", dto.ReservaId);
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
                _logger.LogError("AppUrls:FrontendBaseUrl invalido para Mercado Pago: {FrontendBaseUrl}", frontendBase);
                return StatusCode(500, new
                {
                    message = "No se pudo iniciar el pago porque AppUrls:FrontendBaseUrl no es una URL valida (http/https)."
                });
            }

            var backendBaseRaw = _config["AppUrls:BackendBaseUrl"];
            var backendBase = string.IsNullOrWhiteSpace(backendBaseRaw)
                ? $"{Request.Scheme}://{Request.Host}"
                : backendBaseRaw.Trim().TrimEnd('/');

            if (!Uri.TryCreate(backendBase, UriKind.Absolute, out var backendUri)
                || (backendUri.Scheme != Uri.UriSchemeHttp && backendUri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.LogError("AppUrls:BackendBaseUrl invalido para Mercado Pago: {BackendBaseUrl}", backendBase);
                return StatusCode(500, new
                {
                    message = "No se pudo iniciar el pago porque AppUrls:BackendBaseUrl no es una URL valida (http/https)."
                });
            }

            var successUrl = $"{frontendBase}/cliente/pagos/resultado?estado=aprobado&reservaId={reserva.Id}";
            var failureUrl = $"{frontendBase}/cliente/pagos/resultado?estado=rechazado&reservaId={reserva.Id}";
            var pendingUrl = $"{frontendBase}/cliente/pagos/resultado?estado=pendiente&reservaId={reserva.Id}";

            // Mercado Pago exige back_urls con success absoluto cuando AutoReturn = approved.
            if (!Uri.TryCreate(successUrl, UriKind.Absolute, out _))
            {
                _logger.LogError("URL success invalida para Mercado Pago: {SuccessUrl}", successUrl);
                return StatusCode(500, new
                {
                    message = "No se pudo iniciar el pago porque la URL de retorno success es invalida."
                });
            }

            var client = new PreferenceClient();
            var nights = (int)(reserva.FechaSalida - reserva.FechaEntrada).TotalDays;
            nights = Math.Max(nights, 1);

            var request = new PreferenceRequest
            {
                Items = new List<PreferenceItemRequest>
                {
                    new PreferenceItemRequest
                    {
                        Title = $"Reserva {reserva.Alojamiento?.Nombre ?? "Alojamiento"} - {reserva.Folio}",
                        Quantity = nights,
                        UnitPrice = reserva.Total / nights,
                        CurrencyId = "ARS"
                    }
                },
                ExternalReference = reserva.Id.ToString(),
                BackUrls = new PreferenceBackUrlsRequest
                {
                    Success = successUrl,
                    Failure = failureUrl,
                    Pending = pendingUrl
                },
                NotificationUrl = $"{backendBase}/api/pagos/webhook"
            };

            Preference preference = await client.CreateAsync(request);

            var pago = new Pago
            {
                ReservaId = reserva.Id,
                MercadoPagoPreferenceId = preference.Id,
                Monto = reserva.Total,
                Estado = "Pendiente"
            };
            _db.Pagos.Add(pago);
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
            _logger.LogError(ex,
                "Error API Mercado Pago al crear preferencia. ReservaId={ReservaId}. ApiMessage={ApiMessage}",
                dto.ReservaId,
                ex.ApiError?.Message);

            return StatusCode(502, new
            {
                message = "Mercado Pago rechazo la preferencia. Revisa back_urls y credenciales de la cuenta.",
                detail = ex.ApiError?.Message ?? ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creando preferencia de Mercado Pago. ReservaId={ReservaId}", dto.ReservaId);
            return StatusCode(502, new
            {
                message = "No se pudo iniciar el pago con Mercado Pago. Verifica AccessToken, credenciales de prueba y estado de la cuenta.",
                detail = ex.Message
            });
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
            _logger.LogInformation("MP Webhook recibido: {body}", body);

            if (!EsWebhookValido(body))
            {
                _logger.LogWarning("Webhook MP rechazado por firma inválida.");
                return Unauthorized();
            }

            var tipo    = Request.Query["type"].ToString();
            var topicLegacy = Request.Query["topic"].ToString();

            // Notificaciones de tipo "payment" (IPN moderno) o "payment" (legacy topic)
            if (tipo == "payment" || topicLegacy == "payment")
            {
                string? paymentIdStr = null;

                if (tipo == "payment")
                {
                    // Nuevo formato: {"type":"payment","data":{"id":"xxx"}}
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    paymentIdStr = doc.RootElement
                        .GetProperty("data")
                        .GetProperty("id")
                        .GetString();
                }
                else
                {
                    // Formato legacy: query param "id"
                    paymentIdStr = Request.Query["id"].ToString();
                }

                if (!string.IsNullOrEmpty(paymentIdStr))
                    await ProcesarPago(paymentIdStr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando webhook MP");
        }

        return Ok(); // Responder 200 siempre para que MP no reintente
    }

    // ── Obtener pagos de una reserva ──────────────────────────────────────

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
            mercadoPagoPaymentId = pago.MercadoPagoPaymentId,
            mercadoPagoPreferenceId = pago.MercadoPagoPreferenceId,
            fechaCreacion = pago.FechaCreacion,
            fechaActualizacion = pago.FechaActualizacion
        });
    }

    // ── Resultado del pago (retorno desde MP) ─────────────────────────────

    [Authorize(Roles = "Admin,Oferente,Cliente")]
    [HttpGet("resultado")]
    public async Task<IActionResult> Resultado([FromQuery] int reservaId, [FromQuery] string estado)
    {
        var reserva = await _db.Reservas.Include(r => r.Alojamiento).FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva is null) return NotFound();

        if (User.IsInRole("Cliente") && reserva.ClienteId != _current.UserId)
            return Forbid();

        if (User.IsInRole("Oferente") && reserva.Alojamiento?.OferenteId != _current.UserId)
            return Forbid();

        var pago = await _db.Pagos
            .Where(p => p.ReservaId == reservaId)
            .OrderByDescending(p => p.FechaCreacion)
            .FirstOrDefaultAsync();

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

        // SDK actual no expone Preference en Payment. Resolvemos por paymentId y/o external_reference (reservaId).
        Pago? pago = await _db.Pagos
            .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
            .FirstOrDefaultAsync(p => p.MercadoPagoPaymentId == mpPaymentId);

        if (pago is null && mpPayment.ExternalReference is not null
            && int.TryParse(mpPayment.ExternalReference, out var rId))
        {
            pago = await _db.Pagos
                .Where(p => p.ReservaId == rId)
                .OrderByDescending(p => p.FechaActualizacion ?? p.FechaCreacion)
                .FirstOrDefaultAsync();
        }

        if (pago is null) return;

        pago.MercadoPagoPaymentId = mpPaymentId;
        pago.MetodoPago = mpPayment.PaymentMethodId;
        pago.FechaActualizacion = DateTime.UtcNow;

        pago.Estado = mpPayment.Status switch
        {
            "approved" => "Aprobado",
            "rejected"  => "Rechazado",
            "cancelled" => "Cancelado",
            _ => "Pendiente"
        };

        // Actualizar estado de la reserva
        var reserva = await _db.Reservas.FindAsync(pago.ReservaId);
        if (reserva is not null)
        {
            reserva.Estado = pago.Estado switch
            {
                "Aprobado" => "Confirmada",
                "Rechazado" => "Pendiente",
                "Cancelado" => "Cancelada",
                _ => reserva.Estado
            };
        }

        await _db.SaveChangesAsync();
    }

    private bool EsWebhookValido(string body)
    {
        var secret = _config["MercadoPago:WebhookSecret"]
            ?? Environment.GetEnvironmentVariable("MP_WEBHOOK_SECRET");

        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("MercadoPago WebhookSecret no configurado. Se omite validación de firma.");
            return true;
        }

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
}
