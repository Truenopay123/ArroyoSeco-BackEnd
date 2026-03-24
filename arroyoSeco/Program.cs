using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using System.Text;
using arroyoSeco.Infrastructure.Data;
using arroyoSeco.Infrastructure.Auth;
using arroyoSeco.Infrastructure.Services;
using arroyoSeco.Domain.Entities.Usuarios;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Application.Features.Alojamiento.Commands.Crear;
using arroyoSeco.Application.Features.Reservas.Commands.Crear;
using arroyoSeco.Application.Features.Gastronomia.Commands.Crear;
using arroyoSeco.Application.Features.Reservas.Commands.CambiarEstado;
using arroyoSeco.Infrastructure.Storage;
using System.Text.Json.Serialization;
using System.Runtime.ExceptionServices;
using System.Threading.RateLimiting;
using arroyoSeco.Services;

var builder = WebApplication.CreateBuilder(args);

// Tama�o m�ximo del body a nivel Kestrel (50 MB)
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 50_000_000;
});

// En producción Railway usa la variable PORT, en desarrollo usa HTTPS
if (builder.Environment.IsProduction())
{
    // Railway asigna el puerto automáticamente
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
else
{
    builder.WebHost.UseKestrel(o =>
    {
        o.ListenLocalhost(7190, lo => lo.UseHttps());
    });
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// Capturar excepciones globales (si algo revienta mostrar log)
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    Console.WriteLine("UNHANDLED: " + e.ExceptionObject);
};
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    Console.WriteLine("UNOBSERVED: " + e.Exception);
    e.SetObserved();
};
AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
{
    Console.WriteLine("FIRST CHANCE: " + e.Exception.GetType().Name + " - " + e.Exception.Message);
};
builder.Services.AddHostedService<ShutdownLogger>();
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 50_000_000;
    o.ValueLengthLimit = int.MaxValue;
    o.MemoryBufferThreshold = 1024 * 1024;
});

const string CorsPolicy = "FrontPolicy";
builder.Services.AddCors(p =>
{
    p.AddPolicy(CorsPolicy, policy =>
    {
        // En produccion, permitir dominio frontend configurado.
        if (builder.Environment.IsProduction())
        {
            var configuredFrontend = builder.Configuration["AppUrls:FrontendBaseUrl"]
                ?? Environment.GetEnvironmentVariable("APP_FRONTEND_BASE_URL")
                ?? "https://turismoarroyoseco.vercel.app";

            policy.WithOrigins(configuredFrontend.TrimEnd('/'), "https://turismoarroyoseco.vercel.app")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            // En desarrollo, solo localhost:4200
            policy.WithOrigins("http://localhost:4200", "https://localhost:4200")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
    });
});

// Obtener connection string desde DATABASE_URL (Railway) o ConnectionStrings__DefaultConnection (local)
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
string connectionString;

if (!string.IsNullOrEmpty(databaseUrl))
{
    // Convertir URI de PostgreSQL (formato Render) a connection string de Npgsql
    var uri = new Uri(databaseUrl);
    var port = uri.Port > 0 ? uri.Port : 5432; // Puerto por defecto de PostgreSQL
    connectionString = $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};Username={uri.UserInfo.Split(':')[0]};Password={uri.UserInfo.Split(':')[1]};SSL Mode=Require;Trust Server Certificate=true";
    Console.WriteLine($"=== Using DATABASE_URL from environment (converted from URI)");
}
else
{
    connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? throw new InvalidOperationException("No connection string configured");
    Console.WriteLine($"=== Using DefaultConnection from appsettings.json");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
    )
    .EnableSensitiveDataLogging()
);

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(connectionString, 
        npgsql => npgsql.MigrationsAssembly("arroyoSeco.Infrastructure")));

