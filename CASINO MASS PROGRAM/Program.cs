using System.Text.Json;
using CASINO_MASS_PROGRAM.Data;
using CASINO_MASS_PROGRAM.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Db (SQL Server)
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
#if DEBUG
    options.EnableDetailedErrors();
    options.EnableSensitiveDataLogging();
#endif
});

// Services
builder.Services.AddScoped<ExcelImportService>();

// MVC + JSON
builder.Services
    .AddControllers()
    .AddJsonOptions(o => { o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; });

var app = builder.Build();

// Optional root redirect
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Map controllers
app.MapControllers();

app.Run();