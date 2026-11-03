using TalkToText.App;
using Shouldly;
using Xunit;

namespace TalkToText.AppTests;

/// <summary>One copy of the app at a time; a second launch asks the first to show itself.</summary>
public sealed class SingleInstanceTests
{
    // Unique per run, so these never meet a real Talk-to-Text or another test.
    private readonly string _name = $"TalkToText.Test.{Guid.NewGuid():N}";

    [Fact]
    public void Only_the_first_claim_succeeds_until_it_is_released()
    {
        using (var first = SingleInstance.TryClaim(_name))
        {
            first.ShouldNotBeNull();

            // Claimed from another thread, as a second process would: a mutex is re-entrant
            // on the thread that owns it.
            SingleInstance? second = null;
            var thread = new Thread(() => second = SingleInstance.TryClaim(_name));
            thread.Start();
            thread.Join();
            second.ShouldBeNull();
        }

        using var again = SingleInstance.TryClaim(_name);
        again.ShouldNotBeNull();
    }

    [Fact]
    public void A_second_launch_reaches_the_running_copy()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var running = SingleInstance.TryClaim(_name);
        running.ShouldNotBeNull();

        using var shown = new ManualResetEventSlim();
        running.Listen(shown.Set);

        SingleInstance.SignalRunning(_name).ShouldBeTrue();
        shown.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
    }

    [Fact]
    public void Signalling_with_nothing_running_reports_failure_rather_than_throwing()
    {
        SingleInstance.SignalRunning(_name).ShouldBeFalse();
    }
}
