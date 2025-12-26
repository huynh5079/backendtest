using BusinessLayer.Options;
using BusinessLayer.Service;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using BusinessLayer.Service.ScheduleService;
using BusinessLayer.Storage;
using BusinessLayer.Validators;
using BusinessLayer.Validators.Abstraction;
using DataLayer.Entities;
using DataLayer.Repositories;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using DataLayer.Repositories.GenericType;
using DataLayer.Repositories.GenericType.Abstraction;
using DataLayer.Repositories.Schedule;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Serialization;
using TPEdu_API.Common.Errors;
using TPEdu_API.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure TLS 1.2 for PayOS API (only in Development)
// Note: ServicePointManager may not work in .NET Core/5+, but we set it anyway for compatibility
// In Production, TLS is handled by the cloud platform
if (builder.Environment.IsDevelopment())
{
    // Force TLS 1.2 only (PayOS may not support TLS 1.3)
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    ServicePointManager.DefaultConnectionLimit = 100;
    Console.WriteLine("[Program.cs] 🔧 Development: ServicePointManager.SecurityProtocol = Tls12");
    Console.WriteLine("[Program.cs] 🔧 Development: DefaultConnectionLimit = 100");
}

// Log environment to ensure correct appsettings file is loaded
Console.WriteLine($"🔧 ENVIRONMENT = {builder.Environment.EnvironmentName}");
Console.WriteLine($"🔧 IsDevelopment = {builder.Environment.IsDevelopment()}");
Console.WriteLine($"🔧 IsProduction = {builder.Environment.IsProduction()}");

// Configure Kestrel for large file uploads (documents, videos)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500_000_000; // 500 MB limit
});

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Allow API read string ("Monday", "Tuesday") 
        // Change to enum DayOfWeek (1, 2)
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        
        // Add DateOnly converters for proper JSON serialization
        options.JsonSerializerOptions.Converters.Add(new TPEdu_API.Common.Converters.DateOnlyJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new TPEdu_API.Common.Converters.NullableDateOnlyJsonConverter());
        
        // Add DateTime converters để serialize DateTime với Vietnam timezone (UTC+7)
        // Backend lưu DateTime ở Vietnam time, nên serialize không có timezone info
        // Frontend sẽ parse như local time và hiển thị đúng
        options.JsonSerializerOptions.Converters.Add(new TPEdu_API.Common.Converters.DateTimeJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new TPEdu_API.Common.Converters.NullableDateTimeJsonConverter());
    });

// Configure Form Options for file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500_000_000; // 500 MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "TPEdu API",
        Version = "v1"
    });
    
    // Cấu hình để Swagger hiểu IFormFile cho file uploads
    c.MapType<Microsoft.AspNetCore.Http.IFormFile>(() => new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        // Với SignalR cần AllowCredentials, không thể dùng AllowAnyOrigin
        // Trong development, cho phép tất cả origins nhưng cần SetIsOriginAllowed
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials(); // Cần cho SignalR
    });
});

// DbContext
var sqlConnection = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<TpeduContext>(options =>
    options.UseSqlServer(sqlConnection));

// Generic Repository (open generic)
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// Application Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddSingleton<StoragePathResolver>();
builder.Services.AddScoped<IFileStorageService, CloudinaryStorageService>();
// Configure HttpClient cho VideoAnalysisService với timeout dài hơn cho video lớn
builder.Services.AddHttpClient<VideoAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(30); // Timeout 30 phút cho video lớn
});
builder.Services.AddScoped<IMediaService, MediaService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<ITutorProfileApprovalService, TutorProfileApprovalService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAdminDirectoryService, AdminDirectoryService>();
builder.Services.AddScoped<ITutorProfileService, TutorProfileService>();
builder.Services.AddScoped<IPublicTutorService, PublicTutorService>();
builder.Services.AddScoped<ITutorDashboardService, TutorDashboardService>();
builder.Services.AddScoped<IParentChildrenService, ParentChildrenService>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IWithdrawalService, WithdrawalService>();
builder.Services.AddScoped<IEscrowService, EscrowService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<ICommissionManagementService, CommissionManagementService>();
builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
builder.Services.AddScoped<IClassStatusCheckService, ClassStatusCheckService>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();
builder.Services.AddScoped<ILessonMaterialService, LessonMaterialService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IFavoriteTutorService, FavoriteTutorService>();
builder.Services.AddScoped<IQuizFileParserService, QuizFileParserService>();
builder.Services.AddScoped<IMaterialContentValidatorService, MaterialContentValidatorService>();
builder.Services.AddScoped<IQuizService, QuizService>();
builder.Services.AddScoped<CloudinaryStorageService>();
builder.Services.AddScoped<IAutoReportService, AutoReportService>();

