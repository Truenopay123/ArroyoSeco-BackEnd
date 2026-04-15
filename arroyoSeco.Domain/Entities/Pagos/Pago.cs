using arroyoSeco.Domain.Entities.Alojamientos;

namespace arroyoSeco.Domain.Entities.Pagos;

public class Pago
{
    public int Id { get; set; }
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    public string Estado { get; set; } = "Pendiente";   // Pendiente, PendienteConfirmacion, Aprobado, Rechazado, Cancelado
    public decimal Monto { get; set; }
    public string? MetodoPago { get; set; }
    public string? ComprobanteUrl { get; set; }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public DateTime? FechaActualizacion { get; set; }
}
