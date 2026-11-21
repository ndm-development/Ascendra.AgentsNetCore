using System.Security.Claims;
using System.Threading.RateLimiting;
using Ascendra.Agents.Web.Auth;
using Ascendra.Agents.Web.Components;
using Ascendra.Agents.Web.Domain;
using Ascendra.Agents.Web.Infrastructure;
using Ascendra.Agents.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var dataProtectionPath = builder.Configuration["Security:DataProtectionPath"];
// Render monta el disco persistente en /var/data. Al usarlo también para las
// claves de protección, una actualización no invalida las cookies de sesión.
// El valor explícito de configuración sigue teniendo prioridad.
if (string.IsNullOrWhiteSpace(dataProtectionPath) && Directory.Exists("/var/data"))
{
    dataProtectionPath = "/var/data/ascendra-protection";
}
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("Ascendra.Agents");
}

builder.Services.Configure<AgentDataOptions>(builder.Configuration.GetSection("Data"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.SectionName));
builder.Services.Configure<CallDispositionOptions>(builder.Configuration.GetSection(CallDispositionOptions.SectionName));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ascendra_agentes";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.Events.OnValidatePrincipal = async context =>
        {
            var authenticator = context.HttpContext.RequestServices.GetRequiredService<AgentAuthenticator>();
            if (authenticator.IsSessionCurrent(context.Principal)) return;

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
    });
builder.Services.AddRateLimiter(options =>
{
    // El limitador anterior compartía un único contador para todo el servicio.
    // Un exceso de un usuario podía bloquear el acceso del resto y Render lo
    // presentaba como 503. El contador se separa por la conexión de origen.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

builder.Services.AddSingleton<ExcelSnapshotStore>();
builder.Services.AddHostedService<ExcelRefreshService>();
builder.Services.AddSingleton<AgentAuthenticator>();
builder.Services.AddSingleton<OperationalAssetUrls>();
builder.Services.AddSingleton<ExcelExportService>();
builder.Services.AddScoped<DashboardSession>();
builder.Services.AddScoped<DashboardQueries>();
builder.Services.AddSingleton<WhatsAppTemplateCatalog>();
builder.Services.AddSingleton<PostgresDatabase>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PostgresDatabase>());
builder.Services.AddSingleton<EphemeralChatService>();
builder.Services.AddSingleton<CallDispositionService>();
builder.Services.AddSingleton<PromiseRegistrationService>();
builder.Services.AddHostedService<HistoricalPromiseImportService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<EphemeralChatService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<CallDispositionService>());

var app = builder.Build();

// Una navegación siempre consulta el documento actualizado. Esto impide que
// un navegador mantenga una pantalla de una implementación anterior después
// de que Render termine un despliegue.
app.Use(async (context, next) =>
{
    var acceptsHtml = context.Request.Headers.Accept.Any(value =>
        value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    if (HttpMethods.IsGet(context.Request.Method) && acceptsHtml)
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
    }

    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
// Los fondos operativos viven en /assets en la raíz del proyecto, para que se
// puedan sustituir sin navegar por wwwroot. Los recursos internos conservan un
// respaldo por si la carpeta no existe en un entorno antiguo.
var operationalAssets = OperationalAssetUrls.ResolvePath(app.Environment);
if (operationalAssets is not null)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(operationalAssets),
        RequestPath = "/assets",
        OnPrepareResponse = context =>
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable",
    });
}
app.UseStaticFiles(new StaticFileOptions
{
    // CSS, JavaScript y la infraestructura Blazor se revalidan en cada carga.
    // Las URLs propias también llevan versión, por lo que este ajuste es una
    // segunda defensa cuando se despliega una corrección urgente.
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate",
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapPost("/account/login", async (
    HttpContext context,
    IAntiforgery antiforgery,
    AgentAuthenticator authenticator,
    ExcelSnapshotStore snapshots) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    // Tras un despliegue, el corte se vuelve a cargar en memoria. Si un agente
    // intenta ingresar en esa breve ventana, lo cargamos aquí mismo en vez de
    // rechazar sus credenciales y obligarlo a recargar la página.
    if (!snapshots.Health.IsReady)
    {
        await snapshots.RefreshAsync(context.RequestAborted);
    }

    var user = authenticator.Authenticate(username, password);
    if (user is null)
    {
        return Results.Redirect(snapshots.Health.IsReady ? "/login?error=1" : "/login?starting=1");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Username),
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Role, user.Role),
        new(AgentClaimTypes.AgentId, user.Id ?? string.Empty),
        new(AgentClaimTypes.RegistryVersion, authenticator.RegistryVersion),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

    // Los agentes comienzan en su actividad principal: registrar promesas.
    // Gerencia y supervisión conservan la búsqueda de clientes como inicio.
    var defaultDestination = user.HasFullAccess ? "/referencias" : "/registrar-promesa";
    return Results.LocalRedirect(IsSafeReturnUrl(returnUrl) ? returnUrl : defaultDestination);
}).RequireRateLimiting("login");

