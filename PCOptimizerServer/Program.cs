using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
string connectionString;
try
{
    connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }
}
catch (Exception ex)
{
    // Log the error but don't crash - fall back to in-memory mode
    Console.WriteLine($"Database connection error: {ex.Message}");
    connectionString = null;
}

// Add database context only if we have a connection string
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<LicenseDbContext>(options =>
        options.UseSqlServer(connectionString));
}

var app = builder.Build();

// Configure pipeline
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Initialize database if available
bool databaseAvailable = false;
if (!string.IsNullOrEmpty(connectionString))
{
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            // Test database connection and create tables
            await context.Database.EnsureCreatedAsync();

            // Alternative: Use migrations if EnsureCreated doesn't work
            // await context.Database.MigrateAsync();

            // Seed test licenses if database is empty
            if (!context.Licenses.Any())
            {
                var testLicenses = new List<License>
                {
                    new License
                    {
                        LicenseKey = "PCOPT-12345-ABCDE-67890-FGHIJ",
                        CustomerName = "Test Customer Pro",
                        CustomerEmail = "testpro@example.com",
                        MaxActivations = 3,
                        CreationDate = DateTime.UtcNow,
                        ExpirationDate = DateTime.UtcNow.AddYears(1),
                        IsActive = true
                    },
                    new License
                    {
                        LicenseKey = "PCOPT-TRIAL-DEMO-TEST-12345",
                        CustomerName = "Trial Customer",
                        CustomerEmail = "trial@example.com",
                        MaxActivations = 1,
                        CreationDate = DateTime.UtcNow,
                        ExpirationDate = DateTime.UtcNow.AddMonths(1),
                        IsActive = true
                    }
                };

                context.Licenses.AddRange(testLicenses);
                context.SaveChanges();

                logger.LogInformation("Database seeded with test licenses");
            }

            databaseAvailable = true;
            app.Logger.LogInformation("Database connected successfully");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Database initialization failed - running in memory mode");
            databaseAvailable = false;
        }
    }
}

if (databaseAvailable)
{
    app.Logger.LogInformation("PC Optimizer License Server started with DATABASE");
}
else
{
    app.Logger.LogWarning("PC Optimizer License Server started in IN-MEMORY MODE");
}

app.Run();

// Database Models
public class License
{
    public int Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public int MaxActivations { get; set; } = 1;
    public DateTime CreationDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public bool IsActive { get; set; } = true;

    public List<LicenseActivation> Activations { get; set; } = new();
}

public class LicenseActivation
{
    public int Id { get; set; }
    public int LicenseId { get; set; }
    public string HardwareFingerprint { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime FirstActivated { get; set; }
    public DateTime LastSeen { get; set; }
    public string ProductVersion { get; set; } = "";

    public License License { get; set; } = null!;
}

public class LicenseValidationLog
{
    public int Id { get; set; }
    public int? LicenseId { get; set; }
    public string LicenseKey { get; set; } = "";
    public string HardwareFingerprint { get; set; } = "";
    public DateTime ValidationDate { get; set; }
    public bool IsSuccessful { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string IpAddress { get; set; } = "";
}

// Database Context
public class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<License> Licenses { get; set; }
    public DbSet<LicenseActivation> LicenseActivations { get; set; }
    public DbSet<LicenseValidationLog> LicenseValidationLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<License>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.Property(e => e.LicenseKey).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CustomerName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CustomerEmail).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<LicenseActivation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.HardwareFingerprint);
            entity.HasOne(e => e.License)
                .WithMany(e => e.Activations)
                .HasForeignKey(e => e.LicenseId);
        });

        modelBuilder.Entity<LicenseValidationLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ValidationDate);
        });
    }
}

// API Controller with fallback support
[ApiController]
[Route("api/[controller]")]
public class LicenseController : ControllerBase
{
    private readonly LicenseDbContext? _context;
    private readonly ILogger<LicenseController> _logger;

    // Fallback in-memory licenses if database isn't available
    private readonly Dictionary<string, LicenseInfo> fallbackLicenses = new()
    {
        ["PCOPT-12345-ABCDE-67890-FGHIJ"] = new LicenseInfo
        {
            CustomerName = "Test User Pro",
            ExpirationDate = DateTime.UtcNow.AddYears(1),
            MaxActivations = 3
        },
        ["PCOPT-TRIAL-DEMO-TEST-12345"] = new LicenseInfo
        {
            CustomerName = "Trial User",
            ExpirationDate = DateTime.UtcNow.AddMonths(1),
            MaxActivations = 1
        }
    };

    public LicenseController(ILogger<LicenseController> logger, LicenseDbContext? context = null)
    {
        _context = context;
        _logger = logger;
    }

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateLicense([FromBody] LicenseValidationRequest request)
    {
        try
        {
            _logger.LogInformation($"License validation request for: {request.LicenseKey?.Substring(0, Math.Min(5, request.LicenseKey?.Length ?? 0))}...");

            // Use database if available, otherwise fallback to in-memory
            if (_context != null)
            {
                return await ValidateWithDatabase(request);
            }
            else
            {
                return ValidateWithMemory(request);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during license validation");
            return StatusCode(500, new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "Internal server error"
            });
        }
    }

    private async Task<IActionResult> ValidateWithDatabase(LicenseValidationRequest request)
    {
        // Full database validation logic
        var license = await _context!.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == request.LicenseKey);

