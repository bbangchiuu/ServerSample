# Dockerfile: "công thức" đóng gói ServerSampleApi thành một Docker image.
# Build (chạy trong thư mục ServerSampleApi, nơi có file này):
#     docker build -t serversample-api .
#
# MULTI-STAGE BUILD: 2 giai đoạn trong cùng một file.
#   1. "build":   image có .NET SDK (to, ~800 MB: trình biên dịch, NuGet...) để build và publish.
#   2. "runtime": image chỉ có ASP.NET Core runtime (nhỏ, ~230 MB). Chỉ chép KẾT QUẢ build sang.
# Image cuối cùng chỉ là giai đoạn 2: không có SDK, không có mã nguồn → nhỏ hơn, ít thứ để tấn công hơn.

# ===== Giai đoạn 1: build =====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Chép RIÊNG file .csproj rồi restore TRƯỚC khi chép code.
# Docker cache từng bước (layer): bước nào có đầu vào không đổi thì dùng lại kết quả cũ.
# Sửa code nhưng không đổi package → .csproj không đổi → bỏ qua restore (tải package), build nhanh hơn nhiều.
COPY ServerSampleApi/ServerSampleApi.csproj ServerSampleApi/
RUN dotnet restore ServerSampleApi/ServerSampleApi.csproj

# Giờ mới chép toàn bộ mã nguồn của project server (test không cần cho image chạy).
COPY ServerSampleApi/ ServerSampleApi/

# publish: build Release và gom mọi thứ cần để chạy (dll, appsettings.json, wwwroot...) vào /app/publish.
RUN dotnet publish ServerSampleApi/ServerSampleApi.csproj --configuration Release --output /app/publish --no-restore

# ===== Giai đoạn 2: runtime =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl: để Docker tự gọi /health/live (HEALTHCHECK bên dưới). Image runtime mặc định không có sẵn.
# Cài khi còn là root; xoá danh sách gói tải về để image nhỏ.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
# Chép KẾT QUẢ publish từ giai đoạn 1 (không phải mã nguồn).
COPY --from=build /app/publish .

# Chỉ nghe HTTP ở cổng 8080 (cổng mặc định của image .NET 8+). HTTPS do reverse proxy phía trước lo.
ENV ASPNETCORE_HTTP_PORTS=8080
# Môi trường Production: log JSON, Swagger tắt, không đọc User Secrets / appsettings.Development.json.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Docker tự hỏi "/health/live" mỗi 30 giây; 3 lần liên tiếp không trả lời → container bị đánh dấu "unhealthy".
# --start-period: cho server 20 giây để khởi động trước khi bắt đầu tính lỗi.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health/live || exit 1

# Chạy bằng user THƯỜNG "app" (có sẵn trong image .NET 8+), không phải root:
# lỡ server bị khai thác lỗ hổng thì kẻ tấn công cũng chỉ có quyền của user thường trong container.
USER $APP_UID

ENTRYPOINT ["dotnet", "ServerSampleApi.dll"]