app.MapPost("/account/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// El navegador sólo solicita este endpoint tras una interacción real. Junto
// con la expiración deslizante de la cookie, así la sesión se conserva para
// quien trabaja activamente y vence a las dos horas sin actividad.
app.MapPost("/account/keepalive", async (IAntiforgery antiforgery, HttpContext context) =>
{
    await antiforgery.ValidateRequestAsync(context);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/chat/attachments/{attachmentId:guid}", (Guid attachmentId, HttpContext context, EphemeralChatService chat) =>
{
    var viewer = EphemeralChatService.ParticipantFor(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
        context.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        context.User.FindFirstValue(ClaimTypes.Role) ?? "Agente");
    var attachment = chat.GetAttachment(viewer, attachmentId);
    return attachment is null
        ? Results.NotFound()
        : Results.File(attachment.Path, attachment.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/health", (ExcelSnapshotStore store) => Results.Ok(store.Health));
app.MapGet("/exports/payments", (
    HttpContext context,
    DashboardSession session,
    DashboardQueries queries,
    ExcelExportService exports,
    int? week,
    string? client) =>
{
    session.Apply(context.User);
    var scope = queries.FilterByClient(queries.Scope(), client);
    var selectedWeek = week is > 0 ? week.Value : queries.CurrentWeek(scope);
    var content = exports.Payments(queries.WeeklyPaymentExport(scope, selectedWeek));
    return Results.File(content, ExcelExportService.ExcelContentType, $"mis_pagos_semana_{selectedWeek}.xlsx");
}).RequireAuthorization();

app.MapGet("/exports/promises", (
    HttpContext context,
    DashboardSession session,
    DashboardQueries queries,
    ExcelExportService exports,
    int? week,
    string? client,
    string? status,
    string? metric,
    string? day,
    string? segment) =>
{
    session.Apply(context.User);
    var scope = queries.FilterByClient(queries.Scope(), client);
    var selectedWeek = week is > 0 ? week.Value : queries.CurrentWeek(scope);
    var content = exports.Promises(queries.PromiseExport(scope, selectedWeek, status, metric, day, segment));
    return Results.File(content, ExcelExportService.ExcelContentType, $"mis_promesas_semana_{selectedWeek}.xlsx");
}).RequireAuthorization();

// Exportaciones de equipo: el alcance se vuelve a validar en el servidor para
// que un agente no pueda obtener datos de terceros mediante una URL directa.
app.MapGet("/exports/team/payments", (
    HttpContext context,
    DashboardSession session,
    DashboardQueries queries,
    ExcelExportService exports,
    int? week,
    string? client,
    string? agent,
    DateOnly? start,
    DateOnly? end,
    string? origin) =>
{
    session.Apply(context.User);
    if (!session.HasFullAccess) return Results.Forbid();
    if (start is not null && end is not null && start > end)
        return Results.BadRequest("La fecha inicial no puede ser posterior a la fecha final.");

    var scope = queries.FilterByClient(queries.TeamScope(), client);
    var useDateRange = start is not null || end is not null;
    var selectedWeek = week is > 0 ? week.Value : queries.CurrentWeek(scope);
    var classification = TextNormalization.Normalize(origin);
    var payments = scope.Payments
        .Where(payment => (!useDateRange || payment.ReceivedOn is not null)
            && (!useDateRange || start is null || payment.ReceivedOn >= start)
            && (!useDateRange || end is null || payment.ReceivedOn <= end)
            && (useDateRange || payment.Week == selectedWeek)
            && (string.IsNullOrWhiteSpace(agent) || payment.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase))
            && (classification.Length == 0
                || classification == "organico" && (TextNormalization.IsOrganic(payment.Agent) || TextNormalization.IsOrganic(payment.AssignmentType))
                || classification == "equipo" && !TextNormalization.IsOrganic(payment.Agent) && !TextNormalization.IsOrganic(payment.AssignmentType)))
        .OrderByDescending(payment => payment.ReceivedOn)
        .ThenBy(payment => payment.ClientId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var content = exports.Payments(payments);
    return Results.File(content, ExcelExportService.ExcelContentType, "pagos_equipo.xlsx");
}).RequireAuthorization();

app.MapGet("/exports/team/promises", (
    HttpContext context,
    DashboardSession session,
    DashboardQueries queries,
    ExcelExportService exports,
    int? week,
    string? client,
    string? agent,
    string? status,
    DateOnly? start,
    DateOnly? end) =>
{
    session.Apply(context.User);
    if (!session.HasFullAccess) return Results.Forbid();
    if (start is not null && end is not null && start > end)
        return Results.BadRequest("La fecha inicial no puede ser posterior a la fecha final.");

    var scope = queries.FilterByClient(queries.TeamScope(), client);
    var useDateRange = start is not null || end is not null;
    var selectedWeek = week is > 0 ? week.Value : queries.CurrentWeek(scope);
    var promises = queries.FilterPromises(
        scope, null, null, agent, null, status, start, end, null, null,
        useDateRange ? null : selectedWeek);
    var content = exports.Promises(promises);
    return Results.File(content, ExcelExportService.ExcelContentType, "promesas_equipo.xlsx");
}).RequireAuthorization();

// Estas promesas aún no aparecen en el corte. El identificador técnico debe
// conservarse durante el cruce para que el corte posterior las reemplace en
// Mis promesas en vez de mostrarlas dos veces.
app.MapGet("/exports/team/promise-registrations", async (
    HttpContext context,
    DashboardSession session,
    PromiseRegistrationService registrations,
    ExcelExportService exports,
    CancellationToken cancellationToken) =>
{
    session.Apply(context.User);
    if (!session.HasFullAccess) return Results.Forbid();

    var pending = await registrations.GetPendingForTeamAsync(cancellationToken);
    var content = exports.PendingPromiseRegistrations(pending);
    return Results.File(content, ExcelExportService.ExcelContentType, "promesas_ascendra_pendientes.xlsx");
}).RequireAuthorization();

// Fuente única para el proceso de cruce: histórico migrado más promesas
// creadas en Ascendra. No contiene estatus porque el notebook los calcula.
app.MapGet("/exports/team/promises-source", async (
    HttpContext context,
    DashboardSession session,
    PromiseRegistrationService registrations,
    ExcelExportService exports,
    CancellationToken cancellationToken) =>
{
    session.Apply(context.User);
    if (!session.HasFullAccess) return Results.Forbid();

    var source = await registrations.GetCrossSourceAsync(cancellationToken);
    var content = exports.PromiseCrossSource(source);
    return Results.File(content, ExcelExportService.ExcelContentType, "promesas_ascendra_para_cruce.xlsx");
}).RequireAuthorization();

// Este consolidado contiene gestiones de todos los clientes; por ello la
// autorización se valida también aquí, no sólo al ocultar la pestaña.
app.MapGet("/exports/codificacion", async (
    HttpContext context,
    DashboardSession session,
    CallDispositionService dispositions,
    ExcelExportService exports,
    string? client,
    DateOnly? start,
    DateOnly? end,
    string? agentId,
    string? agent,
    string? outcome,
    string? channel,
    string? phone) =>
{
    session.Apply(context.User);
    if (!session.HasFullAccess) return Results.Forbid();

    var summaries = await dispositions.GetHighestWeightSummariesAsync(context.RequestAborted);
    var exactClient = summaries.FirstOrDefault(summary =>
        string.Equals(summary.ClientId, client?.Trim(), StringComparison.OrdinalIgnoreCase));
    var source = exactClient is null
        ? summaries
        : (await dispositions.GetFullHistoryAsync(exactClient.ClientId, context.RequestAborted))
            .Select(entry => new ClientDispositionSummary(
                entry.ClientId, entry.RecordedAt, entry.AgentId, entry.AgentName,
                entry.Category, entry.Result, entry.Channel, entry.Phone))
            .ToArray();
    var isHistoryExport = exactClient is not null;
    var filtered = source
        .Where(row => (isHistoryExport || Contains(row.ClientId, client))
            && (start is null || OperationalClock.Date(row.RecordedAt) >= start)
            && (end is null || OperationalClock.Date(row.RecordedAt) <= end)
            && Contains(row.AgentId, agentId)
            && Contains(row.AgentName, agent)
            && (string.IsNullOrWhiteSpace(outcome)
                || string.Equals($"{row.Category}|{row.Result}", outcome, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(channel)
                || string.Equals(row.Channel, channel, StringComparison.OrdinalIgnoreCase))
            && Contains(row.Phone, phone))
        .OrderByDescending(row => row.RecordedAt)
        .ToArray();
    var content = exports.Coding(filtered);
    return Results.File(content, ExcelExportService.ExcelContentType, "codificacion_clientes.xlsx");
}).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static bool IsSafeReturnUrl(string value) =>
    !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//");

static bool Contains(string value, string? query) => string.IsNullOrWhiteSpace(query)
    || value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