        if (license == null)
        {
            return Ok(new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "Invalid license key"
            });
        }

        if (!license.IsActive)
        {
            return Ok(new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "License has been deactivated"
            });
        }

        if (DateTime.UtcNow > license.ExpirationDate)
        {
            return Ok(new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "License has expired"
            });
        }

        // Handle hardware activation
        var existingActivation = license.Activations
            .FirstOrDefault(a => a.HardwareFingerprint == request.HardwareFingerprint);

        if (existingActivation == null)
        {
            // New hardware - check activation limit (always 1)
            if (license.Activations.Count >= 1)
            {
                return Ok(new LicenseValidationResponse
                {
                    IsValid = false,
                    ErrorMessage = "License is already activated on another computer"
                });
            }

            // Create new activation
            var newActivation = new LicenseActivation
            {
                LicenseId = license.Id,
                HardwareFingerprint = request.HardwareFingerprint,
                MachineName = request.MachineName ?? "Unknown",
                FirstActivated = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                ProductVersion = request.ProductVersion ?? "1.0"
            };

            _context.LicenseActivations.Add(newActivation);
        }
        else
        {
            // Update existing activation
            existingActivation.LastSeen = DateTime.UtcNow;
            existingActivation.MachineName = request.MachineName ?? existingActivation.MachineName;
        }

        // Log validation
        var validationLog = new LicenseValidationLog
        {
            LicenseId = license.Id,
            LicenseKey = request.LicenseKey,
            HardwareFingerprint = request.HardwareFingerprint,
            ValidationDate = DateTime.UtcNow,
            IsSuccessful = true,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        };

        _context.LicenseValidationLogs.Add(validationLog);
        await _context.SaveChangesAsync();

        return Ok(new LicenseValidationResponse
        {
            IsValid = true,
            CustomerName = license.CustomerName,
            ExpirationDate = license.ExpirationDate,
            RemainingActivations = 0 // Always 0 since only 1 activation allowed
        });
    }

    private IActionResult ValidateWithMemory(LicenseValidationRequest request)
    {
        // Simple in-memory validation
        if (fallbackLicenses.TryGetValue(request.LicenseKey, out var license))
        {
            if (DateTime.UtcNow > license.ExpirationDate)
            {
                return Ok(new LicenseValidationResponse
                {
                    IsValid = false,
                    ErrorMessage = "License has expired"
                });
            }

            return Ok(new LicenseValidationResponse
            {
                IsValid = true,
                CustomerName = license.CustomerName,
                ExpirationDate = license.ExpirationDate,
                RemainingActivations = license.MaxActivations
            });
        }

        return Ok(new LicenseValidationResponse
        {
            IsValid = false,
            ErrorMessage = "Invalid license key"
        });
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            if (_context != null)
            {
                var licenseCount = await _context.Licenses.CountAsync();
                return Ok(new
                {
                    Status = "Healthy",
                    Database = "Connected",
                    LicenseCount = licenseCount,
                    Mode = "Database",
                    Timestamp = DateTime.UtcNow
                });
            }
            else
            {
                return Ok(new
                {
                    Status = "Healthy",
                    Database = "In-Memory",
                    LicenseCount = fallbackLicenses.Count,
                    Mode = "Fallback",
                    Timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(500, new
            {
                Status = "Unhealthy",
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateLicense([FromBody] GenerateLicenseRequest request)
    {
        if (_context == null)
        {
            return BadRequest(new { Message = "Database not available - cannot generate licenses" });
        }

        try
        {
            // Updated admin secret - much more secure
            if (request.AdminSecret != "PCOpt2024_AdminKey_7x9mK3nP8qR5tY2wE6uI9oP1aS4dF7gH")
            {
                return Unauthorized();
            }

            string licenseKey = GenerateUniqueLicenseKey();

            var license = new License
            {
                LicenseKey = licenseKey,
                CustomerName = request.CustomerName,
                CustomerEmail = "", // No longer required
                MaxActivations = 1, // Always 1 - one license per computer
                CreationDate = DateTime.UtcNow,
                ExpirationDate = DateTime.UtcNow.AddDays(request.DaysValid),
                IsActive = true
            };

            _context.Licenses.Add(license);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                LicenseKey = licenseKey,
                CustomerName = request.CustomerName,
                ExpirationDate = license.ExpirationDate,
                ValidForDays = request.DaysValid,
                Message = "License generated successfully - Valid for 1 computer only"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating license");
            return StatusCode(500, "Error generating license");
        }
    }

    private string GenerateUniqueLicenseKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        var result = new StringBuilder();

        for (int i = 0; i < 20; i++)
        {
            result.Append(chars[random.Next(chars.Length)]);
        }

        string key = result.ToString();
        return $"PCOPT-{key.Substring(0, 4)}-{key.Substring(4, 4)}-{key.Substring(8, 4)}-{key.Substring(12, 4)}-{key.Substring(16, 4)}";
    }
}

// Request/Response models
public class LicenseValidationRequest
{
    public string LicenseKey { get; set; } = "";
    public string HardwareFingerprint { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
}

public class LicenseValidationResponse
{
    public bool IsValid { get; set; }
    public string CustomerName { get; set; } = "";
    public DateTime ExpirationDate { get; set; }
    public int RemainingActivations { get; set; }
    public string ErrorMessage { get; set; } = "";
}

public class GenerateLicenseRequest
{
    public string AdminSecret { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public int DaysValid { get; set; } = 365; // How many days the license should last
}

public class LicenseInfo
{
    public string CustomerName { get; set; } = "";
    public DateTime ExpirationDate { get; set; }
    public int MaxActivations { get; set; }
}