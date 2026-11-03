using System.Runtime.InteropServices;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>
/// Reads the selection in the focused app the only way Windows allows for every app: a
/// synthetic Ctrl+C, then the clipboard, then the clipboard put back as it was.
/// </summary>
/// <remarks>
/// <b>Waits for the user's hands first.</b> The command shortcut is still held, or just let go,
/// when this runs: Ctrl+C sent while Shift is down arrives as Ctrl+Shift+C, which is something
/// else entirely in most apps. Whether anything was copied is read from the clipboard's
/// sequence number, so an empty selection is not mistaken for whatever was copied before.
/// </remarks>
public sealed class ClipboardSelectionReader : ISelectionReader
{
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CopyWait = TimeSpan.FromMilliseconds(600);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <inheritdoc />
    public async ValueTask<string?> CopySelectionAsync(CancellationToken cancellationToken)
    {
        for (var waited = TimeSpan.Zero; SendInputTextInjector.AnyModifierDown() && waited < ModifierWait; waited += TimeSpan.FromMilliseconds(40))
        {
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        var previous = SendInputTextInjector.ReadClipboardText(out var opened);
        if (!opened) return null;

        var before = GetClipboardSequenceNumber();
        if (!SendInputTextInjector.PressCtrlC()) return null;

        for (var waited = TimeSpan.Zero; GetClipboardSequenceNumber() == before && waited < CopyWait; waited += TimeSpan.FromMilliseconds(30))
        {
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        }

        // Nothing selected: most apps copy nothing, and the clipboard is as the user left it.
        if (GetClipboardSequenceNumber() == before) return null;

        var selection = SendInputTextInjector.ReadClipboardText(out _);
        if (previous is not null) SendInputTextInjector.WriteClipboardText(previous);

        return string.IsNullOrWhiteSpace(selection) ? null : selection;
    }
}
