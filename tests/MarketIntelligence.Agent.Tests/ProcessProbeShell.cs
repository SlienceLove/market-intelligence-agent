namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Points the runner at the platform shell instead of FFmpeg. The runner's job is
/// process mechanics — exit codes, pipe draining, timeouts, tree kill — and that is
/// worth testing without depending on FFmpeg being installed on the test machine.
/// </summary>
internal static class ProcessProbeShell
{
    internal static string ExecutablePath => OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe")
        : "/bin/sh";

    internal static bool IsAvailable => File.Exists(ExecutablePath);

    /// <summary>Runs an inline command through the shell.</summary>
    internal static IReadOnlyList<string> Command(string windows, string posix) =>
        OperatingSystem.IsWindows() ? ["/c", windows] : ["-c", posix];

    /// <summary>Exits with the given code and writes nothing.</summary>
    internal static IReadOnlyList<string> ExitWith(int code) =>
        Command($"exit {code}", $"exit {code}");

    /// <summary>Writes one line to stdout.</summary>
    internal static IReadOnlyList<string> EchoStdout(string text) =>
        Command($"echo {text}", $"echo {text}");

    /// <summary>Writes many lines to stderr to test the capture ceiling.</summary>
    internal static IReadOnlyList<string> FloodStderr(int lines) =>
        Command(
            $"for /L %i in (1,1,{lines}) do @echo stderr-line-with-some-padding-%i 1>&2",
            $"i=0; while [ $i -lt {lines} ]; do echo stderr-line-with-some-padding-$i 1>&2; i=$((i+1)); done");

    /// <summary>
    /// Appends to a file forever. Whether the file stops growing after a timeout is
    /// the observable proof that the child was actually killed and not just abandoned.
    /// </summary>
    internal static IReadOnlyList<string> AppendForever(string path)
    {
        // A zero step makes "for /L" loop forever, which keeps this a one-liner and
        // avoids needing a batch label (labels only work inside a script file).
        var windowsPath = path.Replace('/', '\\');
        return Command(
            $"for /L %i in (1,0,2) do @echo tick>>\"{windowsPath}\"",
            $"while true; do echo tick >> \"{path}\"; done");
    }
}
