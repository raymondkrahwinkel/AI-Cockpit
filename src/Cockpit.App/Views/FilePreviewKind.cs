namespace Cockpit.App.Views;

// What FilePreviewWindow shows for a resolved path (AC-642).
internal enum FilePreviewKind
{
    Image,
    Svg,
    Markdown,
    Json,
    Csv,
    Text,
    Directory,
    Other,
    Missing,
}
