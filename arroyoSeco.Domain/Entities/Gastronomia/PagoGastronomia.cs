namespace arroyoSeco.Domain.Entities.Gastronomia;

public class PagoGastronomia
{
    public int Id { get; set; }
    public int ReservaGastronomiaId { get; set; }
    public ReservaGastronomia? ReservaGastronomia { get; set; }
    public string Estado { get; set; } = "Pendiente";   // Pendiente, PendienteConfirmacion, Aprobado, Rechazado, Cancelado
    public decimal Monto { get; set; }
    public string? MetodoPago { get; set; }
    public string? ComprobanteUrl { get; set; }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public DateTime? FechaActualizacion { get; set; }
}