// Background Services
builder.Services.AddHostedService<ClassStatusCheckBackgroundService>();
builder.Services.AddHostedService<TPEdu_API.BackgroundServices.AutoReportBackgroundService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<INotificationHubService, NotificationHubService>();
builder.Services.AddSingleton<TPEdu_API.Services.ChatConnectionManager>();
builder.Services.AddScoped<IChatHubService, TPEdu_API.Services.ChatHubService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IMomoPaymentService, MomoPaymentService>();
builder.Services.AddScoped<IPayOSPaymentService, PayOSPaymentService>();
builder.Services.AddScoped<IVideoAnalysisService, VideoAnalysisService>();

// Schedule Transactions
builder.Services.AddScoped<IAvailabilityBlockService, AvailabilityBlockService>();
builder.Services.AddScoped<ITutorApplicationService, TutorApplicationService>();
builder.Services.AddScoped<IClassRequestService, ClassRequestService>();
builder.Services.AddScoped<IScheduleGenerationService, ScheduleGenerationService>();
builder.Services.AddScoped<IClassService, ClassService>();
builder.Services.AddScoped<IScheduleViewService, ScheduleViewService>();
builder.Services.AddScoped<IAssignService, AssignService>();
builder.Services.AddScoped<IStudentProfileService, StudentProfileService>();
builder.Services.AddScoped<ILessonRescheduleService, LessonRescheduleService>();
builder.Services.AddScoped<ILessonService, LessonService>();

// Validation Architecture - Shared Validators
builder.Services.AddScoped<ITextContentValidator, TextContentValidator>();
builder.Services.AddScoped<IImageContentValidator, ImageContentValidator>();
builder.Services.AddScoped<IVideoContentValidator, VideoContentValidator>();
builder.Services.AddScoped<IDocumentContentValidator, DocumentContentValidator>();
builder.Services.AddScoped<IMaterialContentValidatorService, MaterialContentValidatorService>();

// Exception Handler & ProblemDetails
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

// Repositories (specific)
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();
builder.Services.AddScoped<IStudentProfileRepository, StudentProfileRepository>();
builder.Services.AddScoped<IParentProfileRepository, ParentProfileRepository>();
builder.Services.AddScoped<ITutorProfileRepository, TutorProfileRepository>();
builder.Services.AddScoped<IMediaRepository, MediaRepository>();
builder.Services.AddScoped<IClassRepository, ClassRepository>();
builder.Services.AddScoped<IClassScheduleRepository, ClassScheduleRepository>();
builder.Services.AddScoped<IAttendanceRepository, AttendanceRepository>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<IEscrowRepository, EscrowRepository>();
builder.Services.AddScoped<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentLogRepository, PaymentLogRepository>();
builder.Services.AddScoped<IRescheduleRequestRepository, RescheduleRequestRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<ICommissionRepository, CommissionRepository>();
builder.Services.AddScoped<IFavoriteTutorRepository, FavoriteTutorRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<ITutorDepositEscrowRepository, TutorDepositEscrowRepository>();
builder.Services.AddScoped<ISystemSettingsRepository, SystemSettingsRepository>();
builder.Services.AddScoped<IQuizRepository, QuizRepository>();
builder.Services.AddScoped<IQuizQuestionRepository, QuizQuestionRepository>();
builder.Services.AddScoped<IStudentQuizAttemptRepository, StudentQuizAttemptRepository>();
builder.Services.AddScoped<IStudentQuizAnswerRepository, StudentQuizAnswerRepository>();
builder.Services.AddScoped<DataLayer.Repositories.Abstraction.IVideoAnalysisRepository, DataLayer.Repositories.VideoAnalysisRepository>();

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Schedule Transactions
builder.Services.AddScoped<IClassRequestRepository, ClassRequestRepository>();
builder.Services.AddScoped<ITutorApplicationRepository, TutorApplicationRepository>();
builder.Services.AddScoped<IClassAssignRepository, ClassAssignRepository>();
builder.Services.AddScoped<ILessonRepository, LessonRepository>();
builder.Services.AddScoped<IScheduleEntryRepository, ScheduleEntryRepository>();
builder.Services.AddScoped<IAvailabilityBlockRepository, AvailabilityBlockRepository>();
builder.Services.AddScoped<IScheduleUnitOfWork, ScheduleUnitOfWork>();
builder.Services.AddScoped<IClassRepository2, ClassRepository2>();


// Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<CloudinaryOptions>(builder.Configuration.GetSection("Cloudinary"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.Configure<OtpOptions>(builder.Configuration.GetSection("Otp"));
builder.Services.Configure<SystemWalletOptions>(builder.Configuration.GetSection("SystemWallet"));
builder.Services.Configure<MomoOptions>(builder.Configuration.GetSection("Momo"));
builder.Services.Configure<PayOSOptions>(builder.Configuration.GetSection("PayOS"));
builder.Services.Configure<CommissionOptions>(builder.Configuration.GetSection("Commission"));
builder.Services.Configure<AutoReportSettings>(builder.Configuration.GetSection("AutoReportSettings"));
builder.Services.Configure<FrontendSettings>(builder.Configuration.GetSection("FrontendSettings"));

builder.Services.AddHttpClient();

// Configure HttpClient for PayOS
// Development: Use WinHttpHandler (Windows native HTTP stack) for better SSL compatibility
// Production: use proper SSL validation (cloud platform handles TLS)
builder.Services.AddHttpClient("PayOS", client =>
{
    // Configure timeout
    client.Timeout = TimeSpan.FromSeconds(60);
    // Add User-Agent to avoid being blocked
    client.DefaultRequestHeaders.Add("User-Agent", "TPEdu-API/1.0");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    if (builder.Environment.IsDevelopment())
    {
        // DEVELOPMENT: Use WinHttpHandler for Windows native HTTP stack
        // WinHttpHandler uses Windows HTTP Services (WinHTTP) which has better SSL compatibility
        var handler = new System.Net.Http.WinHttpHandler
        {
            // WinHTTP handles TLS automatically based on Windows settings
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            WindowsProxyUsePolicy = System.Net.Http.WindowsProxyUsePolicy.DoNotUseProxy, // Disable proxy
            ServerCertificateValidationCallback = (request, cert, chain, errors) => true, // Bypass SSL for dev
            ReceiveHeadersTimeout = TimeSpan.FromSeconds(30),
            ReceiveDataTimeout = TimeSpan.FromSeconds(60),
            SendTimeout = TimeSpan.FromSeconds(30)
        };
        Console.WriteLine("[Program.cs] ✅ Development: HttpClient 'PayOS' configured with WinHttpHandler");
        Console.WriteLine("[Program.cs] ✅   - Using Windows native HTTP stack (WinHTTP)");
        Console.WriteLine("[Program.cs] ✅   - SSL validation: BYPASSED");
        Console.WriteLine("[Program.cs] ✅   - Proxy: DISABLED");
        return handler;
    }
    else
    {
        // PRODUCTION: Use SocketsHttpHandler with proper SSL validation
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        Console.WriteLine("[Program.cs] ✅ Production: HttpClient 'PayOS' configured with proper SSL validation");
        return handler;
    }
});

// Redis (Azure Cache for Redis)
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

