using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// Băm mật khẩu (thuật toán PBKDF2), có sẵn trong ASP.NET Core, không cần cài thêm package.
builder.Services.AddSingleton<IPasswordHasher<AccountModel>, PasswordHasher<AccountModel>>();

// Database PostgreSQL qua EF Core. Chuỗi kết nối "GameDb" nằm trong appsettings.Development.json.
// AddDbContext đăng ký kiểu Scoped: MỖI REQUEST một GameDbContext riêng, xong request thì bỏ đi.
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); 
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
