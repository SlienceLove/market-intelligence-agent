using Xunit;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Marks a containment test that needs to create real symbolic links, and reports it as
/// *skipped* rather than passed when the machine will not allow that.
///
/// Symbolic links are the one escape a lexical containment check cannot see, so these
/// tests are the only evidence that link containment works. An early <c>return</c> when
/// link creation fails makes them report green instead — meaning an unprivileged CI
/// runner shows a fully green suite that never exercised containment at all. That is
/// worse than a failure, because it looks like proof.
///
/// Capability is probed once by actually creating a link in a temp directory; asking
/// about privileges or developer mode would only approximate the answer.
///
/// Set MI_REQUIRE_SYMLINK_TESTS=1 to turn the skip into a hard failure. Use that in at
/// least one CI lane so these tests cannot silently stop running everywhere.
/// </summary>
public sealed class RequiresSymbolicLinkFactAttribute : FactAttribute
{
    public RequiresSymbolicLinkFactAttribute()
    {
        Skip = SymbolicLinkCapability.SkipReason;
    }
}

internal static class SymbolicLinkCapability
{
    private static readonly Lazy<string?> Probe = new(Detect, isThreadSafe: true);

    internal static string? SkipReason => Probe.Value;

    private static string? Detect()
    {
        var failure = TryProbe();
        if (failure is null)
        {
            return null;
        }

        // An opted-in lane must fail loudly instead of skipping, otherwise "we have a
        // lane for it" quietly becomes "nowhere runs it".
        var required = Environment.GetEnvironmentVariable("MI_REQUIRE_SYMLINK_TESTS");
        if (!string.IsNullOrWhiteSpace(required) && required != "0")
        {
            throw new InvalidOperationException(
                $"MI_REQUIRE_SYMLINK_TESTS is set but symbolic links are unavailable: {failure}");
        }

        return failure;
    }

    private static string? TryProbe()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "mi-symlink-probe",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, "target.txt");
            File.WriteAllText(target, "probe");

            var link = Path.Combine(directory, "link.txt");
            File.CreateSymbolicLink(link, target);

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return $"symbolic link creation unavailable ({exception.GetType().Name}); " +
                   "enable Windows Developer Mode or run elevated to exercise link containment.";
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Probe cleanup is best-effort.
            }
        }
    }
}
