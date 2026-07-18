using Microsoft.Extensions.Logging;

namespace DiskMigrator.Cli;

/// <summary>
/// 로그를 콘솔로 흘려보내는 최소 구현.
/// </summary>
/// <remarks>
/// 이 도구의 목적은 볼륨 잠금·VSS·GPT 보정이 실제로 어떤 순서로 무엇을 했는지
/// 눈으로 확인하는 것이므로, 라이브러리가 남기는 로그가 그대로 보여야 합니다.
/// </remarks>
internal sealed class ConsoleLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

    public void Dispose() { }

    private sealed class ConsoleLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string shortCategory = category[(category.LastIndexOf('.') + 1)..];

            var color = logLevel switch
            {
                LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
                LogLevel.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.DarkGray,
            };

            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"  [{shortCategory}] {formatter(state, exception)}");
            Console.ForegroundColor = previous;

            if (exception is not null) Console.WriteLine($"      {exception.Message}");
        }
    }
}

