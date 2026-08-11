using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace LimLoToolkit;

/// <summary>
/// Small shared ImGui primitives so every tool looks the same without pulling
/// in a UI framework. Deliberately thin — this plugin is standalone and must
/// not take a dependency on PanacheUI or any sibling plugin.
/// </summary>
public static class UiHelpers
{
    public static readonly Vector4 Accent = new(0.42f, 0.72f, 0.98f, 1.00f);
    public static readonly Vector4 Dim    = new(0.62f, 0.64f, 0.68f, 1.00f);
    public static readonly Vector4 Good   = new(0.45f, 0.82f, 0.50f, 1.00f);
    public static readonly Vector4 Warn   = new(0.95f, 0.72f, 0.32f, 1.00f);
    public static readonly Vector4 Bad    = new(0.92f, 0.36f, 0.34f, 1.00f);

    /// <summary>Hand-confirmed values. Outranks the measured states.</summary>
    public static readonly Vector4 Official = new(0.74f, 0.53f, 0.98f, 1.00f);

    /// <summary>Green / amber / red for a solved, partial, or empty data set.</summary>
    public static Vector4 ConfidenceColor(Tools.AggroConfidence confidence) => confidence switch
    {
        Tools.AggroConfidence.Confident => Good,
        Tools.AggroConfidence.Learning  => Warn,
        _                               => Bad,
    };

    /// <summary>
    /// Colour for a mob's overall state. Locked wins over everything: those are
    /// the official values, established by hand rather than inferred, and that
    /// is a different claim from "the samples agree".
    /// </summary>
    public static Vector4 StateColor(Tools.AggroConfidence confidence, bool locked) =>
        locked ? Official : ConfidenceColor(confidence);

    /// <summary>
    /// Coloured text on a single line. For short labels only — table cells,
    /// legends, counts. Anything sentence-length must use
    /// <see cref="ColoredWrapped"/> or it runs off the panel edge.
    /// </summary>
    public static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Coloured text that wraps to the panel width.</summary>
    public static void ColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Accent-coloured heading with a separator under it.</summary>
    public static void SectionHeader(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>Greyed-out explanatory text, wrapped to the content width.</summary>
    public static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>One label/value row. Must be called between Begin/EndTable.</summary>
    public static void Row(string label, string value)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    /// <summary>A grey "(?)" marker that shows <paramref name="text"/> on hover.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted("(?)");
        ImGui.PopStyleColor();

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
