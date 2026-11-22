using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpLogging;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Errors;
using ServerSampleApi.Health;
using ServerSampleApi.Hubs;
using ServerSampleApi.Logging;
using ServerSampleApi.Models;
using ServerSampleApi.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// Lỗi BẤT NGỜ (database sập, bug...) trong request HTTP: ghi log kèm traceId, trả ProblemDetails gọn gàng.
// AddProblemDetails: đăng ký dịch vụ viết lỗi dạng ProblemDetails (có traceId). Kết hợp với UseExceptionHandler và
// UseStatusCodePages bên dưới, lỗi 500/503 và cả 401, 404 không có nội dung đều trả cùng một định dạng JSON.
builder.Services.AddProblemDetails();

// Log MỖI REQUEST MỘT DÒNG: method, path, status, thời gian xử lý (Duration), cộng PlayerId (interceptor).
// Định dạng log (JSON khi chạy thật, dạng dễ đọc khi Development) nằm trong appsettings, không nằm trong code.
//  - CombineLogs: gộp phần request và phần response thành một dòng, thay vì 2 dòng rời nhau.
//  - CHỈ ghi các trường an toàn. KHÔNG ghi header (có "Authorization: Bearer <token>"), KHÔNG ghi query string
//    (SignalR gửi token trong "?access_token=..."), KHÔNG ghi body (có mật khẩu lúc đăng nhập).
//    Log thường được nhiều người đọc và lưu lâu: lộ token trong log cũng nguy hiểm như lộ mật khẩu.
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod
        | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode
        | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});
builder.Services.AddHttpLoggingInterceptor<PlayerIdHttpLoggingInterceptor>();
// Health check (/health/...) được gọi vài giây một lần: không ghi dòng log HttpLogging cho chúng.
builder.Services.AddHttpLoggingInterceptor<SkipHealthCheckHttpLoggingInterceptor>();

// Health check: /health/live (process còn sống) và /health/ready (PostgreSQL + Redis). Xem Health/HealthCheckSetup.cs.
builder.Services.AddGameHealthChecks();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

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
// "Đồng hồ" của server (lớp TimeProvider có sẵn trong .NET 8+). TimeProvider.System = giờ thật của máy.
// Class nào cần biết "bây giờ" thì nhận TimeProvider qua constructor thay vì gọi DateTime.UtcNow,
// để unit test thay bằng đồng hồ giả (hiện có: TokenService).
builder.Services.AddSingleton(TimeProvider.System);
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

            // Chỉ chấp nhận đúng thuật toán mình dùng để ký (HS256): token ký bằng thuật toán khác bị từ chối.
            // (Token "alg":"none" không có chữ ký thì vốn đã bị từ chối, vì RequireSignedTokens mặc định là true.)
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
        HttpContext httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        // Viết qua IProblemDetailsService (giống GlobalExceptionHandler), KHÔNG tự WriteAsJsonAsync:
        // dịch vụ này tự thêm traceId và đặt Content-Type là application/problem+json, đúng định dạng với
        // các lỗi 401/404/409/503. Tự viết bằng WriteAsJsonAsync thì thiếu cả hai.
        // RequestServices: lấy service từ DI ngay trong request (lambda này không có constructor để nhận service).
        IProblemDetailsService problemDetails = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Title = "Too many requests",
                Detail = "Please wait and try again later.",
                Status = StatusCodes.Status429TooManyRequests,
            },
        });
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
//    ở phía sau. Thiếu nó thì Connect() ném lỗi và cả API không khởi động được chỉ vì Redis chưa bật
//    (bảng xếp hạng, danh sách online).
// Dùng dạng "factory" (sp => ...) để chỉ kết nối lúc được lấy ra lần đầu, lúc đó cấu hình đã đầy đủ (kể cả phần
// test ghi đè). Thực tế "lần đầu" là ngay lúc server khởi động: PresenceHeartbeatService (hosted service, được tạo
// khi khởi động) cần PresenceService, mà PresenceService cần IConnectionMultiplexer. Connect() là hàm đồng bộ,
// nên Redis tắt thì server khởi động chậm đi một chút (chờ hết thời gian kết nối) rồi mới chạy tiếp.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!));
// Scoped (không phải Singleton) vì LeaderboardService dùng GameDbContext, mà GameDbContext là Scoped.
builder.Services.AddScoped<LeaderboardService>();

// Luật của clan (ClansController và GameHub cùng dùng). Scoped vì dùng GameDbContext.
builder.Services.AddScoped<ClanService>();

// Redis chưa có bảng xếp hạng thì dựng lại từ PostgreSQL, chạy NGẦM khi server khởi động (không làm server chờ).
builder.Services.AddHostedService<LeaderboardStartupRebuildService>();

