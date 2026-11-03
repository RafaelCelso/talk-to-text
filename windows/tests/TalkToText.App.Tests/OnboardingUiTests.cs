using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TalkToText.App.Views;
using TalkToText.Core;
using Shouldly;

namespace TalkToText.AppTests;

/// <summary>The first-run setup.</summary>
public sealed class OnboardingUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ttt-ui-onb-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [AvaloniaFact]
    public void Every_step_renders_and_skipping_the_last_one_closes_the_setup()
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        var setup = new OnboardingWindow(null);
        var closed = false;
        setup.Closed += (_, _) => closed = true;
        setup.Show(owner);

        for (var step = 0; step <= 6; step++)
        {
            setup.Go(step);
            setup.Step.ShouldBe(step);
            setup.Bounds.Width.ShouldBeGreaterThan(0);
        }

        closed.ShouldBeFalse();
        setup.Go(7);
        closed.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Going_back_through_every_step_and_forward_again_does_not_throw()
    {
        // Going back once rebuilt a page around controls the old page still held, and crashed.
        var setup = new OnboardingWindow(null);
        setup.Show();
        for (var step = 1; step <= 6; step++) setup.Go(step);
        for (var step = 5; step >= 0; step--)
        {
            setup.Go(step);
            setup.Step.ShouldBe(step);
        }
        for (var step = 1; step <= 6; step++) setup.Go(step);
        setup.Step.ShouldBe(6);
        setup.Close();
    }

    [AvaloniaFact]
    public void Going_back_from_the_first_step_stays_on_the_welcome()
    {
        var setup = new OnboardingWindow(null);
        setup.Show();
        setup.Go(-1);
        setup.Step.ShouldBe(0);
        setup.Close();
    }

    [AvaloniaFact]
    public void A_fresh_install_and_an_older_settings_file_both_show_the_setup()
    {
        new SettingsData().OnboardingDone.ShouldBeFalse();

        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "Mode": "Toggle", "DisplayName": "Rafael" }""");
        var settings = new AppSettings(path);
        settings.Data.OnboardingDone.ShouldBeFalse();
        settings.Data.Mode.ShouldBe(ActivationMode.Toggle);

        settings.Update(settings.Data with { OnboardingDone = true });
        new AppSettings(path).Data.OnboardingDone.ShouldBeTrue();
    }
}
