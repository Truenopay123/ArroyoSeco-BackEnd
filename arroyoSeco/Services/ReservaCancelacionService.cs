using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using arroyoSeco.Application.Common.Interfaces;

namespace arroyoSeco.Services;

/// <summary>
/// Servicio en segundo plano que cancela automáticamente las reservas (alojamiento y gastronomía)
/// que llevan más de 48 horas en estado "Pendiente" sin comprobante de pago adjunto.
/// Se ejecuta cada hora.
/// </summary>
public class ReservaCancelacionService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReservaCancelacionService> _logger;
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    private static readonly TimeSpan LimitePago = TimeSpan.FromHours(48);

    public ReservaCancelacionService(IServiceProvider sp, ILogger<ReservaCancelacionService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CancelarReservasVencidasAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al cancelar reservas vencidas");
            }

            await Task.Delay(Intervalo, stoppingToken);
        }
    }

    private async Task CancelarReservasVencidasAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var limite = DateTime.UtcNow - LimitePago;

        // -- Alojamientos --
        var reservasAlojamiento = await db.Reservas
            .Where(r => r.Estado == "Pendiente"
                     && r.ComprobanteUrl == null
                     && r.FechaReserva < limite)
            .ToListAsync(ct);

        foreach (var r in reservasAlojamiento)
        {
            r.Estado = "Cancelada";
            _logger.LogInformation("Reserva alojamiento {Id} (Folio={Folio}) cancelada por falta de comprobante tras 48h", r.Id, r.Folio);
        }

        // -- Gastronomía --
        var reservasGastro = await db.ReservasGastronomia
            .Where(r => r.Estado == "Pendiente"
                     && r.ComprobanteUrl == null
                     && r.FechaReserva < limite)
            .ToListAsync(ct);

        foreach (var r in reservasGastro)
        {
            r.Estado = "Cancelada";
            _logger.LogInformation("Reserva gastronomia {Id} (Folio={Folio}) cancelada por falta de comprobante tras 48h", r.Id, r.Folio);
        }

        var total = reservasAlojamiento.Count + reservasGastro.Count;
        if (total > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Canceladas {Count} reservas por falta de pago en 48h", total);
        }
    }
}
