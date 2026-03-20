using Microsoft.AspNetCore.Identity;

namespace arroyoSeco.Domain.Entities.Usuarios;

public class ApplicationUser : IdentityUser
{
    public bool RequiereCambioPassword { get; set; }
    public DateTime? FechaPrimerLogin { get; set; }

    // Datos demográficos para estadísticas de turismo
    public string? Sexo { get; set; }           // Masculino, Femenino, Otro, Prefiero no decir
    public DateTime? FechaNacimiento { get; set; }
    public string? LugarOrigen { get; set; }    // Ciudad/Provincia de origen

    // Descriptor facial para 2FA con reconocimiento facial (Float32Array serializado como JSON)
    public string? FaceDescriptor { get; set; }
}
