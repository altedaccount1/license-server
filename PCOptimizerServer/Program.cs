using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "PC Optimizer License Server API",
        Version = "v1",
        Description = "API for PC Optimizer license management"
    });
});

// Enhanced logging with detailed SQL logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

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

// Database configuration with enhanced error handling
string connectionString = null;
bool databaseAvailable = false;

try
{
    connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.WriteLine("⚠️ WARNING: No connection string found. Server will run in memory mode.");
    }
    else
    {
        Console.WriteLine($"📊 Connection string found: {connectionString.Substring(0, Math.Min(50, connectionString.Length))}...");

        // Add database context with detailed logging
        builder.Services.AddDbContext<LicenseDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(120); // 2 minute timeout
            });

            // Enable detailed logging in development
            if (builder.Environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
                options.LogTo(Console.WriteLine, LogLevel.Information);
            }
        });

        databaseAvailable = true;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Database configuration error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    databaseAvailable = false;
}

var app = builder.Build();

// Configure pipeline - Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "PC Optimizer License Server v1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "PC Optimizer License Server API";
});

// Only show developer exception page in development
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Enhanced database initialization with detailed error logging
if (databaseAvailable && !string.IsNullOrEmpty(connectionString))
{
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            // Test database connection
            app.Logger.LogInformation("🔍 Testing database connection...");
            var canConnect = await context.Database.CanConnectAsync();

            if (!canConnect)
            {
                app.Logger.LogError("❌ Cannot connect to database");
                databaseAvailable = false;
            }
            else
            {
                // Check if database exists, if not create it
                app.Logger.LogInformation("📋 Ensuring database is created...");
                var created = await context.Database.EnsureCreatedAsync();
                if (created)
                {
                    app.Logger.LogInformation("🆕 Database created successfully");
                }
                else
                {
                    app.Logger.LogInformation("✅ Database already exists");
                }

                // Test basic database operations
                app.Logger.LogInformation("🧪 Testing basic database operations...");
                var existingLicenseCount = await context.Licenses.CountAsync();
                app.Logger.LogInformation($"📊 Found {existingLicenseCount} existing licenses");

                // Seed with standard licenses only if none exist
                if (existingLicenseCount == 0)
                {
                    app.Logger.LogInformation("🌱 Seeding database with demo licenses...");
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
                    await context.SaveChangesAsync();

                    app.Logger.LogInformation("✅ Database seeded with demo standard licenses");
                }

                databaseAvailable = true;
                app.Logger.LogInformation("✅ Database connected and initialized successfully");
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "❌ Database initialization failed - running in memory mode");
            app.Logger.LogError($"Exception details: {ex}");
            if (ex.InnerException != null)
            {
                app.Logger.LogError($"Inner exception: {ex.InnerException}");
            }
            databaseAvailable = false;
        }
    }
}

app.Logger.LogInformation(databaseAvailable ?
    "🚀 PC Optimizer License Server started with DATABASE SUPPORT" :
    "⚠️ PC Optimizer License Server started in IN-MEMORY MODE (Limited functionality)");

app.Run();

// Database Models with better constraints
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

// Enhanced Database Context with better configuration
public class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<License> Licenses { get; set; }
    public DbSet<LicenseActivation> LicenseActivations { get; set; }
    public DbSet<LicenseValidationLog> LicenseValidationLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enhanced License configuration
        modelBuilder.Entity<License>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.Property(e => e.LicenseKey).IsRequired().HasMaxLength(200); // Increased size
            entity.Property(e => e.CustomerName).IsRequired().HasMaxLength(300); // Increased size
            entity.Property(e => e.CreationDate).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.MaxActivations).HasDefaultValue(1);
        });

        // Enhanced LicenseActivation configuration
        modelBuilder.Entity<LicenseActivation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.HardwareFingerprint);
            entity.Property(e => e.HardwareFingerprint).IsRequired().HasMaxLength(100);
            entity.Property(e => e.MachineName).HasMaxLength(100);
            entity.Property(e => e.ProductVersion).HasMaxLength(50);
            entity.Property(e => e.FirstActivated).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.LastSeen).HasDefaultValueSql("GETUTCDATE()");

            entity.HasOne(e => e.License)
                .WithMany(e => e.Activations)
                .HasForeignKey(e => e.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Enhanced LicenseValidationLog configuration
        modelBuilder.Entity<LicenseValidationLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ValidationDate);
            entity.Property(e => e.LicenseKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.HardwareFingerprint).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.ValidationDate).HasDefaultValueSql("GETUTCDATE()");
        });
    }
}

