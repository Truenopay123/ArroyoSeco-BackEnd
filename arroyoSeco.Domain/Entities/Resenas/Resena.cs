using arroyoSeco.Domain.Entities.Alojamientos;

namespace arroyoSeco.Domain.Entities.Resenas;

public class Resena
{
    public int Id { get; set; }
    public int AlojamientoId { get; set; }
    public Alojamiento? Alojamiento { get; set; }
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    public string ClienteId { get; set; } = string.Empty;   // IdentityUser.Id (quien escribió la reseña)
    public int Calificacion { get; set; }                    // 1–5
    public string Comentario { get; set; } = string.Empty;
    
    // Estados: "publicada" (visible), "reportada" (bajo revisión), "eliminada" (no visible)
    public string Estado { get; set; } = "publicada";
    
    // Campos de reporte
    public string? MotivoReporte { get; set; }              // Motivo por el que fue reportada
    public DateTime? FechaReporte { get; set; }             // Cuándo fue reportada
    public string? OfferenteIdQueReporto { get; set; }      // ID del Oferente que la reportó
    
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}
