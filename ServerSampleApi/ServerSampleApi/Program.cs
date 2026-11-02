using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
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

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// Thêm kiểu xác thực "Bearer" vào tài liệu OpenAPI, để Swagger UI hiện nút "Authorize" cho dán token vào.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, context, cancellationToken) =>
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
    document.Security ??= new List<OpenApiSecurityRequirement>();
    document.Security.Add(new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>(),
    });
    return Task.CompletedTask;
}));

var app = builder.Build();

// Redis chưa có bảng xếp hạng thì dựng lại từ PostgreSQL, TRƯỚC khi server nhận request.
// Ngoài HTTP request thì không có scope sẵn, nên tự tạo một scope để lấy LeaderboardService (Scoped).
// Test tắt bước này (Leaderboard:RebuildOnStartup = false) vì lúc server ảo khởi động,
// database test chưa chạy migration, chưa có bảng Players.
if (app.Configuration.GetValue("Leaderboard:RebuildOnStartup", defaultValue: true))
{
    using IServiceScope scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LeaderboardService>().RebuildIfMissingAsync();
}

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); 
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));
}

app.UseHttpsRedirection();

// Thứ tự quan trọng: phải biết "bạn là ai" (Authentication) rồi mới xét "bạn được làm gì" (Authorization).
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