if (!string.IsNullOrEmpty(redisConnectionString))
{
    // Đăng ký IConnectionMultiplexer (Dành cho OtpService và các service dùng Redis trực tiếp)
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var configuration = ConfigurationOptions.Parse(redisConnectionString, true);
        configuration.AbortOnConnectFail = false; // Quan trọng cho Azure để tránh lỗi timeout lúc khởi động
        configuration.ConnectTimeout = 3000; // 3 seconds
        configuration.SyncTimeout = 3000;
        configuration.AsyncTimeout = 3000;
        return ConnectionMultiplexer.Connect(configuration);
    });

    // Đăng ký IDistributedCache nếu bạn muốn dùng cache của .NET
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "tpedu:";
    });
}
else
{
    // Fallback to in-memory cache nếu không có Redis config
    builder.Services.AddDistributedMemoryCache();
    
    // Tạo một mock IConnectionMultiplexer để tránh lỗi DI
    // Lưu ý: OtpService sẽ không hoạt động nếu không có Redis thực sự
    // Để chạy local không cần Redis, comment Redis connection string trong appsettings.Development.json
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        // Tạm thời tạo mock connection để tránh lỗi DI khi chạy local
        // Lưu ý: Các service dùng Redis (như OtpService) sẽ không hoạt động
        try
        {
            // Thử connect với abortConnect=false để không block nếu không có Redis
            return ConnectionMultiplexer.Connect("localhost:6379,abortConnect=false,connectTimeout=1000");
        }
        catch
        {
            // Nếu không connect được, vẫn trả về null để tránh crash
            // Service sẽ handle null check
            return null!;
        }
    });
}

// Debug: Log Gemini API Keys khi app start
var geminiApiKey = builder.Configuration["Gemini:ApiKey"] ?? "";
var geminiVideoApiKey = builder.Configuration["Gemini_Video:ApiKey"] ?? "";

if (!string.IsNullOrEmpty(geminiApiKey))
{
    var keyPreview = geminiApiKey.Substring(0, Math.Min(15, geminiApiKey.Length));
    Console.WriteLine($"🔑 [Program.cs] Gemini API Key loaded at startup: {keyPreview}...");
}
else
{
    Console.WriteLine("⚠️ [Program.cs] WARNING: Gemini API Key is empty or not found!");
}

if (!string.IsNullOrEmpty(geminiVideoApiKey))
{
    var keyPreview = geminiVideoApiKey.Substring(0, Math.Min(15, geminiVideoApiKey.Length));
    Console.WriteLine($"🔑 [Program.cs] Gemini_Video API Key loaded at startup: {keyPreview}...");
}
else
{
    Console.WriteLine("⚠️ [Program.cs] WARNING: Gemini_Video API Key is empty or not found!");
}

// JWT
var jwtKey = builder.Configuration["Jwt:Key"] ?? "";
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // Cấu hình JWT cho SignalR WebSocket
        // SignalR sử dụng query string để truyền token khi handshake
        o.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Lấy token từ query string (SignalR WebSocket handshake)
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                // Chỉ lấy token từ SignalR hub endpoint
                if (!string.IsNullOrEmpty(accessToken) && 
                    (path.StartsWithSegments("/tpedu/v1/notificationhub") || 
                     path.StartsWithSegments("/tpedu/v1/chathub")))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// SignalR
builder.Services.AddSignalR();

var app = builder.Build();

// HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

// Log all incoming requests for debugging
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/tpedu/v1/payments/payos"))
    {
        Console.WriteLine($"[Middleware] 🔍 Request: {context.Request.Method} {context.Request.Path}");
        Console.WriteLine($"[Middleware] 🔍 Query: {context.Request.QueryString}");
        Console.WriteLine($"[Middleware] 🔍 Headers: Authorization={context.Request.Headers.ContainsKey("Authorization")}");
        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            var authHeader = context.Request.Headers["Authorization"].ToString();
            Console.WriteLine($"[Middleware] 🔍 Authorization Header: {authHeader.Substring(0, Math.Min(50, authHeader.Length))}...");
        }
    }
    await next();
    // Log response status after processing
    if (context.Request.Path.StartsWithSegments("/tpedu/v1/payments/payos"))
    {
        Console.WriteLine($"[Middleware] ✅ Response Status: {context.Response.StatusCode}");
    }
});

app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Map SignalR Hub
app.MapHub<TPEdu_API.Hubs.NotificationHub>("/tpedu/v1/notificationhub");
app.MapHub<TPEdu_API.Hubs.ChatHub>("/tpedu/v1/chathub");

app.Run();

// Test Deploy
