using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;

using Lumina.Excel.Sheets;

namespace LimLoToolkit.Tools;

/// <summary>
/// Muting a character in-game does not remove their messages — it replaces the
/// text with "(This message is from a muted character.)" and still prints the
/// line, sender name and all. This tool deletes that line outright, so a muted
/// character is actually silent.
///
/// The placeholder is <c>Addon</c> sheet row <b>14832</b>, read from the game
/// at load so the match works in every client language. See
/// <c>docs/actually-mute.md</c> for how that row was established.
///
/// Suppression happens on <see cref="Dalamud.Plugin.Services.IChatGui.ChatMessage"/>
/// via <see cref="IHandleableChatMessage.PreventOriginal"/>, which stops the
/// game processing the line any further — it never reaches the chat log, and it
/// is never written to the log file on disk.
/// </summary>
public sealed class ActuallyMuteTool : ITool
{
    public string Id          => "actually-mute";
    public string Name        => "Actually Mute";
    public string Description => "Delete the \"(This message is from a muted character.)\" placeholder from chat.";
    public string Category    => "Toolkit";

    /// <summary>
    /// <c>Addon</c> row holding the muted-character placeholder.
    ///
    /// **Verified** on 2026-08-14 by reading the Addon sheet straight out of
    /// sqpack with Lumina: row 14832 is the only row in either the Addon or
    /// LogMessage sheet whose text is exactly this placeholder. Reading the row
    /// rather than hard-coding English text is what makes the tool work on a
    /// French, German or Japanese client.
    /// </summary>
    private const uint MutedPlaceholderRow = 14832;

    /// <summary>
    /// Fallback used only if the sheet read fails (very early load, or SE moves
    /// the row in a future patch). English clients keep working either way.
    /// </summary>
    private const string MutedPlaceholderEnglish = "(This message is from a muted character.)";

    private readonly Configuration _config;

    /// <summary>The placeholder text this tool matches against, trimmed.</summary>
    private readonly string _placeholder;

    /// <summary>True if <see cref="_placeholder"/> came from the game's sheet.</summary>
    private readonly bool _fromSheet;

    private bool _subscribed;

    private int       _suppressedThisSession;
    private DateTime? _lastSuppressedAt;
    private string    _lastSuppressedSender  = string.Empty;
    private string    _lastSuppressedChannel = string.Empty;

    public ActuallyMuteTool(Configuration config)
    {
        _config = config;

        var fromSheet = ReadPlaceholderFromSheet();
        _fromSheet    = fromSheet is not null;
        _placeholder  = (fromSheet ?? MutedPlaceholderEnglish).Trim();

        try
        {
            Plugin.ChatGui.ChatMessage += OnChatMessage;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Actually Mute could not subscribe to chat messages.");
        }
    }

    /// <summary>
    /// Pulls the placeholder out of the Addon sheet. Returns null if the sheet
    /// or the row is not available, which is not an error worth logging loudly —
    /// the English fallback covers it.
    /// </summary>
    private static string? ReadPlaceholderFromSheet()
    {
        try
        {
            var text = Plugin.DataManager.GetExcelSheet<Addon>()?
                             .GetRowOrDefault(MutedPlaceholderRow)?
                             .Text.ExtractText();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Actually Mute could not read Addon row {MutedPlaceholderRow}; using the English string.");
            return null;
        }
    }

    /// <summary>
    /// Runs on the game thread for every chat line before it is printed. Kept
    /// deliberately cheap — this is on the hot path for combat log spam, so the
    /// enabled checks come before any string work.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage chat)
    {
        try
        {
            if (!_config.SuppressMutedCharacterMessages)
                return;

            // The registry gates OnFrameworkUpdate and DrawOverlay on this, but
            // a chat subscription is ours to gate ourselves.
            if (!_config.IsToolEnabled(Id))
                return;

            var text = chat.Message.TextValue;
            if (string.IsNullOrEmpty(text) || !IsMutedPlaceholder(text))
                return;

            chat.PreventOriginal();

            _suppressedThisSession++;
            _lastSuppressedAt      = DateTime.Now;
            _lastSuppressedSender  = chat.Sender.TextValue;
            _lastSuppressedChannel = chat.LogKind.ToString();
        }
        catch (Exception ex)
        {
            // Never let a chat handler take the game down. A miss just means the
            // line prints as it normally would.
            Plugin.Log.Error(ex, "Actually Mute threw while handling a chat message.");
        }
    }

    /// <summary>
    /// Exact match on the placeholder, plus an ends-with case.
    ///
    /// Ends-with is there because the sender is normally a separate field but is
    /// baked into the message text on some channel types; without it the tool
    /// would silently do nothing on those channels. Nothing else legitimately
    /// ends with this sentence, and if someone types it by hand then removing it
    /// is still what was asked for.
    /// </summary>
    private bool IsMutedPlaceholder(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Equals(_placeholder, StringComparison.Ordinal)
            || trimmed.EndsWith(_placeholder, StringComparison.Ordinal);
    }

    public void Draw()
    {
        UiHelpers.SectionHeader("Actually Mute");
        UiHelpers.Muted(
            "Muting someone in-game does not silence them — the game still prints their name and " +
            "replaces the text with a placeholder. This deletes that line completely, so a muted " +
            "character leaves no trace in chat at all.");

        ImGui.Spacing();

        var suppress = _config.SuppressMutedCharacterMessages;
        if (ImGui.Checkbox("Remove muted-character messages", ref suppress))
        {
            _config.SuppressMutedCharacterMessages = suppress;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Only lines whose entire text is the muted placeholder are removed. Nothing else in " +
            "chat is touched, and your own mute list is not modified.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Status");

        if (!_subscribed)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad,
                "Not listening to chat — the subscription failed at load. Disable and re-enable the " +
                "plugin to retry.");
            return;
        }

        if (!_config.SuppressMutedCharacterMessages)
        {
            UiHelpers.Muted("Switched off. Muted characters print their placeholder line as usual.");
            return;
        }

        UiHelpers.Colored(UiHelpers.Good, "Listening.");

        if (ImGui.BeginTable("##limlo-actuallymute", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Removed this session", _suppressedThisSession.ToString());
            UiHelpers.Row("Last removed",
                _lastSuppressedAt is { } at ? at.ToString("HH:mm:ss") : "—");
            UiHelpers.Row("From",
                string.IsNullOrEmpty(_lastSuppressedSender) ? "—" : _lastSuppressedSender);
            UiHelpers.Row("Channel",
                string.IsNullOrEmpty(_lastSuppressedChannel) ? "—" : _lastSuppressedChannel);
            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (_suppressedThisSession == 0)
        {
            UiHelpers.Muted(
                "Nothing removed yet. The counter only moves when a character you have muted " +
                "actually says something.");
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Matching");

        if (ImGui.BeginTable("##limlo-actuallymute-match", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Text matched", _placeholder);
            UiHelpers.Row("Source",
                _fromSheet ? $"Game data (Addon row {MutedPlaceholderRow})" : "Built-in English fallback");
            ImGui.EndTable();
        }

        if (!_fromSheet)
        {
            ImGui.Spacing();
            UiHelpers.ColoredWrapped(UiHelpers.Warn,
                "The game's own text could not be read, so the English wording is being used. On a " +
                "non-English client nothing will match.");
        }
    }

    public void Dispose()
    {
        if (!_subscribed)
            return;

        try
        {
            Plugin.ChatGui.ChatMessage -= OnChatMessage;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Actually Mute failed to unsubscribe from chat messages.");
        }

        _subscribed = false;
    }
}
