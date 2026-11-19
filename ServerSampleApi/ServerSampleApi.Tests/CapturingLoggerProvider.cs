using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ServerSampleApi.Tests;

/// <summary>Một dòng log bắt được: category, mức độ, câu log, và mọi cặp khoá/giá trị (của chính log + của các scope).</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// "Nơi ghi log" giả cho test: thay vì in ra console, giữ mọi dòng log trong bộ nhớ để test đọc lại và kiểm tra.
/// Gắn vào server ảo bằng builder.ConfigureLogging(logging => logging.AddProvider(provider)).
///
/// ISupportExternalScope: để nhận được cả dữ liệu của scope (TraceId, PlayerId...) chứ không chỉ của chính dòng log.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Logs { get; } = new();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    /// <summary>Chờ tới khi có dòng log thoả điều kiện (log tổng kết request được ghi SAU khi trả response).</summary>
    public async Task<CapturedLog> WaitForAsync(Func<CapturedLog, bool> condition)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            CapturedLog? found = Logs.FirstOrDefault(condition);
            if (found != null)
            {
                return found;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Expected log entry was not written.");
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (KeyValuePair<string, object?> pair in pairs)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            // Gom dữ liệu của mọi scope đang mở (giống bộ định dạng JSON với IncludeScopes = true).
            owner._scopes.ForEachScope((scope, collected) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                {
                    foreach (KeyValuePair<string, object?> pair in scopePairs)
                    {
                        collected[pair.Key] = pair.Value;
                    }
                }
            }, values);

            owner.Logs.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values));
        }
    }
}
