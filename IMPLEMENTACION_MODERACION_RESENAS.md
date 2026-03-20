# Implementación: Sistema de Moderación de Reseñas por Reporte

## Cambios Realizados

### 1. Entidad `Resena` (Domain)
**Archivo:** `arroyoSeco.Domain/Entities/Resenas/Resena.cs`

**Cambios:**
- Campo `Estado` ahora usa valores: `"publicada"`, `"reportada"`, `"eliminada"` (antes: `"Pendiente"`, `"Aprobada"`, `"Rechazada"`)
- **Nuevos campos:**
  - `MotivoReporte` (string?): Motivo por el cual la reseña fue reportada
  - `FechaReporte` (DateTime?): Fecha y hora del reporte
  - `OfferenteIdQueReporto` (string?): ID del Oferente que reportó la reseña

### 2. Controlador `ResenasController`
**Archivo:** `arroyoSeco/Controllers/ResenasController.cs`

**Cambios:**

#### a) **Crear Reseña (Cliente)**
- `POST /api/resenas`
- Las reseñas ahora se publican **automáticamente** sin aprobación previa
- Estado inicial: `"publicada"`
- Respuesta: "Reseña publicada exitosamente."

#### b) **Listar Reseñas Públicas**
- `GET /api/resenas/alojamiento/{alojamientoId}`
- Solo muestra reseñas con estado `"publicada"`
- Sin cambios en la lógica de cálculo de promedios

#### c) **Nuevo: Reportar Reseña (Oferente)**
- `POST /api/resenas/{id}/reportar`
- Requiere rol de Oferente
- Body: `{ "motivo": "..." }` (mínimo 10 caracteres)
- Solo el Oferente dueño del alojamiento puede reportar
- Cambios de estado: `"publicada"` → `"reportada"`
- Guarda motivo, fecha y ID del oferente que reportó

#### d) **Nuevo: Listar Reseñas Reportadas (Admin)**
- `GET /api/resenas/reportadas`
- Requiere rol de Admin
- Devuelve solo las reseñas con estado `"reportada"`
- Incluye: motivo, fecha de reporte, oferente que reportó

#### e) **Nuevo: Eliminar Reseña Reportada (Admin)**
- `DELETE /api/resenas/{id}`
- Requiere rol de Admin
- Solo permite eliminar reseñas con estado `"reportada"`
- Cambios de estado: `"reportada"` → `"eliminada"` (soft delete, no se borra físicamente)
- Las reseñas eliminadas no son visibles en ningún lugar

#### f) **Nuevo: Desestimar Reporte (Admin)**
- `PATCH /api/resenas/{id}/desestimar-reporte`
- Requiere rol de Admin
- Limpia los campos de reporte: `MotivoReporte`, `FechaReporte`, `OfferenteIdQueReporto`
- Devuelve el estado a `"publicada"`

#### g) **Actualización: Ver Mis Reseñas**
- `GET /api/resenas/mias` (Cliente)
- `GET /api/resenas/mis-alojamientos` (Oferente)
- `GET /api/resenas` (Admin)
- Todos filtran ahora las reseñas `eliminada` (no se muestran)

### 3. Migración de Base de Datos
**Archivo:** `arroyoSeco.Infrastructure/Migrations/20260319120000_ModeracionResenasPorReporte.cs`

**Cambios:**
- ALTER `Resenas.Estado` defaultValue: `"Pendiente"` → `"publicada"`
- ADD `Resenas.MotivoReporte` (text, nullable)
- ADD `Resenas.FechaReporte` (timestamp with time zone, nullable)
- ADD `Resenas.OfferenteIdQueReporto` (text, nullable)

---

## Flujo de Moderación

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Cliente publica reseña                                   │
│    → POST /api/resenas                                      │
│    → Estado: "publicada" (inmediatamente visible)           │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│ 2. Oferente (opcional) reporta reseña injusta              │
│    → POST /api/resenas/{id}/reportar                       │
│    → Estado: "reportada"                                    │
│    → Guarda motivo + fecha + oferente que reportó          │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│ 3. Admin revisa reportes                                    │
│    → GET /api/resenas/reportadas                           │
│                                                             │
│    Opción A: Eliminar reseña                              │
│    → DELETE /api/resenas/{id}                             │
│    → Estado: "eliminada"                                   │
│                                                             │
│    Opción B: Desestimar reporte                           │
│    → PATCH /api/resenas/{id}/desestimar-reporte          │
│    → Estado: "publicada"                                   │
└─────────────────────────────────────────────────────────────┘
```

---

## Instrucciones para Aplicar la Migración

### Opción A: Entity Framework Core CLI

```powershell
cd C:\ArroyoSeco\back-alojamientos

# Aplicar la migración
dotnet ef database update --project arroyoSeco.Infrastructure

# Si deseas especificar la conexión:
# dotnet ef database update --project arroyoSeco.Infrastructure --connection "Host=localhost;Database=arroyoseco;User Id=postgres;Password=..."
```

### Opción B: SQL Directo (alternativa)

Si prefieres ejecutar el SQL manualmente en pgAdmin:

```sql
-- Cambiar el default value de Estado
ALTER TABLE "Resenas" 
ALTER COLUMN "Estado" SET DEFAULT 'publicada';

-- Agregar los nuevos campos
ALTER TABLE "Resenas" 
ADD COLUMN IF NOT EXISTS "MotivoReporte" TEXT;

ALTER TABLE "Resenas" 
ADD COLUMN IF NOT EXISTS "FechaReporte" TIMESTAMP WITH TIME ZONE;

ALTER TABLE "Resenas" 
ADD COLUMN IF NOT EXISTS "OfferenteIdQueReporto" TEXT;
```

---

## Consideraciones Importantes

1. **Confidencialidad:** Las reseñas eliminadas se marcan con estado `"eliminada"` pero no se borran físicamente. Esto permite usar un soft delete si necesitas un historial de auditoría en el futuro.

2. **Visibilidad:** Las reseñas eliminadas (estado `"eliminada"`) no aparecen en ningún endpoint público, de Cliente, ni de Oferente. Solo el Admin podría verlas si agregamos un endpoint específico.

3. **Sin Aprobación Previa:** A diferencia del sistema anterior, las reseñas se publican inmediatamente. Esto genera confianza porque:
   - Los clientes ven sus reseñas al instante
   - Los oferentes pueden reportar rápidamente si hay abusos
   - El Admin solo interviene en casos problemáticos (reduce carga administrativa)

4. **Reportes Auditables:** Cada reporte guarda:
   - Motivo (transparencia)
   - Fecha/hora (para estadísticas)
   - Oferente que reportó (para investigaciones si es necesario)

---

## Próximas Mejoras (Opcionales)

- [ ] Agregar validación de lenguaje ofensivo automático
- [ ] Endpoint para que Admin vea histórico de reseñas eliminadas
- [ ] Notificaciones al Oferente cuando su reporte es procesado
- [ ] Límite de reportes por oferente (para evitar abuso)
- [ ] UI correspondiente en el frontend para reportar/gestionar
