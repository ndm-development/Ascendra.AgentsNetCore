using System.Security.Claims;
using System.Threading.RateLimiting;
using Ascendra.Agents.Web.Auth;
using Ascendra.Agents.Web.Components;
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
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("Ascendra.Agents");
}

builder.Services.Configure<AgentDataOptions>(builder.Configuration.GetSection("Data"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
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
    options.AddFixedWindowLimiter("login", rule =>
    {
        rule.PermitLimit = 8;
        rule.Window = TimeSpan.FromMinutes(10);
        rule.QueueLimit = 0;
        rule.AutoReplenishment = true;
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
    AgentAuthenticator authenticator) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var user = authenticator.Authenticate(username, password);
    if (user is null)
    {
        return Results.Redirect("/login?error=1");
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

    return Results.LocalRedirect(IsSafeReturnUrl(returnUrl) ? returnUrl : "/referencias");
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
    string? day,
    string? segment) =>
{
    session.Apply(context.User);
    var scope = queries.FilterByClient(queries.Scope(), client);
    var selectedWeek = week is > 0 ? week.Value : queries.CurrentWeek(scope);
    var content = exports.Promises(queries.PromiseExport(scope, selectedWeek, status, day, segment));
    return Results.File(content, ExcelExportService.ExcelContentType, $"mis_promesas_semana_{selectedWeek}.xlsx");
}).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static bool IsSafeReturnUrl(string value) =>
    !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//");
