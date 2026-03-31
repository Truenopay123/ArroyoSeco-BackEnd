namespace arroyoSeco.Domain.Entities.Gastronomia;

public class ResenaGastronomia
{
    public int Id { get; set; }
    public int EstablecimientoId { get; set; }
    public Establecimiento? Establecimiento { get; set; }
    public int ReservaGastronomiaId { get; set; }
    public ReservaGastronomia? ReservaGastronomia { get; set; }
    public string ClienteId { get; set; } = string.Empty;
    public int Calificacion { get; set; }                    // 1–5
    public string Comentario { get; set; } = string.Empty;

    // Estados: "publicada" (visible), "reportada" (bajo revisión), "eliminada" (no visible)
    public string Estado { get; set; } = "publicada";

    // Campos de reporte
    public string? MotivoReporte { get; set; }
    public DateTime? FechaReporte { get; set; }
    public string? OfferenteIdQueReporto { get; set; }

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}
