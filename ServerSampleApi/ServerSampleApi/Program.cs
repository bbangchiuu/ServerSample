using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Hubs;
using ServerSampleApi.Models;
using ServerSampleApi.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// Băm mật khẩu (thuật toán PBKDF2), có sẵn trong ASP.NET Core, không cần cài thêm package.
builder.Services.AddSingleton<IPasswordHasher<AccountModel>, PasswordHasher<AccountModel>>();

// JWT: đọc nhóm "Jwt" trong cấu hình vào JwtOptions.
//  - ValidateDataAnnotations: kiểm tra [Required], [MinLength(32)]... trong JwtOptions.
//  - ValidateOnStart: kiểm tra NGAY khi server khởi động. Quên đặt Jwt:SigningKey thì server báo lỗi rõ ràng
//    và không chạy, thay vì chạy bình thường rồi mới lỗi ở lần đăng nhập đầu tiên.
builder.Services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<TokenService>();
// Scoped (không phải Singleton như TokenService) vì RefreshTokenService dùng GameDbContext.
builder.Services.AddScoped<RefreshTokenService>();
// Chạy ngầm suốt đời server: định kỳ xoá refresh token đã hết hạn.
builder.Services.AddHostedService<RefreshTokenCleanupService>();

// Authentication (xác thực: "bạn là ai?"): mỗi request, đọc header "Authorization: Bearer <token>" và kiểm tra token.
// Token hợp lệ thì HttpContext.User (trong controller là "User") chứa các claim trong token: sub, unique_name...
// Không có token hoặc token sai thì User là "khách" (chưa đăng nhập).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Cấu hình cách kiểm tra token. Viết bằng Options pattern để lấy JwtOptions LÚC CHẠY
// (test ghi đè Jwt:SigningKey thì phần kiểm tra cũng dùng đúng khoá của test).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
    {
        JwtOptions jwt = jwtOptions.Value;

        // Giữ nguyên tên claim như trong token ("sub", "unique_name"). Mặc định thư viện đổi "sub" thành một tên
        // dài kiểu "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier" vì lý do lịch sử.
        bearerOptions.MapInboundClaims = false;

        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            // Token phải do CHÍNH server này phát hành (iss), cho ĐÚNG client game (aud),
            // và ký bằng ĐÚNG khoá bí mật. Sai một trong ba là từ chối.
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.CreateSigningKey(),

            // Chỉ chấp nhận đúng thuật toán mình dùng để ký. Không để kẻ xấu gửi token "alg":"none" (không chữ ký).
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },

            // Đồng hồ các máy có thể lệch nhau vài giây, nên cho phép trễ hạn một chút.
            // Mặc định là 5 phút: token hết hạn rồi vẫn dùng thêm được 5 phút, quá dài.
            ClockSkew = TimeSpan.FromSeconds(30),

            // User.Identity.Name sẽ lấy từ claim "unique_name" (tên tài khoản).
            NameClaimType = JwtRegisteredClaimNames.UniqueName,
        };

        bearerOptions.Events = new JwtBearerEvents
        {
            // Riêng hub SignalR: lấy token từ URL "?access_token=...".
            // Trình duyệt KHÔNG cho JavaScript đặt header khi mở WebSocket, nên client SignalR gửi token trên URL.
            // Chỉ nhận cách này cho đường dẫn của hub: URL dễ bị ghi vào log, các API thường vẫn bắt dùng header.
            OnMessageReceived = context =>
            {
                string? accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments(GameHub.Path))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });

// Khoá hệ thống cho các API quản trị ([RequireSystemKey]). Bí mật, nằm trong User Secrets như Jwt:SigningKey.
// ValidateOnStart: quên đặt System:ApiKey thì server báo lỗi ngay lúc khởi động.
builder.Services.AddOptions<SystemKeyOptions>()
    .BindConfiguration(SystemKeyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Rate limiting: giới hạn số lần gọi /login và /register của mỗi IP. Có sẵn trong ASP.NET Core.
builder.Services.AddOptions<RateLimitingOptions>()
    .BindConfiguration(RateLimitingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddRateLimiter(options =>
{
    // Vượt giới hạn: trả 429 kèm Retry-After (số giây phải chờ) và ProblemDetails như mọi lỗi khác.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        HttpResponse response = context.HttpContext.Response;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
        await response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Too many requests",
            Detail = "Please wait and try again later.",
            Status = StatusCodes.Status429TooManyRequests,
        }, cancellationToken);
    };

    options.AddPolicy(RateLimitingOptions.LoginPolicy, httpContext =>
    {
        RateLimitingOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        return PerIpFixedWindow(httpContext, limits.LoginPermitLimit, limits.LoginWindowSeconds);
    });
    options.AddPolicy(RateLimitingOptions.RegisterPolicy, httpContext =>
    {
        RateLimitingOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        return PerIpFixedWindow(httpContext, limits.RegisterPermitLimit, limits.RegisterWindowSeconds);
    });

    // Mỗi IP một bộ đếm riêng ("partition"). Fixed window: chia thời gian thành từng cửa sổ (ví dụ mỗi 60 giây),
    // trong một cửa sổ được tối đa permitLimit request, sang cửa sổ mới thì đếm lại từ 0.
    // Bộ đếm nằm trong RAM của server: khởi động lại là mất, chạy nhiều server thì mỗi server đếm riêng.
    static RateLimitPartition<string> PerIpFixedWindow(HttpContext httpContext, int permitLimit, int windowSeconds) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0, // hết hạn mức thì từ chối NGAY, không xếp hàng chờ
            });
});