// SignalR (realtime): có sẵn trong ASP.NET Core. Hub nằm trong thư mục Hubs.
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);     // server gửi ping
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30); // chờ client tối đa

    // Lỗi bất ngờ trong hàm hub: ghi log mức Error kèm tên hàm và người chơi (hub không đi qua UseExceptionHandler).
    options.AddFilter<HubErrorLoggingFilter>();
})
    // Giao thức JSON có sẵn (AddSignalR tự đăng ký). Thêm MessagePack (package
    // Microsoft.AspNetCore.SignalR.Protocols.MessagePack): tin nhắn dạng nhị phân, nhỏ và nhanh hơn JSON.
    // Server hỗ trợ CẢ HAI, mỗi client tự chọn lúc handshake ({"protocol":"json"} hay {"protocol":"messagepack"}),
    // nên các client JSON đang có (HubClient, thư viện trong Unity, chat-test.html) không bị ảnh hưởng.
    .AddMessagePackProtocol()
    // Redis backplane (package Microsoft.AspNetCore.SignalR.StackExchangeRedis): cho phép chạy NHIỀU server.
    // Không có nó, mỗi server chỉ biết các kết nối của CHÍNH nó: Clients.All trên server 1 không tới được
    // người chơi đang nối vào server 2. Có nó, mỗi lần gửi (Clients.All, Clients.User...) server KHÔNG gửi thẳng
    // mà đăng tin lên Redis (PUBLISH). Các server đang nghe kênh đó (SUBSCRIBE), KỂ CẢ chính server gửi, nhận lại tin
    // rồi gửi cho các kết nối của mình. (Kênh của Clients.User chỉ được nghe bởi server đang giữ kết nối của người đó.)
    // Hệ quả: Redis sập thì lệnh gửi ném lỗi, cả người chơi nối cùng server cũng không nhận được tin.
    .AddStackExchangeRedis();

// Cấu hình backplane bằng Options pattern để đọc cấu hình LÚC CHẠY (test ghi đè được, giống JwtBearerOptions).
builder.Services.AddOptions<RedisOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        // Dùng chung Redis với bảng xếp hạng và danh sách online (chuỗi kết nối "Redis").
        options.Configuration = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);

        // Prefix của các kênh PUBLISH/SUBSCRIBE: các server cùng một game phải dùng CÙNG prefix mới nghe thấy nhau.
        // Test đặt prefix riêng cho từng server ảo, để tin của lớp test này không lọt sang lớp test khác.
        options.Configuration.ChannelPrefix = RedisChannel.Literal(configuration["SignalR:RedisChannelPrefix"] ?? "ServerSample");
    });
// Kết nối nào thuộc người chơi nào (claim "sub"), để Clients.User(playerId) gửi được cho đúng người.
builder.Services.AddSingleton<IUserIdProvider, PlayerIdUserIdProvider>();
// Ai đang online (Redis). Singleton vì chỉ dùng kết nối Redis, không dùng GameDbContext,
// và mỗi process server chỉ có MỘT ServerId.
builder.Services.AddSingleton<PresenceService>();
// Chạy ngầm: heartbeat mỗi 10 giây, dọn phần của server đã chết (người chơi bị "kẹt" online).
builder.Services.AddHostedService<PresenceHeartbeatService>();

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

// NGOÀI CÙNG pipeline: ghi dòng log tổng kết SAU KHI mọi middleware bên trong đã xong, nên thấy đúng status cuối cùng,
// kể cả 500/503 do UseExceptionHandler bên dưới tạo ra, 429 của rate limiter, 401 của Authentication.
app.UseHttpLogging();

// Ngay sau HttpLogging: bọc ngoài mọi middleware phía sau, nên exception ném ra ở bất cứ đâu bên dưới
// (controller, service, database...) đều được GlobalExceptionHandler bắt.
app.UseExceptionHandler();

// Response lỗi KHÔNG có nội dung (404 do sai đường dẫn, 401 do thiếu token...): tự thêm ProblemDetails,
// để client luôn đọc lỗi theo MỘT định dạng.
app.UseStatusCodePages();

// Swagger UI (/swagger) và tài liệu OpenAPI (/openapi/v1.json): luôn bật khi Development.
// Môi trường khác (server thật) thì TẮT, trừ khi chủ động bật bằng cấu hình Swagger:Enabled = true
// (biến môi trường Swagger__Enabled=true): người ngoài không cần xem toàn bộ danh sách API của game.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));
}

// Chỉ chuyển http → https khi chạy trên máy dev (https://localhost:5257).
// Khi chạy trong Docker, server chỉ nghe HTTP (cổng 8080); HTTPS do reverse proxy phía trước lo (giai đoạn 7, bước 5):
// proxy nhận https từ người chơi, rồi chuyển tiếp vào container bằng http. Nếu bật ở đây thì server không biết
// cổng https nào để chuyển hướng tới (cảnh báo "Failed to determine the https port for redirect").
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Trả về các file tĩnh trong thư mục wwwroot, ví dụ trang thử chat: https://localhost:5257/chat-test.html
app.UseStaticFiles();

// Đặt TRƯỚC Authentication: request vượt giới hạn bị chặn ngay, không tốn công kiểm tra token hay đọc database.
// Chỉ áp dụng cho API có [EnableRateLimiting(...)], các API khác đi qua bình thường.
app.UseRateLimiter();

// Thứ tự quan trọng: phải biết "bạn là ai" (Authentication) rồi mới xét "bạn được làm gì" (Authorization).
app.UseAuthentication();
// Đã biết token của ai: từ đây mọi log trong request đều mang PlayerId (xem PlayerIdLogScopeMiddleware).
app.UsePlayerIdLogScope();
app.UseAuthorization();

app.MapControllers();

// Kênh realtime: client kết nối tới wss://localhost:5257/hubs/game
app.MapHub<GameHub>(GameHub.Path);

// /health/live và /health/ready: không cần token (Docker, load balancer không đăng nhập).
app.MapGameHealthChecks();

app.Run();
