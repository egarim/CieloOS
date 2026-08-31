using System.Globalization;
using System.Text;

namespace WorkspaceRuntime.Application;

// The HyperFrames composition for a recording (#19). A tutorial is an HTML
// document: the recording is the A-roll <video>, and each step becomes an
// overlay track at its timestamp — so an agent edits the tutorial by editing
// HTML, which is exactly what an agent is good at. Rendering the composition is
// HyperFrames; this is the interface it renders.
public static class HyperFrameComposition
{
    public static string Render(string recordingPath, double startedAtUnix, int seconds, int width, int height, IReadOnlyList<AuditStep> steps)
    {
        var video = recordingPath.Replace("\\", "/");
        var start = DateTimeOffset.FromUnixTimeSeconds((long)startedAtUnix);
        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>CieloOS tutorial</title></head><body>");
        sb.AppendLine($"<video id=\"a-roll\" src=\"{video}\" width=\"{width}\" height=\"{height}\" controls></video>");

        foreach (var step in steps)
        {
            var at = (step.At - start).TotalSeconds;
            sb.AppendLine(
                "<div class=\"overlay\" " +
                $"data-start=\"{at.ToString("F2", CultureInfo.InvariantCulture)}\" " +
                "data-duration=\"4\" data-track-index=\"1\" " +
                $"data-title=\"{Escape(step.Action)}\">" +
                $"<p>{Escape(step.Action)} — {Escape(step.Detail)}</p></div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