// Authorization (phân quyền: "bạn được làm gì?"): các API có [Authorize] chỉ cho người đã đăng nhập gọi.
builder.Services.AddAuthorization();

// Database PostgreSQL qua EF Core. Chuỗi kết nối "GameDb" nằm trong appsettings.Development.json.
// AddDbContext đăng ký kiểu Scoped: MỖI REQUEST một GameDbContext riêng, xong request thì bỏ đi.
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")));

// Redis. Chuỗi kết nối "Redis" nằm trong appsettings.Development.json: "localhost:6379,abortConnect=false".
//  - ConnectionMultiplexer giữ kết nối tới Redis, tự kết nối lại khi bị rớt. Tạo MỘT lần (Singleton) và dùng chung.
//  - abortConnect=false: lúc server khởi động mà Redis chưa bật thì server VẪN chạy, thư viện tự thử kết nối lại
//    ở phía sau. Thiếu nó thì Connect() ném lỗi và cả API không khởi động được chỉ vì thiếu bảng xếp hạng.
// Dùng dạng "factory" (sp => ...) để chỉ kết nối khi có nơi cần dùng lần đầu, lúc đó cấu hình đã đầy đủ
// (kể cả phần test ghi đè).
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!));
// Scoped (không phải Singleton) vì LeaderboardService dùng GameDbContext, mà GameDbContext là Scoped.
builder.Services.AddScoped<LeaderboardService>();

// Redis chưa có bảng xếp hạng thì dựng lại từ PostgreSQL, chạy NGẦM khi server khởi động (không làm server chờ).
builder.Services.AddHostedService<LeaderboardStartupRebuildService>();

// SignalR (realtime): có sẵn trong ASP.NET Core, không cần cài package. Hub nằm trong thư mục Hubs.
builder.Services.AddSignalR();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    // 1. Khai báo kiểu xác thực "Bearer" một lần cho cả tài liệu, để Swagger UI hiện nút "Authorize" cho dán token vào.
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Dán accessToken lấy từ /api/accounts/login (không cần gõ chữ Bearer).",
        };

#if ALLDOCUMENT_SYSTEMKEY
        // Kiểu thứ hai: khoá hệ thống. ApiKey = "gửi NGUYÊN VĂN chuỗi này trong header tên Name", không thêm chữ gì phía trước.
        // Nhờ khai báo ở đây mà hộp Authorize có thêm ô "SystemKey": dán một lần, Swagger tự gửi cho mọi API cần key.
        document.Components.SecuritySchemes["SystemKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = RequireSystemKeyAttribute.HeaderName,
            Description = "Dán System:ApiKey trong User Secrets (dùng cho API quản trị, ví dụ dựng lại bảng xếp hạng).",
        };
#endif
        return Task.CompletedTask;
    });

    // 2. Với TỪNG API: có [Authorize] (ở hàm hoặc ở class) và không có [AllowAnonymous] thì mới gắn "cần Bearer".
    //    Swagger UI chỉ hiện ổ khoá ở những API này, nhìn vào là biết API nào cần đăng nhập.
    //    Đọc thẳng từ attribute trong code, nên thêm/bỏ [Authorize] là tài liệu tự đúng theo, không phải sửa tay.
    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
        bool requiresLogin = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        if (requiresLogin)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>(),
            });
        }

#if ALLDOCUMENT_SYSTEMKEY
        // 3. API có [RequireSystemKey]: gắn "cần SystemKey". Swagger hiện ổ khoá, và gửi kèm header System-Authorization
        //    với key đã dán trong hộp Authorize.
        if (metadata.OfType<RequireSystemKeyAttribute>().Any())
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("SystemKey", context.Document)] = new List<string>(),
            });
        }
#else
        // 3. API có [RequireSystemKey]: hiện ô nhập header "System-Authorization" ngay trong API đó.
        //    Được hiện thành ô riêng vì đây là header tự đặt tên. Header tên "Authorization" thì Swagger bỏ qua.
        if (metadata.OfType<RequireSystemKeyAttribute>().Any())
        {
            operation.Parameters ??= new List<IOpenApiParameter>();
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = RequireSystemKeyAttribute.HeaderName,
                In = ParameterLocation.Header,
                Required = true,
                Description = "System:ApiKey trong User Secrets",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }
#endif
        return Task.CompletedTask;
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); 
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));
}

app.UseHttpsRedirection();

// Trả về các file tĩnh trong thư mục wwwroot, ví dụ trang thử chat: https://localhost:5257/chat-test.html
app.UseStaticFiles();

// Đặt TRƯỚC Authentication: request vượt giới hạn bị chặn ngay, không tốn công kiểm tra token hay đọc database.
// Chỉ áp dụng cho API có [EnableRateLimiting(...)], các API khác đi qua bình thường.
app.UseRateLimiter();

// Thứ tự quan trọng: phải biết "bạn là ai" (Authentication) rồi mới xét "bạn được làm gì" (Authorization).
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Kênh realtime: client kết nối tới wss://localhost:5257/hubs/game
app.MapHub<GameHub>(GameHub.Path);

app.Run();
