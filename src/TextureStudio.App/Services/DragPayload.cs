namespace TextureStudio.App.Services;

/// <summary>HTML5 drag-and-drop can't carry app objects across Blazor components without
/// string marshaling, so the dragged tile keys ride in this static slot instead. Dragging a
/// tile that belongs to the current selection carries the whole selection.</summary>
public static class DragPayload
{
    public static List<string> Keys { get; set; } = [];
}
