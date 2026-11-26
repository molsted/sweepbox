using Sweepbox.Core.Running;

namespace Sweepbox.Tests;

public sealed class ElevatedScriptTests
{
    private static string Build(string path = @"C:\Windows\system32", IReadOnlyDictionary<string, string>? env = null,
        string exe = @"C:\ProgramData\chocolatey\bin\choco.exe", string[]? args = null) =>
        ElevatedScript.Build(path, env ?? new Dictionary<string, string>(), exe, args ?? ["upgrade", "all", "--yes"], @"C:\Temp\x\output.log");

    [Fact]
    public void Script_switches_to_utf8_quotes_everything_and_returns_the_exit_code()
    {
        var lines = Build(env: new Dictionary<string, string> { ["NO_COLOR"] = "1" }).Split("\r\n");

        Assert.Equal("@echo off", lines[0]);
        Assert.Equal("chcp 65001 >nul", lines[1]);
        Assert.Contains("set \"PATH=C:\\Windows\\system32\"", lines);
        Assert.Contains("set \"NO_COLOR=1\"", lines);
        Assert.Contains("\"C:\\ProgramData\\chocolatey\\bin\\choco.exe\" \"upgrade\" \"all\" \"--yes\" > \"C:\\Temp\\x\\output.log\" 2>&1", lines);
        Assert.Contains("exit /b %ERRORLEVEL%", lines);
    }

    [Fact]
    public void Percent_signs_are_escaped_so_nothing_expands()
    {
        var script = Build(path: @"C:\%USERNAME%\bin", args: ["--except=100%"]);

        Assert.Contains("set \"PATH=C:\\%%USERNAME%%\\bin\"", script);
        Assert.Contains("\"--except=100%%\"", script);
    }

    [Theory]
    [InlineData("C:\\bin\" & calc & \"")]
    [InlineData("C:\\bin\r\ncalc")]
    [InlineData("C:\\bin\ncalc")]
    [InlineData("C:\\bin\0")]
    public void A_path_that_could_inject_commands_is_refused(string path) =>
        Assert.Throws<ArgumentException>(() => Build(path: path));

    [Fact]
    public void Quotes_in_arguments_environment_or_executable_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(args: ["a\" & calc & \"b"]));
        Assert.Throws<ArgumentException>(() => Build(env: new Dictionary<string, string> { ["X"] = "\"" }));
        Assert.Throws<ArgumentException>(() => Build(env: new Dictionary<string, string> { ["X & calc"] = "1" }));
        Assert.Throws<ArgumentException>(() => Build(exe: "C:\\x\".exe"));
    }

    [Theory]
    [InlineData("/opt/choco/choco.exe", true)]
    [InlineData("/Users/ann/bin/choco.exe", false)]
    [InlineData("/Users/ANN/bin/choco.exe", false)]
    [InlineData("/tmp/x/choco.exe", false)]
    [InlineData("choco.exe", false)]
    [InlineData("/Users/anna/choco.exe", true)]
    public void Only_executables_outside_user_writable_folders_may_run_elevated(string exe, bool expected) =>
        Assert.Equal(expected, ElevatedScript.IsSafeExecutable(exe, ["/Users/ann", "/tmp"]));
}
