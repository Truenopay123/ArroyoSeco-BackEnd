using System.Collections.Generic;
using UsuarioOferente = arroyoSeco.Domain.Entities.Usuarios.Oferente;

namespace arroyoSeco.Domain.Entities.Gastronomia;

public class Establecimiento
{
    public int Id { get; set; }
    public string OferenteId { get; set; } = null!;
    public UsuarioOferente? Oferente { get; set; }

    public string Nombre { get; set; } = null!;
    public string Ubicacion { get; set; } = null!;
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public string? Direccion { get; set; }
    public string? Descripcion { get; set; }
    public string? FotoPrincipal { get; set; }

    public List<FotoEstablecimiento> Fotos { get; set; } = new();
    public List<Menu> Menus { get; set; } = new();
    public List<Mesa> Mesas { get; set; } = new();
    public List<ReservaGastronomia> Reservas { get; set; } = new();
}