builder.Services
    .AddIdentityCore<ApplicationUser>(opt =>
    {
        opt.User.RequireUniqueEmail = true;
        // Política de contraseñas segura
        opt.Password.RequireDigit = true;
        opt.Password.RequireLowercase = true;
        opt.Password.RequireNonAlphanumeric = true;
        opt.Password.RequireUppercase = true;
        opt.Password.RequiredLength = 8;
        // Bloqueo de cuenta tras 5 intentos fallidos
        opt.Lockout.AllowedForNewUsers = true;
        opt.Lockout.MaxFailedAccessAttempts = 5;
        opt.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
{
    o.TokenLifespan = TimeSpan.FromHours(1);
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IFolioGenerator, FolioGenerator>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IStorageService, DiskStorageService>();
builder.Services.AddScoped<CrearAlojamientoCommandHandler>();
builder.Services.AddScoped<CrearEstablecimientoCommandHandler>();
builder.Services.AddScoped<CrearMenuCommandHandler>();
builder.Services.AddScoped<AgregarMenuItemCommandHandler>();
builder.Services.AddScoped<CrearMesaCommandHandler>();
builder.Services.AddScoped<CrearReservaGastronomiaCommandHandler>();
builder.Services.AddScoped<CrearReservaCommandHandler>();
builder.Services.AddScoped<CambiarEstadoReservaCommandHandler>();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase; // ← Agregar camelCase
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ArroyoSeco API", Version = "v1" });
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Bearer token"
    };
    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, new[] { "Bearer" } }
    });
});

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
if (string.IsNullOrWhiteSpace(storage.ComprobantesPath))
{
    storage.ComprobantesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "arroyoSeco", "comprobantes");
}
builder.Services.PostConfigure<StorageOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.ComprobantesPath))
        o.ComprobantesPath = storage.ComprobantesPath;
});

// Configurar opciones de Email
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.PostConfigure<EmailOptions>(o =>
{
    // Permite sobreescribir por variables de entorno sin dejar defaults silenciosos.
    o.SmtpHost = Environment.GetEnvironmentVariable("EMAIL_SMTP_HOST") ?? o.SmtpHost;
    o.SmtpUsername = Environment.GetEnvironmentVariable("EMAIL_SMTP_USERNAME") ?? o.SmtpUsername;
    o.SmtpPassword = Environment.GetEnvironmentVariable("EMAIL_SMTP_PASSWORD") ?? o.SmtpPassword;
    o.FromEmail = Environment.GetEnvironmentVariable("EMAIL_FROM") ?? o.FromEmail;
    o.FromName = Environment.GetEnvironmentVariable("EMAIL_FROM_NAME") ?? o.FromName;

    var smtpPortEnv = Environment.GetEnvironmentVariable("EMAIL_SMTP_PORT");
    if (int.TryParse(smtpPortEnv, out var smtpPort) && smtpPort > 0)
    {
        o.SmtpPort = smtpPort;
    }

    var enableSslEnv = Environment.GetEnvironmentVariable("EMAIL_ENABLE_SSL");
    if (bool.TryParse(enableSslEnv, out var enableSsl))
    {
        o.EnableSsl = enableSsl;
    }

    var timeoutMsEnv = Environment.GetEnvironmentVariable("EMAIL_TIMEOUT_MS");
    if (int.TryParse(timeoutMsEnv, out var timeoutMs) && timeoutMs > 0)
    {
        o.TimeoutMs = timeoutMs;
    }

    var fallback2525Env = Environment.GetEnvironmentVariable("EMAIL_USE_PORT_2525_FALLBACK");
    if (bool.TryParse(fallback2525Env, out var usePort2525Fallback))
    {
        o.UsePort2525Fallback = usePort2525Fallback;
    }

    var preferBrevoApiEnv = Environment.GetEnvironmentVariable("EMAIL_PREFER_BREVO_API");
    if (bool.TryParse(preferBrevoApiEnv, out var preferBrevoApi))
    {
        o.PreferBrevoApi = preferBrevoApi;
    }
});

var app = builder.Build();

// Middleware global de errores (evita cierre silencioso)
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.WriteLine("GLOBAL EXCEPTION: " + ex);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsync("Error interno");
    }
});

// Crear carpeta y servir archivos
var storageOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
var comprobantesPath = storageOptions.ComprobantesPath;

// En producción, usar una ruta temporal si no está configurada o no es absoluta
if (string.IsNullOrEmpty(comprobantesPath) || !Path.IsPathRooted(comprobantesPath))
{
    comprobantesPath = Path.Combine(Path.GetTempPath(), "arroyoseco-comprobantes");
}