// Enhanced API Controller with detailed error logging
[ApiController]
[Route("api/[controller]")]
public class LicenseController : ControllerBase
{
    private readonly LicenseDbContext? _context;
    private readonly ILogger<LicenseController> _logger;

    // Admin secret for license generation
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

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateStandardLicense([FromBody] GenerateStandardLicenseRequest request)
    {
        _logger.LogInformation("📝 License generation request received");

        try
        {
            // Enhanced request validation
            if (request == null)
            {
                _logger.LogWarning("❌ License generation failed: Request is null");
                return BadRequest(new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Request body is required"
                });
            }

            // Log request details (without sensitive data)
            _logger.LogInformation($"Request details - Customer: '{request.CustomerName}', Days: {request.ValidityDays}");

            // Validate admin secret
            if (string.IsNullOrEmpty(request.AdminSecret) || request.AdminSecret != ADMIN_SECRET)
            {
                _logger.LogWarning($"🚫 Unauthorized license generation attempt from IP: {HttpContext.Connection.RemoteIpAddress}");
                return Unauthorized(new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Invalid admin secret"
                });
            }

            // Validate customer name
            if (string.IsNullOrWhiteSpace(request.CustomerName))
            {
                _logger.LogWarning("❌ License generation failed: Customer name is required");
                return BadRequest(new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Customer name is required"
                });
            }

            // Validate customer name length
            if (request.CustomerName.Trim().Length > 250)
            {
                _logger.LogWarning($"❌ License generation failed: Customer name too long: {request.CustomerName.Length} characters");
                return BadRequest(new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Customer name must be 250 characters or less"
                });
            }

            // Validate validity days
            if (request.ValidityDays <= 0 || request.ValidityDays > 3650)
            {
                _logger.LogWarning($"❌ License generation failed: Invalid validity days: {request.ValidityDays}");
                return BadRequest(new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Validity days must be between 1 and 3650"
                });
            }

            // Check database availability
            if (_context == null)
            {
                _logger.LogError("❌ License generation failed: Database context is null");
                return StatusCode(503, new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "License generation unavailable - database not connected"
                });
            }

            // Generate license key
            string licenseKey;
            try
            {
                licenseKey = GenerateSecureLicenseKey();
                _logger.LogInformation($"🔑 Generated license key: {licenseKey}");
            }
            catch (Exception keyEx)
            {
                _logger.LogError(keyEx, "❌ Failed to generate license key");
                return StatusCode(500, new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = "Failed to generate secure license key"
                });
            }

            // Check for duplicate license key (very unlikely but good practice)
            try
            {
                var existingLicense = await _context.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == licenseKey);
                if (existingLicense != null)
                {
                    _logger.LogWarning($"⚠️ Duplicate license key generated: {licenseKey}, regenerating...");
                    licenseKey = GenerateSecureLicenseKey(); // Try once more
                }
            }
            catch (Exception dupEx)
            {
                _logger.LogError(dupEx, "❌ Error checking for duplicate license key");
                // Continue anyway, very unlikely to have duplicates
            }

            // Save to database with detailed error logging
            try
            {
                var license = new License
                {
                    LicenseKey = licenseKey,
                    CustomerName = request.CustomerName.Trim(),
                    MaxActivations = 1, // Always 1 for standard licenses
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = DateTime.UtcNow.AddDays(request.ValidityDays),
                    IsActive = true
                };

                _logger.LogInformation($"💾 Adding license to database: {licenseKey}");
                _context.Licenses.Add(license);

                _logger.LogInformation($"💾 Saving changes to database...");
                var changes = await _context.SaveChangesAsync();
                _logger.LogInformation($"✅ Saved {changes} changes to database");

                _logger.LogInformation($"✅ Successfully generated license {licenseKey} for '{request.CustomerName}' (valid for {request.ValidityDays} days)");

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
            catch (DbUpdateException dbUpdateEx)
            {
                _logger.LogError(dbUpdateEx, $"❌ Database update error generating license for '{request.CustomerName}'");
                _logger.LogError($"DbUpdateException details: {dbUpdateEx.Message}");

                if (dbUpdateEx.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {dbUpdateEx.InnerException.Message}");
                    if (dbUpdateEx.InnerException.InnerException != null)
                    {
                        _logger.LogError($"Inner-inner exception: {dbUpdateEx.InnerException.InnerException.Message}");
                    }
                }

                // Check for specific database errors
                var errorMessage = "Database error occurred";
                if (dbUpdateEx.InnerException?.Message.Contains("duplicate") == true)
                {
                    errorMessage = "Duplicate license key error";
                }
                else if (dbUpdateEx.InnerException?.Message.Contains("constraint") == true)
                {
                    errorMessage = "Database constraint violation";
                }
                else if (dbUpdateEx.InnerException?.Message.Contains("timeout") == true)
                {
                    errorMessage = "Database timeout error";
                }

                return StatusCode(500, new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = $"Database error: {errorMessage}. Please check server logs for details."
                });
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, $"❌ General database error generating license for '{request.CustomerName}'");
                _logger.LogError($"Exception type: {dbEx.GetType().Name}");
                _logger.LogError($"Exception message: {dbEx.Message}");
                _logger.LogError($"Stack trace: {dbEx.StackTrace}");

                if (dbEx.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {dbEx.InnerException.Message}");
                }

                return StatusCode(500, new GenerateStandardLicenseResponse
                {
                    Success = false,
                    Message = $"Database error: {dbEx.Message}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Unexpected error generating license for customer: {request?.CustomerName}");
            _logger.LogError($"Exception details: {ex}");
            return StatusCode(500, new GenerateStandardLicenseResponse
            {
                Success = false,
                Message = $"Internal server error: {ex.Message}"
            });
        }
    }

    // Enhanced license key generation with collision detection
    private string GenerateSecureLicenseKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        try
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var result = new StringBuilder();
                byte[] randomBytes = new byte[25];
                rng.GetBytes(randomBytes);

                for (int i = 0; i < 20; i++)
                {
                    result.Append(chars[randomBytes[i] % chars.Length]);
                }

                string randomPart = result.ToString();
                string timestamp = DateTime.UtcNow.ToString("yyMMdd");

                // Add milliseconds to reduce collision chance
                string milliseconds = DateTime.UtcNow.Millisecond.ToString("000");

                return $"PCOPT-{timestamp}-{randomPart.Substring(0, 4)}-{randomPart.Substring(4, 4)}-{randomPart.Substring(8, 4)}-{randomPart.Substring(12, 4)}-{milliseconds}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error generating secure license key");
            throw new InvalidOperationException("Failed to generate license key", ex);
        }
    }

    // Test endpoint to check database connectivity
    [HttpGet("test-db")]
    public async Task<IActionResult> TestDatabase()
    {
        try
        {
            if (_context == null)
            {
                return Ok(new { Status = "No database context", CanConnect = false });
            }

            var canConnect = await _context.Database.CanConnectAsync();
            var licenseCount = canConnect ? await _context.Licenses.CountAsync() : -1;

            return Ok(new
            {
                Status = "Database test completed",
                CanConnect = canConnect,
                LicenseCount = licenseCount,
                ConnectionString = _context.Database.GetConnectionString()?.Substring(0, 50) + "..."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database test failed");
            return StatusCode(500, new { Status = "Database test failed", Error = ex.Message });
        }
    }

    // Rest of your existing methods (validate, health, etc.)...
    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            if (_context != null)
            {
                var canConnect = await _context.Database.CanConnectAsync();
                if (!canConnect)
                {
                    return Ok(new
                    {
                        Status = "Degraded",
                        Database = "Connection Failed",
                        Message = "Database connection test failed",
                        ServerTime = DateTime.UtcNow,
                        Version = "2.1"
                    });
                }

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
                    Version = "2.1"
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
                    Version = "2.1"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Health check failed");
            return StatusCode(500, new
            {
                Status = "Unhealthy",
                Error = ex.Message,
                ServerTime = DateTime.UtcNow
            });
        }
    }
}

// Request/Response Models (same as before)
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

public class StandardLicenseInfo
{
    public string CustomerName { get; set; } = "";
    public DateTime ExpirationDate { get; set; }
}