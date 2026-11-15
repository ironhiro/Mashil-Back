using Serilog;
using Serilog.Formatting.Compact;

namespace Mashil.Api.Ops;

public static class ConsoleLogging
{
    /// <summary>
    /// 개발: 사람이 읽는 텍스트. 그 외(운영): 한 줄에 JSON 하나 (CLEF, @t·@m·@l·속성) — docker logs 나 수집기가 그대로 파싱한다.
    /// Worker 도 같은 형식 (src/Mashil.Worker/Program.cs).
    /// </summary>
    public static LoggerConfiguration WriteToMashilConsole(this LoggerConfiguration lc, IHostEnvironment env) =>
        env.IsDevelopment()
            ? lc.WriteTo.Console()
            : lc.WriteTo.Console(new RenderedCompactJsonFormatter());
}