Directory.CreateDirectory(comprobantesPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(comprobantesPath),
    RequestPath = "/comprobantes"
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors(CorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Endpoint de salud para verificar que no se cay�
app.MapGet("/health", () => Results.Ok("OK"));

app.MapControllers();

// Aplicar migraciones automáticamente en producción
using (var scope = app.Services.CreateScope())
{
    try
    {
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var authDbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        
        Console.WriteLine("=== Applying database migrations...");
        appDbContext.Database.Migrate();
        authDbContext.Database.Migrate();

        // Autocorrección defensiva para entornos donde la migración no quedó aplicada.
        appDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""Alojamientos""
            ADD COLUMN IF NOT EXISTS ""Amenidades"" text NOT NULL DEFAULT '[]';");

        // Campos demográficos en AspNetUsers
        authDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""AspNetUsers""
            ADD COLUMN IF NOT EXISTS ""Sexo"" text NULL,
            ADD COLUMN IF NOT EXISTS ""FechaNacimiento"" timestamp with time zone NULL,
            ADD COLUMN IF NOT EXISTS ""LugarOrigen"" text NULL;");

        // Descriptor facial para 2FA con reconocimiento facial
        authDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""AspNetUsers""
            ADD COLUMN IF NOT EXISTS ""FaceDescriptor"" jsonb NULL;");

        // Tabla de reseñas
        appDbContext.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Resenas"" (
                ""Id"" serial PRIMARY KEY,
                ""AlojamientoId"" integer NOT NULL REFERENCES ""Alojamientos""(""Id"") ON DELETE CASCADE,
                ""ReservaId"" integer NOT NULL UNIQUE REFERENCES ""Reservas""(""Id"") ON DELETE CASCADE,
                ""ClienteId"" text NOT NULL,
                ""Calificacion"" integer NOT NULL,
                ""Comentario"" text NOT NULL,
                ""Estado"" text NOT NULL DEFAULT 'Pendiente',
                ""FechaCreacion"" timestamp with time zone NOT NULL DEFAULT now()
            );");

        // Compatibilidad con bases antiguas: columnas de moderación de reseñas.
        appDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""Resenas""
            ADD COLUMN IF NOT EXISTS ""MotivoReporte"" text NULL,
            ADD COLUMN IF NOT EXISTS ""FechaReporte"" timestamp with time zone NULL,
            ADD COLUMN IF NOT EXISTS ""OfferenteIdQueReporto"" text NULL;");

        appDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""Resenas""
            ALTER COLUMN ""Estado"" SET DEFAULT 'publicada';");

        // Tabla de pagos (Mercado Pago)
        appDbContext.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Pagos"" (
                ""Id"" serial PRIMARY KEY,
                ""ReservaId"" integer NOT NULL REFERENCES ""Reservas""(""Id"") ON DELETE CASCADE,
                ""MercadoPagoPreferenceId"" text NULL,
                ""MercadoPagoPaymentId"" text NULL,
                ""Estado"" text NOT NULL DEFAULT 'Pendiente',
                ""Monto"" numeric(18,2) NOT NULL DEFAULT 0,
                ""MetodoPago"" text NULL,
                ""FechaCreacion"" timestamp with time zone NOT NULL DEFAULT now(),
                ""FechaActualizacion"" timestamp with time zone NULL
            );");

        // Cantidad de huéspedes por reserva
        appDbContext.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""Reservas""
            ADD COLUMN IF NOT EXISTS ""NumeroHuespedes"" integer NOT NULL DEFAULT 1;");

        Console.WriteLine("=== Migrations applied successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"=== Error applying migrations: {ex.Message}");
        throw;
    }
}

// Crear roles si no existen
using (var scope = app.Services.CreateScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    string[] roles = { "Cliente", "Oferente", "Admin" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }
    
    // Crear usuario admin si no existe
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var adminEmail = builder.Configuration["SeedAdmin:Email"];
    var adminPassword = builder.Configuration["SeedAdmin:Password"];

    if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
    {
        Console.WriteLine("=== SeedAdmin no configurado. Se omite creación automática de admin.");
    }
    else
    {
        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true
            };
            
            var result = await userManager.CreateAsync(adminUser, adminPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(adminUser, "Admin");
                Console.WriteLine($"=== Admin user created: {adminEmail}");
            }
            else
            {
                Console.WriteLine($"=== Error creating admin: {string.Join(", ", result.Errors.Select(e => e.Description))}");
            }
        }
        else
        {
            // Verify and assign Admin role if user exists but doesn't have it
            var adminRoles = await userManager.GetRolesAsync(adminUser);
            if (!adminRoles.Contains("Admin"))
            {
                await userManager.AddToRoleAsync(adminUser, "Admin");
                Console.WriteLine($"=== Admin role assigned to existing user: {adminEmail}");
            }
            else
            {
                Console.WriteLine($"=== Admin user already exists with correct role: {adminEmail}");
            }
        }
    }
}

app.Run();