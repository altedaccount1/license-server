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
            await context.Database.EnsureCreatedAsync();

            // Seed with standard licenses only
            if (!context.Licenses.Any())
            {
                var standardLicenses = new List<License>
                {
                    new License
                    {
                        LicenseKey = "PCOPT-STD01-DEMO1-TEST1-12345",
                        CustomerName = "Demo User 1",
                        MaxActivations = 1,
                        CreationDate = DateTime.UtcNow,
                        ExpirationDate = DateTime.UtcNow.AddYears(1),
                        IsActive = true
                    },
                    new License
                    {
                        LicenseKey = "PCOPT-STD02-DEMO2-TEST2-67890",
                        CustomerName = "Demo User 2",
                        MaxActivations = 1,
                        CreationDate = DateTime.UtcNow,
                        ExpirationDate = DateTime.UtcNow.AddYears(1),
                        IsActive = true
                    }
                };

                context.Licenses.AddRange(standardLicenses);
                context.SaveChanges();

                app.Logger.LogInformation("Database seeded with demo standard licenses");
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

app.Logger.LogInformation(databaseAvailable ?
    "PC Optimizer License Server started with DATABASE" :
    "PC Optimizer License Server started in IN-MEMORY MODE");

app.Run();

// Database Models (unchanged)
public class License
{
    public int Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public string CustomerName { get; set; } = "";
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

// Database Context (unchanged)
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

// Enhanced API Controller
[ApiController]
[Route("api/[controller]")]
public class LicenseController : ControllerBase
{
    private readonly LicenseDbContext? _context;
    private readonly ILogger<LicenseController> _logger;

    // Admin secret for license generation (change this to your own secret)
    private const string ADMIN_SECRET = "PCOptimizer_Admin_Secret_2024!@#$%^&*()_+";

    // Fallback standard licenses if database isn't available
    private readonly Dictionary<string, StandardLicenseInfo> fallbackLicenses = new()
    {
        ["PCOPT-STD01-DEMO1-TEST1-12345"] = new StandardLicenseInfo
        {
            CustomerName = "Demo User 1",
            ExpirationDate = DateTime.UtcNow.AddYears(1)
        },
        ["PCOPT-STD02-DEMO2-TEST2-67890"] = new StandardLicenseInfo
        {
            CustomerName = "Demo User 2",
            ExpirationDate = DateTime.UtcNow.AddYears(1)
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

    // NEW: Simple license generation endpoint
    [HttpPost("generate")]
    public async Task<IActionResult> GenerateStandardLicense([FromBody] GenerateStandardLicenseRequest request)
    {
        try
        {
            // Validate admin secret
            if (request.AdminSecret != ADMIN_SECRET)
            {
                _logger.LogWarning($"Unauthorized license generation attempt from IP: {HttpContext.Connection.RemoteIpAddress}");
                return Unauthorized(new { Message = "Invalid admin secret" });
            }

            // Validate request
            if (string.IsNullOrWhiteSpace(request.CustomerName))
            {
                return BadRequest(new { Message = "Customer name is required" });
            }

            if (request.ValidityDays <= 0 || request.ValidityDays > 3650) // Max 10 years
            {
                return BadRequest(new { Message = "Validity days must be between 1 and 3650" });
            }

            // Generate new license key
            string licenseKey = GenerateSecureLicenseKey();

            if (_context != null)
            {
                // Save to database
                var license = new License
                {
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName,
                    MaxActivations = 1, // Always 1 for standard licenses
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(request.ValidityDays),
                    IsActive = true
                };

                _context.Licenses.Add(license);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"Generated license {licenseKey} for {request.CustomerName} (valid for {request.ValidityDays} days)");

                return Ok(new GenerateStandardLicenseResponse
                {
                    Success = true,
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName,
                    CreationDate = license.CreationDate,
                    ExpirationDate = license.ExpirationDate,
                    ValidityDays = request.ValidityDays,
                    Message = "Standard license generated successfully"
                });
            }
            else
            {
                // Database not available
                return StatusCode(503, new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "License generation unavailable - database not connected"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating standard license");
            return StatusCode(500, new GenerateStandardLicenseResponse
            {
                Success = false,
                Message = "Internal error generating license"
            });
        }
    }

    // NEW: Bulk license generation
    [HttpPost("generate-bulk")]
    public async Task<IActionResult> GenerateBulkStandardLicenses([FromBody] GenerateBulkLicensesRequest request)
    {
        try
        {
            // Validate admin secret
            if (request.AdminSecret != ADMIN_SECRET)
            {
                return Unauthorized(new { Message = "Invalid admin secret" });
            }

            // Validate request
            if (request.Count <= 0 || request.Count > 1000) // Max 1000 at once
            {
                return BadRequest(new { Message = "Count must be between 1 and 1000" });
            }

            if (request.ValidityDays <= 0 || request.ValidityDays > 3650)
            {
                return BadRequest(new { Message = "Validity days must be between 1 and 3650" });
            }

            if (_context == null)
            {
                return StatusCode(503, new { Message = "Bulk generation unavailable - database not connected" });
            }

            var generatedLicenses = new List<GenerateStandardLicenseResponse>();
            var licenses = new List<License>();

            for (int i = 1; i <= request.Count; i++)
            {
                string licenseKey = GenerateSecureLicenseKey();
                string customerName = request.CustomerNamePrefix + (request.Count > 1 ? $" #{i:000}" : "");

                var license = new License
                {
                    LicenseKey = licenseKey,
                    CustomerName = customerName,
                    MaxActivations = 1,
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(request.ValidityDays),
                    IsActive = true
                };

                licenses.Add(license);
                generatedLicenses.Add(new GenerateStandardLicenseResponse
                {
                    Success = true,
                    LicenseKey = licenseKey,
                    CustomerName = customerName,
                    CreationDate = license.CreationDate,
                    ExpirationDate = license.ExpirationDate,
                    ValidityDays = request.ValidityDays
                });
            }

            // Save all licenses to database
            _context.Licenses.AddRange(licenses);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Generated {request.Count} bulk licenses with prefix '{request.CustomerNamePrefix}'");

            return Ok(new
            {
                Success = true,
                Count = request.Count,
                Licenses = generatedLicenses,
                Message = $"Successfully generated {request.Count} standard licenses"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating bulk licenses");
            return StatusCode(500, new { Message = "Internal error generating bulk licenses" });
        }
    }

    // Enhanced license key generation with better security
    private string GenerateSecureLicenseKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        using (var rng = RandomNumberGenerator.Create())
        {
            var result = new StringBuilder();
            byte[] randomBytes = new byte[25]; // Extra bytes for better randomness
            rng.GetBytes(randomBytes);

            for (int i = 0; i < 20; i++)
            {
                result.Append(chars[randomBytes[i] % chars.Length]);
            }

            string randomPart = result.ToString();
            string timestamp = DateTime.UtcNow.ToString("yyMMdd");

            // Create formatted license key
            return $"PCOPT-{timestamp}-{randomPart.Substring(0, 4)}-{randomPart.Substring(4, 4)}-{randomPart.Substring(8, 4)}-{randomPart.Substring(12, 4)}-{randomPart.Substring(16, 4)}";
        }
    }

    private async Task<IActionResult> ValidateWithDatabase(LicenseValidationRequest request)
    {
        var license = await _context!.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == request.LicenseKey);

        if (license == null)
        {
            await LogValidation(null, request.LicenseKey, request.HardwareFingerprint, false, "Invalid license key");
            return Ok(new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "Invalid license key"
            });
        }

        if (!license.IsActive)
        {
            await LogValidation(license.Id, request.LicenseKey, request.HardwareFingerprint, false, "License deactivated");
            return Ok(new LicenseValidationResponse
            {
                IsValid = false,
                ErrorMessage = "License has been deactivated"
            });
        }

        if (DateTime.UtcNow > license.ExpirationDate)
        {
            await LogValidation(license.Id, request.LicenseKey, request.HardwareFingerprint, false, "License expired");
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
            // New hardware - check activation limit
            if (license.Activations.Count >= license.MaxActivations)
            {
                await LogValidation(license.Id, request.LicenseKey, request.HardwareFingerprint, false, "Activation limit reached");
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

        await LogValidation(license.Id, request.LicenseKey, request.HardwareFingerprint, true, "Success");
        await _context.SaveChangesAsync();

        return Ok(new LicenseValidationResponse
        {
            IsValid = true,
            CustomerName = license.CustomerName,
            ExpirationDate = license.ExpirationDate,
            RemainingActivations = Math.Max(0, license.MaxActivations - license.Activations.Count)
        });
    }

    private async Task LogValidation(int? licenseId, string licenseKey, string hardwareFingerprint, bool isSuccessful, string errorMessage)
    {
        if (_context == null) return;

        var validationLog = new LicenseValidationLog
        {
            LicenseId = licenseId,
            LicenseKey = licenseKey,
            HardwareFingerprint = hardwareFingerprint,
            ValidationDate = DateTime.UtcNow,
            IsSuccessful = isSuccessful,
            ErrorMessage = errorMessage,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        };

        _context.LicenseValidationLogs.Add(validationLog);
    }

    private IActionResult ValidateWithMemory(LicenseValidationRequest request)
    {
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
                RemainingActivations = 1
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
                var activeCount = await _context.Licenses.CountAsync(l => l.IsActive && l.ExpirationDate > DateTime.UtcNow);

                return Ok(new
                {
                    Status = "Healthy",
                    Database = "Connected",
                    TotalLicenses = licenseCount,
                    ActiveLicenses = activeCount,
                    Mode = "Database",
                    ServerTime = DateTime.UtcNow,
                    Version = "2.0"
                });
            }
            else
            {
                return Ok(new
                {
                    Status = "Healthy",
                    Database = "In-Memory",
                    TotalLicenses = fallbackLicenses.Count,
                    ActiveLicenses = fallbackLicenses.Count(kvp => kvp.Value.ExpirationDate > DateTime.UtcNow),
                    Mode = "Fallback",
                    ServerTime = DateTime.UtcNow,
                    Version = "2.0"
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
                ServerTime = DateTime.UtcNow
            });
        }
    }
}

// Request/Response Models
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

public class GenerateStandardLicenseRequest
{
    public string AdminSecret { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public int ValidityDays { get; set; } = 365; // Default 1 year
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

public class GenerateBulkLicensesRequest
{
    public string AdminSecret { get; set; } = "";
    public string CustomerNamePrefix { get; set; } = "Standard User";
    public int Count { get; set; } = 1;
    public int ValidityDays { get; set; } = 365;
}

public class StandardLicenseInfo
{
    public string CustomerName { get; set; } = "";
    public DateTime ExpirationDate { get; set; }
}