# Deploy guide - Vercel (Front) + Railway (API + PostgreSQL)

## URLs de produccion
- Frontend: https://alojamientosarroyoseco.vercel.app
- Backend: https://arroyoseco-api-production-fcae.up.railway.app

## Cambios ya preparados en el codigo
- CORS de backend en produccion habilitado para el frontend de Vercel.
- AppUrls del backend configuradas con URLs productivas.
- environment.production.ts del frontend apuntando al backend productivo.

## 1) Railway - Backend + PostgreSQL

### Servicio API
- Proyecto: Railway
- Runtime: Nixpacks (ya existe `nixpacks.toml`)
- Puerto: Railway inyecta `PORT` automaticamente
- Dominio: `https://arroyoseco-api-production-fcae.up.railway.app`

### Base de datos
- Crear plugin PostgreSQL en Railway.
- Conectar plugin al servicio backend.
- Railway crea `DATABASE_URL` automaticamente (usar esa, no hardcodear credenciales).

### Variables obligatorias (backend)
Estas variables son necesarias para operar en produccion de forma correcta y segura:

- `ASPNETCORE_ENVIRONMENT=Production`
- `DATABASE_URL=<railway-postgres-url>`
- `Jwt__Key=<clave-larga-y-secreta-min-32-chars>`
- `Jwt__Issuer=arroyoSeco`
- `Jwt__Audience=arroyoSeco-client`
- `AppUrls__FrontendBaseUrl=https://alojamientosarroyoseco.vercel.app`
- `AppUrls__BackendBaseUrl=https://arroyoseco-api-production-fcae.up.railway.app`
- `EMAIL_SMTP_HOST=smtp-relay.brevo.com`
- `EMAIL_SMTP_PORT=587`
- `EMAIL_ENABLE_SSL=true`
- `EMAIL_SMTP_USERNAME=<usuario-brevo-smtp>`
- `EMAIL_SMTP_PASSWORD=<password-o-api-key-smtp-brevo>`
- `EMAIL_FROM=<email-remitente-verificado-en-brevo>`
- `EMAIL_FROM_NAME=Arroyo Seco`
- `MercadoPago__AccessToken=<TEST-... o APP_USR-... segun entorno>`

### Variables recomendadas (backend)
- `SeedAdmin__Email=<admin@tu-dominio.com>`
- `SeedAdmin__Password=<password-fuerte-admin>`
- `MercadoPago__CurrencyId=ARS`
- `MercadoPago__WebhookSecret=<secreto-webhook-mp>`

## 2) Vercel - Frontend Angular

### Configuracion del proyecto
- Root directory: `ArroyoSeco-FrontEnd`
- Framework: Angular
- Build command: `npm run build`
- Output directory: `dist/arroyo-seco/browser`

Nota: `vercel.json` ya tiene `outputDirectory` y `rewrites` para SPA.

### Variables en Vercel
Actualmente el frontend usa `src/environments/environment.production.ts` (valor ya seteado a Railway), por lo que no requiere variable de entorno obligatoria para URL del API.

Si cambias de backend en el futuro, actualiza:
- `src/environments/environment.production.ts`

## 3) Checklist de puesta en marcha
- Crear PostgreSQL en Railway y vincular a la API.
- Cargar todas las variables obligatorias.
- Deploy backend y verificar `GET /health`.
- Deploy frontend en Vercel (root en carpeta FrontEnd).
- Probar registro/login, confirmacion email y reset password.
- Probar flujo de pago de Mercado Pago (crear preferencia y webhook).

## 4) Verificaciones rapidas post-deploy
- Salud API: `GET https://arroyoseco-api-production-fcae.up.railway.app/health`
- Swagger: `https://arroyoseco-api-production-fcae.up.railway.app/swagger`
- CORS desde front productivo: login/register sin errores de navegador.
- Correo Brevo: prueba de confirmacion y recuperacion de password.

## 5) Notas importantes
- No uses claves de prueba en produccion (JWT, Brevo, Mercado Pago).
- Si no configuras `MercadoPago__WebhookSecret`, el backend acepta webhook sin validar firma (funciona, pero menos seguro).
- `DATABASE_URL` es la fuente principal en Railway y tiene prioridad en la app.
