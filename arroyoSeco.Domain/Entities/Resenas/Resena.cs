using arroyoSeco.Domain.Entities.Alojamientos;

namespace arroyoSeco.Domain.Entities.Resenas;

public class Resena
{
    public int Id { get; set; }
    public int AlojamientoId { get; set; }
    public Alojamiento? Alojamiento { get; set; }
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    public string ClienteId { get; set; } = string.Empty;   // IdentityUser.Id
    public int Calificacion { get; set; }                    // 1–5
    public string Comentario { get; set; } = string.Empty;
    public string Estado { get; set; } = "Pendiente";       // Pendiente, Aprobada, Rechazada
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}
