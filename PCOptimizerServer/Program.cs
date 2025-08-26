using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Database configuration with fallback
string connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
bool databaseAvailable = false;

if (!string.IsNullOrEmpty(connectionString))
{
    try
    {
        builder.Services.AddDbContext<LicenseDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(30), null);
                sqlOptions.CommandTimeout(120);
            });
        });
        databaseAvailable = true;
        Console.WriteLine($"Database configured: {connectionString.Substring(0, Math.Min(50, connectionString.Length))}...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database configuration failed: {ex.Message}");
        databaseAvailable = false;
    }
}
else
{
    Console.WriteLine("No connection string found - running in memory mode");
}

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "PC Optimizer License Server v1");
    c.RoutePrefix = "swagger";
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthorization();

// Add basic endpoints
app.MapGet("/", () => "PC Optimizer License Server is running!");
app.MapGet("/health", () => new {
    status = "healthy",
    time = DateTime.UtcNow,
    database = databaseAvailable ? "configured" : "memory-mode",
    version = "1.1"
});

// Map controllers
app.MapControllers();

// Initialize database if available
if (databaseAvailable)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetService<LicenseDbContext>();

        if (context != null)
        {
            Console.WriteLine("Testing database connection...");
            var canConnect = await context.Database.CanConnectAsync();

            if (canConnect)
            {
                Console.WriteLine("Database connection successful");
                await context.Database.EnsureCreatedAsync();

                // Seed demo data if needed
                if (!await context.Licenses.AnyAsync())
                {
                    var demoLicenses = new[]
                    {
                        new License
                        {
                            LicenseKey = "PCOPT-STD01-DEMO1-TEST1-12345",
                            CustomerName = "Demo User 1",
                            CustomerEmail = "demo1@example.com",
                            ExpirationDate = DateTime.UtcNow.AddYears(1)
                        }
                    };

                    context.Licenses.AddRange(demoLicenses);
                    await context.SaveChangesAsync();
                    Console.WriteLine("Demo data seeded");
                }
            }
            else
            {
                Console.WriteLine("Cannot connect to database - using fallback mode");
                databaseAvailable = false;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database initialization failed: {ex.Message}");
        databaseAvailable = false;
    }
}

Console.WriteLine($"Server starting - Database: {(databaseAvailable ? "Connected" : "Memory Mode")}");

app.Run();

// Database Models - Updated to match existing database schema
public class License
{
    public int Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = ""; // Added to match database schema
    public int MaxActivations { get; set; } = 1;
    public DateTime CreationDate { get; set; } = DateTime.UtcNow;
    public DateTime ExpirationDate { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<License> Licenses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<License>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.Property(e => e.LicenseKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CustomerName).IsRequired().HasMaxLength(300);
            entity.Property(e => e.CustomerEmail).IsRequired().HasMaxLength(255);
        });
    }
}

[ApiController]
[Route("api/[controller]")]
public class LicenseController : ControllerBase
{
    private readonly LicenseDbContext? _context;
    private readonly ILogger<LicenseController> _logger;
    private const string ADMIN_SECRET = "fg92uh92872jfjefioesjfiejf28r2i0fff!@#$%^&***()";

    public LicenseController(ILogger<LicenseController> logger, LicenseDbContext? context = null)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            if (_context != null)
            {
                var canConnect = await _context.Database.CanConnectAsync();
                var licenseCount = canConnect ? await _context.Licenses.CountAsync() : -1;

                return Ok(new
                {
                    Status = canConnect ? "Healthy" : "Degraded",
                    Database = canConnect ? "Connected" : "Connection Failed",
                    TotalLicenses = licenseCount,
                    Mode = "Database",
                    ServerTime = DateTime.UtcNow,
                    Version = "1.1"
                });
            }
            else
            {
                return Ok(new
                {
                    Status = "Healthy",
                    Database = "In-Memory",
                    Mode = "Fallback",
                    ServerTime = DateTime.UtcNow,
                    Version = "1.1"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(500, new { Status = "Unhealthy", Error = ex.Message });
        }
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateStandardLicense([FromBody] GenerateStandardLicenseRequest request)
    {
        _logger.LogInformation("License generation request received");

        try
        {
            // Validate request
            if (request == null)
                return BadRequest(new GenerateStandardLicenseResponse { Success = false, Message = "Request is null" });

            if (string.IsNullOrEmpty(request.AdminSecret) || request.AdminSecret != ADMIN_SECRET)
                return Unauthorized(new GenerateStandardLicenseResponse { Success = false, Message = "Invalid admin secret" });

            if (string.IsNullOrWhiteSpace(request.CustomerName))
                return BadRequest(new GenerateStandardLicenseResponse { Success = false, Message = "Customer name is required" });

            if (request.ValidityDays <= 0 || request.ValidityDays > 3650)
                return BadRequest(new GenerateStandardLicenseResponse { Success = false, Message = "Validity days must be between 1 and 3650" });

            // Generate license key
            string licenseKey = GenerateSecureLicenseKey();
            _logger.LogInformation($"Generated license key: {licenseKey}");

            if (_context != null)
            {
                // Save to database
                var license = new License
                {
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName.Trim(),
                    CustomerEmail = $"{request.CustomerName.Trim().Replace(" ", "").ToLower()}@example.com",
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(request.ValidityDays),
                    IsActive = true
                };

                _context.Licenses.Add(license);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"License saved to database: {licenseKey}");

                return Ok(new GenerateStandardLicenseResponse
                {
                    Success = true,
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName,
                    CreationDate = license.CreationDate,
                    ExpirationDate = license.ExpirationDate,
                    ValidityDays = request.ValidityDays,
                    Message = "License generated successfully"
                });
            }
            else
            {
                // Fallback to memory mode
                return Ok(new GenerateStandardLicenseResponse
                {
                    Success = true,
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName,
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(request.ValidityDays),
                    ValidityDays = request.ValidityDays,
                    Message = "License generated successfully (memory mode - not persisted)"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "License generation failed");
            return StatusCode(500, new GenerateStandardLicenseResponse
            {
                Success = false,
                Message = $"Internal error: {ex.Message}"
            });
        }
    }

    private string GenerateSecureLicenseKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        using var rng = RandomNumberGenerator.Create();
        var result = new StringBuilder();
        byte[] randomBytes = new byte[20];
        rng.GetBytes(randomBytes);

        for (int i = 0; i < 16; i++)
        {
            result.Append(chars[randomBytes[i] % chars.Length]);
        }

        string randomPart = result.ToString();
        string timestamp = DateTime.UtcNow.ToString("yyMMdd");

        return $"PCOPT-{timestamp}-{randomPart.Substring(0, 4)}-{randomPart.Substring(4, 4)}-{randomPart.Substring(8, 4)}-{randomPart.Substring(12, 4)}";
    }
}

public class GenerateStandardLicenseRequest
{
    public string AdminSecret { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public int ValidityDays { get; set; } = 365;
}

public class GenerateStandardLicenseResponse
{
    public bool Success { get; set; }
    public string LicenseKey { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public DateTime CreationDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public int ValidityDays { get; set; }
    public string Message { get; set; } = "";
}