using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using CTFChallenge.Middleware;
using CTFChallenge.Services;

var builder = WebApplication.CreateBuilder(args);

// ── JWT ───────────────────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT Secret is not configured.");
var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep JWT claim names as-is (don't remap sub→NameIdentifier etc.)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(jwtKey),
            RoleClaimType            = "role",   // maps [Authorize(Roles="admin")] to the "role" claim
            NameClaimType            = "sub"
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Direct claim check — works regardless of RoleClaimType/MapInboundClaims interaction
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireClaim("role", "admin"));
});

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<UserStore>();
builder.Services.AddHostedService<ResumeCleanupService>();
builder.Services.AddHostedService<ShellCleanupService>();
builder.Services.AddControllers();

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "AcmeCorp Hiring Portal API",
        Version = "v1",
        Description =
            "Internal REST API backing the AcmeCorp Hiring Portal.\n\n" +
            "Maintained by the Platform Engineering team — questions or issues? Email **platform-dev@acmecorp.io**"
    });

    // Bearer auth in Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Format: **Bearer &lt;token&gt;**",
        Name        = "Authorization",
        In          = ParameterLocation.Header,
        Type        = SecuritySchemeType.ApiKey,
        Scheme      = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });

    // Include XML comments for rich Swagger descriptions
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath);
});

// ── App pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Ensure required directories exist (not served as static files)
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "resumes"));

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "AcmeCorp Hiring Portal API v1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "AcmeCorp Hiring Portal — API Docs";
});

app.UseStaticFiles();

// Leak the API docs path in a response header — visible in the browser network tab
app.Use(async (context, next) =>
{
    context.Response.Headers["X-API-Docs"] = "/swagger";
    await next();
});

app.UseStatusCodePages(async ctx =>
{
    var res = ctx.HttpContext.Response;
    res.ContentType = "application/json";
    res.StatusCode = ctx.HttpContext.Response.StatusCode;
    await res.WriteAsync(
        res.StatusCode == 401 ? """{"error":"Unauthorized"}""" :
        res.StatusCode == 403 ? """{"error":"Forbidden"}""" :
        $$$"""{"error":"HTTP {{{res.StatusCode}}}"}""");
});

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<AspxExecutorMiddleware>();

app.MapControllers();

app.MapGet("/", async context =>
{
    var file = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(file);
});

app.MapGet("/dashboard", async context =>
{
    var file = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "dashboard.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(file);
});

app.Run();
